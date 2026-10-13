using System.Linq;
using RoR2;
using UnityEngine.Networking;

namespace RiskOfCraft.Game;

/// <summary>
/// Testing: /riskofcraft spawns off|on in Minecraft's chat stops RoR2's monster spawns (and clears
/// the monsters already out) or starts them again, through RoR2's own combat director switch
/// (director_combat_disable). A new run always starts with spawns on.
/// </summary>
internal static class SpawnToggle
{
    private static Run? offRun;

    public static void Init()
    {
        Run.onRunStartGlobal += _ =>
        {
            if (NetworkServer.active && CombatDirector.cvDirectorCombatDisable.value)
            {
                CombatDirector.cvDirectorCombatDisable.SetString("0");
                Plugin.Log.LogInfo("spawns: new run: monster spawns back on");
            }
            offRun = null;
        };
    }

    /// <summary>Server: a player asked for spawns on or off.</summary>
    public static void ServerSet(CharacterBody? body, bool on)
    {
        if (!NetworkServer.active || Run.instance == null)
        {
            return;
        }
        CombatDirector.cvDirectorCombatDisable.SetString(on ? "0" : "1");
        int cleared = 0;
        if (!on)
        {
            offRun = Run.instance;
            foreach (CharacterMaster master in CharacterMaster.readOnlyInstancesList.ToArray())
            {
                if (master != null && (master.teamIndex is TeamIndex.Monster or TeamIndex.Void or TeamIndex.Lunar) && master.hasBody)
                {
                    master.TrueKill();
                    cleared++;
                }
            }
        }
        string who = body != null ? body.GetUserName() : "A player";
        Plugin.Log.LogInfo($"spawns: {who} turned monster spawns {(on ? "on" : "off")}{(on ? "" : $" ({cleared} monsters cleared)")}");
        Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = $"<color=#ffd966>{who} turned monster spawns {(on ? "on" : "off")} (testing).</color>" });
    }
}
