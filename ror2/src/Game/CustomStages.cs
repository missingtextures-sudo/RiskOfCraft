using System.Globalization;
using RiskOfCraft.Net;
using RiskOfCraft.Render;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace RiskOfCraft.Game;

/// <summary>
/// The Nether and The End (design/gameplay.json portals and custom_stages). A player stepping into a
/// Nether or End portal in Minecraft sends the whole run to the custom stage: the host's server
/// advances the run to the stage's base scene and tells every player's game which custom stage it
/// is. Each player's game then dresses it in that player's own Minecraft: retextured terrain, fog,
/// decorations and loot, or the End's arena and Ender Dragon. Killing the dragon wins the run.
/// </summary>
internal sealed class CustomStages
{
    private static string? pendingMode;   // server: the portal's stage, until its scene starts
    private static string serverMode = "";
    private static Stage? serverModeStage; // server: the stage serverMode belongs to
    private static float lastPortal = -100f;
    private static bool won;

    // Server: the dragon is dead and every player's Minecraft is showing the End Poem and credits.
    private static bool creditsRunning;
    private static float creditsStart, firstCreditsDone;
    private static readonly System.Collections.Generic.HashSet<CharacterBody> creditsWaiting = new();

    /// <summary>Server: this run ends because the dragon died (the ending skips RoR2's outro).</summary>
    public static bool DragonWin { get; private set; }

    // Every game: the mode the server sent and the network id of the Stage it is for. A message can
    // arrive before or after this game's Stage object for it, so it only counts once they match.
    private static string mode = "";
    private static uint modeStageId;
    private static uint appliedStageId;

    /// <summary>True once the server has said what this game's current stage is.</summary>
    public static bool Known => Stage.instance != null && modeStageId != 0 && Stage.instance.netId.Value == modeStageId;

    /// <summary>The custom stage this player is on ("" for an ordinary stage, or while not yet known).</summary>
    public static string Mode => Known ? mode : "";

    public static Tables2.CustomStage? Current => Mode.Length > 0 && Tables2.CustomStages.TryGetValue(Mode, out Tables2.CustomStage s) ? s : null;

    public static void Init()
    {
        Stage.onStageStartGlobal += OnStageStart;
    }

    // ---- server ----

    /// <summary>A player's Minecraft reports a portal (1 Nether, 2 End): the run goes to its custom stage.</summary>
    public static void ServerPortal(CharacterBody? body, uint kind)
    {
        if (!NetworkServer.active || body == null || Run.instance == null || SceneExitController.isRunning || Time.unscaledTime - lastPortal < 8f)
        {
            return;
        }
        string id = kind == 1 ? "nether_portal" : kind == 2 ? "end_portal" : "";
        Tables2.Portal? portal = System.Array.Find(Tables2.Portals, p => p.Id == id);
        if (portal == null || !Tables2.CustomStages.TryGetValue(portal.CustomStage, out Tables2.CustomStage stage))
        {
            return;
        }
        SceneDef? scene = SceneCatalog.FindSceneDef(stage.BaseScene);
        if (scene == null)
        {
            Plugin.Log.LogError($"stages: RoR2 has no scene {stage.BaseScene} for {stage.Title}");
            return;
        }
        lastPortal = Time.unscaledTime;
        pendingMode = stage.Id;
        Plugin.Log.LogInfo($"stages: {body.GetUserName()} stepped into the {(kind == 1 ? "Nether" : "End")} portal: the run goes to {stage.Title} ({stage.BaseScene})");
        Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = $"<color=#c4a3ff>{body.GetUserName()} stepped through the portal to {stage.Title}.</color>" });
        Run.instance.AdvanceStage(scene);
    }

    /// <summary>The custom stage a scene that is starting on the server is ("" if ordinary).</summary>
    public static string ServerModeFor(string sceneName)
    {
        if (pendingMode != null && Tables2.CustomStages.TryGetValue(pendingMode, out Tables2.CustomStage s) && s.BaseScene == sceneName)
        {
            return pendingMode;
        }
        return ServerMode;
    }

    /// <summary>Server: the custom stage the current stage is ("" once the run has moved on).</summary>
    private static string ServerMode => Stage.instance != null && Stage.instance == serverModeStage ? serverMode : "";

    private static void OnStageStart(Stage stage)
    {
        if (!NetworkServer.active)
        {
            return;
        }
        string scene = SceneCatalog.mostRecentSceneDef != null ? SceneCatalog.mostRecentSceneDef.baseSceneName : "";
        serverMode = pendingMode != null && ServerModeFor(scene) == pendingMode ? pendingMode : "";
        serverModeStage = stage;
        pendingMode = null;
        won = false;
        creditsRunning = false;
        DragonWin = false;
        SteveNet.SendStageModeToAll(stage.netId.Value, serverMode);
    }

    /// <summary>A player's Ender Dragon died: the run is won.</summary>
    public static void ServerDragonKilled(CharacterBody? body)
    {
        if (!NetworkServer.active || Run.instance == null || won || ServerMode != "end")
        {
            return;
        }
        won = true;
        Plugin.Log.LogInfo($"stages: {(body != null ? body.GetUserName() : "a player")} killed the Ender Dragon: the run is won; Minecraft's credits first");
        Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = "<color=#c4a3ff>The Ender Dragon is slain. The End is free.</color>" });
        // Minecraft's ending instead of RoR2's: every player's Minecraft shows the End Poem and credits,
        // and the run ends (straight to the report) once they're over.
        creditsWaiting.Clear();
        foreach (PlayerCharacterMasterController player in PlayerCharacterMasterController.instances)
        {
            CharacterBody? steve = player.master != null ? player.master.GetBody() : null;
            if (steve != null)
            {
                creditsWaiting.Add(steve);
                SteveNet.SendCommand(steve, Commands.Format("credits"));
            }
        }
        creditsRunning = true;
        creditsStart = Time.unscaledTime;
        firstCreditsDone = -1f;
    }

    /// <summary>A player's credits screen closed (watched or skipped).</summary>
    public static void ServerCreditsDone(CharacterBody? body)
    {
        if (!NetworkServer.active || !creditsRunning)
        {
            return;
        }
        if (body != null)
        {
            creditsWaiting.Remove(body);
        }
        if (firstCreditsDone < 0f)
        {
            firstCreditsDone = Time.unscaledTime;
        }
        Plugin.Log.LogInfo($"stages: {(body != null ? body.GetUserName() : "a player")} finished the credits; {creditsWaiting.Count} still watching");
    }

    /// <summary>Server, every frame: the run ends when everyone's credits are over (or after a grace period).</summary>
    private static void ServerUpdate()
    {
        if (!creditsRunning || !NetworkServer.active)
        {
            return;
        }
        float now = Time.unscaledTime;
        creditsWaiting.RemoveWhere(b => b == null);
        bool done = creditsWaiting.Count == 0
            || (firstCreditsDone >= 0f && now - firstCreditsDone > Tables2.Dragon["credits_grace"])
            || now - creditsStart > Tables2.Dragon["credits_max"];
        if (!done)
        {
            return;
        }
        creditsRunning = false;
        if (Run.instance == null)
        {
            return;
        }
        DragonWin = true;
        Plugin.Log.LogInfo("stages: the credits are over: the run ends in victory");
        Run.instance.BeginGameOver(RoR2Content.GameEndings.MainEnding);
    }

    // ---- every player's game ----

    /// <summary>The server's word on a stage: "&lt;stage net id&gt;:&lt;mode&gt;".</summary>
    public static void ClientMode(string message)
    {
        int colon = (message ?? "").IndexOf(':');
        if (colon <= 0 || !uint.TryParse(message!.Substring(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out uint stageId))
        {
            Plugin.Log.LogWarning($"stages: bad stage mode message '{message}'");
            return;
        }
        mode = message.Substring(colon + 1);
        modeStageId = stageId;
        string title = mode.Length == 0 ? "an ordinary RoR2 stage" : Tables2.CustomStages.TryGetValue(mode, out Tables2.CustomStage s) ? s.Title : mode;
        Plugin.Log.LogInfo($"stages: stage {stageId} is {title}");
    }

    /// <summary>Every frame: once this player's Minecraft and body are ready, build the custom stage.</summary>
    public static void Update(bool linked, CharacterBody? body, Retexture retexture)
    {
        ServerUpdate();
        if (!Known || appliedStageId == modeStageId)
        {
            return;
        }
        Tables2.CustomStage? stage = Current;
        if (stage == null)
        {
            appliedStageId = modeStageId; // an ordinary stage: nothing to build
            return;
        }
        if (!linked || body == null)
        {
            return;
        }
        appliedStageId = modeStageId;
        Plugin.Log.LogInfo($"stages: building {stage.Title} in this player's Minecraft");
        Chat.AddMessage($"<color=#c4a3ff><size=120%>{stage.Title}</size></color>  <color=#a0a0a0>{stage.Subtitle}</color>");
        retexture.Begin(stage);
        Vector3 centre = body.footPosition;
        var random = new System.Random((int)(Run.instance != null ? Run.instance.seed : 1UL) ^ Run.instance!.stageClearCount * 7919);
        if (stage.Decor == "nether")
        {
            foreach (Vector3 spot in ChestSpawner.GroundSpots(28, random, centre, 6f))
            {
                World.Coords.ToMc(spot, out double x, out double y, out double z);
                Commands.Push(Commands.Format("decor", ("kind", "nether"), ("x", F(x)), ("y", F(y)), ("z", F(z)), ("seed", random.Next())));
            }
        }
        else if (stage.Decor == "end")
        {
            // The podium is a bedrock column: it stands on open ground away from where players land.
            System.Collections.Generic.List<Vector3> spots = ChestSpawner.GroundSpots(1, random, centre, 14f);
            Vector3 podium = spots.Count > 0 ? spots[0] : centre + body.transform.forward * 16f;
            World.Coords.ToMc(podium, out double x, out double y, out double z);
            int players = Mathf.Max(1, NetworkUser.readOnlyInstancesList.Count);
            float difficulty = Run.instance != null ? Run.instance.difficultyCoefficient : 1f;
            float health = Tables2.Dragon["base_health"] * (1f + Tables2.Dragon["health_per_difficulty"] * Mathf.Max(0f, difficulty - 1f))
                * (1f + Tables2.Dragon["health_per_extra_player"] * (players - 1));
            Commands.Push(Commands.Format("end_arena", ("x", F(x)), ("y", F(y)), ("z", F(z)), ("pillars", (int)Tables2.Dragon["pillars"]),
                ("radius", F(Tables2.Dragon["pillar_radius"])), ("caged", (int)Tables2.Dragon["caged_pillars"])));
            Commands.Push(Commands.Format("summon_dragon", ("x", F(x)), ("y", F(y)), ("z", F(z)), ("health", F(health))));
            Plugin.Log.LogInfo($"stages: Ender Dragon with {health:F0} health (difficulty {difficulty:F2}, {players} players)");
        }
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
