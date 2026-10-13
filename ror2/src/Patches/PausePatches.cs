using HarmonyLib;
using RoR2;

namespace RiskOfCraft.Patches;

/// <summary>Esc closes an open Minecraft screen first; only with none open does it reach RoR2's pause menu.</summary>
[HarmonyPatch(typeof(PauseManager), nameof(PauseManager.TogglePauseScreen))]
internal static class PausePatches
{
    private static bool Prefix()
    {
        Plugin plugin = Plugin.Instance;
        return !(plugin.InputBridge != null && plugin.InputBridge.ScreenOpen && !PauseManager.isPaused);
    }
}
