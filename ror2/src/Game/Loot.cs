using RiskOfCraft.Net;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace RiskOfCraft.Game;

/// <summary>
/// Server side: RoR2 chests, barrels and shrines also roll a real Minecraft loot table into the
/// Steve's Minecraft inventory, and monsters a Steve kills drop their Minecraft mob loot and XP
/// (design/gameplay.json loot_chests and monsters). RoR2's own drops are untouched.
/// </summary>
internal static class Loot
{
    public static void Init()
    {
        GlobalEventManager.OnInteractionsGlobal += OnInteraction;
        GlobalEventManager.onCharacterDeathServer += OnDeath;
    }

    private static string BaseName(string name)
    {
        int clone = name.IndexOf("(Clone)", System.StringComparison.Ordinal);
        return (clone >= 0 ? name.Substring(0, clone) : name).Trim();
    }

    private static void OnInteraction(Interactor interactor, IInteractable interactable, GameObject target)
    {
        if (!NetworkServer.active || interactor == null || target == null)
        {
            return;
        }
        CharacterBody body = interactor.GetComponent<CharacterBody>();
        if (!SteveRegistry.IsSteve(body))
        {
            return;
        }
        string name = BaseName(target.name);
        if (Tables.ChestLoot.TryGetValue(name, out string table))
        {
            SteveNet.SendCommand(body, Commands.Format("loot", ("table", table)));
            Plugin.Log.LogInfo($"loot: {body.GetUserName()} opened {name}: Minecraft loot table {table}");
        }
    }

    private static void OnDeath(DamageReport report)
    {
        if (!NetworkServer.active || report == null || !SteveRegistry.IsSteve(report.attackerBody) || report.victimTeamIndex == TeamIndex.Player)
        {
            return;
        }
        string name = BodyCatalog.GetBodyName(report.victimBodyIndex) ?? "";
        if (!Tables.MonsterLoot.TryGetValue(name, out (string table, int xp) row))
        {
            row = Tables.MonsterLoot["*"];
        }
        int xp = report.victimIsElite ? row.xp * 2 : row.xp;
        SteveNet.SendCommand(report.attackerBody, Commands.Format("loot", ("table", row.table)));
        SteveNet.SendCommand(report.attackerBody, Commands.Format("xp", ("points", xp)));
        Plugin.Log.LogInfo($"loot: {report.attackerBody.GetUserName()} killed {name}: Minecraft loot table {row.table}, {xp} XP");
    }
}
