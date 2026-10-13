using System.Text;
using RiskOfCraft.Link;
using RoR2;
using UnityEngine;

namespace RiskOfCraft.World;

/// <summary>
/// RoR2's monsters near the player, published every frame as the protocol's actor table. The
/// guest mirrors each as an invisible, hittable proxy entity (SkyCraft's SkyrimActorEntity), so
/// Minecraft's sword swings, arrows and tridents can hit them; hits come back as HitActor events.
/// An actor's id is its body's network id, the same on every player's game.
/// </summary>
internal sealed unsafe class ActorExporter
{
    private const float Range = 72f;

    private readonly HostLink link;
    private readonly Proto.ActorRecord[] records = new Proto.ActorRecord[Proto.ActorTableEntries];
    private int lastCount = -1;

    public ActorExporter(HostLink link)
    {
        this.link = link;
    }

    public void Update(CharacterBody? local)
    {
        int count = 0;
        if (local != null)
        {
            Vector3 centre = local.corePosition;
            TeamIndex team = local.teamComponent != null ? local.teamComponent.teamIndex : TeamIndex.Player;
            foreach (CharacterBody body in CharacterBody.readOnlyInstancesList)
            {
                if (count >= records.Length)
                {
                    break;
                }
                if (body == null || body == local || body.teamComponent == null || body.teamComponent.teamIndex == team
                    || body.healthComponent == null || !body.healthComponent.alive || body.netId.Value == 0)
                {
                    continue;
                }
                if ((body.corePosition - centre).sqrMagnitude > Range * Range)
                {
                    continue;
                }
                Fill(ref records[count++], body);
            }
        }
        link.WriteActors(records, count);
        if (count != lastCount && (count == 0 || lastCount <= 0))
        {
            Plugin.Log.LogInfo($"actors: {count} RoR2 monsters near the player are hittable in Minecraft");
        }
        lastCount = count;
    }

    private static void Fill(ref Proto.ActorRecord r, CharacterBody body)
    {
        // The hittable shape: the main hurtbox's bounds (flyers and worms have no capsule).
        Bounds bounds = body.mainHurtBox != null && body.mainHurtBox.collider != null
            ? body.mainHurtBox.collider.bounds
            : new Bounds(body.corePosition, new Vector3(body.radius * 2f, body.radius * 2f, body.radius * 2f));
        Vector3 feet = new(bounds.center.x, bounds.min.y, bounds.center.z);
        Coords.ToMc(feet, out double x, out double y, out double z);
        r.formId = body.netId.Value;
        r.flags = (uint)Proto.ActorFlags.Hostile | (uint)Proto.ActorFlags.InCombat;
        r.x = (float)x;
        r.y = (float)y;
        r.z = (float)z;
        Vector3 forward = body.characterDirection != null ? body.characterDirection.forward : body.transform.forward;
        r.yaw = Coords.McYawFromUnity(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg);
        r.width = Mathf.Clamp(Mathf.Max(bounds.size.x, bounds.size.z), 0.3f, 12f);
        r.height = Mathf.Clamp(bounds.size.y, 0.3f, 16f);
        r.healthFrac = body.healthComponent.combinedHealthFraction;
        r.level = (ushort)Mathf.Clamp(body.level, 0f, 65535f);
        r.pad = 0;
        string name = body.GetDisplayName() ?? body.name;
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        fixed (byte* p = r.name)
        {
            int n = Mathf.Min(utf8.Length, 23);
            for (int i = 0; i < 24; i++)
            {
                p[i] = i < n ? utf8[i] : (byte)0;
            }
        }
    }
}
