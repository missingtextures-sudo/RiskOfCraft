using System.Collections.Generic;
using System.Globalization;
using RiskOfCraft.World;
using RoR2;
using RoR2.Navigation;
using UnityEngine;

namespace RiskOfCraft.Game;

/// <summary>
/// This player's side of every new stage: real Minecraft chests on the stage's walkable ground,
/// more and better the further and harder the run (design/gameplay.json chest_tiers, loot tables
/// generated into the guest), and on stage 5 the stronghold End portal frame near the teleporter.
/// </summary>
internal sealed class ChestSpawner
{
    private Stage? done;

    /// <summary>Dev probe: where this stage's chests and the End portal frame went (Unity space).</summary>
    public static readonly List<Vector3> LastChests = new();
    public static Vector3? EndFrame;

    public void Update(bool linked, CharacterBody? body)
    {
        Stage? stage = Stage.instance;
        if (!linked || body == null || stage == null || stage == done || Run.instance == null || SceneInfo.instance == null)
        {
            return;
        }
        if (!CustomStages.Known)
        {
            return; // wait for the server's word on whether this is the Nether or The End
        }
        if (CustomStages.Mode.Length == 0 && SceneCatalog.mostRecentSceneDef != null && SceneCatalog.mostRecentSceneDef.sceneType != SceneType.Stage)
        {
            done = stage; // the Bazaar, Void Fields and the like: no chests
            return;
        }
        done = stage;
        int stageNumber = Run.instance.stageClearCount + 1;
        int difficultyBonus = Run.instance.selectedDifficulty >= DifficultyIndex.Hard ? 1 : 0;
        int loops = Run.instance.loopClearCount;
        Tables2.CustomStage? custom = CustomStages.Current;
        Tables2.ChestTier tier;
        int count;
        if (custom != null && custom.Decor == "nether")
        {
            tier = System.Array.Find(Tables2.ChestTiers, t => t.Id == "tier_nether");
            count = custom.McChests;
        }
        else if (custom != null)
        {
            return; // The End: no chests, only the dragon
        }
        else
        {
            int t = Mathf.Clamp(stageNumber + difficultyBonus + loops, 1, 5);
            tier = System.Array.Find(Tables2.ChestTiers, r => r.Tier == t);
            count = tier.Chests;
        }
        var random = new System.Random((int)Run.instance.seed ^ (stageNumber * 104729));
        int placed = 0;
        LastChests.Clear();
        EndFrame = null;
        foreach (Vector3 spot in GroundSpots(count, random, body.footPosition, 12f))
        {
            Coords.ToMc(spot, out double x, out double y, out double z);
            string facing = new[] { "north", "south", "east", "west" }[random.Next(4)];
            Commands.Push(Commands.Format("place_chest", ("x", F(x)), ("y", F(y + 0.05)), ("z", F(z)), ("table", "riskofcraft:chests/" + tier.Id), ("facing", facing)));
            placed++;
            LastChests.Add(spot);
        }
        Plugin.Log.LogInfo($"chests: stage {stageNumber}: {placed} Minecraft chests of {tier.Id} (difficulty bonus {difficultyBonus}, loops {loops})");

        if (stageNumber == (int)Tables2.EndPortalFrame["stage"] && custom == null && TeleporterInteraction.instance != null)
        {
            PlaceEndPortal(random);
        }
    }

    private static void PlaceEndPortal(System.Random random)
    {
        Vector3 tp = TeleporterInteraction.instance.transform.position;
        float distance = Tables2.EndPortalFrame["distance"];
        for (int tries = 0; tries < 24; tries++)
        {
            float a = (float)(random.NextDouble() * Mathf.PI * 2.0);
            Vector3 probe = tp + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * distance + Vector3.up * 30f;
            if (!Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 80f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore) || hit.normal.y < 0.85f)
            {
                continue; // a slope or a drop: try another side of the teleporter
            }
            Coords.ToMc(hit.point, out double x, out double y, out double z);
            EndFrame = hit.point;
            Commands.Push(Commands.Format("end_portal", ("x", F(x)), ("y", F(y + 0.05)), ("z", F(z)), ("eye_chance", F(Tables2.EndPortalFrame["eye_chance"]))));
            Plugin.Log.LogInfo($"chests: stage 5: End portal frame {distance:F0} m from the teleporter at {hit.point:F1}");
            return;
        }
        Plugin.Log.LogWarning("chests: no flat ground near the teleporter for the End portal frame");
    }

    /// <summary>Up to <paramref name="count"/> random walkable ground spots, at least <paramref name="minFromStart"/> from the start, spread out.</summary>
    public static List<Vector3> GroundSpots(int count, System.Random random, Vector3 start, float minFromStart)
    {
        var spots = new List<Vector3>();
        NodeGraph? graph = SceneInfo.instance != null ? SceneInfo.instance.groundNodes : null;
        if (graph == null || count <= 0)
        {
            return spots;
        }
        List<NodeGraph.NodeIndex> nodes = graph.GetActiveNodesForHullMask(HullMask.Human);
        for (int tries = 0; tries < count * 12 && spots.Count < count && nodes.Count > 0; tries++)
        {
            if (!graph.GetNodePosition(nodes[random.Next(nodes.Count)], out Vector3 p))
            {
                continue;
            }
            if ((p - start).sqrMagnitude < minFromStart * minFromStart || spots.Exists(s => (s - p).sqrMagnitude < 64f))
            {
                continue;
            }
            spots.Add(p);
        }
        return spots;
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
