using HarmonyLib;
using RoR2;

namespace RiskOfCraft.Patches;

/// <summary>
/// RoR2 places its camera in CameraRigController.LateUpdate. Overwriting it right after means the
/// renderer, the crosshair and everything aimed from the camera use Minecraft's view.
/// </summary>
[HarmonyPatch(typeof(CameraRigController), "LateUpdate")]
internal static class CameraPatches
{
    private static void Postfix(CameraRigController __instance)
    {
        Plugin plugin = Plugin.Instance;
        if (plugin.Ownership.HostCamera || plugin.LocalBody == null || __instance.target != plugin.LocalBody.gameObject || __instance.localUserViewer == null)
        {
            return;
        }
        plugin.Camera.Apply(__instance, plugin.Follower, plugin.Guest);
    }
}
