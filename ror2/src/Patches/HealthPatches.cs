using HarmonyLib;
using RiskOfCraft.Game;
using RiskOfCraft.Net;
using RoR2;
using UnityEngine.Networking;

namespace RiskOfCraft.Patches;

/// <summary>
/// Minecraft's health is a linked Steve's only health. RoR2 still runs its whole damage pipeline
/// on the body (armour, Tougher Times, shields, barrier, One-Shot Protection), and whatever reached
/// its health goes to that player's Minecraft as damage, at 20 Minecraft health per body.baseMaxHealth;
/// the body's health is put back (its Buddha flag means RoR2 never kills it on its own). RoR2
/// healing and regen on the body heal Minecraft at the same rate. Server only.
/// </summary>
[HarmonyPatch(typeof(HealthComponent))]
internal static class HealthPatches
{
    private static int logged;

    [HarmonyPatch("TakeDamageProcess")]
    [HarmonyPrefix]
    private static void TakeDamagePrefix(HealthComponent __instance, out float __state)
    {
        __state = __instance.health;
    }

    [HarmonyPatch("TakeDamageProcess")]
    [HarmonyPostfix]
    private static void TakeDamagePostfix(HealthComponent __instance, DamageInfo damageInfo, float __state)
    {
        CharacterBody body = __instance.body;
        if (!NetworkServer.active || !SteveRegistry.IsLinkedSteve(body))
        {
            return;
        }
        float lost = __state - __instance.health;
        if (lost <= 0f)
        {
            return;
        }
        __instance.Networkhealth = __state;
        float mc = lost * 20f / UnityEngine.Mathf.Max(1f, body.baseMaxHealth);
        SteveNet.SendHurt(body, mc, Combat.Kind(damageInfo), Combat.AttackerId(damageInfo));
        if (logged++ < 20)
        {
            Plugin.Log.LogInfo($"hurt: {body.GetUserName()} took {lost:F1} RoR2 damage from {(damageInfo.attacker != null ? damageInfo.attacker.name : "the world")} = {mc:F2} Minecraft health");
        }
    }

    [HarmonyPatch(nameof(HealthComponent.Heal))]
    [HarmonyPrefix]
    private static bool HealPrefix(HealthComponent __instance, float amount, ref float __result)
    {
        CharacterBody body = __instance.body;
        if (!NetworkServer.active || !SteveRegistry.IsLinkedSteve(body) || !__instance.alive || amount <= 0f)
        {
            return true;
        }
        SteveRegistry.AddHeal(body, amount * 20f / UnityEngine.Mathf.Max(1f, body.baseMaxHealth));
        __result = amount;
        return false;
    }
}
