using RoR2;
using UnityEngine;

namespace RiskOfCraft.Game;

/// <summary>
/// This player's side of runs and stages. A new run resets the Minecraft player (empty inventory,
/// full health and food, no XP) and gives the starter kit (design/gameplay.json starter_kit). A new
/// stage clears the blocks placed on the last one, the items and arrows lying around, and the
/// collision Minecraft was sent.
/// </summary>
internal sealed class RunStart
{
    private Run? kitRun;
    private Stage? lastStage;

    /// <summary>Raised on a new stage (the plugin resets collision and blocks).</summary>
    public event System.Action<string>? StageChanged;

    public void Update(bool linked, CharacterBody? body)
    {
        Run? run = Run.instance;
        if (run == null)
        {
            kitRun = null;
            lastStage = null;
            return;
        }
        Stage? stage = Stage.instance;
        if (stage != null && stage != lastStage)
        {
            lastStage = stage;
            string scene = SceneCatalog.mostRecentSceneDef != null ? SceneCatalog.mostRecentSceneDef.baseSceneName : stage.name;
            StageChanged?.Invoke(scene);
            if (linked)
            {
                Commands.Push(Commands.Format("clear_stage"));
                Commands.Push(Commands.Format("kill_items"));
                Commands.Push(Commands.Format("kill_arrows"));
                Commands.Push(Commands.Format("kill_villagers"));
                Commands.Push(Commands.Format("kill_displays"));
                Commands.Push(Commands.Format("kill_crystals"));
                Commands.Push(Commands.Format("kill_dragons"));
                Commands.Push(Commands.Format("clear_mobs"));
            }
        }
        // The kit waits until this player's Minecraft is linked and their body exists, so a slow
        // Minecraft start still gets it; a Minecraft that restarts mid-run keeps what it has.
        if (!linked || body == null || kitRun == run)
        {
            return;
        }
        kitRun = run;
        Commands.Push(Commands.Format("reset_run"));
        foreach ((string item, int count) in Tables.StarterKit)
        {
            Commands.Push(Commands.Format("give", ("item", item), ("count", count)));
        }
        Commands.Push(Commands.Format("time", ("ticks", 6000)));
        Plugin.Log.LogInfo($"run: new run; Minecraft reset and starter kit given ({Tables.StarterKit.Length} stacks)");
    }
}
