using HarmonyLib;
using RiskOfCraft.Game;
using RoR2;
using UnityEngine.Networking;

namespace RiskOfCraft.Patches;

/// <summary>The Nether has more to loot: its interactable credits are raised before RoR2 spends them.</summary>
[HarmonyPatch(typeof(SceneDirector), "PopulateScene")]
internal static class StagePatches
{
    private static void Prefix(SceneDirector __instance)
    {
        string scene = SceneCatalog.mostRecentSceneDef != null ? SceneCatalog.mostRecentSceneDef.baseSceneName : "";
        string mode = NetworkServer.active ? CustomStages.ServerModeFor(scene) : "";
        if (mode.Length > 0 && Tables2.CustomStages.TryGetValue(mode, out Tables2.CustomStage stage) && stage.InteractableCreditMultiplier != 1f)
        {
            int before = __instance.interactableCredit;
            __instance.interactableCredit = (int)(before * stage.InteractableCreditMultiplier);
            Plugin.Log.LogInfo($"stages: {stage.Title}: interactable credits {before} -> {__instance.interactableCredit}");
        }
    }
}

/// <summary>
/// The dragon win ends with Minecraft's End Poem and credits, already played: RoR2's own ending
/// (the outro cutscene and RoR2's credits) is skipped and the run goes straight to its report.
/// </summary>
[HarmonyPatch(typeof(GameOverController), nameof(GameOverController.SetRunReport))]
internal static class EndingPatches
{
    private static void Postfix(GameOverController __instance, RunReport newRunReport)
    {
        if (!NetworkServer.active || !CustomStages.DragonWin || newRunReport.gameEnding != RoR2Content.GameEndings.MainEnding)
        {
            return;
        }
        EntityStateMachine main = EntityStateMachine.FindByCustomName(__instance.gameObject, "Main");
        if (main != null)
        {
            main.initialStateType = new EntityStates.SerializableEntityStateType(typeof(EntityStates.GameOver.ShowReport));
            Plugin.Log.LogInfo("stages: RoR2's outro skipped: straight to the run report");
        }
    }
}
