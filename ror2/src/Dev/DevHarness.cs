using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Path = System.IO.Path;
using System.Linq;
using System.Text;
using BepInEx;
using RiskOfCraft.Game;
using RiskOfCraft.Link;
using RiskOfCraft.World;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace RiskOfCraft.Dev;

/// <summary>
/// Development only ([Dev] Harness = true; off for players). Polls BepInEx/riskofcraft-dev.txt and
/// runs its lines in order, one command each, so every in-game check is a script and a screenshot:
///   host | startrun | console &lt;cmd&gt; | sleep &lt;s&gt; | shot &lt;name&gt; | probe | mc &lt;command&gt;
///   key &lt;scancode&gt; &lt;1|0&gt; | mouse &lt;button&gt; &lt;1|0&gt; | look &lt;yaw&gt; &lt;pitch&gt; | aim_nearest
///   interact &lt;s&gt; | hurt &lt;mc&gt; | goto_chest | log &lt;text&gt;
/// </summary>
internal sealed class DevHarness
{
    private readonly Plugin plugin;
    private readonly string commandFile = Path.Combine(Paths.BepInExRootPath, "riskofcraft-dev.txt");
    private readonly string shotDir = Path.Combine(Paths.BepInExRootPath, "riskofcraft-shots");
    private readonly Queue<string> queue = new();
    private float nextPoll;
    private float sleepUntil;
    private string? waitScene;
    private float waitUntil;
    private bool waitBody;
    private int wantView = -1;      // view <mode>: F5 until Minecraft's camera mode matches
    private float viewNextPress, viewGiveUp;

    public DevHarness(Plugin plugin)
    {
        this.plugin = plugin;
        Directory.CreateDirectory(shotDir);
        Plugin.Log.LogWarning($"dev harness ON: commands from {commandFile}, screenshots to {shotDir}");
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Runs these lines as if they came from the command file (Melty's test runs).</summary>
    public void Enqueue(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            queue.Enqueue(line);
        }
    }

    public void Update()
    {
        if (Time.unscaledTime >= nextPoll)
        {
            nextPoll = Time.unscaledTime + 0.5f;
            try
            {
                if (File.Exists(commandFile))
                {
                    string[] lines = File.ReadAllLines(commandFile);
                    File.Delete(commandFile);
                    foreach (string line in lines)
                    {
                        if (line.Trim().Length > 0 && !line.TrimStart().StartsWith("#"))
                        {
                            queue.Enqueue(line.Trim());
                        }
                    }
                }
            }
            catch (IOException)
            {
                // being written; next poll
            }
        }
        if (wantView >= 0)
        {
            if (plugin.Guest.cameraMode == (uint)wantView || Time.unscaledTime > viewGiveUp)
            {
                Plugin.Log.LogInfo($"dev: view {plugin.Guest.cameraMode}{(plugin.Guest.cameraMode == (uint)wantView ? "" : $" (wanted {wantView}; gave up)")}");
                wantView = -1;
            }
            else
            {
                if (Time.unscaledTime >= viewNextPress)
                {
                    viewNextPress = Time.unscaledTime + 0.4f;
                    plugin.Link.PushInput(Proto.InputType.Key, Input.Scancodes.F5, 1);
                    plugin.Link.PushInput(Proto.InputType.Key, Input.Scancodes.F5, 0);
                }
                return;
            }
        }
        if (waitScene != null || waitBody)
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            bool done = waitBody ? plugin.LocalBody != null && plugin.Ownership.State == Player.Owner.MinecraftOwns : scene == waitScene;
            if (!done && Time.unscaledTime < waitUntil)
            {
                return;
            }
            Plugin.Log.LogInfo(done ? $"dev: wait done (scene {scene})" : $"dev: wait timed out (scene {scene}, {plugin.Ownership.State})");
            waitScene = null;
            waitBody = false;
        }
        while (queue.Count > 0 && Time.unscaledTime >= sleepUntil && waitScene == null && !waitBody)
        {
            string line = queue.Dequeue();
            try
            {
                Run(line);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"dev: '{line}' failed: {e}");
            }
        }
    }

    private void Run(string line)
    {
        string[] a = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string rest = line.Length > a[0].Length ? line.Substring(a[0].Length).Trim() : "";
        Plugin.Log.LogInfo($"dev> {line}");
        CharacterBody? body = plugin.LocalBody;
        switch (a[0].ToLowerInvariant())
        {
            case "log":
                Plugin.Log.LogInfo("dev: " + rest);
                break;
            case "waitscene":
                waitScene = a[1];
                waitUntil = Time.unscaledTime + (a.Length > 2 ? F(a[2]) : 120f);
                break;
            case "waitbody":
                waitBody = true;
                waitUntil = Time.unscaledTime + (a.Length > 1 ? F(a[1]) : 120f);
                break;
            case "sleep":
                sleepUntil = Time.unscaledTime + F(a[1]);
                break;
            case "shot":
            {
                string path = Path.Combine(shotDir, (a.Length > 1 ? a[1] : DateTime.Now.ToString("HHmmss")) + ".png");
                ScreenCapture.CaptureScreenshot(path);
                Plugin.Log.LogInfo($"dev: screenshot {path}");
                break;
            }
            case "console":
                RoR2.Console.instance.SubmitCmd((NetworkUser)null!, rest);
                break;
            case "host":
                if (LocalUserManager.readOnlyLocalUsersList.Count == 0)
                {
                    // The title screen's profile picker: sign in the last-used profile, as a click would.
                    RoR2.Console.instance.SubmitCmd((NetworkUser)null!, "test_splitscreen 1");
                }
                RoR2.Console.instance.SubmitCmd((NetworkUser)null!, "host 0");
                break;
            case "startrun":
                if (PreGameController.instance != null && NetworkServer.active)
                {
                    PreGameController.instance.StartRun(); // what the lobby's countdown ends in
                }
                else
                {
                    Plugin.Log.LogWarning("dev: no lobby to start a run from (use 'host' first)");
                }
                break;
            case "mc":
                Commands.Push(rest);
                break;
            case "key":
                plugin.Link.PushInput(Proto.InputType.Key, ushort.Parse(a[1], CultureInfo.InvariantCulture), int.Parse(a[2], CultureInfo.InvariantCulture));
                break;
            case "mouse":
                plugin.Link.PushInput(Proto.InputType.MouseButton, ushort.Parse(a[1], CultureInfo.InvariantCulture), int.Parse(a[2], CultureInfo.InvariantCulture));
                break;
            case "scroll":
                plugin.Link.PushInput(Proto.InputType.Scroll, 0, int.Parse(a[1], CultureInfo.InvariantCulture) * 120);
                break;
            case "look":
                plugin.Camera.Yaw = F(a[1]);
                plugin.Camera.Pitch = F(a[2]);
                break;
            case "aim_nearest":
                AimNearest(body);
                break;
            case "interact":
                plugin.InputBridge.DevInteractUntil = Time.unscaledTime + F(a[1]);
                break;
            case "hurt":
                Combat.ClientHurt(F(a[1]), (ushort)Proto.HurtKind.Other, 0);
                break;
            case "goto":
                GotoChest(body, a.Length > 1 ? a[1] : "Chest1", teleport: true);
                break;
            case "aim":
                GotoChest(body, a.Length > 1 ? a[1] : "Chest1", teleport: false);
                break;
            case "exitpod":
                // What pressing interact in the drop pod does (RoR2 asks the seat to let you out).
                if (body != null && body.currentVehicle != null && NetworkServer.active)
                {
                    body.currentVehicle.OnInteractionBegin(body.GetComponent<Interactor>());
                }
                break;
            case "blockshader":
                plugin.Blocks.Atlas.ForceShader(rest);
                plugin.Blocks.Sections.Rematerial(plugin.Blocks.Atlas);
                break;
            case "stage":
                // stage <scene> [stages cleared before it]: the server sends the run there.
                if (NetworkServer.active && RoR2.Run.instance != null)
                {
                    if (a.Length > 2)
                    {
                        RoR2.Run.instance.stageClearCount = int.Parse(a[2], CultureInfo.InvariantCulture);
                    }
                    RoR2.Run.instance.AdvanceStage(SceneCatalog.FindSceneDef(a[1]));
                }
                break;
            case "goto_spot":
            {
                Vector3? spot = a[1] == "endframe" ? Game.ChestSpawner.EndFrame : a[1] == "villagers" ? Game.LunarShop.VillagerSpot : Game.ChestSpawner.LastChests.Count > 0 ? Game.ChestSpawner.LastChests[0] : null;
                if (spot == null || body == null)
                {
                    Plugin.Log.LogInfo($"dev: no {a[1]} spot this stage");
                    break;
                }
                Vector3 target = spot.Value + new Vector3(3.5f, 0.3f, 0f);
                TeleportHelper.TeleportBody(body, target);
                plugin.Ownership.RequestTeleport();
                Vector3 dir = spot.Value + Vector3.up * 0.4f - (target + Vector3.up * 1.6f);
                Quaternion look = Quaternion.LookRotation(dir);
                plugin.Camera.Yaw = Coords.McYawFromUnity(look.eulerAngles.y);
                plugin.Camera.Pitch = Mathf.DeltaAngle(0f, look.eulerAngles.x);
                Plugin.Log.LogInfo($"dev: at the {a[1]} {spot.Value:F1}");
                break;
            }
            case "face_spot":
            {
                Vector3? spot = a[1] == "endframe" ? Game.ChestSpawner.EndFrame : a[1] == "villagers" ? Game.LunarShop.VillagerSpot : Game.ChestSpawner.LastChests.Count > 0 ? Game.ChestSpawner.LastChests[0] : null;
                if (spot != null)
                {
                    Quaternion look = Quaternion.LookRotation(spot.Value + Vector3.up * 0.4f - plugin.Camera.Eye);
                    plugin.Camera.Yaw = Coords.McYawFromUnity(look.eulerAngles.y);
                    plugin.Camera.Pitch = Mathf.DeltaAngle(0f, look.eulerAngles.x);
                }
                break;
            }
            case "mc_at_spot":
            {
                // mc_at_spot <chest|endframe> <command with {x} {y} {z}>: a Minecraft command at the spot's block.
                Vector3? spot = a[1] == "endframe" ? Game.ChestSpawner.EndFrame : a[1] == "villagers" ? Game.LunarShop.VillagerSpot : Game.ChestSpawner.LastChests.Count > 0 ? Game.ChestSpawner.LastChests[0] : null;
                if (spot != null)
                {
                    // The same block StageBuilder built on: the first cell standing on the ground (y sent + 0.05).
                    Coords.ToMc(spot.Value, out double x, out double y, out double z);
                    string cmd = string.Join(" ", a.Skip(2)).Replace("{x}", ((int)System.Math.Floor(x)).ToString(CultureInfo.InvariantCulture))
                        .Replace("{y}", ((int)System.Math.Ceiling(y + 0.05 - 0.15)).ToString(CultureInfo.InvariantCulture)).Replace("{z}", ((int)System.Math.Floor(z)).ToString(CultureInfo.InvariantCulture));
                    Commands.Push(cmd);
                    Plugin.Log.LogInfo($"dev: mc {cmd}");
                }
                break;
            }
            case "goto_terminal":
            {
                // The nearest RiskOfCraft Bazaar terminal: stand in front of it, facing it.
                Game.LunarShop.Offer? offer = UnityEngine.Object.FindObjectsOfType<Game.LunarShop.Offer>().OrderBy(o => body != null ? (o.transform.position - body.footPosition).sqrMagnitude : 0f).FirstOrDefault();
                if (offer == null || body == null)
                {
                    Plugin.Log.LogInfo("dev: no Minecraft terminal here");
                    break;
                }
                Vector3 target = offer.transform.position + offer.transform.forward * 2.5f + Vector3.up * 0.3f;
                TeleportHelper.TeleportBody(body, target);
                plugin.Ownership.RequestTeleport();
                Quaternion look = Quaternion.LookRotation(offer.transform.position + Vector3.up * 1.2f - (target + Vector3.up * 1.6f));
                plugin.Camera.Yaw = Coords.McYawFromUnity(look.eulerAngles.y);
                plugin.Camera.Pitch = Mathf.DeltaAngle(0f, look.eulerAngles.x);
                Plugin.Log.LogInfo($"dev: at the {offer.RareId} terminal");
                break;
            }
            case "buy_mc":
            {
                // A paid purchase at the nearest Minecraft terminal, without spending the profile's lunar coins.
                Game.LunarShop.Offer? offer = UnityEngine.Object.FindObjectsOfType<Game.LunarShop.Offer>().OrderBy(o => body != null ? (o.transform.position - body.footPosition).sqrMagnitude : 0f).FirstOrDefault();
                if (offer != null && body != null && NetworkServer.active)
                {
                    offer.Buyer = body;
                    offer.GetComponent<ShopTerminalBehavior>().DropPickup();
                }
                break;
            }
            case "dumptex":
            {
                // Every model texture as received (and a copy with see-through texels in magenta).
                foreach (uint id in plugin.Blocks.Atlas.TextureIds.ToList())
                {
                    Texture2D? t = plugin.Blocks.Atlas.Texture(id);
                    if (id == 0 || t == null || t.width > 512)
                    {
                        continue;
                    }
                    File.WriteAllBytes(Path.Combine(shotDir, $"tex_{id}.png"), t.EncodeToPNG());
                    var copy = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false);
                    Color32[] px = t.GetPixels32();
                    int clear = 0;
                    for (int i = 0; i < px.Length; i++)
                    {
                        if (px[i].a < 26)
                        {
                            px[i] = new Color32(255, 0, 255, 255);
                            clear++;
                        }
                        else
                        {
                            px[i].a = 255;
                        }
                    }
                    copy.SetPixels32(px);
                    File.WriteAllBytes(Path.Combine(shotDir, $"tex_{id}_alpha.png"), copy.EncodeToPNG());
                    UnityEngine.Object.Destroy(copy);
                    Plugin.Log.LogInfo($"dev: texture {id} {t.width}x{t.height}, {clear} see-through texels");
                }
                break;
            }
            case "view":
                // view <0 first person | 1 behind | 2 in front>
                wantView = int.Parse(a[1], CultureInfo.InvariantCulture);
                viewNextPress = 0f;
                viewGiveUp = Time.unscaledTime + 5f;
                break;
            case "item":
                // item <ItemDef name> [count]: the server gives the local body RoR2 items.
                if (body != null && NetworkServer.active)
                {
                    ItemIndex item = ItemCatalog.FindItemIndex(a[1]);
                    int count = a.Length > 2 ? int.Parse(a[2], CultureInfo.InvariantCulture) : 1;
                    if (item != ItemIndex.None)
                    {
                        body.inventory.GiveItem(item, count);
                    }
                    Plugin.Log.LogInfo($"dev: item {a[1]} x{count} {(item != ItemIndex.None ? "given" : "not found")}");
                }
                break;
            case "equip":
                // equip <EquipmentDef name>: the local body's equipment.
                if (body != null && NetworkServer.active)
                {
                    EquipmentIndex equipment = EquipmentCatalog.FindEquipmentIndex(a[1]);
                    if (equipment != EquipmentIndex.None)
                    {
                        body.inventory.SetEquipmentIndex(equipment);
                    }
                    Plugin.Log.LogInfo($"dev: equipment {a[1]} {(equipment != EquipmentIndex.None ? "set" : "not found")}");
                }
                break;
            case "useequip":
                plugin.InputBridge.DevEquipmentUntil = Time.unscaledTime + F(a[1]);
                break;
            case "stats":
                if (body != null)
                {
                    Plugin.Log.LogInfo($"dev: stats hp {body.maxHealth:F0} (base {body.baseMaxHealth:F0}) speed {body.moveSpeed:F2} attack {body.attackSpeed:F2} damage {body.damage:F1} crit {body.crit:F0}% armor {body.armor:F0} luck {(body.master != null ? body.master.luck : 0f):F0}; equipment stock {body.equipmentSlot?.stock} cooldown {body.equipmentSlot?.cooldownTimer:F1}");
                }
                break;
            case "hudtree":
                // What is still drawn of RoR2's HUD near the hotbar: every active label with a short key text.
                foreach (RoR2.UI.HUD hud in RoR2.UI.HUD.readOnlyInstanceList)
                {
                    foreach (TMPro.TMP_Text label in hud.GetComponentsInChildren<TMPro.TMP_Text>(false))
                    {
                        if (label.text != null && label.text.Trim().Length is > 0 and <= 3)
                        {
                            Transform t = label.transform;
                            string path = t.name;
                            for (int up = 0; up < 5 && t.parent != null; up++)
                            {
                                t = t.parent;
                                path = t.name + "/" + path;
                            }
                            Plugin.Log.LogInfo($"dev: hud label '{label.text.Trim()}' at {path}");
                        }
                    }
                }
                break;
            case "mcmenu":
                // Minecraft's pause menu, as the menu key opens it.
                plugin.Link.PushInput(Proto.InputType.OpenMenu);
                break;
            case "timescale":
                Plugin.Log.LogInfo($"dev: timeScale {Time.timeScale:F2}, RoR2 paused {(RoR2.PauseStopController.instance != null && RoR2.PauseStopController.instance.isPaused)}");
                break;
            case "meshprobe":
            {
                // Can RoR2's world meshes be cut? Readable, statically batched, and their collision meshes.
                int world = LayerIndex.world.intVal;
                int renderers = 0, readable = 0, batched = 0, batchedReadable = 0;
                long tris = 0;
                var seen = new HashSet<Mesh>();
                foreach (MeshRenderer r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
                {
                    Collider? c = r.GetComponent<Collider>();
                    if (r.gameObject.layer != world && (c == null || c.gameObject.layer != world))
                    {
                        continue;
                    }
                    MeshFilter? f = r.GetComponent<MeshFilter>();
                    Mesh? m = f != null ? f.sharedMesh : null;
                    if (m == null)
                    {
                        continue;
                    }
                    renderers++;
                    if (r.isPartOfStaticBatch)
                    {
                        batched++;
                        batchedReadable += m.isReadable ? 1 : 0;
                    }
                    if (m.isReadable)
                    {
                        readable++;
                    }
                    if (seen.Add(m))
                    {
                        for (int sub = 0; sub < m.subMeshCount; sub++)
                        {
                            tris += m.GetIndexCount(sub) / 3;
                        }
                    }
                }
                int colliders = 0, collidersReadable = 0;
                foreach (MeshCollider mc in UnityEngine.Object.FindObjectsOfType<MeshCollider>())
                {
                    if (mc.gameObject.layer != world || mc.sharedMesh == null)
                    {
                        continue;
                    }
                    colliders++;
                    collidersReadable += mc.sharedMesh.isReadable ? 1 : 0;
                }
                Plugin.Log.LogInfo($"dev: meshes: {renderers} world renderers, {readable} readable, {batched} statically batched ({batchedReadable} readable), {seen.Count} distinct meshes, {tris} triangles; {colliders} world mesh colliders, {collidersReadable} readable");
                break;
            }
            case "check":
            {
                // check <name>: one of Melty's test checks, judged on the game's state now.
                string name = a[1];
                (bool pass, string why) = name switch
                {
                    "minecraft-linked" => (plugin.Linked, "the Rust link to Minecraft is down"),
                    "steve-in-run" => (body != null && plugin.Ownership.State == Player.Owner.MinecraftOwns, $"no body under Minecraft's control ({plugin.Ownership.State})"),
                    "minecraft-hud" => (plugin.Overlay.HasFrame, "no frame from Minecraft's HUD yet"),
                    "chests-placed" => (Game.ChestSpawner.LastChests.Count > 0, "no Minecraft chests were placed on the stage"),
                    "dig-cuts" => (plugin.Cutter.CellsCut > 0, "digging cut nothing out of RoR2's ground"),
                    _ => (false, "unknown check"),
                };
                if (plugin.Melty != null)
                {
                    plugin.Melty.Report(name, pass, why);
                }
                else
                {
                    Plugin.Log.LogInfo($"dev: check {name}: {(pass ? "pass" : "fail: " + why)}");
                }
                break;
            }
            case "probe":
                Plugin.Log.LogInfo($"probe: blocks: {plugin.Blocks.Sections.Describe()}");
                Probe(body);
                break;
            default:
                Plugin.Log.LogWarning($"dev: unknown command {a[0]}");
                break;
        }
    }

    private void AimNearest(CharacterBody? body)
    {
        if (body == null)
        {
            return;
        }
        CharacterBody? best = null;
        float bestD = float.MaxValue;
        foreach (CharacterBody other in CharacterBody.readOnlyInstancesList)
        {
            if (other == null || other == body || other.teamComponent == null || other.teamComponent.teamIndex == body.teamComponent.teamIndex || !other.healthComponent.alive)
            {
                continue;
            }
            float d = (other.corePosition - body.corePosition).sqrMagnitude;
            if (d < bestD)
            {
                bestD = d;
                best = other;
            }
        }
        if (best == null)
        {
            Plugin.Log.LogInfo("dev: no monster to aim at");
            return;
        }
        Vector3 dir = best.corePosition - plugin.Camera.Eye;
        Quaternion look = Quaternion.LookRotation(dir);
        plugin.Camera.Yaw = Coords.McYawFromUnity(look.eulerAngles.y);
        plugin.Camera.Pitch = Mathf.DeltaAngle(0f, look.eulerAngles.x);
        Plugin.Log.LogInfo($"dev: aiming at {best.name} {Mathf.Sqrt(bestD):F1} m away");
    }

    /// <summary>Teleports next to the nearest interactable whose name starts with <paramref name="prefix"/> and looks at it.</summary>
    private void GotoChest(CharacterBody? body, string prefix, bool teleport)
    {
        if (body == null)
        {
            return;
        }
        Transform? best = null;
        float bestD = float.MaxValue;
        foreach (MonoBehaviour b in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
        {
            if (b is not IInteractable || !b.name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            float d = (b.transform.position - body.corePosition).sqrMagnitude;
            if (d < bestD)
            {
                bestD = d;
                best = b.transform;
            }
        }
        if (best == null)
        {
            Plugin.Log.LogInfo($"dev: nothing named {prefix}* found");
            return;
        }
        Vector3 to = best.position + best.forward * 2.2f + Vector3.up * 0.2f;
        if (teleport)
        {
            TeleportHelper.TeleportBody(body, to);
            plugin.Ownership.RequestTeleport();
        }
        else
        {
            to = body.footPosition;
        }
        Vector3 dir = best.position + Vector3.up * 0.4f - (to + Vector3.up * 1.6f);
        Quaternion look = Quaternion.LookRotation(dir);
        plugin.Camera.Yaw = Coords.McYawFromUnity(look.eulerAngles.y);
        plugin.Camera.Pitch = Mathf.DeltaAngle(0f, look.eulerAngles.x);
        Plugin.Log.LogInfo($"dev: teleported to {best.name} at {best.position}");
    }

    private static void Probe(CharacterBody? body)
    {
        var sb = new StringBuilder("probe:");
        sb.Append($" scene {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name};");
        if (body != null)
        {
            CharacterMotor motor = body.characterMotor;
            sb.Append($" body {body.name} at {body.transform.position:F2}, feet {body.footPosition:F2}, capsule {(motor != null ? motor.capsuleHeight : 0f):F2} m, radius {body.radius:F2}, aimOrigin {body.inputBank.aimOrigin:F2};");
            sb.Append($" stats hp {body.maxHealth:F0}/{body.baseMaxHealth:F0} speed {body.moveSpeed:F2}/{body.baseMoveSpeed:F2} attack {body.attackSpeed:F2}/{body.baseAttackSpeed:F2} damage {body.damage:F1} level {body.level:F0};");
            if (Physics.Raycast(body.corePosition, Vector3.down, out RaycastHit hit, 50f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
            {
                SurfaceDef sd = SurfaceDefProvider.GetObjectSurfaceDef(hit.collider, hit.point);
                sb.Append($" ground {hit.point.y:F2} ({hit.collider.name}, layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}, surface {(sd != null ? sd.name : "none")});");
                // What draws that ground: the renderer on (or around) the collider and its materials.
                Renderer? groundRenderer = hit.collider.GetComponent<Renderer>() ?? hit.collider.GetComponentInParent<Renderer>() ?? hit.collider.GetComponentInChildren<Renderer>();
                if (groundRenderer != null)
                {
                    sb.Append($" drawn by {groundRenderer.name} layer {LayerMask.LayerToName(groundRenderer.gameObject.layer)}: {string.Join(", ", groundRenderer.sharedMaterials.Where(m => m != null).Select(m => $"{m.name} ({m.shader.name})"))};");
                }
                else
                {
                    sb.Append(" no renderer on the ground collider;");
                }
            }
        }
        Plugin.Log.LogInfo(sb.ToString());
        if (body != null)
        {
            var shown = new List<string>();
            foreach (Renderer r in body.GetComponentsInChildren<Renderer>(includeInactive: false))
            {
                if (r.enabled && !r.forceRenderingOff)
                {
                    shown.Add($"{r.name} ({r.GetType().Name}, layer {LayerMask.LayerToName(r.gameObject.layer)}, parent {r.transform.parent?.name})");
                }
            }
            Plugin.Log.LogInfo("probe: body renderers still drawn: " + string.Join("; ", shown));
        }
        var surfaces = new Dictionary<string, int>();
        foreach (SurfaceDefProvider p in UnityEngine.Object.FindObjectsOfType<SurfaceDefProvider>())
        {
            string n = p.surfaceDef != null ? p.surfaceDef.name : "none";
            surfaces.TryGetValue(n, out int c);
            surfaces[n] = c + 1;
        }
        Plugin.Log.LogInfo("probe: surfaces in the stage: " + string.Join(", ", surfaces.OrderByDescending(p => p.Value).Select(p => $"{p.Key}={p.Value}")));
        var bodies = new HashSet<string>();
        foreach (CharacterBody b in CharacterBody.readOnlyInstancesList)
        {
            bodies.Add(BodyCatalog.GetBodyName(b.bodyIndex));
        }
        Plugin.Log.LogInfo("probe: bodies in the stage: " + string.Join(", ", bodies));
    }
}
