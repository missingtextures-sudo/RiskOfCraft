using System;
using System.Collections.Generic;
using System.Diagnostics;
using RiskOfCraft.Link;
using UnityEngine;

namespace RiskOfCraft.World;

/// <summary>
/// Streams the stage's physics world around the player to Minecraft (PeakCraft's ray-cast sampler).
///
/// RoR2's stage collider meshes are not readable at runtime, so the world is sampled with ray casts on a
/// half-block grid. Each grid column becomes a list of solid spans (top and underside), found by
/// casting down repeatedly and testing every gap for "air or inside something". Neighbouring
/// columns are joined into triangles: tops at similar heights become floor, undersides become
/// ceiling, and where a floor ends against something solid (or drops off a solid ledge) the
/// joining triangles stand up as a wall. Minecraft's local player collides with those triangles;
/// the 1/8-block occupancy sent alongside serves the teleport hold, other entities and block support.
///
/// Work is per region column (8x8 blocks, the full vertical window) and amortised by a per-frame
/// time budget, nearest first. Far columns are evicted by sending them empty.
/// </summary>
internal sealed unsafe class CollisionExporter
{
    public const int RegionSize = 8;
    private const float Step = 0.5f;
    private const int Cells = 16;          // per region edge
    private const int Verts = Cells + 1;
    private const int MaxSpans = 8;
    private const int Radius = 3;          // region columns around the centre: 7 x 7
    private const int WindowBelow = 3;     // regions below the centre's region
    private const int WindowAbove = 2;     // regions above it
    private const float JoinTolerance = 1.0f;   // tops this close (blocks) are one surface
    private const float Big = 1e9f;
    private const float NearRefreshSeconds = 6f;
    private const float FarRefreshSeconds = 45f;
    private const double BudgetMs = 2.5;
    private const long MaxBacklogBytes = 12L << 20;

    private struct Span
    {
        public float top;     // Unity Y; Big when it runs out of the top of the window
        public float bottom;  // Unity Y of the underside; -Big when solid all the way down
        public uint flags;    // the top surface's ColTri flags: diggable, terrain, dig material
    }

    private struct Exported
    {
        public int ryMin, ryMax;
        public float time;
    }

    private readonly HostLink link;
    private readonly int mask;
    private readonly Span[] spans = new Span[Verts * Verts * MaxSpans];
    private readonly byte[] spanCount = new byte[Verts * Verts];
    private readonly Dictionary<long, Exported> exported = new();
    private readonly List<long> scratchKeys = new();
    private readonly HashSet<ulong> cellPolygons = new();
    private readonly Stopwatch watch = new();
    private List<Proto.ColTri>[] tris = Array.Empty<List<Proto.ColTri>>();
    private ulong[][] occupancy = Array.Empty<ulong[]>();
    private byte[] payload = new byte[64 << 10];
    private int windowRyMin, windowRyMax;
    private float lastStats;
    private int statColumns, statTris, statRays;

    public CollisionExporter(HostLink link)
    {
        this.link = link;
        // What a RoR2 character stands on: the stage's world layer. Characters, pickups and
        // projectiles are on other layers and are left out.
        mask = RoR2.LayerIndex.world.mask;
    }

    public uint Epoch { get; private set; } = 1;

    /// <summary>World changed (scene, map segment, a new Minecraft): Minecraft drops everything, we start over.</summary>
    public void Reset(string why)
    {
        surfaceFlags.Clear();
        Epoch++;
        exported.Clear();
        uint epoch = Epoch;
        link.WriteCollision((uint)Proto.ColMsg.Clear, &epoch, sizeof(uint));
        Plugin.Log.LogInfo($"collision: cleared, epoch {Epoch} ({why})");
    }

    /// <summary>Exports around <paramref name="centreMc"/> (Minecraft coords) within this frame's time budget.</summary>
    public void Update(double centreX, double centreY, double centreZ)
    {
        if (link.CollisionBacklog > MaxBacklogBytes)
        {
            return; // Minecraft is behind; don't pile on
        }
        int crx = FloorDiv((int)Math.Floor(centreX), RegionSize);
        int cry = FloorDiv((int)Math.Floor(centreY), RegionSize);
        int crz = FloorDiv((int)Math.Floor(centreZ), RegionSize);
        windowRyMin = cry - WindowBelow;
        windowRyMax = cry + WindowAbove;

        watch.Restart();
        Evict(crx, crz);
        float now = Time.unscaledTime;
        while (watch.Elapsed.TotalMilliseconds < BudgetMs)
        {
            if (!PickColumn(crx, cry, crz, now, out int rx, out int rz))
            {
                break;
            }
            if (!ExportColumn(rx, rz))
            {
                break; // ring full
            }
        }
        if (now - lastStats > 10f && statColumns > 0)
        {
            Plugin.Log.LogInfo($"collision: {statColumns} region columns sent in the last {now - lastStats:F0} s ({statTris} triangles, {statRays} ray casts), {exported.Count} columns live, epoch {Epoch}");
            lastStats = now;
            statColumns = statTris = statRays = 0;
        }
    }

    private static long Key(int rx, int rz) => ((long)rx << 32) | (uint)rz;

    private static int FloorDiv(int a, int b) => (a >= 0 ? a : a - b + 1) / b;

    /// <summary>The nearest column that was never sent, no longer covers the vertical window, or is due a refresh.</summary>
    private bool PickColumn(int crx, int cry, int crz, float now, out int bestRx, out int bestRz)
    {
        bestRx = bestRz = 0;
        float bestScore = float.MaxValue;
        for (int dz = -Radius; dz <= Radius; dz++)
        {
            for (int dx = -Radius; dx <= Radius; dx++)
            {
                int ring = Math.Max(Math.Abs(dx), Math.Abs(dz));
                float score;
                if (!exported.TryGetValue(Key(crx + dx, crz + dz), out Exported e))
                {
                    score = ring;
                }
                else if (e.ryMin > cry - 1 || e.ryMax < cry + 1)
                {
                    score = ring; // the player moved up or down past what was sent
                }
                else if (e.ryMin != windowRyMin && Math.Abs(e.ryMin - windowRyMin) >= 2)
                {
                    score = 50 + ring;
                }
                else
                {
                    float age = now - e.time;
                    float due = ring <= 1 ? NearRefreshSeconds : FarRefreshSeconds;
                    if (age < due)
                    {
                        continue;
                    }
                    score = 100 + ring - age * 0.01f;
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    bestRx = crx + dx;
                    bestRz = crz + dz;
                }
            }
        }
        return bestScore != float.MaxValue;
    }

    private void Evict(int crx, int crz)
    {
        scratchKeys.Clear();
        foreach (KeyValuePair<long, Exported> pair in exported)
        {
            int rx = (int)(pair.Key >> 32);
            int rz = (int)(uint)pair.Key;
            if (Math.Abs(rx - crx) > Radius + 2 || Math.Abs(rz - crz) > Radius + 2)
            {
                scratchKeys.Add(pair.Key);
            }
        }
        foreach (long key in scratchKeys)
        {
            Exported e = exported[key];
            int rx = (int)(key >> 32);
            int rz = (int)(uint)key;
            for (int ry = e.ryMin; ry <= e.ryMax; ry++)
            {
                if (!SendRegion(rx, ry, rz, null, null))
                {
                    return;
                }
            }
            exported.Remove(key);
        }
    }

    private bool ExportColumn(int rx, int rz)
    {
        int ryMin = windowRyMin, ryMax = windowRyMax;
        int regions = ryMax - ryMin + 1;
        if (tris.Length != regions)
        {
            tris = new List<Proto.ColTri>[regions];
            occupancy = new ulong[regions][];
            for (int r = 0; r < regions; r++)
            {
                tris[r] = new List<Proto.ColTri>(1024);
                occupancy[r] = new ulong[512 * 8];
            }
        }
        for (int r = 0; r < regions; r++)
        {
            tris[r].Clear();
            Array.Clear(occupancy[r], 0, occupancy[r].Length);
        }

        float yTop = (float)((ryMax + 1) * RegionSize - Coords.YOffset);
        float yBottom = (float)(ryMin * RegionSize - Coords.YOffset);
        bool backfaces = Physics.queriesHitBackfaces;
        try
        {
            for (int j = 0; j < Verts; j++)
            {
                for (int i = 0; i < Verts; i++)
                {
                    float ux = rx * RegionSize + i * Step;
                    float uz = -(rz * RegionSize + j * Step);
                    fixed (Span* column = &spans[(j * Verts + i) * MaxSpans])
                    {
                        spanCount[j * Verts + i] = (byte)SampleColumn(ux, uz, yTop, yBottom, column);
                    }
                }
            }
        }
        finally
        {
            Physics.queriesHitBackfaces = backfaces;
        }

        for (int j = 0; j < Cells; j++)
        {
            for (int i = 0; i < Cells; i++)
            {
                Triangulate(rx, rz, i, j, ryMin, ryMax);
                Fill(i, j, ryMin, ryMax);
            }
        }

        // Regions this column covered before but no longer does go out empty.
        if (exported.TryGetValue(Key(rx, rz), out Exported before))
        {
            for (int ry = before.ryMin; ry <= before.ryMax; ry++)
            {
                if ((ry < ryMin || ry > ryMax) && !SendRegion(rx, ry, rz, null, null))
                {
                    return false;
                }
            }
        }
        for (int r = 0; r < regions; r++)
        {
            if (!SendRegion(rx, ryMin + r, rz, tris[r], occupancy[r]))
            {
                return false;
            }
            statTris += tris[r].Count;
        }
        exported[Key(rx, rz)] = new Exported { ryMin = ryMin, ryMax = ryMax, time = Time.unscaledTime };
        statColumns++;
        return true;
    }

    // ---- sampling ----

    private readonly Dictionary<int, uint> surfaceFlags = new();

    /// <summary>What a stage surface is made of, as ColTri flags (design/gameplay.json surfaces).</summary>
    private uint SurfaceFlags(Collider collider, Vector3 point)
    {
        int id = collider.GetInstanceID();
        if (surfaceFlags.TryGetValue(id, out uint cached))
        {
            return cached;
        }
        RoR2.SurfaceDef? def = RoR2.SurfaceDefProvider.GetObjectSurfaceDef(collider, point);
        if (def == null || !Game.Tables.Surfaces.TryGetValue(def.name, out var row))
        {
            row = Game.Tables.Surfaces["*"];
        }
        uint flags = 0;
        if (row.diggable)
        {
            flags = (uint)Proto.TriFlags.Diggable | ((uint)row.material << 8) | (row.terrain ? (uint)Proto.TriFlags.Terrain : 0u);
        }
        surfaceFlags[id] = flags;
        return flags;
    }

    private static bool Dynamic(Collider collider)
    {
        Rigidbody body = collider.attachedRigidbody;
        return body != null && !body.isKinematic;
    }

    /// <summary>Casts down the column and returns its solid spans, top first.</summary>
    private int SampleColumn(float ux, float uz, float yTop, float yBottom, Span* output)
    {
        int count = 0;
        bool haveSpan = InsideSolid(new Vector3(ux, yTop, uz));
        float spanTop = Big;
        uint spanFlags = 0;
        float above = yTop; // the surface (or window top) above the gap being tested
        float y = yTop;
        for (int iteration = 0; iteration < 24 && count < MaxSpans - 1; iteration++)
        {
            Physics.queriesHitBackfaces = false;
            statRays++;
            if (!Physics.Raycast(new Vector3(ux, y, uz), Vector3.down, out RaycastHit hit, y - yBottom, mask, QueryTriggerInteraction.Ignore))
            {
                break;
            }
            float hitY = hit.point.y;
            y = hitY - 0.02f;
            if (Dynamic(hit.collider))
            {
                continue; // loose items and props are not world
            }
            if (!haveSpan)
            {
                haveSpan = true;
                spanTop = hitY;
                spanFlags = SurfaceFlags(hit.collider, hit.point);
            }
            else if (GapIsAir(ux, uz, hitY, above, out float ceiling))
            {
                output[count++] = new Span { top = spanTop, bottom = ceiling, flags = spanFlags };
                spanTop = hitY;
                spanFlags = SurfaceFlags(hit.collider, hit.point);
            }
            // else: this floor is buried inside the span above (terrain under a rock); the span goes on down
            above = hitY;
        }
        if (haveSpan)
        {
            output[count++] = new Span { top = spanTop, bottom = -Big, flags = spanFlags };
        }
        return count;
    }

    /// <summary>
    /// Between a floor at <paramref name="lowY"/> and the surface above it at <paramref name="highY"/>:
    /// open air under a ceiling, or the inside of something solid? Looking up, the nearest face is a
    /// front face (an underside) in air and a back face inside a mesh.
    /// </summary>
    private bool GapIsAir(float ux, float uz, float lowY, float highY, out float ceiling)
    {
        ceiling = highY;
        float distance = highY - lowY - 0.04f;
        if (distance < 0.1f)
        {
            return false;
        }
        var origin = new Vector3(ux, lowY + 0.02f, uz);
        statRays += 2;
        Physics.queriesHitBackfaces = false;
        bool front = Physics.Raycast(origin, Vector3.up, out RaycastHit frontHit, distance, mask, QueryTriggerInteraction.Ignore);
        Physics.queriesHitBackfaces = true;
        bool any = Physics.Raycast(origin, Vector3.up, out RaycastHit anyHit, distance + 0.03f, mask, QueryTriggerInteraction.Ignore);
        if (front && (!any || frontHit.distance <= anyHit.distance + 0.01f))
        {
            ceiling = frontHit.point.y;
            return true;
        }
        // A back face first: inside a mesh. Nothing at all: the origin is inside the same primitive
        // or convex collider whose top is the surface above.
        return false;
    }

    private bool InsideSolid(Vector3 point)
    {
        statRays += 2;
        Physics.queriesHitBackfaces = false;
        bool front = Physics.Raycast(point, Vector3.up, out RaycastHit frontHit, 400f, mask, QueryTriggerInteraction.Ignore);
        Physics.queriesHitBackfaces = true;
        bool any = Physics.Raycast(point, Vector3.up, out RaycastHit anyHit, 400f, mask, QueryTriggerInteraction.Ignore);
        if (any && (!front || anyHit.distance < frontHit.distance - 0.01f))
        {
            return true;
        }
        return Physics.CheckSphere(point, 0.05f, mask, QueryTriggerInteraction.Ignore);
    }

    // ---- triangles ----

    private void Triangulate(int rx, int rz, int i, int j, int ryMin, int ryMax)
    {
        // Corners in cyclic order around the cell.
        int* ci = stackalloc int[4] { i, i + 1, i + 1, i };
        int* cj = stackalloc int[4] { j, j, j + 1, j + 1 };
        int* column = stackalloc int[4];
        int* n = stackalloc int[4];
        for (int c = 0; c < 4; c++)
        {
            column[c] = cj[c] * Verts + ci[c];
            n[c] = spanCount[column[c]];
        }
        if (n[0] + n[1] + n[2] + n[3] == 0)
        {
            return;
        }
        bool* used = stackalloc bool[4 * MaxSpans];
        int* group = stackalloc int[4];
        float* ys = stackalloc float[4];
        cellPolygons.Clear();

        // Floors: join span tops of similar height, highest first; what's left over gets walls.
        for (int k = 0; k < 4 * MaxSpans; k++)
        {
            used[k] = false;
        }
        while (true)
        {
            int seedCorner = -1, seedSpan = -1;
            float seedTop = -Big;
            for (int c = 0; c < 4; c++)
            {
                for (int s = 0; s < n[c]; s++)
                {
                    float top = spans[column[c] * MaxSpans + s].top;
                    if (!used[c * MaxSpans + s] && top < Big && top > seedTop)
                    {
                        seedTop = top;
                        seedCorner = c;
                        seedSpan = s;
                    }
                }
            }
            if (seedCorner < 0)
            {
                break;
            }
            int size = 0;
            for (int c = 0; c < 4; c++)
            {
                group[c] = -1;
                if (c == seedCorner)
                {
                    group[c] = seedSpan;
                }
                else
                {
                    float best = JoinTolerance;
                    for (int s = 0; s < n[c]; s++)
                    {
                        float top = spans[column[c] * MaxSpans + s].top;
                        float apart = Math.Abs(top - seedTop);
                        if (!used[c * MaxSpans + s] && top < Big && apart <= best)
                        {
                            best = apart;
                            group[c] = s;
                        }
                    }
                }
                if (group[c] >= 0)
                {
                    used[c * MaxSpans + group[c]] = true;
                    ys[c] = spans[column[c] * MaxSpans + group[c]].top;
                    size++;
                }
                else
                {
                    ys[c] = float.NaN;
                }
            }
            if (size < 4)
            {
                AnchorWalls(column, n, group, ys, seedTop);
            }
            EmitPolygon(rx, rz, ci, cj, ys, ryMin, ryMax, spans[column[seedCorner] * MaxSpans + seedSpan].flags, up: true);
        }

        // Ceilings: the same joining on span undersides, without walls.
        for (int k = 0; k < 4 * MaxSpans; k++)
        {
            used[k] = false;
        }
        while (true)
        {
            int seedCorner = -1, seedSpan = -1;
            float seedBottom = -Big;
            for (int c = 0; c < 4; c++)
            {
                for (int s = 0; s < n[c]; s++)
                {
                    float bottom = spans[column[c] * MaxSpans + s].bottom;
                    if (!used[c * MaxSpans + s] && bottom > seedBottom)
                    {
                        seedBottom = bottom;
                        seedCorner = c;
                        seedSpan = s;
                    }
                }
            }
            if (seedCorner < 0)
            {
                break;
            }
            for (int c = 0; c < 4; c++)
            {
                int pick = c == seedCorner ? seedSpan : -1;
                if (c != seedCorner)
                {
                    float best = JoinTolerance;
                    for (int s = 0; s < n[c]; s++)
                    {
                        float bottom = spans[column[c] * MaxSpans + s].bottom;
                        float apart = Math.Abs(bottom - seedBottom);
                        if (!used[c * MaxSpans + s] && bottom > -Big && apart <= best)
                        {
                            best = apart;
                            pick = s;
                        }
                    }
                }
                if (pick >= 0)
                {
                    used[c * MaxSpans + pick] = true;
                    ys[c] = spans[column[c] * MaxSpans + pick].bottom;
                }
                else
                {
                    ys[c] = float.NaN;
                }
            }
            EmitPolygon(rx, rz, ci, cj, ys, ryMin, ryMax, 0, up: false);
        }
    }

    /// <summary>
    /// A floor that does not reach all four corners ends against something. For each missing corner:
    /// if that column is solid at the floor's height, the surface climbs to the top of what's there
    /// (a wall in front of the player); if it is lower ground and the floor's own spans are solid
    /// down to it, the surface drops to that ground (the face of a ledge). An overhang's lip gets neither.
    /// </summary>
    private void AnchorWalls(int* column, int* n, int* group, float* ys, float height)
    {
        float groupBottom = -Big;
        for (int c = 0; c < 4; c++)
        {
            if (group[c] >= 0)
            {
                groupBottom = Math.Max(groupBottom, spans[column[c] * MaxSpans + group[c]].bottom);
            }
        }
        for (int c = 0; c < 4; c++)
        {
            if (group[c] >= 0)
            {
                continue;
            }
            float below = -Big;
            bool anchored = false;
            for (int s = 0; s < n[c]; s++)
            {
                Span span = spans[column[c] * MaxSpans + s];
                if (span.bottom < height - 0.05f && span.top > height + 0.05f)
                {
                    ys[c] = Math.Min(span.top, height + 4f);
                    anchored = true;
                    break;
                }
                if (span.top <= height + 0.05f && span.top > below)
                {
                    below = span.top;
                }
            }
            if (!anchored && below > -Big && groupBottom <= below + 0.3f)
            {
                ys[c] = below;
            }
        }
    }

    /// <summary>Emits the triangle or quad through the corners that have a height (NaN = none), once per cell.</summary>
    private void EmitPolygon(int rx, int rz, int* ci, int* cj, float* ys, int ryMin, int ryMax, uint flags, bool up)
    {
        int count = 0;
        ulong signature = 0;
        for (int c = 0; c < 4; c++)
        {
            bool has = !float.IsNaN(ys[c]);
            count += has ? 1 : 0;
            signature = (signature << 16) | (has ? (ulong)((int)Math.Round(ys[c] * 16f) & 0xFFFF) : 0xFFFFul);
        }
        if (count < 3 || !cellPolygons.Add(signature))
        {
            return;
        }
        float* x = stackalloc float[4];
        float* y = stackalloc float[4];
        float* z = stackalloc float[4];
        int m = 0;
        for (int c = 0; c < 4; c++)
        {
            if (float.IsNaN(ys[c]))
            {
                continue;
            }
            x[m] = rx * RegionSize + ci[c] * Step;
            y[m] = (float)(ys[c] + Coords.YOffset);
            z[m] = rz * RegionSize + cj[c] * Step;
            m++;
        }
        if (m == 3)
        {
            AddTriangle(x, y, z, 0, 1, 2, ryMin, ryMax, flags, up);
        }
        else if (Math.Abs(y[0] - y[2]) <= Math.Abs(y[1] - y[3]))
        {
            // Split along the flatter diagonal.
            AddTriangle(x, y, z, 0, 1, 2, ryMin, ryMax, flags, up);
            AddTriangle(x, y, z, 0, 2, 3, ryMin, ryMax, flags, up);
        }
        else
        {
            AddTriangle(x, y, z, 0, 1, 3, ryMin, ryMax, flags, up);
            AddTriangle(x, y, z, 1, 2, 3, ryMin, ryMax, flags, up);
        }
    }

    private void AddTriangle(float* x, float* y, float* z, int a, int b, int c, int ryMin, int ryMax, uint flags, bool up)
    {
        // Diggable surfaces face out of the solid (the guest steps into the solid against the normal):
        // floors face up, ceilings down. Minecraft space, so the cross product is right-handed.
        float ny = (z[b] - z[a]) * (x[c] - x[a]) - (x[b] - x[a]) * (z[c] - z[a]);
        if ((up && ny < 0f) || (!up && ny > 0f))
        {
            (b, c) = (c, b);
        }
        Proto.ColTri tri = default;
        tri.flags = flags;
        tri.v[0] = x[a];
        tri.v[1] = y[a];
        tri.v[2] = z[a];
        tri.v[3] = x[b];
        tri.v[4] = y[b];
        tri.v[5] = z[b];
        tri.v[6] = x[c];
        tri.v[7] = y[c];
        tri.v[8] = z[c];
        float centre = (y[a] + y[b] + y[c]) / 3f;
        int ry = Mathf.Clamp(FloorDiv((int)Math.Floor(centre), RegionSize), ryMin, ryMax);
        tris[ry - ryMin].Add(tri);
    }

    // ---- occupancy ----

    /// <summary>Marks a one-block-thick shell under every span top of grid column (i, j) in the 1/8-block masks.</summary>
    private void Fill(int i, int j, int ryMin, int ryMax)
    {
        int column = j * Verts + i;
        int subMin = ryMin * 64, subMax = (ryMax + 1) * 64 - 1;
        for (int s = 0; s < spanCount[column]; s++)
        {
            Span span = spans[column * MaxSpans + s];
            double top = span.top >= Big ? subMax / 8.0 + 1 : span.top + Coords.YOffset;
            double bottom = Math.Max(span.bottom + Coords.YOffset, span.top >= Big ? -1e9 : top - 1.0);
            int y0 = Math.Max((int)Math.Floor(bottom * 8.0), subMin);
            int y1 = Math.Min((int)Math.Ceiling(top * 8.0) - 1, subMax);
            for (int ysub = y0; ysub <= y1; ysub++)
            {
                int ry = FloorDiv(ysub, 64);
                int local = ysub - ry * 64;
                ulong[] region = occupancy[ry - ryMin];
                int by = local >> 3, sy = local & 7;
                for (int dz = 0; dz < 4; dz++)
                {
                    int zsub = j * 4 + dz;
                    int bz = zsub >> 3, sz = zsub & 7;
                    for (int dx = 0; dx < 4; dx++)
                    {
                        int xsub = i * 4 + dx;
                        int bx = xsub >> 3, sx = xsub & 7;
                        region[(bx + 8 * (bz + 8 * by)) * 8 + sy] |= 1ul << (sz * 8 + sx);
                    }
                }
            }
        }
    }

    // ---- messages ----

    /// <summary>Sends a region's triangles, then its occupancy (null = empty; the region still becomes "known").</summary>
    private bool SendRegion(int rx, int ry, int rz, List<Proto.ColTri>? triangles, ulong[]? region)
    {
        var header = new Proto.ColRegion
        {
            minX = rx * RegionSize,
            minY = ry * RegionSize,
            minZ = rz * RegionSize,
            maxX = rx * RegionSize + RegionSize - 1,
            maxY = ry * RegionSize + RegionSize - 1,
            maxZ = rz * RegionSize + RegionSize - 1,
            epoch = Epoch,
        };
        int triCount = triangles?.Count ?? 0;
        int need = sizeof(Proto.ColRegion) + Math.Max(triCount * sizeof(Proto.ColTri), 512 * sizeof(Proto.ColBlock));
        if (payload.Length < need)
        {
            payload = new byte[need];
        }
        fixed (byte* p = payload)
        {
            header.count = (uint)triCount;
            *(Proto.ColRegion*)p = header;
            var triOut = (Proto.ColTri*)(p + sizeof(Proto.ColRegion));
            for (int t = 0; t < triCount; t++)
            {
                triOut[t] = triangles![t];
            }
            if (!link.WriteCollision((uint)Proto.ColMsg.Tris, p, (uint)(sizeof(Proto.ColRegion) + triCount * sizeof(Proto.ColTri))))
            {
                return false;
            }

            uint blocks = 0;
            var blockOut = (Proto.ColBlock*)(p + sizeof(Proto.ColRegion));
            if (region != null)
            {
                for (int b = 0; b < 512; b++)
                {
                    ulong any = 0;
                    for (int layer = 0; layer < 8; layer++)
                    {
                        any |= region[b * 8 + layer];
                    }
                    if (any == 0)
                    {
                        continue;
                    }
                    Proto.ColBlock* block = &blockOut[blocks++];
                    block->x = rx * RegionSize + (b & 7);
                    block->z = rz * RegionSize + ((b >> 3) & 7);
                    block->y = ry * RegionSize + (b >> 6);
                    block->pad = 0;
                    for (int layer = 0; layer < 8; layer++)
                    {
                        block->bits[layer] = region[b * 8 + layer];
                    }
                }
            }
            header.count = blocks;
            *(Proto.ColRegion*)p = header;
            return link.WriteCollision((uint)Proto.ColMsg.Region, p, (uint)(sizeof(Proto.ColRegion) + blocks * sizeof(Proto.ColBlock)));
        }
    }
}
