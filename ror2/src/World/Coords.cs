using UnityEngine;

namespace RiskOfCraft.World;

/// <summary>
/// The one place Unity space and Minecraft space meet. One block is one Unity metre: Commando's
/// capsule is 1.82 m tall and a Minecraft player 1.8 blocks (measured, docs/RISKOFRAIN2-NOTES.md).
/// Unity is left-handed (X right, Y up, Z forward); Minecraft is right-handed (X east, Y up,
/// Z south), so Z flips. YOffset keeps every stage inside the mirror dimension's build range
/// (-1024 to 1023).
///
///     mc.x = unity.x      mc.y = unity.y + YOffset      mc.z = -unity.z
///     mc.yaw = unity yaw + 180 (Minecraft yaw 0 looks along +Z, south)
///     mc.pitch = unity pitch (both positive looking down)
/// </summary>
internal static class Coords
{
    /// <summary>Added to Unity Y to get Minecraft Y. A multiple of 8 so collision regions line up.</summary>
    public static double YOffset = 0.0;

    public static void ToMc(Vector3 unity, out double x, out double y, out double z)
    {
        x = unity.x;
        y = unity.y + YOffset;
        z = -unity.z;
    }

    public static Vector3 ToUnity(double x, double y, double z) => new((float)x, (float)(y - YOffset), (float)-z);

    public static float McYawFromUnity(float unityYaw) => Mathf.DeltaAngle(0f, unityYaw + 180f);

    public static float UnityYawFromMc(float mcYaw) => Mathf.DeltaAngle(0f, mcYaw - 180f);

    /// <summary>Unity rotation of a view with Minecraft yaw and pitch.</summary>
    public static Quaternion Rotation(float mcYaw, float mcPitch) => Quaternion.Euler(mcPitch, mcYaw + 180f, 0f);

    /// <summary>Unity direction of a view with Minecraft yaw and pitch.</summary>
    public static Vector3 Forward(float mcYaw, float mcPitch) => Rotation(mcYaw, mcPitch) * Vector3.forward;
}
