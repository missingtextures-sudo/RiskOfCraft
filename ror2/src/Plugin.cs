using System;
using System.IO;
using Path = System.IO.Path;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using RiskOfCraft.Dev;
using RiskOfCraft.Game;
using RiskOfCraft.Input;
using RiskOfCraft.Link;
using RiskOfCraft.Net;
using RiskOfCraft.Patches;
using RiskOfCraft.Player;
using RiskOfCraft.Render;
using RiskOfCraft.World;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace RiskOfCraft;

/// <summary>
/// RiskOfCraft: Risk of Rain 2 with a real Minecraft 26.3 player inside it. Minecraft runs hidden
/// next to RoR2 and owns the player's physics, inventory, HUD, blocks and health; RoR2 owns the
/// stage, the monsters, the items, the run and co-op. The two talk through shared memory owned
/// by the Rust link (riskofcraft_link.bin, a native DLL).
///
/// Each frame: read Minecraft's state and events, decide who owns the player, place the body,
/// export collision and monsters around it, pump input, run the game rules (kit, scaling, models,
/// HUD), publish the host state. The camera is overwritten after RoR2's own (CameraPatches) and
/// Minecraft's HUD is drawn last (OnGUI).
/// </summary>
[BepInPlugin(Id, Name, Version)]
[DefaultExecutionOrder(-1000)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Id = "com.riskofcraft.riskofcraft";
    public const string Name = "RiskOfCraft";
    public const string Version = "0.2.1";

    internal static ManualLogSource Log { get; private set; } = null!;
    internal static Plugin Instance { get; private set; } = null!;

    internal HostLink Link { get; private set; } = null!;
    internal Ownership Ownership { get; } = new();
    internal Follower Follower { get; private set; } = null!;
    internal CameraDriver Camera { get; } = new();
    internal InputBridge InputBridge { get; private set; } = null!;
    internal CollisionExporter Collision { get; private set; } = null!;
    internal ActorExporter Actors { get; private set; } = null!;
    internal Overlay Overlay { get; private set; } = null!;
    internal RenderRing Blocks { get; private set; } = null!;
    internal SteveModels Models { get; private set; } = null!;
    internal HudHider Hud { get; } = new();
    internal RunStart Runs { get; } = new();
    internal Scaling Scaling { get; } = new();
    internal UnderMapRescue Rescue { get; } = new();
    internal ChestSpawner Chests { get; } = new();
    internal Retexture Retexture { get; private set; } = null!;
    internal EquipmentHud EquipmentHud { get; } = new();
    internal SoloPause SoloPause { get; } = new();
    internal TerrainCutter Cutter { get; } = new();
    internal NetherMobs NetherMobs { get; } = new();
    internal DevHarness? Dev { get; private set; }
    internal MeltyTest? Melty { get; private set; }

    /// <summary>Minecraft's state as read this frame.</summary>
    internal Proto.GuestState Guest => guest;

    /// <summary>True while this player's Minecraft is alive on the other end of the link.</summary>
    internal bool Linked => linked;

    /// <summary>This player's own body, if they have one.</summary>
    internal CharacterBody? LocalBody { get; private set; }

    private ConfigEntry<string> linkName = null!;
    private ConfigEntry<float> yOffset = null!;
    private ConfigEntry<bool> devHarness = null!;
    private ConfigEntry<bool> startMinecraft = null!;
    private Harmony? harmony;
    private bool active;
    private bool quitting;
    private bool linked;
    private uint guestPid;
    private Proto.GuestState guest;
    private float lastStateLog;
    private bool sentSteveLinked;
    private float nextLinkedResend;
    private uint stageVisit;

    // Minecraft keeps dug cells per world id in its save: a random base per game session keeps holes
    // from an earlier session (same stage, same visit number) from coming back.
    private readonly uint worldIdBase = (uint)new System.Random().Next(1, 0x7FFF) << 16;
    private GUIStyle? promptStyle;

    private void Awake()
    {
        Log = Logger;
        Instance = this;
        linkName = Config.Bind("Link", "MappingName", Proto.MappingName,
            "Name of the shared-memory mapping. Must match Minecraft's -Dskycraft.link.");
        yOffset = Config.Bind("World", "YOffset", 0f,
            "Added to RoR2's height to get Minecraft's. Keeps every stage inside the mirror world's build range (-1024 to 1023). Use a multiple of 8.");
        startMinecraft = Config.Bind("Minecraft", "StartWithGame", true,
            "Start RiskOfCraft's Minecraft (its Prism Launcher instance) when Risk of Rain 2 starts, unless Prism is already running.");
        devHarness = Config.Bind("Dev", "Harness", false,
            "Development only: run commands from BepInEx/riskofcraft-dev.txt (screenshots, probes, scripted input).");
        Coords.YOffset = yOffset.Value;
        Log.LogInfo($"{Name} {Version} loading (protocol {Proto.Version})");

        string dir = Path.GetDirectoryName(Info.Location)!;
        Link = new HostLink(Log);
        if (!HostLink.Load(dir, Log) || !Link.Create(linkName.Value) || !ApplyPatches())
        {
            Log.LogError("RiskOfCraft is disabled for this session: Risk of Rain 2 runs unmodified.");
            enabled = false;
            return;
        }
        active = true;
        Application.quitting += () => quitting = true;
        Commands.Init(Link);
        Follower = new Follower(Link);
        InputBridge = new InputBridge(Link, Camera, Config);
        Collision = new CollisionExporter(Link);
        Actors = new ActorExporter(Link);
        Overlay = new Overlay(Link);
        Blocks = new RenderRing(Link, Config);
        Models = new SteveModels(Blocks);
        unsafe
        {
            Blocks.Dug = (payload, bytes) =>
            {
                Rescue.OnDug(payload, bytes);
                Cutter.OnDug(payload, bytes);
            };
        }
        Retexture = new Retexture(Blocks.Atlas);
        SteveNet.Init();
        Loot.Init();
        CustomStages.Init();
        SpawnToggle.Init();
        LunarShop.Init();
        MapZone.onBodyTeleportGlobal += OnBodyTeleported;
        Runs.StageChanged += OnStageChanged;
        if (devHarness.Value)
        {
            Dev = new DevHarness(this);
        }
        // Melty's on-PC release test: RiskOfCraft plays and checks its own run (see MeltyTest).
        Melty = MeltyTest.TryLoad();
        if (Melty != null)
        {
            Dev ??= new DevHarness(this);
            Dev.Enqueue(MeltyTest.Script);
        }
        else if (startMinecraft.Value && !devHarness.Value)
        {
            MinecraftStarter.StartIfNeeded();
        }
        Log.LogInfo($"{Name} ready: waiting for Minecraft on {linkName.Value}");
    }

    /// <summary>One patch class per concern. A patch that fails to apply disables the plugin rather than half-running.</summary>
    private bool ApplyPatches()
    {
        harmony = new Harmony(Id);
        Type[] patches = { typeof(InputPatches), typeof(MotorPatches), typeof(CameraPatches), typeof(HealthPatches), typeof(SurvivorPatches), typeof(LobbyPatches), typeof(PausePatches), typeof(StagePatches), typeof(EndingPatches), typeof(ShopPatches) };
        foreach (Type patch in patches)
        {
            try
            {
                harmony.CreateClassProcessor(patch).Patch();
                Log.LogInfo($"patch ok   {patch.Name}");
            }
            catch (Exception e)
            {
                Log.LogError($"patch FAIL {patch.Name}: {e.Message}");
                harmony.UnpatchSelf();
                return false;
            }
        }
        return true;
    }

    private void OnDestroy()
    {
        // When the game quits, Windows frees the patches and the shared memory with the process. Unpatching
        // and unmapping while Unity tears its objects down crashed players' games in ntdll on the way out.
        if (!active || quitting)
        {
            return;
        }
        MapZone.onBodyTeleportGlobal -= OnBodyTeleported;
        Follower?.Release();
        harmony?.UnpatchSelf();
        Link?.Close();
    }

    /// <summary>
    /// A scene Minecraft's world belongs in: a stage or an intermission (the Bazaar, A Moment), not the
    /// ending cutscene or a finished run's report, where the last stage's blocks would hang in the air.
    /// </summary>
    private static bool InPlayableScene
    {
        get
        {
            SceneDef? scene = SceneCatalog.mostRecentSceneDef;
            return scene != null && scene.sceneType is SceneType.Stage or SceneType.Intermission or SceneType.UntimedStage
                && GameOverController.instance == null;
        }
    }

    private void OnStageChanged(string scene)
    {
        stageVisit++;
        Collision.Reset($"stage {scene}");
        Blocks.Reset();
        Scaling.Reset();
        Rescue.Clear();
        Cutter.Clear();
        Retexture.Reset();
    }

    /// <summary>RoR2 moved the body (out of bounds rescue, teleports): Minecraft must follow.</summary>
    private void OnBodyTeleported(CharacterBody body)
    {
        if (body != null && body == LocalBody && linked)
        {
            Ownership.RequestTeleport();
            Log.LogInfo($"teleport: RoR2 moved the body to {body.footPosition:F1}; Minecraft follows (teleport {Ownership.TeleportSeq})");
        }
    }

    private void Update()
    {
        Link.Heartbeat();
        Dev?.Update();

        bool nowLinked = Link.GuestAlive;
        if (nowLinked && (!linked || Link.GuestPid != guestPid))
        {
            guestPid = Link.GuestPid;
            Log.LogInfo($"link up: Minecraft (pid {guestPid}) is on the other end");
            Collision.Reset("Minecraft linked");
            Link.ResetOverlay();
        }
        else if (!nowLinked && linked)
        {
            Log.LogInfo("link down: Minecraft's heartbeat stopped; Risk of Rain 2 has the player");
            if (Melty is { AllReported: true })
            {
                MeltyTest.ExitNow();
            }
            Overlay.Clear();
            Blocks.Reset();
        }
        linked = nowLinked;
        if (linked && Link.ReadGuestState(out Proto.GuestState state))
        {
            guest = state; // on a torn read keep last frame's
        }

        LocalUser? user = LocalUserManager.GetFirstLocalUser();
        CharacterBody? body = user?.cachedBody;
        LocalBody = body;
        DrainEvents();

        Ownership.Update(linked, guest, body);
        if (body != null && Ownership.HostCamera)
        {
            Camera.SetLookFromHost(body); // Minecraft starts out looking where RoR2 looks
        }
        Follower.Update(Ownership, guest, body);
        Rescue.Update(Ownership, Follower, body, guest);
        UpdateCollision(body);
        if (linked)
        {
            Actors.Update(Ownership.BodyFollows ? body : null);
        }
        InputBridge.Update(Ownership, guest);
        Hud.Update(linked && Run.instance != null && InPlayableScene, !Ownership.HostHud);
        if (Ownership.OverlayAndBlocks)
        {
            Overlay.Update();
        }
        if (linked)
        {
            Blocks.Update(Ownership.OverlayAndBlocks && InPlayableScene, Follower.Feet, guest.cameraMode == 0);
        }
        Models.Update(linked, Ownership.BodyFollows || Ownership.State == Owner.Handoff, body);
        Survivors.Update();
        Runs.Update(linked, body);
        Chests.Update(linked, body);
        CustomStages.Update(linked, body, Retexture);
        Retexture.Update();
        LunarShop.Update(linked, body);
        NetherMobs.Update(linked, body);
        SoloPause.Update(linked, guest);
        if (linked)
        {
            Cutter.Update();
        }
        Scaling.Update(linked && Ownership.BodyFollows, body);
        Commands.Flush();
        UpdateSteveLinked(body);
        SteveRegistry.Update();
        PublishHostState(body);

        if (linked && Time.unscaledTime - lastStateLog > 10f)
        {
            lastStateLog = Time.unscaledTime;
            Log.LogInfo($"state: {Ownership.State}; {1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f):F0} fps; Minecraft flags 0x{guest.flags:X} feet ({guest.x:F2}, {guest.y:F2}, {guest.z:F2}) ack {guest.teleportAck}/{Ownership.TeleportSeq}{(body != null ? $"; body feet {body.footPosition:F2}" : "")}");
        }
    }

    private void DrainEvents()
    {
        while (Link.PopEvent(out Proto.GuestEvent e))
        {
            switch ((Proto.EventType)e.type)
            {
                case Proto.EventType.HitActor:
                    Combat.OnGuestHit(e);
                    break;
                case Proto.EventType.PortalEntered:
                    Log.LogInfo($"portal: the Minecraft player stepped into the {(e.formId == 1 ? "Nether" : "End")} portal");
                    SteveNet.SendPortal(e.formId);
                    break;
                case Proto.EventType.SetSpawns:
                    Log.LogInfo($"spawns: the Minecraft player asked for spawns {(e.formId != 0 ? "on" : "off")}");
                    SteveNet.SendSetSpawns(e.formId != 0);
                    break;
                case Proto.EventType.CreditsDone:
                    Log.LogInfo("stages: the Minecraft player's credits are over");
                    SteveNet.SendCreditsDone();
                    break;
                case Proto.EventType.DragonKilled:
                    Log.LogInfo("portal: the Minecraft player's Ender Dragon died");
                    SteveNet.SendDragonKilled();
                    break;
                case Proto.EventType.PlayerDied:
                    Log.LogInfo("death: the Minecraft player died");
                    if (LocalBody != null)
                    {
                        SteveNet.SendDied();
                    }
                    break;
            }
        }
    }

    /// <summary>Tells the host's server whether Minecraft's health is this player's health.</summary>
    private void UpdateSteveLinked(CharacterBody? body)
    {
        bool steve = linked && body != null && Ownership.State is Owner.MinecraftOwns or Owner.HostMenu or Owner.Handoff;
        if (body == null || !NetworkClient.active)
        {
            sentSteveLinked = false;
            return;
        }
        if (steve != sentSteveLinked || Time.unscaledTime > nextLinkedResend)
        {
            sentSteveLinked = steve;
            nextLinkedResend = Time.unscaledTime + 3f; // new bodies on new stages, a host that missed it
            SteveNet.SendLinked(steve);
        }
    }

    private void UpdateCollision(CharacterBody? body)
    {
        if (!linked || body == null)
        {
            return;
        }
        // While RoR2 owns the body or a teleport is pending, centre on where Minecraft is about to be put.
        if (Ownership.BodyFollows)
        {
            Collision.Update(guest.x, guest.y, guest.z);
        }
        else if (body.healthComponent != null && body.healthComponent.alive)
        {
            Coords.ToMc(Follower.HostFeet(body), out double x, out double y, out double z);
            Collision.Update(x, y, z);
        }
    }

    private void PublishHostState(CharacterBody? body)
    {
        var host = new Proto.HostState
        {
            collisionEpoch = Collision.Epoch,
            teleportSeq = Ownership.TeleportSeq,
            viewportW = (uint)Screen.width,
            viewportH = (uint)Screen.height,
            gameHour = 12f,
            yaw = Camera.Yaw,
            pitch = Camera.Pitch,
            worldId = worldIdBase + stageVisit + 1, // a fresh dig map for every stage visit, in every session
        };
        Owner state = Ownership.State;
        bool loading = body == null || state == Owner.Cutscene;
        if (body != null)
        {
            host.flags |= (uint)Proto.HostFlags.InGame;
            Vector3 feet = Ownership.BodyFollows ? Follower.Feet : Follower.HostFeet(body);
            Coords.ToMc(feet, out host.posX, out host.posY, out host.posZ);
        }
        if (loading)
        {
            host.flags |= (uint)Proto.HostFlags.Loading; // Minecraft parks its player and teleports to us afterwards
        }
        if (state == Owner.HostMenu)
        {
            host.flags |= (uint)Proto.HostFlags.MenuOpen; // Minecraft drops its held keys
        }
        // Minecraft paces its frames on this sequence number: write it every RoR2 frame.
        Link.WriteHostState(host);
    }

    /// <summary>RoR2's own prompt is hidden with its crosshair, so say what the interact key would do.</summary>
    private void DrawInteractPrompt()
    {
        if (Ownership.State != Owner.MinecraftOwns || InputBridge.ScreenOpen || Event.current.type != EventType.Repaint || LocalBody == null)
        {
            return;
        }
        InteractionDriver driver = LocalBody.GetComponent<InteractionDriver>();
        GameObject? target = driver != null ? driver.currentInteractable : null;
        if (target == null)
        {
            return;
        }
        IInteractable? interactable = target.GetComponent<IInteractable>();
        string what = interactable != null ? interactable.GetContextString(driver!.interactor) : target.name;
        if (string.IsNullOrEmpty(what))
        {
            return;
        }
        string text = $"[{InputBridge.InteractKeyName}] {ImguiRichText(what)}";
        promptStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, richText = true };
        promptStyle.fontSize = Mathf.Max(16, Screen.height / 45);
        var rect = new Rect(0f, Screen.height * 0.56f, Screen.width, promptStyle.fontSize * 2f);
        promptStyle.normal.textColor = Color.black;
        GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, promptStyle);
        promptStyle.normal.textColor = Color.white;
        GUI.Label(rect, text, promptStyle);
    }

    /// <summary>RoR2's TextMeshPro rich text for Unity's GUI: coin sprites become words, unsupported tags go.</summary>
    private static string ImguiRichText(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<sprite[^>]*name=""LunarCoin""[^>]*>\s*(\d+)", "$1 lunar");
        text = System.Text.RegularExpressions.Regex.Replace(text, "<sprite[^>]*>", "");
        return System.Text.RegularExpressions.Regex.Replace(text, "</?(nobr|style|link|noparse|voffset|mspace|cspace|align|font|mark|u|s|sup|sub|indent|line-height|margin|pos|rotate|width|allcaps|smallcaps|lowercase|uppercase|alpha)(=[^>]*)?>", "");
    }

    private void OnGUI()
    {
        // Not over the run's report or RoR2's ending: Minecraft's HUD and hand don't belong there.
        if (!active || !Ownership.OverlayAndBlocks || Ownership.State == Owner.HostMenu || LocalBody == null || !InPlayableScene)
        {
            return;
        }
        Overlay.Draw(InputBridge.ScreenOpen && !InputBridge.UsingOsCursor, InputBridge.Cursor); // RoR2 shows the freed Windows cursor itself
        if (!InputBridge.ScreenOpen)
        {
            EquipmentHud.Draw(LocalBody, guest.guiScale, InputBridge.EquipmentKeyName);
        }
        DrawInteractPrompt();
    }
}
