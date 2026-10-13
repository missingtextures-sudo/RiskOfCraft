using System.Collections.Generic;
using System.Text;
using RiskOfCraft.Link;

namespace RiskOfCraft.Game;

/// <summary>
/// Minecraft commands for this player's own Minecraft (design/gameplay.json commands). Filled from
/// the sheet's templates and queued on the Rust link's command ring; the guest runs them as the
/// player. Commands that arrive while Minecraft isn't linked are kept (a few) and sent on link.
/// </summary>
internal static class Commands
{
    private static HostLink? link;
    private static readonly Queue<string> waiting = new();
    private const int MaxBytes = 184; // a command ring entry's text (design/protocol_structs.json HostCommand)

    public static void Init(HostLink hostLink) => link = hostLink;

    /// <summary>A sheet template with its {placeholders} filled.</summary>
    public static string Format(string id, params (string key, object value)[] values)
    {
        var sb = new StringBuilder(Tables.Commands[id]);
        foreach ((string key, object value) in values)
        {
            sb.Replace("{" + key + "}", value is float f ? f.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : value.ToString());
        }
        return sb.Replace("{{", "{").Replace("}}", "}").ToString(); // {{ }}: literal braces (SNBT)
    }

    public static void Push(string command)
    {
        if (Encoding.UTF8.GetByteCount(command) > MaxBytes)
        {
            Plugin.Log.LogError($"commands: too long for the command ring ({Encoding.UTF8.GetByteCount(command)} > {MaxBytes} bytes), dropped: {command}");
            return;
        }
        if (link == null || !Plugin.Instance.Linked)
        {
            if (waiting.Count < 256)
            {
                waiting.Enqueue(command);
            }
            return;
        }
        Flush();
        if (!link.PushCommand(command))
        {
            waiting.Enqueue(command); // ring full: next frame
        }
    }

    /// <summary>Every frame while linked: send what waited.</summary>
    public static void Flush()
    {
        if (link == null || !Plugin.Instance.Linked)
        {
            return;
        }
        while (waiting.Count > 0)
        {
            if (!link.PushCommand(waiting.Peek()))
            {
                return;
            }
            waiting.Dequeue();
        }
    }

    public static void Clear() => waiting.Clear();
}
