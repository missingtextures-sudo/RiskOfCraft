using HarmonyLib;
using RoR2;
using UnityEngine;

namespace RiskOfCraft.Patches;

/// <summary>
/// CharacterMotor.UpdateVelocity is RoR2's movement step (gravity, acceleration, jumps' velocity)
/// inside the kinematic character controller. While the local body follows Minecraft it has no
/// velocity of its own: the follower places it at Minecraft's feet every frame.
/// </summary>
[HarmonyPatch(typeof(CharacterMotor), nameof(CharacterMotor.UpdateVelocity))]
internal static class MotorPatches
{
    private static bool Prefix(CharacterMotor __instance, ref Vector3 currentVelocity)
    {
        Plugin plugin = Plugin.Instance;
        if (!plugin.Ownership.BodyFollows || __instance.body == null || __instance.body != plugin.LocalBody)
        {
            return true;
        }
        currentVelocity = Vector3.zero;
        __instance.velocity = Vector3.zero;
        return false;
    }
}
