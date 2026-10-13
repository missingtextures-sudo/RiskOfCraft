using HarmonyLib;
using RiskOfCraft.Game;
using RoR2;

namespace RiskOfCraft.Patches;

/// <summary>Every player is Steve: the survivor a player spawns as, and the name RoR2 shows for it.</summary>
[HarmonyPatch]
internal static class SurvivorPatches
{
    [HarmonyPatch(typeof(NetworkUser), nameof(NetworkUser.GetSurvivorPreference))]
    [HarmonyPostfix]
    private static void SurvivorPreference(ref SurvivorDef __result)
    {
        SurvivorDef? steve = Survivors.Steve;
        if (steve != null)
        {
            __result = steve;
        }
    }

    [HarmonyPatch(typeof(Language), nameof(Language.GetString), typeof(string))]
    [HarmonyPostfix]
    private static void SteveName(string token, ref string __result)
    {
        if (token.StartsWith(LunarShop.TokenPrefix))
        {
            bool buy = token.StartsWith(LunarShop.TokenPrefix + "BUY_");
            string id = token.Substring((LunarShop.TokenPrefix + (buy ? "BUY_" : "")).Length);
            Tables2.RareItem? rare = System.Array.Find(Tables2.RareItems, r => r.Id.ToUpperInvariant() == id);
            if (rare != null)
            {
                __result = buy ? $"Buy {rare.DisplayName} (Minecraft)" : $"{rare.DisplayName} (Minecraft)";
            }
            return;
        }
        Tables2.CustomStage? stage = CustomStages.Current;
        if (stage != null && SceneCatalog.mostRecentSceneDef != null && SceneCatalog.mostRecentSceneDef.baseSceneName == stage.BaseScene)
        {
            if (token == SceneCatalog.mostRecentSceneDef.nameToken)
            {
                __result = stage.Title;
                return;
            }
            if (token == SceneCatalog.mostRecentSceneDef.subtitleToken)
            {
                __result = stage.Subtitle;
                return;
            }
        }
        switch (token)
        {
            case "COMMANDO_BODY_NAME":
                __result = "Steve";
                break;
            case "COMMANDO_BODY_SUBTITLE":
                __result = "From the Overworld";
                break;
            case "COMMANDO_DESCRIPTION":
                __result = "Steve fights with whatever is in his Minecraft hotbar.<style=cSub>\r\n\r\n< ! > Swords, axes and bows hit as hard as your Risk of Rain level and items allow.\r\n\r\n< ! > Eat to heal. Chests and kills fill your Minecraft inventory.\r\n\r\n< ! > G opens chests and starts the teleporter. R uses equipment.</style>";
                break;
        }
    }
}

/// <summary>The lobby's character pads show Steve, not Commando (see SteveMannequin).</summary>
[HarmonyPatch(typeof(RoR2.SurvivorMannequins.SurvivorMannequinSlotController))]
internal static class LobbyPatches
{
    [HarmonyPatch("RebuildMannequinInstance")]
    [HarmonyPostfix]
    private static void Rebuilt(RoR2.SurvivorMannequins.SurvivorMannequinSlotController __instance) => Attach(__instance);

    [HarmonyPatch("ApplyLoadoutToMannequinInstance")]
    [HarmonyPostfix]
    private static void Reskinned(RoR2.SurvivorMannequins.SurvivorMannequinSlotController __instance) => Attach(__instance);

    private static void Attach(RoR2.SurvivorMannequins.SurvivorMannequinSlotController slot)
    {
        UnityEngine.Transform? mannequin = slot.mannequinInstanceTransform;
        if (mannequin == null)
        {
            return;
        }
        Render.SteveMannequin steve = mannequin.GetComponent<Render.SteveMannequin>() ?? mannequin.gameObject.AddComponent<Render.SteveMannequin>();
        steve.Refresh();
    }
}
