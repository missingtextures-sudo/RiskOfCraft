using System.Collections.Generic;
using RoR2;
using UnityEngine;

namespace RiskOfCraft.Game;

/// <summary>
/// RoR2 levels and items scale Steve's real Minecraft attributes (design/gameplay.json scaling):
/// max health, movement speed, attack speed and jump strength follow the body's stat over its
/// base, as commands to this player's Minecraft whenever a value moves enough.
/// </summary>
internal sealed class Scaling
{
    private readonly Dictionary<string, float> sent = new();
    private float nextCheck;

    public void Reset() => sent.Clear();

    public void Update(bool linked, CharacterBody? body)
    {
        if (!linked || body == null || Time.unscaledTime < nextCheck)
        {
            return;
        }
        nextCheck = Time.unscaledTime + 0.5f;
        foreach (Tables.ScalingRow row in Tables.Scaling)
        {
            float stat = Stat(body, row.Ror2Stat), baseStat = Stat(body, row.Ror2Base);
            float value;
            if (row.Mode == "add")
            {
                value = row.McBase + stat; // luck: RoR2's 57 Leaf Clover is Minecraft's luck (better loot rolls)
            }
            else if (baseStat <= 0f || stat <= 0f)
            {
                continue;
            }
            else
            {
                value = row.McBase * stat / baseStat;
            }
            if (sent.TryGetValue(row.Id, out float last) && Mathf.Abs(last - value) < row.MinChange)
            {
                continue;
            }
            sent[row.Id] = value;
            Commands.Push(Commands.Format(row.Command, ("attribute", row.McTarget), ("value", value)));
        }
    }

    private static float Stat(CharacterBody body, string name) => name switch
    {
        "maxHealth" => body.maxHealth,
        "baseMaxHealth" => body.baseMaxHealth,
        "moveSpeed" => body.moveSpeed,
        "baseMoveSpeed" => body.baseMoveSpeed,
        "attackSpeed" => body.attackSpeed,
        "baseAttackSpeed" => body.baseAttackSpeed,
        "jumpPower" => body.jumpPower,
        "baseJumpPower" => body.baseJumpPower,
        "luck" => body.master != null ? body.master.luck : 0f,
        _ => 0f,
    };
}
