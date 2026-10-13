using System.Text;
using RiskOfCraft.Game;
using RoR2;
using RoR2.Networking;
using UnityEngine;
using UnityEngine.Networking;

namespace RiskOfCraft.Net;

/// <summary>
/// RiskOfCraft's messages between each player's game and the host's (design/gameplay.json
/// messages). Every player has their own Minecraft; the host's game decides hits, loot and kills,
/// so it tells each player's game what to do to that player's Minecraft. In single player the same
/// messages go through UNet's local connection.
/// </summary>
internal static class SteveNet
{
    private sealed class LinkedMsg : MessageBase
    {
        public bool linked;
        public override void Serialize(NetworkWriter w) => w.Write(linked);
        public override void Deserialize(NetworkReader r) => linked = r.ReadBoolean();
    }

    private sealed class HitMsg : MessageBase
    {
        public uint target, flags, weapon;
        public float mcDamage;
        public Vector3 hitPos, knockback;

        public override void Serialize(NetworkWriter w)
        {
            w.Write(target);
            w.Write(mcDamage);
            w.Write(flags);
            w.Write(weapon);
            w.Write(hitPos);
            w.Write(knockback);
        }

        public override void Deserialize(NetworkReader r)
        {
            target = r.ReadUInt32();
            mcDamage = r.ReadSingle();
            flags = r.ReadUInt32();
            weapon = r.ReadUInt32();
            hitPos = r.ReadVector3();
            knockback = r.ReadVector3();
        }
    }

    private sealed class EmptyMsg : MessageBase
    {
        public override void Serialize(NetworkWriter w) { }
        public override void Deserialize(NetworkReader r) { }
    }

    private sealed class CommandMsg : MessageBase
    {
        public string command = "";
        public override void Serialize(NetworkWriter w) => w.Write(command);
        public override void Deserialize(NetworkReader r) => command = r.ReadString();
    }

    private sealed class HurtMsg : MessageBase
    {
        public float mcDamage;
        public ushort kind;
        public uint attacker;

        public override void Serialize(NetworkWriter w)
        {
            w.Write(mcDamage);
            w.Write(kind);
            w.Write(attacker);
        }

        public override void Deserialize(NetworkReader r)
        {
            mcDamage = r.ReadSingle();
            kind = r.ReadUInt16();
            attacker = r.ReadUInt32();
        }
    }

    private sealed class UIntMsg : MessageBase
    {
        public uint value;
        public override void Serialize(NetworkWriter w) => w.Write(value);
        public override void Deserialize(NetworkReader r) => value = r.ReadUInt32();
    }

    private sealed class OfferMsg : MessageBase
    {
        public uint terminal;
        public string rareId = "";

        public override void Serialize(NetworkWriter w)
        {
            w.Write(terminal);
            w.Write(rareId);
        }

        public override void Deserialize(NetworkReader r)
        {
            terminal = r.ReadUInt32();
            rareId = r.ReadString();
        }
    }

    public static void Init()
    {
        NetworkManagerSystem.onStartServerGlobal += RegisterServer;
        NetworkManagerSystem.onStartClientGlobal += RegisterClient;
    }

    private static void RegisterServer()
    {
        NetworkServer.RegisterHandler(Tables.MsgSteveLinked, msg =>
        {
            var m = msg.ReadMessage<LinkedMsg>();
            SteveRegistry.SetLinked(SenderBody(msg.conn), m.linked);
        });
        NetworkServer.RegisterHandler(Tables.MsgSteveHit, msg =>
        {
            var m = msg.ReadMessage<HitMsg>();
            Combat.ServerHit(SenderBody(msg.conn), m.target, m.mcDamage, m.flags, m.weapon, m.hitPos, m.knockback);
        });
        NetworkServer.RegisterHandler(Tables.MsgSteveDied, msg =>
        {
            msg.ReadMessage<EmptyMsg>();
            SteveRegistry.ServerKill(SenderBody(msg.conn));
        });
        NetworkServer.RegisterHandler(Tables.MsgStevePortal, msg =>
        {
            var m = msg.ReadMessage<UIntMsg>();
            CustomStages.ServerPortal(SenderBody(msg.conn), m.value);
        });
        NetworkServer.RegisterHandler(Tables.MsgDragonKilled, msg =>
        {
            msg.ReadMessage<EmptyMsg>();
            CustomStages.ServerDragonKilled(SenderBody(msg.conn));
        });
        NetworkServer.RegisterHandler(Tables.MsgSetSpawns, msg =>
        {
            var m = msg.ReadMessage<UIntMsg>();
            SpawnToggle.ServerSet(SenderBody(msg.conn), m.value != 0);
        });
        NetworkServer.RegisterHandler(Tables.MsgCreditsDone, msg =>
        {
            msg.ReadMessage<EmptyMsg>();
            CustomStages.ServerCreditsDone(SenderBody(msg.conn));
        });
        Plugin.Log.LogInfo("net: server handlers registered");
    }

    private static void RegisterClient(NetworkClient client)
    {
        client.RegisterHandler(Tables.MsgMcCommand, msg => Commands.Push(msg.ReadMessage<CommandMsg>().command));
        client.RegisterHandler(Tables.MsgSteveHurt, msg =>
        {
            var m = msg.ReadMessage<HurtMsg>();
            Combat.ClientHurt(m.mcDamage, m.kind, m.attacker);
        });
        client.RegisterHandler(Tables.MsgStageMode, msg => CustomStages.ClientMode(msg.ReadMessage<CommandMsg>().command));
        client.RegisterHandler(Tables.MsgShopOffer, msg =>
        {
            var m = msg.ReadMessage<OfferMsg>();
            LunarShop.ClientOffer(m.terminal, m.rareId);
        });
        Plugin.Log.LogInfo("net: client handlers registered");
    }

    /// <summary>The body of the player whose game sent this message.</summary>
    private static CharacterBody? SenderBody(NetworkConnection conn)
    {
        foreach (NetworkUser user in NetworkUser.readOnlyInstancesList)
        {
            if (user != null && user.connectionToClient == conn)
            {
                return user.GetCurrentBody();
            }
        }
        return null;
    }

    private static NetworkConnection? OwnerConnection(CharacterBody body)
    {
        NetworkUser? user = Util.LookUpBodyNetworkUser(body);
        return user != null ? user.connectionToClient : null;
    }

    // ---- client -> server ----

    private static void ToServer(short type, MessageBase msg)
    {
        NetworkConnection? conn = ClientScene.readyConnection;
        if (conn != null)
        {
            conn.Send(type, msg);
        }
    }

    public static void SendLinked(bool linked) => ToServer(Tables.MsgSteveLinked, new LinkedMsg { linked = linked });

    public static void SendHit(uint target, float mcDamage, uint flags, uint weapon, Vector3 hitPos, Vector3 knockback)
        => ToServer(Tables.MsgSteveHit, new HitMsg { target = target, mcDamage = mcDamage, flags = flags, weapon = weapon, hitPos = hitPos, knockback = knockback });

    public static void SendDied() => ToServer(Tables.MsgSteveDied, new EmptyMsg());

    public static void SendPortal(uint kind) => ToServer(Tables.MsgStevePortal, new UIntMsg { value = kind });

    public static void SendDragonKilled() => ToServer(Tables.MsgDragonKilled, new EmptyMsg());

    public static void SendCreditsDone() => ToServer(Tables.MsgCreditsDone, new EmptyMsg());

    public static void SendSetSpawns(bool on) => ToServer(Tables.MsgSetSpawns, new UIntMsg { value = on ? 1u : 0u });

    // ---- server -> every player's game ----

    public static void SendStageModeToAll(uint stageId, string mode) => NetworkServer.SendToAll(Tables.MsgStageMode, new CommandMsg { command = stageId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + mode });

    public static void SendOfferToAll(uint terminal, string rareId) => NetworkServer.SendToAll(Tables.MsgShopOffer, new OfferMsg { terminal = terminal, rareId = rareId });

    // ---- server -> one player's game ----

    public static void SendCommand(CharacterBody body, string command)
    {
        NetworkConnection? conn = OwnerConnection(body);
        if (conn != null && Encoding.UTF8.GetByteCount(command) < 184)
        {
            conn.Send(Tables.MsgMcCommand, new CommandMsg { command = command });
        }
    }

    public static void SendHurt(CharacterBody body, float mcDamage, ushort kind, uint attacker)
    {
        NetworkConnection? conn = OwnerConnection(body);
        conn?.Send(Tables.MsgSteveHurt, new HurtMsg { mcDamage = mcDamage, kind = kind, attacker = attacker });
    }
}
