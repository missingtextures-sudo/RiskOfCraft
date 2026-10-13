using RiskOfCraft.Link;
using RiskOfCraft.World;
using RoR2;
using UnityEngine;

namespace RiskOfCraft.Player;

/// <summary>
/// Keeps the local RoR2 body where Minecraft's player is. The body stays a real, networked
/// CharacterBody (its hurtboxes take hits, its capsule picks up items, monsters chase it, the
/// teleporter counts it), but RoR2's own movement is off (MotorPatches) and its capsule is placed
/// at Minecraft's feet every frame. Its model is hidden by SteveModels.
/// </summary>
internal sealed class Follower
{
    private readonly HostLink link;
    private CharacterBody? held;

    public Follower(HostLink link)
    {
        this.link = link;
    }

    /// <summary>Minecraft's interpolated feet this frame, in Unity space.</summary>
    public Vector3 Feet { get; private set; }

    /// <summary>Minecraft's eye height above the feet this frame.</summary>
    public float EyeHeight { get; private set; } = 1.62f;

    public CharacterBody? Held => held;

    /// <summary>Where RoR2's own body stands (its feet), in Unity space.</summary>
    public static Vector3 HostFeet(CharacterBody body) => body.footPosition;

    public void Update(Ownership ownership, in Proto.GuestState guest, CharacterBody? body)
    {
        bool follow = ownership.BodyFollows && body != null;
        if (held != null && (!follow || held != body))
        {
            Release();
        }
        if (!follow)
        {
            return;
        }
        if (held == null)
        {
            Take(body!);
        }
        Interpolate(guest);
        Place(body!, guest);
    }

    private void Take(CharacterBody body)
    {
        held = body;
        CharacterMotor motor = body.characterMotor;
        if (motor != null && motor.Motor != null)
        {
            // Minecraft already collided with RoR2's world; the capsule must not be pushed off what
            // only Minecraft knows (placed blocks, dug holes) or snapped to the ground.
            motor.Motor.SetGroundSolvingActivation(false);
            motor.Motor.SetMovementCollisionsSolvingActivation(false);
            motor.velocity = Vector3.zero;
        }
        Plugin.Log.LogInfo($"follower: took {body.name} (capsule {(motor != null ? motor.capsuleHeight : 0f):F2} m)");
    }

    /// <summary>Hands the body back to RoR2's own physics, standing where it is.</summary>
    public void Release()
    {
        if (held != null)
        {
            CharacterMotor motor = held.characterMotor;
            if (motor != null && motor.Motor != null)
            {
                motor.Motor.SetGroundSolvingActivation(true);
                motor.Motor.SetMovementCollisionsSolvingActivation(true);
                motor.velocity = Vector3.zero;
            }
        }
        held = null;
        Plugin.Log.LogInfo("follower: released the body to RoR2");
    }

    // Same idea as Minecraft's renderer: blend the last two 20 Hz physics ticks on our own frame
    // clock, so the two games' frame phases don't beat against each other.
    private void Interpolate(in Proto.GuestState guest)
    {
        Vector3 reported = Coords.ToUnity(guest.x, guest.y, guest.z);
        float eye = guest.eyeHeight > 0.1f ? guest.eyeHeight : 1.62f;
        if (guest.tickQpc != 0 && guest.tickMs > 1f)
        {
            double ageMs = (HostLink.Qpc() - guest.tickQpc) * 1000.0 / link.QpcFrequency;
            if (ageMs >= 0 && ageMs < 250)
            {
                float t = Mathf.Clamp01((float)(ageMs / guest.tickMs));
                Vector3 previous = Coords.ToUnity(guest.prevX, guest.prevY, guest.prevZ);
                Vector3 current = Coords.ToUnity(guest.curX, guest.curY, guest.curZ);
                Vector3 blended = Vector3.Lerp(previous, current, t);
                if ((blended - reported).sqrMagnitude < 4f)
                {
                    reported = blended;
                    eye = Mathf.Lerp(guest.tickEyeO, guest.tickEye, t);
                }
            }
        }
        Feet = reported;
        EyeHeight = eye;
    }

    private void Place(CharacterBody body, in Proto.GuestState guest)
    {
        CharacterMotor motor = body.characterMotor;
        if (motor == null || motor.Motor == null)
        {
            return;
        }
        Vector3 centre = Feet + Vector3.up * (motor.capsuleHeight * 0.5f);
        if ((centre - body.transform.position).sqrMagnitude > 1e-8f)
        {
            motor.Motor.SetPosition(centre, bypassInterpolation: true);
        }
        motor.velocity = Vector3.zero;
        motor.rootMotion = Vector3.zero;
        if (body.characterDirection != null)
        {
            body.characterDirection.yaw = Coords.UnityYawFromMc(guest.yaw);
            body.characterDirection.moveVector = Vector3.zero;
        }
        // Sprinting is Minecraft's; RoR2's sprint flag drives its own speed bonuses and effects.
        body.isSprinting = false;
    }
}
