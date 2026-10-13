using HarmonyLib;
using RoR2;
using UnityEngine;

namespace RiskOfCraft.Patches;

/// <summary>
/// PlayerCharacterMasterController turns Rewired input into the body's InputBankTest: movement and
/// aim in Update, buttons in FixedUpdate (PollButtonInput). While the body follows Minecraft, RoR2
/// must not move, jump, sprint or fire skills; only interact (G) and equipment (R) get through, and
/// the aim is Minecraft's look (from the body's head, which sits at Minecraft's eye), so RoR2's
/// interaction and equipment aim where Steve looks.
/// </summary>
[HarmonyPatch(typeof(PlayerCharacterMasterController))]
internal static class InputPatches
{
    [HarmonyPatch("Update")]
    [HarmonyPostfix]
    private static void UpdatePostfix(PlayerCharacterMasterController __instance)
    {
        Plugin plugin = Plugin.Instance;
        if (!plugin.Ownership.BodyFollows || !IsLocal(__instance) || __instance.bodyInputs == null)
        {
            return;
        }
        InputBankTest bank = __instance.bodyInputs;
        bank.moveVector = Vector3.zero;
        bank.SetRawMoveStates(Vector2.zero);
        bank.aimDirection = plugin.Camera.AimDirection;
    }

    [HarmonyPatch("PollButtonInput")]
    [HarmonyPostfix]
    private static void PollButtonInputPostfix(PlayerCharacterMasterController __instance)
    {
        Plugin plugin = Plugin.Instance;
        if (!plugin.Ownership.BodyFollows || !IsLocal(__instance) || __instance.bodyInputs == null)
        {
            return;
        }
        InputBankTest bank = __instance.bodyInputs;
        bool owns = plugin.Ownership.State == Player.Owner.MinecraftOwns;
        // Re-push this tick's states: PushState keeps justPressed/justReleased right.
        bank.skill1.PushState(false);
        bank.skill2.PushState(false);
        bank.skill3.PushState(false);
        bank.skill4.PushState(false);
        bank.jump.PushState(false);
        bank.sprint.PushState(false);
        bank.rawSprintInput.PushState(false);
        bank.ping.PushState(false);
        bank.interact.PushState(owns && plugin.InputBridge.InteractHeld);
        bank.activateEquipment.PushState(owns && plugin.InputBridge.EquipmentHeld);
    }

    private static bool IsLocal(PlayerCharacterMasterController controller)
    {
        return controller.hasEffectiveAuthority && controller.body != null && controller.body == Plugin.Instance.LocalBody;
    }
}
