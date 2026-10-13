using System.Collections.Generic;
using RiskOfCraft.Link;
using RiskOfCraft.World;
using RoR2;
using UnityEngine;

namespace RiskOfCraft.Player;

/// <summary>
/// Puts Steve back on top of the ground when he ends up inside it. RoR2's terrain reaches Minecraft
/// as sampled triangles, and a hard knockback against a steep spot could shove the Minecraft player
/// through them. Every quarter second: a ray up from the feet that meets the underside of an
/// upward-facing surface (a back face) means the feet are below RoR2's ground, not in a cave
/// (whose ceiling faces down). Cells the player dug out are not counted, since digging under the
/// surface is allowed: Minecraft reports them in the render ring (Dug messages).
/// </summary>
internal sealed unsafe class UnderMapRescue
{
    private const float CheckSeconds = 0.25f;
    private const float StuckSeconds = 0.5f;

    private readonly Dictionary<(int, int, int), byte[]> dug = new();
    private float nextCheck;
    private float stuckFor;
    private int rescues;
    private float lastRescueTime = -100f;
    private Vector3 lastRescueAt;

    /// <summary>The closest node of the stage's ground graph at least <paramref name="minDistance"/> away.</summary>
    private static Vector3? NearestGroundNode(Vector3 from, float minDistance)
    {
        RoR2.Navigation.NodeGraph? graph = SceneInfo.instance != null ? SceneInfo.instance.groundNodes : null;
        if (graph == null)
        {
            return null;
        }
        Vector3? best = null;
        float bestDistance = float.MaxValue;
        foreach (RoR2.Navigation.NodeGraph.NodeIndex index in graph.GetActiveNodesForHullMask(HullMask.Human))
        {
            if (!graph.GetNodePosition(index, out Vector3 p))
            {
                continue;
            }
            float d = (p - from).sqrMagnitude;
            if (d >= minDistance * minDistance && d < bestDistance)
            {
                bestDistance = d;
                best = p;
            }
        }
        return best;
    }

    /// <summary>Render-ring Dug message: which blocks of a section were dug out of RoR2's ground.</summary>
    public void OnDug(byte* payload, uint bytes)
    {
        if (bytes < (uint)sizeof(Proto.RenDug))
        {
            return;
        }
        var header = (Proto.RenDug*)payload;
        var key = (header->sx, header->sy, header->sz);
        if (header->count == 0 || bytes < (uint)sizeof(Proto.RenDug) + 512)
        {
            dug.Remove(key);
            return;
        }
        var bits = new byte[512];
        for (int i = 0; i < 512; i++)
        {
            bits[i] = payload[sizeof(Proto.RenDug) + i];
        }
        dug[key] = bits;
    }

    public void Clear() => dug.Clear();

    private bool IsDug(int x, int y, int z)
    {
        if (!dug.TryGetValue((x >> 4, y >> 4, z >> 4), out byte[]? bits))
        {
            return false;
        }
        int bit = (x & 15) + 16 * (z & 15) + 256 * (y & 15);
        return (bits[bit >> 3] & (1 << (bit & 7))) != 0;
    }

    public void Update(Ownership ownership, Follower follower, CharacterBody? body, in Proto.GuestState guest)
    {
        if (ownership.State != Owner.MinecraftOwns || body == null || (guest.flags & (uint)Proto.GuestFlags.Flying) != 0)
        {
            stuckFor = 0f;
            return;
        }
        if (Time.unscaledTime < nextCheck)
        {
            return;
        }
        nextCheck = Time.unscaledTime + CheckSeconds;
        Vector3 feet = follower.Feet;
        if (!Buried(feet, out float top))
        {
            stuckFor = 0f;
            return;
        }
        stuckFor += CheckSeconds;
        if (stuckFor < StuckSeconds)
        {
            return;
        }
        stuckFor = 0f;
        var target = new Vector3(feet.x, top + 0.1f, feet.z);
        // Buried again right where the last rescue put him: that patch of ground doesn't hold in
        // Minecraft, so go to the nearest walkable ground node a few metres away instead.
        if (Time.unscaledTime - lastRescueTime < 6f && (new Vector2(feet.x, feet.z) - new Vector2(lastRescueAt.x, lastRescueAt.z)).sqrMagnitude < 9f)
        {
            Vector3? node = NearestGroundNode(feet, 4f);
            if (node != null)
            {
                target = node.Value + Vector3.up * 0.1f;
            }
        }
        lastRescueTime = Time.unscaledTime;
        lastRescueAt = target;
        TeleportHelper.TeleportBody(body, target);
        ownership.RequestTeleport();
        if (rescues++ < 20)
        {
            Plugin.Log.LogInfo($"rescue: Steve was {top - feet.y:F1} m inside RoR2's ground at {feet:F1}; put back on top at {target:F1}");
        }
    }

    /// <summary>True when the feet are inside RoR2's ground (and not in a dug-out cell); <paramref name="top"/> is the surface above.</summary>
    private bool Buried(Vector3 feet, out float top)
    {
        top = 0f;
        Coords.ToMc(feet + Vector3.up * 0.2f, out double x, out double y, out double z);
        int bx = (int)System.Math.Floor(x), by = (int)System.Math.Floor(y), bz = (int)System.Math.Floor(z);
        if (IsDug(bx, by, bz) || IsDug(bx, by + 1, bz))
        {
            return false;
        }
        bool backfaces = Physics.queriesHitBackfaces;
        try
        {
            // Two casts up from the feet: one that sees back faces and one that doesn't. Under a cave
            // or a bridge the first thing above faces down at us (both casts hit it): open air. Inside
            // the ground the first thing above is the ground's own top seen from below, a back face
            // only the first cast sees. (Unity flips a back-face hit's normal, so normals can't tell.)
            Vector3 from = feet + Vector3.up * 0.3f;
            Physics.queriesHitBackfaces = true;
            if (!Physics.Raycast(from, Vector3.up, out RaycastHit any, 40f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)
                || (any.collider.attachedRigidbody != null && !any.collider.attachedRigidbody.isKinematic))
            {
                return false;
            }
            Physics.queriesHitBackfaces = false;
            if (Physics.Raycast(from, Vector3.up, out RaycastHit front, any.distance + 0.05f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)
                && front.distance <= any.distance + 0.02f)
            {
                return false;
            }
            // The ground's top: the surface just above that back face, seen from above.
            top = any.point.y;
            if (Physics.Raycast(any.point + Vector3.up * 0.6f, Vector3.down, out RaycastHit ground, 1.2f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
            {
                top = ground.point.y;
            }
            return top > feet.y + 0.4f;
        }
        finally
        {
            Physics.queriesHitBackfaces = backfaces;
        }
    }
}
