using System.Globalization;
using RiskOfCraft.World;
using RoR2;
using RoR2.Navigation;
using UnityEngine;

namespace RiskOfCraft.Game;

/// <summary>
/// The Nether stage's Minecraft mobs (design/gameplay.json nether_mobs, nether_spawning): every few
/// seconds of game time (so not while paused), one of Minecraft's Nether mobs, picked by weight,
/// is spawned in this player's Minecraft on walkable ground a fair way from Steve, unless enough
/// of them are already about. They fight with Minecraft's rules, tougher as RoR2's difficulty
/// rises, and drop Minecraft loot. The next stage clears them (clear_mobs).
/// </summary>
internal sealed class NetherMobs
{
    private float next;
    private readonly System.Random random = new();

    public void Update(bool linked, CharacterBody? body)
    {
        Tables2.CustomStage? stage = CustomStages.Current;
        if (!linked || body == null || stage == null || stage.Decor != "nether" || Run.instance == null || Time.time < next)
        {
            return;
        }
        next = Time.time + Tables2.NetherSpawning["interval"];
        Tables2.NetherMob mob = Pick();
        if (!Spot(body.footPosition, out Vector3 spot))
        {
            return;
        }
        if (mob.InAir)
        {
            spot += Vector3.up * 6f;
        }
        Coords.ToMc(spot, out double x, out double y, out double z);
        float over = Mathf.Max(0f, Run.instance.difficultyCoefficient - 1f);
        float health = 1f + Tables2.NetherSpawning["health_per_difficulty"] * over;
        float damage = 1f + Tables2.NetherSpawning["damage_per_difficulty"] * over;
        Commands.Push(Commands.Format("stage_mob", ("entity", mob.McEntity), ("x", F(x)), ("y", F(y + 0.1)), ("z", F(z)),
            ("cap", (int)Tables2.NetherSpawning["cap"]), ("health", health), ("damage", damage)));
    }

    private Tables2.NetherMob Pick()
    {
        int total = 0;
        foreach (Tables2.NetherMob m in Tables2.NetherMobs)
        {
            total += m.Weight;
        }
        int roll = random.Next(total);
        foreach (Tables2.NetherMob m in Tables2.NetherMobs)
        {
            roll -= m.Weight;
            if (roll < 0)
            {
                return m;
            }
        }
        return Tables2.NetherMobs[0];
    }

    /// <summary>A walkable ground node between the near and far distances from the player.</summary>
    private bool Spot(Vector3 from, out Vector3 spot)
    {
        spot = default;
        NodeGraph? graph = SceneInfo.instance != null ? SceneInfo.instance.groundNodes : null;
        if (graph == null)
        {
            return false;
        }
        var nodes = graph.GetActiveNodesForHullMask(HullMask.Human);
        float min = Tables2.NetherSpawning["min_distance"], max = Tables2.NetherSpawning["max_distance"];
        for (int tries = 0; tries < 24 && nodes.Count > 0; tries++)
        {
            if (!graph.GetNodePosition(nodes[random.Next(nodes.Count)], out Vector3 p))
            {
                continue;
            }
            float d = (p - from).magnitude;
            if (d >= min && d <= max)
            {
                spot = p;
                return true;
            }
        }
        return false;
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
