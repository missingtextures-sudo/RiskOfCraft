using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RiskOfCraft.Net;
using RiskOfCraft.World;
using RoR2;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;

namespace RiskOfCraft.Game;

/// <summary>
/// Minecraft in the Bazaar Between Time (design/gameplay.json rare_items, lunar_shop, villagers).
/// The host's server puts a row of lunar terminals next to the Bazaar's own: each sells one of
/// Minecraft's rarest items for lunar coins, given straight into the buyer's Minecraft inventory.
/// Every player's game shows the item in its own Minecraft above each terminal, and stands master
/// villagers by the counter to trade with through Minecraft's own trading screen.
/// </summary>
internal static class LunarShop
{
    private const string TerminalKey = "RoR2/Base/LunarShopTerminal/LunarShopTerminal.prefab";
    public const string TokenPrefix = "RISKOFCRAFT_SHOP_";

    /// <summary>Server: a Minecraft terminal's item and who is buying it.</summary>
    public sealed class Offer : MonoBehaviour
    {
        public string RareId = "";
        public CharacterBody? Buyer;
    }

    private static GameObject? prefab;
    private static readonly Dictionary<uint, string> offers = new();   // terminal net id -> rare id (every game)
    private static readonly HashSet<uint> shown = new();
    private static Stage? villagersFor;

    /// <summary>Dev probe: the middle of the villagers' row (Unity space).</summary>
    public static Vector3? VillagerSpot;

    public static Tables2.RareItem? Rare(string id) => System.Array.Find(Tables2.RareItems, r => r.Id == id);

    public static void Init()
    {
        Stage.onStageStartGlobal += OnStageStart;
        // Every game must know the terminal prefab, so the server's spawned terminals appear there.
        RoR2.Networking.NetworkManagerSystem.onStartClientGlobal += _ =>
        {
            GameObject? p = Prefab();
            if (p != null)
            {
                ClientScene.RegisterPrefab(p);
            }
        };
    }

    private static bool InBazaar => SceneCatalog.mostRecentSceneDef != null && SceneCatalog.mostRecentSceneDef.baseSceneName == "bazaar";

    private static List<ShopTerminalBehavior> VanillaTerminals() => Object.FindObjectsOfType<ShopTerminalBehavior>()
        .Where(t => t.name.StartsWith("LunarShopTerminal") && t.GetComponent<Offer>() == null).ToList();

    /// <summary>All machines load the terminal prefab once so spawned terminals resolve everywhere.</summary>
    private static GameObject? Prefab()
    {
        if (prefab == null)
        {
            prefab = Addressables.LoadAssetAsync<GameObject>(TerminalKey).WaitForCompletion();
        }
        return prefab;
    }

    private static void OnStageStart(Stage stage)
    {
        offers.Clear();
        shown.Clear();
        if (!NetworkServer.active || !InBazaar)
        {
            return;
        }
        GameObject? terminal = Prefab();
        List<ShopTerminalBehavior> vanilla = VanillaTerminals();
        if (terminal == null || vanilla.Count == 0)
        {
            Plugin.Log.LogWarning($"shop: no Bazaar terminals to stand next to ({vanilla.Count}) or no prefab");
            return;
        }
        // The Bazaar's own terminals stand in a row; ours stand in a parallel row in front of them.
        Vector3 first = vanilla[0].transform.position, last = vanilla[vanilla.Count - 1].transform.position;
        Vector3 centre = vanilla.Aggregate(Vector3.zero, (s, t) => s + t.transform.position) / vanilla.Count;
        Vector3 along = (last - first).sqrMagnitude > 1f ? (last - first).normalized : vanilla[0].transform.right;
        along.y = 0f;
        along.Normalize();
        Vector3 front = vanilla[0].transform.forward;
        front.y = 0f;
        front = front.sqrMagnitude > 0.01f ? front.normalized : Vector3.Cross(Vector3.up, along);
        int count = Mathf.Min((int)Tables2.LunarShop["terminals"], Tables2.RareItems.Length);
        float spacing = Tables2.LunarShop["terminal_spacing"];
        var random = new System.Random((int)(Run.instance != null ? Run.instance.seed : 7UL) ^ (Run.instance != null ? Run.instance.stageClearCount : 0));
        List<Tables2.RareItem> picks = Tables2.RareItems.OrderBy(_ => random.Next()).Take(count).ToList();
        for (int i = 0; i < picks.Count; i++)
        {
            Vector3 at = centre + front * 6f + along * ((i - (picks.Count - 1) * 0.5f) * spacing) + Vector3.up * 5f;
            if (Physics.Raycast(at, Vector3.down, out RaycastHit hit, 20f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
            {
                at = hit.point;
            }
            GameObject made = Object.Instantiate(terminal, at, Quaternion.LookRotation(-front, Vector3.up));
            Offer offer = made.AddComponent<Offer>();
            offer.RareId = picks[i].Id;
            PurchaseInteraction purchase = made.GetComponent<PurchaseInteraction>();
            purchase.NetworkcostType = CostTypeIndex.LunarCoin;
            purchase.cost = picks[i].LunarCost;
            purchase.Networkcost = picks[i].LunarCost;
            purchase.displayNameToken = TokenPrefix + picks[i].Id.ToUpperInvariant();
            purchase.contextToken = TokenPrefix + "BUY_" + picks[i].Id.ToUpperInvariant();
            NetworkServer.Spawn(made);
            uint id = made.GetComponent<NetworkIdentity>().netId.Value;
            offers[id] = picks[i].Id;
            SteveNet.SendOfferToAll(id, picks[i].Id);
        }
        Plugin.Log.LogInfo($"shop: {picks.Count} Minecraft terminals in the Bazaar: {string.Join(", ", picks.Select(p => $"{p.DisplayName} ({p.LunarCost} lunar)"))}");
    }

    /// <summary>Every game: a terminal sells a Minecraft item.</summary>
    public static void ClientOffer(uint terminal, string rareId)
    {
        offers[terminal] = rareId;
    }

    /// <summary>Every game, every frame in the Bazaar: label our terminals, show their items, stand the villagers.</summary>
    public static void Update(bool linked, CharacterBody? body)
    {
        if (!InBazaar || !linked || body == null)
        {
            return;
        }
        foreach (KeyValuePair<uint, string> pair in offers)
        {
            if (shown.Contains(pair.Key))
            {
                continue;
            }
            GameObject? go = ClientScene.FindLocalObject(new NetworkInstanceId(pair.Key));
            Tables2.RareItem? rare = Rare(pair.Value);
            if (go == null || rare == null)
            {
                continue;
            }
            shown.Add(pair.Key);
            PurchaseInteraction purchase = go.GetComponent<PurchaseInteraction>();
            if (purchase != null)
            {
                purchase.displayNameToken = TokenPrefix + rare.Id.ToUpperInvariant();
                purchase.contextToken = TokenPrefix + "BUY_" + rare.Id.ToUpperInvariant();
            }
            ShopTerminalBehavior shop = go.GetComponent<ShopTerminalBehavior>();
            Vector3 top = (shop != null && shop.pickupDisplay != null ? shop.pickupDisplay.transform.position : go.transform.position + Vector3.up * 1.5f) + Vector3.up * 0.1f;
            Coords.ToMc(top, out double x, out double y, out double z);
            Commands.Push(Commands.Format("item_display", ("x", F(x)), ("y", F(y)), ("z", F(z)), ("item", rare.McItem)));
        }
        if (villagersFor != Stage.instance)
        {
            villagersFor = Stage.instance;
            StandVillagers();
        }
    }

    private static void StandVillagers()
    {
        List<ShopTerminalBehavior> vanilla = VanillaTerminals();
        if (vanilla.Count == 0)
        {
            return;
        }
        Vector3 centre = vanilla.Aggregate(Vector3.zero, (s, t) => s + t.transform.position) / vanilla.Count;
        Vector3 front = vanilla[0].transform.forward;
        front.y = 0f;
        front = front.sqrMagnitude > 0.01f ? front.normalized : Vector3.forward;
        Vector3 along = Vector3.Cross(Vector3.up, front);
        float spacing = Tables2.LunarShop["villager_spacing"];
        int placed = 0;
        VillagerSpot = null;
        for (int i = 0; i < Tables2.Villagers.Length; i++)
        {
            Tables2.Villager v = Tables2.Villagers[i];
            Vector3 at = centre + front * 11f + along * ((i - (Tables2.Villagers.Length - 1) * 0.5f) * spacing) + Vector3.up * 5f;
            if (!Physics.Raycast(at, Vector3.down, out RaycastHit hit, 20f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
            {
                continue;
            }
            if (i == Tables2.Villagers.Length / 2)
            {
                VillagerSpot = hit.point;
            }
            Coords.ToMc(hit.point, out double x, out double y, out double z);
            // Face the player coming in: towards the counter.
            float yaw = Coords.McYawFromUnity(Quaternion.LookRotation(-front).eulerAngles.y);
            Commands.Push(Commands.Format("villager", ("x", F(x)), ("y", F(y + 0.05)), ("z", F(z)), ("profession", v.Profession), ("level", v.Level), ("biome_type", v.BiomeType), ("yaw", F(yaw))));
            placed++;
        }
        Plugin.Log.LogInfo($"shop: {placed} villagers stand by the Bazaar counter");
    }

    /// <summary>Server: the buyer of a Minecraft terminal gets its item in their Minecraft.</summary>
    public static void Deliver(Offer offer)
    {
        Tables2.RareItem? rare = Rare(offer.RareId);
        if (rare == null || offer.Buyer == null)
        {
            return;
        }
        SteveNet.SendCommand(offer.Buyer, Commands.Format("give_rare", ("item", rare.McItem), ("count", rare.Count)));
        Plugin.Log.LogInfo($"shop: {offer.Buyer.GetUserName()} bought {rare.DisplayName} for {rare.LunarCost} lunar coins");
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
