using RiskOfCraft.Link;
using RiskOfCraft.Net;
using RiskOfCraft.World;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace RiskOfCraft.Game;

/// <summary>
/// Hits both ways. Minecraft's weapons hit invisible proxies of RoR2's monsters (the guest's
/// SkyrimActorEntity, fed by ActorExporter); each hit comes back as an event and becomes RoR2
/// damage on the host's server, scaled by the Steve's RoR2 damage stat (level and items) and
/// proccing RoR2's on-hit items. RoR2's hits on a linked Steve arrive as Minecraft damage
/// (HealthPatches -> SteveNet -> ClientHurt).
/// </summary>
internal static class Combat
{
    private static int hitsLogged;

    // ---- this player's game ----

    /// <summary>A Minecraft hit on a RoR2 monster's proxy (guest event HitActor).</summary>
    public static void OnGuestHit(in Proto.GuestEvent e)
    {
        CharacterBody? local = Plugin.Instance.LocalBody;
        if (local == null)
        {
            return;
        }
        Vector3 at = local.corePosition;
        GameObject? target = ClientScene.FindLocalObject(new NetworkInstanceId(e.formId));
        if (target != null && target.TryGetComponent(out CharacterBody victim))
        {
            at = victim.corePosition;
        }
        // Knockback direction (Minecraft x/z) and strength.
        Vector3 knock = new Vector3(e.b, 0f, -e.c).normalized * e.d;
        SteveNet.SendHit(e.formId, e.a, e.flags, e.weapon, at, knock);
    }

    /// <summary>The host's game hit this player's Steve: Minecraft takes it (armour, shield, hurt sound).</summary>
    public static void ClientHurt(float mcDamage, ushort kind, uint attacker)
    {
        if (!Plugin.Instance.Linked || mcDamage <= 0f)
        {
            return;
        }
        // The guest divides the host value by hurt_divisor (SkyCombat.SKYRIM_TO_MC_DAMAGE).
        float host = mcDamage * Tables.HurtDivisor;
        Plugin.Instance.Link.PushInput(Proto.InputType.Hurt, kind, Mathf.RoundToInt(host * 100f), (int)attacker, 0);
    }

    // ---- the host's server ----

    public static void ServerHit(CharacterBody? steve, uint target, float mcDamage, uint flags, uint weapon, Vector3 hitPos, Vector3 knockback)
    {
        if (!NetworkServer.active || steve == null || !steve.healthComponent.alive || mcDamage <= 0f || mcDamage > 1000f)
        {
            return;
        }
        GameObject? victimObject = NetworkServer.FindLocalObject(new NetworkInstanceId(target));
        if (victimObject == null || !victimObject.TryGetComponent(out CharacterBody victim) || victim.healthComponent == null || !victim.healthComponent.alive)
        {
            return;
        }
        if (victim.teamComponent.teamIndex == steve.teamComponent.teamIndex)
        {
            return;
        }
        float reach = Tables.MaxHitDistance + victim.radius + (((Proto.HitFlags)flags & Proto.HitFlags.Projectile) != 0 ? 120f : 0f);
        if ((victim.corePosition - steve.corePosition).sqrMagnitude > reach * reach)
        {
            return;
        }
        var info = new DamageInfo
        {
            attacker = steve.gameObject,
            inflictor = steve.gameObject,
            damage = mcDamage * steve.damage * Tables.OutPerMc,
            crit = steve.RollCrit(),
            position = hitPos,
            force = knockback * 400f,
            procCoefficient = Tables.ProcCoefficient,
            damageType = DamageType.Generic,
            damageColorIndex = DamageColorIndex.Default,
            procChainMask = default,
        };
        victim.healthComponent.TakeDamage(info);
        GlobalEventManager.instance.OnHitEnemy(info, victim.gameObject);
        GlobalEventManager.instance.OnHitAll(info, victim.gameObject);
        if (hitsLogged++ < 20)
        {
            Plugin.Log.LogInfo($"combat: {steve.GetUserName()} hit {victim.name} with {(Proto.HitWeapon)weapon} for {mcDamage:F1} Minecraft = {info.damage:F1} RoR2{(info.crit ? " (crit)" : "")}; health now {victim.healthComponent.health:F0}/{victim.healthComponent.fullHealth:F0}");
        }
    }

    /// <summary>How a RoR2 hit reached the Steve, for Minecraft's damage source.</summary>
    public static ushort Kind(DamageInfo info)
    {
        if (info.dotIndex != DotController.DotIndex.None)
        {
            return (ushort)Proto.HurtKind.Other;
        }
        if (info.attacker != null && info.inflictor != null && info.inflictor != info.attacker)
        {
            return (ushort)Proto.HurtKind.Projectile;
        }
        return info.attacker != null ? (ushort)Proto.HurtKind.Melee : (ushort)Proto.HurtKind.Other;
    }

    public static uint AttackerId(DamageInfo info)
    {
        if (info.attacker != null && info.attacker.TryGetComponent(out NetworkIdentity id))
        {
            return id.netId.Value;
        }
        return 0;
    }
}
