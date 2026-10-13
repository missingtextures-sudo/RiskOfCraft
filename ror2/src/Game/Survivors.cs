using RoR2;
using UnityEngine.Networking;

namespace RiskOfCraft.Game;

/// <summary>
/// Steve replaces every survivor: all survivors but one are hidden from character select, the one
/// left (Commando's body: human-sized, no special movement) is shown as Steve, and every player
/// spawns as it whatever they picked before (SurvivorPatches).
/// </summary>
internal static class Survivors
{
    public const string SteveSurvivor = "Commando";
    private static bool applied;

    public static SurvivorDef? Steve => SurvivorCatalog.FindSurvivorDef(SteveSurvivor);

    /// <summary>Once the catalog is ready: hide everyone else.</summary>
    public static void Apply()
    {
        if (applied)
        {
            return;
        }
        SurvivorDef? steve = Steve;
        if (steve == null)
        {
            return;
        }
        applied = true;
        int hidden = 0;
        foreach (SurvivorDef def in SurvivorCatalog.allSurvivorDefs)
        {
            if (def != null && def != steve && !def.hidden)
            {
                def.hidden = true;
                hidden++;
            }
        }
        Plugin.Log.LogInfo($"survivors: Steve ({steve.cachedName}'s body) is the only survivor; {hidden} others hidden");
    }

    private static float nextPreferenceCheck;

    /// <summary>In the lobby, point each local player's own choice at Steve so the screen shows him.</summary>
    public static void Update()
    {
        Apply();
        if (UnityEngine.Time.unscaledTime < nextPreferenceCheck || PreGameController.instance == null)
        {
            return;
        }
        nextPreferenceCheck = UnityEngine.Time.unscaledTime + 1f;
        SurvivorDef? steve = Steve;
        if (steve == null)
        {
            return;
        }
        foreach (NetworkUser user in NetworkUser.readOnlyLocalPlayersList)
        {
            if (user != null && user.isLocalPlayer && user.bodyIndexPreference != SurvivorCatalog.GetBodyIndexFromSurvivorIndex(steve.survivorIndex))
            {
                user.SetSurvivorPreferenceClient(steve);
            }
        }
    }
}
