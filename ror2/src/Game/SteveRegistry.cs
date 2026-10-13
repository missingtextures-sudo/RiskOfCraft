using System.Collections.Generic;
using RiskOfCraft.Net;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace RiskOfCraft.Game;

/// <summary>
/// Server side: which players' bodies are Steves linked to a running Minecraft. While linked,
/// Minecraft's health is the only health: RoR2 never kills the body by itself (Buddha flag), hits
/// and healing are forwarded to that player's Minecraft, and a Minecraft death kills the body.
/// </summary>
internal static class SteveRegistry
{
    private static readonly HashSet<CharacterBody> linked = new();
    private static readonly Dictionary<CharacterBody, float> pendingHeal = new();
    private static float lastFlush;

    public static bool IsSteve(CharacterBody? body) => body != null && body.isPlayerControlled;

    public static bool IsLinkedSteve(CharacterBody? body) => body != null && linked.Contains(body);

    public static void SetLinked(CharacterBody? body, bool on)
    {
        if (!NetworkServer.active || body == null)
        {
            return;
        }
        bool changed = on ? linked.Add(body) : linked.Remove(body);
        if (on)
        {
            body.bodyFlags |= CharacterBody.BodyFlags.Buddha;
        }
        else
        {
            body.bodyFlags &= ~CharacterBody.BodyFlags.Buddha;
        }
        if (changed)
        {
            Plugin.Log.LogInfo($"steves: {body.GetUserName()} {(on ? "linked to Minecraft (Minecraft's health is theirs)" : "unlinked (RoR2's health is theirs)")}");
        }
    }

    /// <summary>The player's Minecraft died: their RoR2 body dies too.</summary>
    public static void ServerKill(CharacterBody? body)
    {
        if (!NetworkServer.active || body == null || body.healthComponent == null || !body.healthComponent.alive)
        {
            return;
        }
        linked.Remove(body);
        body.bodyFlags &= ~CharacterBody.BodyFlags.Buddha;
        Plugin.Log.LogInfo($"steves: {body.GetUserName()} died in Minecraft; killing their body");
        body.healthComponent.Suicide();
    }

    /// <summary>RoR2 healing on a linked Steve, in Minecraft health points.</summary>
    public static void AddHeal(CharacterBody body, float mcHealth)
    {
        pendingHeal.TryGetValue(body, out float sum);
        pendingHeal[body] = sum + mcHealth;
    }

    /// <summary>Server, every frame: forget dead bodies; send summed healing at most every half second.</summary>
    public static void Update()
    {
        if (!NetworkServer.active)
        {
            linked.Clear();
            pendingHeal.Clear();
            return;
        }
        linked.RemoveWhere(b => b == null);
        if (Time.unscaledTime - lastFlush < Tables.HealBatchSeconds || pendingHeal.Count == 0)
        {
            return;
        }
        lastFlush = Time.unscaledTime;
        foreach (KeyValuePair<CharacterBody, float> pair in pendingHeal)
        {
            if (pair.Key != null && pair.Value >= 0.05f && linked.Contains(pair.Key))
            {
                SteveNet.SendCommand(pair.Key, Commands.Format("heal", ("amount", pair.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))));
            }
        }
        pendingHeal.Clear();
    }
}
