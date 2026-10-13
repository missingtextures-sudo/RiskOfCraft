using RiskOfCraft.Link;
using RoR2;
using UnityEngine;

namespace RiskOfCraft.Player;

/// <summary>Who owns the player. Every patch and module reads this one value.</summary>
internal enum Owner
{
    /// <summary>No link (or no body): Risk of Rain 2 runs as if the plugin were not there.</summary>
    HostOwns,

    /// <summary>Minecraft has been asked to teleport to the body and has not confirmed yet.</summary>
    Handoff,

    /// <summary>Minecraft physics drives the body; RoR2's body is a hidden follower.</summary>
    MinecraftOwns,

    /// <summary>A RoR2 menu (pause, settings, chat) is open over a Minecraft-owned body.</summary>
    HostMenu,

    /// <summary>RoR2 holds the body: drop pod, stage exit, death, vehicles, scripted cameras.</summary>
    Cutscene,
}

/// <summary>
/// The ownership state machine. Heartbeat loss drops to <see cref="Owner.HostOwns"/>, which
/// restores RoR2's movement, camera, HUD and model in one transition.
///
/// | State          | RoR2 movement | RoR2 input | RoR2 camera | RoR2 HUD | Commando shown | Overlay and blocks |
/// | HostOwns       | on            | on         | on          | on       | yes            | off                |
/// | Handoff        | frozen        | off        | on          | on       | no             | off                |
/// | MinecraftOwns  | off           | off        | off         | off      | no             | on                 |
/// | HostMenu       | off           | on         | off         | on       | no             | on                 |
/// | Cutscene       | on            | off        | on          | on       | no             | on                 |
/// </summary>
internal sealed class Ownership
{
    public Owner State { get; private set; } = Owner.HostOwns;

    /// <summary>The teleport Minecraft must acknowledge before it gets the body.</summary>
    public uint TeleportSeq { get; private set; } = (uint)(HostLink.TickCount / 1000 % 1000000) * 64 + 2; // never a number an earlier session used

    public string CutsceneReason { get; private set; } = "";

    public bool HostMovement => State is Owner.HostOwns or Owner.Handoff or Owner.Cutscene;
    public bool HostInput => State is Owner.HostOwns or Owner.HostMenu;
    public bool HostCamera => State is Owner.HostOwns or Owner.Handoff or Owner.Cutscene;
    public bool HostHud => State != Owner.MinecraftOwns;
    public bool OverlayAndBlocks => State is Owner.MinecraftOwns or Owner.HostMenu or Owner.Cutscene;

    /// <summary>The body follows Minecraft (MinecraftOwns or HostMenu).</summary>
    public bool BodyFollows => State is Owner.MinecraftOwns or Owner.HostMenu;

    /// <summary>Asks Minecraft to teleport to the published position again (RoR2 moved the body).</summary>
    public void RequestTeleport() => TeleportSeq++;

    public void Update(bool linkUp, in Proto.GuestState guest, CharacterBody? body)
    {
        Owner next = Decide(linkUp, guest, body);
        if (next == State)
        {
            return;
        }
        if (next == Owner.Handoff && State is Owner.HostOwns or Owner.Cutscene)
        {
            RequestTeleport(); // a fresh takeover starts from where RoR2's body is now
        }
        Plugin.Log.LogInfo($"ownership: {State} -> {next}{(next == Owner.Cutscene ? $" ({CutsceneReason})" : "")}{(next == Owner.Handoff ? $" (teleport {TeleportSeq})" : "")}");
        State = next;
    }

    private Owner Decide(bool linkUp, in Proto.GuestState guest, CharacterBody? body)
    {
        if (!linkUp)
        {
            return Owner.HostOwns;
        }
        if (body == null)
        {
            // Menus, lobby, between stages: nothing to own. In a run it is a stage transition.
            return Run.instance != null && Stage.instance != null ? Cutscene("no body") : Owner.HostOwns;
        }
        string? reason = HostHoldsBody(body);
        if (reason != null)
        {
            return Cutscene(reason);
        }
        if ((guest.flags & (uint)Proto.GuestFlags.InWorld) == 0)
        {
            return Owner.HostOwns;
        }
        if ((guest.flags & (uint)Proto.GuestFlags.Dead) != 0)
        {
            return Cutscene("Minecraft player dead");
        }
        if (State is Owner.HostOwns or Owner.Cutscene)
        {
            return Owner.Handoff;
        }
        if (guest.teleportAck != TeleportSeq)
        {
            return Owner.Handoff;
        }
        return HostMenuOpen() ? Owner.HostMenu : Owner.MinecraftOwns;
    }

    private Owner Cutscene(string reason)
    {
        CutsceneReason = reason;
        return Owner.Cutscene;
    }

    /// <summary>Non-null while RoR2 itself moves or shows the body (see docs/RISKOFRAIN2-NOTES.md).</summary>
    private static string? HostHoldsBody(CharacterBody body)
    {
        HealthComponent health = body.healthComponent;
        if (health != null && !health.alive)
        {
            return "dead";
        }
        if (body.currentVehicle != null)
        {
            return "vehicle"; // the drop pod, Commencement's jump pads, the void cradles
        }
        CameraRigController? rig = LocalRig(body);
        if (rig != null && (rig.isCutscene || rig.hasOverride))
        {
            return "camera override";
        }
        if (SceneExitController.isRunning)
        {
            return "stage exit";
        }
        if ((Run.instance != null && Run.instance.isGameOverServer) || GameOverController.instance != null) // the server's flag, or the report on any player's game
        {
            return "game over";
        }
        return null;
    }

    public static CameraRigController? LocalRig(CharacterBody body)
    {
        foreach (CameraRigController rig in CameraRigController.readOnlyInstancesList)
        {
            if (rig != null && rig.target == body.gameObject && rig.localUserViewer != null)
            {
                return rig;
            }
        }
        return null;
    }

    /// <summary>A RoR2 window owns the mouse: pause menu, console, chat, settings.</summary>
    public static bool HostMenuOpen()
    {
        // RoR2 paused for Minecraft's own pause menu (SoloPause) stays Minecraft's: its menu is up.
        if (PauseManager.isPaused && (!Game.SoloPause.Holding || PauseManager.PauseScreenIsOpen()))
        {
            return true;
        }
        LocalUser? user = LocalUserManager.GetFirstLocalUser();
        return user != null && user.isUIFocused;
    }
}
