using System.Collections.Generic;
using RiskOfCraft.Link;
using RoR2;
using UnityEngine;
using UnityEngine.Rendering;

namespace RiskOfCraft.World;

/// <summary>
/// Digging into Risk of Rain 2's world: every block the player digs out in Minecraft (the guest's
/// Dug messages) is cut out of RoR2's own meshes too, both what is drawn and what is solid, so the
/// hole is really there: Steve (and monsters) can go down into it, and Minecraft's dirt and stone
/// walls show inside it. Each RoR2 triangle crossing a dug 1 m cell is clipped against the cell's
/// six faces and only the part inside is removed, so holes follow the blocks exactly. A cut object
/// gets its own copy of its mesh; the stage's other copies of that mesh are untouched.
/// RoR2's statically batched scenery has no readable render mesh: when a dig reaches one whose
/// materials texture by world position (Hopoo's triplanar terrain and rock shaders), its readable
/// collision mesh is drawn in its place with the same material, cut like the rest.
/// </summary>
internal sealed unsafe class TerrainCutter
{
    private const float BatchSeconds = 0.15f;
    private const float Eps = 1e-4f;

    private readonly Dictionary<(int, int, int), byte[]> sections = new();
    private readonly HashSet<(int x, int y, int z)> pending = new();
    private readonly Dictionary<Object, Mesh> cutMeshes = new(); // MeshFilter / MeshCollider -> its own cut copy
    private List<MeshRenderer>? candidates;
    private List<MeshRenderer>? batched;
    private readonly Dictionary<MeshRenderer, MeshFilter> standIns = new();
    private float nextBatch;
    private int cellsCut, trianglesCut, logged;

    /// <summary>Cells cut out of this stage so far.</summary>
    public int CellsCut => cellsCut;

    /// <summary>Render-ring Dug message: the cells of one section dug out of RoR2's world.</summary>
    public void OnDug(byte* payload, uint bytes)
    {
        if (bytes < (uint)sizeof(Proto.RenDug))
        {
            return;
        }
        var header = (Proto.RenDug*)payload;
        var key = (header->sx, header->sy, header->sz);
        sections.TryGetValue(key, out byte[]? old);
        if (header->count == 0 || bytes < (uint)sizeof(Proto.RenDug) + 512)
        {
            sections.Remove(key);
            return; // filled back in: the RoR2 mesh stays cut, Minecraft's blocks fill the hole
        }
        var bits = new byte[512];
        byte* src = payload + sizeof(Proto.RenDug);
        for (int i = 0; i < 512; i++)
        {
            bits[i] = src[i];
            int fresh = bits[i] & ~(old != null ? old[i] : 0);
            for (int b = 0; fresh != 0 && b < 8; b++)
            {
                if ((fresh & (1 << b)) == 0)
                {
                    continue;
                }
                int bit = i * 8 + b; // x + 16z + 256y within the section
                pending.Add((header->sx * 16 + (bit & 15), header->sy * 16 + (bit >> 8), header->sz * 16 + ((bit >> 4) & 15)));
            }
        }
        sections[key] = bits;
    }

    /// <summary>A new stage: its meshes are new, nothing is cut yet.</summary>
    public void Clear()
    {
        sections.Clear();
        pending.Clear();
        cutMeshes.Clear();
        cellsCut = 0;
        trianglesCut = 0;
        candidates = null;
        batched = null;
        standIns.Clear();
    }

    public void Update()
    {
        if (pending.Count == 0 || Time.unscaledTime < nextBatch)
        {
            return;
        }
        nextBatch = Time.unscaledTime + BatchSeconds;
        var boxes = new List<Bounds>(pending.Count);
        foreach ((int x, int y, int z) in pending)
        {
            // Minecraft block (x, y, z) in Unity: x..x+1, y..y+1 (less the Y offset), -(z+1)..-z.
            Vector3 lo = Coords.ToUnity(x, y, z + 1), hi = Coords.ToUnity(x + 1, y + 1, z);
            var box = new Bounds();
            box.SetMinMax(Vector3.Min(lo, hi), Vector3.Max(lo, hi));
            boxes.Add(box);
        }
        pending.Clear();
        Cut(boxes);
    }

    private void Cut(List<Bounds> boxes)
    {
        var work = new Dictionary<Object, (Mesh source, Transform transform, List<Bounds> boxes)>();
        int world = LayerIndex.world.mask;
        Candidates(); // also fills the statically batched list
        foreach (Bounds box in boxes)
        {
            foreach (Collider c in Physics.OverlapBox(box.center, box.extents + Vector3.one * 0.02f, Quaternion.identity, world, QueryTriggerInteraction.Ignore))
            {
                if (c is MeshCollider mc && !mc.convex && mc.sharedMesh != null && mc.sharedMesh.isReadable)
                {
                    Add(work, mc, mc.sharedMesh, mc.transform, box);
                }
            }
            foreach (MeshRenderer r in batched ?? new List<MeshRenderer>())
            {
                if (r != null && r.bounds.Intersects(box) && StandIn(r) is MeshFilter stand)
                {
                    Add(work, stand, stand.sharedMesh, stand.transform, box);
                }
            }
            foreach (MeshRenderer r in Candidates())
            {
                if (r != null && r.enabled && r.bounds.Intersects(box))
                {
                    MeshFilter f = r.GetComponent<MeshFilter>();
                    if (f != null && f.sharedMesh != null && f.sharedMesh.isReadable)
                    {
                        Add(work, f, f.sharedMesh, f.transform, box);
                    }
                }
            }
        }
        foreach (KeyValuePair<Object, (Mesh source, Transform transform, List<Bounds> boxes)> pair in work)
        {
            if (!cutMeshes.TryGetValue(pair.Key, out Mesh mesh))
            {
                mesh = Object.Instantiate(pair.Value.source);
                mesh.name = pair.Value.source.name + " (dug)";
                cutMeshes[pair.Key] = mesh;
            }
            int removed = CutMesh(mesh, pair.Value.transform.localToWorldMatrix, pair.Value.boxes);
            if (removed == 0 && mesh != (pair.Key is MeshCollider mc0 ? mc0.sharedMesh : ((MeshFilter)pair.Key).sharedMesh))
            {
                continue; // a fresh copy that needed no cut yet: keep the original in place
            }
            trianglesCut += removed;
            if (pair.Key is MeshCollider collider)
            {
                collider.sharedMesh = null; // re-cooked with the hole
                collider.sharedMesh = mesh;
            }
            else if (pair.Key is MeshFilter filter)
            {
                filter.sharedMesh = mesh;
            }
        }
        cellsCut += boxes.Count;
        if (logged++ < 40 || logged % 50 == 0)
        {
            Plugin.Log.LogInfo($"dig: {boxes.Count} cells cut out of RoR2's world ({work.Count} meshes; {cellsCut} cells, {trianglesCut} triangles cut this stage)");
        }
    }

    private static void Add(Dictionary<Object, (Mesh, Transform, List<Bounds>)> work, Object key, Mesh mesh, Transform transform, Bounds box)
    {
        if (!work.TryGetValue(key, out (Mesh, Transform, List<Bounds> boxes) entry))
        {
            entry = (mesh, transform, new List<Bounds>());
            work[key] = entry;
        }
        entry.boxes.Add(box);
    }

    /// <summary>The stage's world renderers that can be cut (readable meshes, not RoR2's static batches).</summary>
    private List<MeshRenderer> Candidates()
    {
        if (candidates != null)
        {
            return candidates;
        }
        candidates = new List<MeshRenderer>();
        batched = new List<MeshRenderer>();
        int world = LayerIndex.world.intVal;
        foreach (MeshRenderer r in Object.FindObjectsOfType<MeshRenderer>())
        {
            if (r.name.StartsWith("section ") || r.GetComponentInParent<CharacterBody>() != null)
            {
                continue;
            }
            Collider? c = r.GetComponent<Collider>();
            if (r.gameObject.layer != world && (c == null || c.gameObject.layer != world))
            {
                continue;
            }
            (r.isPartOfStaticBatch ? batched : candidates).Add(r);
        }
        return candidates;
    }

    /// <summary>
    /// A statically batched renderer's stand-in: its collision mesh, drawn with its own material, so
    /// it can be cut. Only for materials that texture by world position (no UVs needed).
    /// </summary>
    private MeshFilter? StandIn(MeshRenderer original)
    {
        if (standIns.TryGetValue(original, out MeshFilter existing))
        {
            return existing;
        }
        MeshCollider? collider = original.GetComponent<MeshCollider>();
        Material[] materials = original.sharedMaterials;
        if (collider == null || collider.sharedMesh == null || !collider.sharedMesh.isReadable || materials.Length == 0)
        {
            return null;
        }
        foreach (Material m in materials)
        {
            string shader = m != null && m.shader != null ? m.shader.name : "";
            if (!shader.Contains("Triplanar") && !shader.Contains("Snow Topped"))
            {
                return null; // UV-mapped: a collision mesh has no UVs to draw it with
            }
        }
        var go = new GameObject(original.name + " (dug stand-in)") { layer = original.gameObject.layer };
        go.transform.SetParent(original.transform, worldPositionStays: false);
        Mesh mesh = Object.Instantiate(collider.sharedMesh);
        mesh.name = collider.sharedMesh.name + " (drawn)";
        if (!mesh.HasVertexAttribute(VertexAttribute.Normal))
        {
            mesh.RecalculateNormals();
        }
        var filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = materials[0];
        renderer.shadowCastingMode = original.shadowCastingMode;
        original.forceRenderingOff = true;
        standIns[original] = filter;
        Plugin.Log.LogInfo($"dig: {original.name} is statically batched: drawn from its collision mesh ({materials[0].shader.name}) so it can be dug");
        return filter;
    }

    // ---- the cut itself ----------------------------------------------------------------------------

    private struct PolyVertex
    {
        public Vector3 world;
        public Vector3 bary; // weights of the source triangle's three corners
    }

    /// <summary>Removes the parts of the mesh's triangles inside the boxes (world space). Returns how many triangles were cut.</summary>
    private static int CutMesh(Mesh mesh, Matrix4x4 localToWorld, List<Bounds> boxes)
    {
        var positions = new List<Vector3>();
        mesh.GetVertices(positions);
        bool hasNormals = mesh.HasVertexAttribute(VertexAttribute.Normal);
        bool hasTangents = mesh.HasVertexAttribute(VertexAttribute.Tangent);
        bool hasColors = mesh.HasVertexAttribute(VertexAttribute.Color);
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var colors = new List<Color>();
        if (hasNormals) mesh.GetNormals(normals);
        if (hasTangents) mesh.GetTangents(tangents);
        if (hasColors) mesh.GetColors(colors);
        var uvs = new List<Vector4>?[8];
        for (int ch = 0; ch < 8; ch++)
        {
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + ch))
            {
                uvs[ch] = new List<Vector4>();
                mesh.GetUVs(ch, uvs[ch]);
            }
        }
        var world = new Vector3[positions.Count];
        for (int i = 0; i < world.Length; i++)
        {
            world[i] = localToWorld.MultiplyPoint3x4(positions[i]);
        }
        Bounds all = boxes[0];
        foreach (Bounds b in boxes)
        {
            all.Encapsulate(b);
        }

        int cut = 0;
        int subCount = mesh.subMeshCount;
        var newIndices = new List<int>[subCount];
        for (int sub = 0; sub < subCount; sub++)
        {
            var indices = new List<int>();
            newIndices[sub] = indices;
            if (mesh.GetTopology(sub) != MeshTopology.Triangles)
            {
                mesh.GetIndices(indices, sub);
                continue;
            }
            var source = new List<int>();
            mesh.GetIndices(source, sub);
            for (int t = 0; t + 2 < source.Count; t += 3)
            {
                int a = source[t], b = source[t + 1], c = source[t + 2];
                Vector3 wa = world[a], wb = world[b], wc = world[c];
                Vector3 min = Vector3.Min(wa, Vector3.Min(wb, wc)), max = Vector3.Max(wa, Vector3.Max(wb, wc));
                if (!Overlaps(min, max, all.min, all.max))
                {
                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);
                    continue;
                }
                var pieces = new List<List<PolyVertex>>
                {
                    new() { new PolyVertex { world = wa, bary = new Vector3(1, 0, 0) }, new PolyVertex { world = wb, bary = new Vector3(0, 1, 0) }, new PolyVertex { world = wc, bary = new Vector3(0, 0, 1) } },
                };
                bool touched = false;
                foreach (Bounds box in boxes)
                {
                    if (!Overlaps(min, max, box.min, box.max))
                    {
                        continue;
                    }
                    var next = new List<List<PolyVertex>>();
                    foreach (List<PolyVertex> piece in pieces)
                    {
                        touched |= SubtractBox(piece, box, next);
                    }
                    pieces = next;
                }
                if (!touched)
                {
                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);
                    continue;
                }
                cut++;
                foreach (List<PolyVertex> piece in pieces)
                {
                    int first = positions.Count;
                    foreach (PolyVertex v in piece)
                    {
                        Vector3 w = v.bary;
                        positions.Add(positions[a] * w.x + positions[b] * w.y + positions[c] * w.z);
                        if (hasNormals) normals.Add((normals[a] * w.x + normals[b] * w.y + normals[c] * w.z).normalized);
                        if (hasTangents) tangents.Add(tangents[a] * w.x + tangents[b] * w.y + tangents[c] * w.z);
                        if (hasColors) colors.Add(colors[a] * w.x + colors[b] * w.y + colors[c] * w.z);
                        for (int ch = 0; ch < 8; ch++)
                        {
                            List<Vector4>? uv = uvs[ch];
                            uv?.Add(uv[a] * w.x + uv[b] * w.y + uv[c] * w.z);
                        }
                    }
                    for (int k = 1; k + 1 < piece.Count; k++)
                    {
                        indices.Add(first);
                        indices.Add(first + k);
                        indices.Add(first + k + 1);
                    }
                }
            }
        }
        if (cut == 0)
        {
            return 0;
        }
        if (positions.Count > 65535)
        {
            mesh.indexFormat = IndexFormat.UInt32;
        }
        mesh.SetVertices(positions);
        if (hasNormals) mesh.SetNormals(normals);
        if (hasTangents) mesh.SetTangents(tangents);
        if (hasColors) mesh.SetColors(colors);
        for (int ch = 0; ch < 8; ch++)
        {
            if (uvs[ch] != null)
            {
                mesh.SetUVs(ch, uvs[ch]);
            }
        }
        for (int sub = 0; sub < subCount; sub++)
        {
            mesh.SetIndices(newIndices[sub], mesh.GetTopology(sub), sub, calculateBounds: false);
        }
        mesh.RecalculateBounds();
        return cut;
    }

    private static bool Overlaps(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax) =>
        aMin.x <= bMax.x + Eps && aMax.x >= bMin.x - Eps && aMin.y <= bMax.y + Eps && aMax.y >= bMin.y - Eps && aMin.z <= bMax.z + Eps && aMax.z >= bMin.z - Eps;

    /// <summary>
    /// Adds the parts of a convex polygon outside the box to <paramref name="output"/>; the part
    /// inside is dropped. Peels the polygon plane by plane: what lies beyond a face is kept, the rest
    /// goes on to the next face. True if anything was inside.
    /// </summary>
    private static bool SubtractBox(List<PolyVertex> polygon, Bounds box, List<List<PolyVertex>> output)
    {
        List<PolyVertex> inside = polygon;
        Vector3 min = box.min, max = box.max;
        for (int plane = 0; plane < 6; plane++)
        {
            int axis = plane >> 1;
            bool high = (plane & 1) == 1;
            float limit = high ? max[axis] : min[axis];
            Split(inside, axis, limit, high, out List<PolyVertex> beyond, out List<PolyVertex> within);
            if (beyond.Count >= 3)
            {
                output.Add(beyond);
            }
            inside = within;
            if (inside.Count < 3)
            {
                return false; // nothing of it was inside the box (the pieces in output make it up whole)
            }
        }
        return true; // a part was inside the box (dropped)
    }

    /// <summary>Splits a convex polygon by an axis plane into the part beyond it (outside the box) and the part within.</summary>
    private static void Split(List<PolyVertex> polygon, int axis, float limit, bool high, out List<PolyVertex> beyond, out List<PolyVertex> within)
    {
        beyond = new List<PolyVertex>(polygon.Count + 2);
        within = new List<PolyVertex>(polygon.Count + 2);
        for (int i = 0; i < polygon.Count; i++)
        {
            PolyVertex p = polygon[i], q = polygon[(i + 1) % polygon.Count];
            float dp = high ? p.world[axis] - limit : limit - p.world[axis]; // > 0: beyond the face
            float dq = high ? q.world[axis] - limit : limit - q.world[axis];
            if (dp > Eps)
            {
                beyond.Add(p);
            }
            else if (dp < -Eps)
            {
                within.Add(p);
            }
            else
            {
                beyond.Add(p);
                within.Add(p);
            }
            if ((dp > Eps && dq < -Eps) || (dp < -Eps && dq > Eps))
            {
                float t = dp / (dp - dq);
                var cross = new PolyVertex { world = Vector3.Lerp(p.world, q.world, t), bary = Vector3.Lerp(p.bary, q.bary, t) };
                beyond.Add(cross);
                within.Add(cross);
            }
        }
    }
}
