using RiskOfCraft.Link;
using RiskOfCraft.World;
using RoR2;
using UnityEngine;

namespace RiskOfCraft.Player;

/// <summary>
/// Puts RoR2's camera where Minecraft's is: its eye in first person, or its F5 camera. Runs after
/// RoR2's own camera update (postfix on CameraRigController.LateUpdate); RoR2's screen shake still
/// applies through SetCameraState.
/// </summary>
internal sealed class CameraDriver
{
    private float lastCompare;

    /// <summary>The authoritative look, Minecraft degrees. The input bridge integrates the mouse into it.</summary>
    public float Yaw;
    public float Pitch;

    /// <summary>Minecraft's eye this frame (Unity space).</summary>
    public Vector3 Eye { get; private set; }

    public Vector3 AimDirection => Coords.Forward(Yaw, Pitch);

    public void SetLookFromHost(CharacterBody body)
    {
        InputBankTest bank = body.inputBank;
        Vector3 forward = bank != null ? bank.aimDirection : body.transform.forward;
        if (forward.sqrMagnitude < 1e-6f)
        {
            return;
        }
        Quaternion look = Quaternion.LookRotation(forward);
        Vector3 euler = look.eulerAngles;
        Yaw = Coords.McYawFromUnity(euler.y);
        Pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, euler.x), -90f, 90f);
    }

    public void Apply(CameraRigController rig, Follower follower, in Proto.GuestState guest)
    {
        Eye = follower.Feet + Vector3.up * follower.EyeHeight;
        Quaternion rotation = Coords.Rotation(Yaw, Pitch);
        Vector3 position = Eye;
        // Minecraft's F5: 1 = behind the player, 2 = in front, looking back. The distance already
        // includes Minecraft's own zoom collision against blocks and RoR2's triangles.
        if (guest.cameraMode == 1)
        {
            position = Eye - rotation * Vector3.forward * guest.cameraDistance;
        }
        else if (guest.cameraMode == 2)
        {
            position = Eye + rotation * Vector3.forward * guest.cameraDistance;
            rotation = Coords.Rotation(Yaw + 180f, -Pitch);
        }
        float fov = guest.fovDeg > 1f ? guest.fovDeg : rig.currentCameraState.fov; // both vertical degrees
        rig.SetCameraState(new CameraState { position = position, rotation = rotation, fov = fov });
        if (rig.sceneCam != null)
        {
            rig.sceneCam.nearClipPlane = 0.05f;
        }

        if (Time.unscaledTime - lastCompare > 10f)
        {
            lastCompare = Time.unscaledTime;
            Vector3 mcEye = Coords.ToUnity(guest.eyeX, guest.eyeY, guest.eyeZ);
            Plugin.Log.LogInfo($"camera: eye {Eye:F2}, Minecraft eye {mcEye:F2}, apart {(Eye - mcEye).magnitude * 100f:F1} cm, mode {guest.cameraMode}, fov {fov:F1}");
        }
    }
}
