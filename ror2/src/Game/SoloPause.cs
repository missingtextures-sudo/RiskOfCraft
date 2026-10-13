using RiskOfCraft.Link;
using RoR2;
using UnityEngine;

namespace RiskOfCraft.Game;

/// <summary>
/// Minecraft's pause menu pauses the run when you play alone: while it (or a screen it opened,
/// like the options) is up, RoR2's time stands still and Minecraft's menu stays on top to use.
/// RoR2's own pause is not used for this: it opens RoR2's paused screen over Minecraft's menu and
/// takes the mouse. In co-op the run goes on, as Minecraft's pause menu does on a server.
/// </summary>
internal sealed class SoloPause
{
    private float savedTimeScale = 1f;
    private bool restorePending;

    /// <summary>True while the run stands still for Minecraft's menu.</summary>
    public static bool Holding { get; private set; }

    public void Update(bool linked, in Proto.GuestState guest)
    {
        bool want = linked && (guest.flags & (uint)Proto.GuestFlags.PauseMenu) != 0 && Run.instance != null
            && NetworkUser.readOnlyInstancesList.Count <= 1 && !PauseManager.isPaused;
        if (want)
        {
            if (!Holding)
            {
                Holding = true;
                savedTimeScale = Time.timeScale > 0f ? Time.timeScale : (restorePending ? savedTimeScale : 1f);
                Plugin.Log.LogInfo("pause: Minecraft's pause menu is open: the run is paused");
            }
            restorePending = false;
            Time.timeScale = 0f; // every frame: nothing else gets to start the run again meanwhile
            return;
        }
        if (Holding)
        {
            Holding = false;
            restorePending = true;
            Plugin.Log.LogInfo("pause: Minecraft's pause menu closed: the run goes on");
        }
        // Give the time back once RoR2's own pause (if it was opened on top) has let go too.
        if (restorePending && !PauseManager.isPaused)
        {
            restorePending = false;
            if (Time.timeScale == 0f)
            {
                Time.timeScale = savedTimeScale;
            }
        }
    }
}
