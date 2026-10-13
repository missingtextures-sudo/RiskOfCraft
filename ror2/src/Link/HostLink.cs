using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace RiskOfCraft.Link;

/// <summary>
/// Risk of Rain 2's end of the link to Minecraft. The shared memory itself (mapping, seqlocks,
/// rings, triple buffer, clocks) lives in the Rust library riskofcraft_link.dll next to this
/// plugin; this class is its typed C# face. Struct layouts come from the same design sheets as
/// the Rust code (Generated/Proto.g.cs and link/src/proto.rs).
/// </summary>
internal sealed unsafe class HostLink
{
    // Shipped as .bin, not .dll: BepInEx tries every .dll in plugins as a .NET plugin and logs an
    // error for a native one. Loaded by full path first; the imports then bind to that module.
    private const string Dll = "riskofcraft_link.bin";

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport(Dll)] private static extern uint rc_version();
    [DllImport(Dll)] private static extern uint rc_self_test();
    [DllImport(Dll)] private static extern int rc_struct_size(byte* name, uint len);
    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern uint rc_create(string name);
    [DllImport(Dll)] private static extern void rc_close();
    [DllImport(Dll)] private static extern uint rc_last_error();
    [DllImport(Dll)] private static extern ulong rc_tick_ms();
    [DllImport(Dll)] private static extern long rc_qpc();
    [DllImport(Dll)] private static extern long rc_qpc_frequency();
    [DllImport(Dll)] private static extern void rc_heartbeat();
    [DllImport(Dll)] private static extern uint rc_guest_alive(ulong timeoutMs);
    [DllImport(Dll)] private static extern uint rc_guest_pid();
    [DllImport(Dll)] private static extern void rc_write_host_state(Proto.HostState* state);
    [DllImport(Dll)] private static extern uint rc_read_guest_state(Proto.GuestState* state);
    [DllImport(Dll)] private static extern void rc_write_actors(Proto.ActorRecord* records, uint count);
    [DllImport(Dll)] private static extern uint rc_read_world_entities(Proto.WorldEntity* records, uint max, uint* count, uint* hasSelection, float* selection);
    [DllImport(Dll)] private static extern uint rc_push_input(Proto.InputEvent* e);
    [DllImport(Dll)] private static extern uint rc_pop_event(Proto.GuestEvent* e);
    [DllImport(Dll)] private static extern uint rc_push_command(byte* text, uint len);
    [DllImport(Dll)] private static extern uint rc_write_collision(uint type, void* payload, uint bytes);
    [DllImport(Dll)] private static extern ulong rc_collision_backlog();
    [DllImport(Dll)] private static extern uint rc_render_peek(uint* type, byte** payload, uint* bytes);
    [DllImport(Dll)] private static extern ulong rc_render_consume();
    [DllImport(Dll)] private static extern uint rc_overlay_acquire();
    [DllImport(Dll)] private static extern void rc_overlay_reset();
    [DllImport(Dll)] private static extern void rc_overlay_front_header(Proto.OverlaySlotHdr* header);
    [DllImport(Dll)] private static extern byte* rc_overlay_front_pixels();

    public delegate void MessageHandler(uint type, byte* payload, uint payloadBytes);

    private readonly ManualLogSource log;
    private bool created;
    private Proto.OverlaySlotHdr front;

    public HostLink(ManualLogSource log)
    {
        this.log = log;
    }

    /// <summary>
    /// Loads riskofcraft_link.dll from the plugin's folder (Mono only looks next to the game), then
    /// checks it speaks this plugin's protocol, that its structs match ours, and that its rings
    /// pass their wrap-around self-test.
    /// </summary>
    public static bool Load(string pluginDir, ManualLogSource log)
    {
        string path = Path.Combine(pluginDir, Dll);
        if (LoadLibraryW(path) == IntPtr.Zero)
        {
            log.LogError($"link: could not load {path} (Windows error {Marshal.GetLastWin32Error()})");
            return false;
        }
        // An install from before 0.2.0 left riskofcraft_link.dll here: BepInEx would keep logging it.
        string stale = Path.Combine(pluginDir, "riskofcraft_link.dll");
        try
        {
            if (File.Exists(stale))
            {
                File.Delete(stale);
                log.LogInfo("link: removed riskofcraft_link.dll from an older install");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning($"link: couldn't remove {stale}: {e.Message}");
        }
        uint version = rc_version();
        if (version != Proto.Version)
        {
            log.LogError($"link: riskofcraft_link.dll speaks protocol {version}, this plugin {Proto.Version}");
            return false;
        }
        bool ok = true;
        foreach ((string name, int expected, int actual) in Proto.Sizes)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(name);
            int rust;
            fixed (byte* p = utf8)
            {
                rust = rc_struct_size(p, (uint)utf8.Length);
            }
            if (rust != expected || actual != expected)
            {
                ok = false;
                log.LogError($"layout FAIL sizeof({name}): sheet {expected}, C# {actual}, Rust {rust}");
            }
        }
        foreach ((Type type, string field, int expected) in Proto.Offsets)
        {
            int actual = Marshal.OffsetOf(type, field).ToInt32();
            if (actual != expected)
            {
                ok = false;
                log.LogError($"layout FAIL {type.Name}.{field}: sheet {expected}, C# {actual}");
            }
        }
        log.LogInfo(ok ? $"protocol layout self-check: PASS ({Proto.Sizes.Length} structs, {Proto.Offsets.Length} fields, C# and Rust)" : "protocol layout self-check: FAIL");
        uint rings = rc_self_test();
        if (rings != 0)
        {
            log.LogError($"ring self-test: FAIL (mask 0x{rings:X})");
            return false;
        }
        log.LogInfo("ring self-test: PASS (entry, byte and command rings, field offsets; in Rust)");
        return ok;
    }

    public bool Created => created;
    public long QpcFrequency { get; } = rc_qpc_frequency();
    public static ulong TickCount => rc_tick_ms();
    public static long Qpc() => rc_qpc();

    public bool Create(string name)
    {
        uint result = rc_create(name);
        if (result == 0)
        {
            log.LogError($"shared memory {name}: could not be created (Windows error {rc_last_error()})");
            return false;
        }
        created = true;
        log.LogInfo($"shared memory {name} ({Proto.MappingBytes >> 20} MB, {(result == 2 ? "reused" : "created")}) through the Rust link");
        return true;
    }

    public void Close()
    {
        if (created)
        {
            rc_close();
            created = false;
        }
    }

    public void Heartbeat() => rc_heartbeat();

    public bool GuestAlive => rc_guest_alive((ulong)Proto.GuestTimeoutMs) != 0;

    public uint GuestPid => rc_guest_pid();

    public void WriteHostState(Proto.HostState state) => rc_write_host_state(&state);

    public bool ReadGuestState(out Proto.GuestState state)
    {
        Proto.GuestState s;
        bool ok = rc_read_guest_state(&s) != 0;
        state = s;
        return ok;
    }

    public bool PushInput(Proto.InputType type, ushort code = 0, int a = 0, int b = 0, int c = 0)
    {
        var e = new Proto.InputEvent { type = (ushort)type, code = code, a = a, b = b, c = c };
        return rc_push_input(&e) != 0;
    }

    public bool PopEvent(out Proto.GuestEvent e)
    {
        Proto.GuestEvent ev;
        bool ok = rc_pop_event(&ev) != 0;
        e = ev;
        return ok;
    }

    /// <summary>Queues a Minecraft command for the guest (no leading slash).</summary>
    public bool PushCommand(string command)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(command);
        fixed (byte* p = utf8)
        {
            return rc_push_command(p, (uint)utf8.Length) != 0;
        }
    }

    public void WriteActors(Proto.ActorRecord[] records, int count)
    {
        fixed (Proto.ActorRecord* p = records)
        {
            rc_write_actors(p, (uint)count);
        }
    }

    public bool WriteCollision(uint type, void* payload, uint bytes) => rc_write_collision(type, payload, bytes) != 0;

    /// <summary>Bytes Minecraft has not consumed yet.</summary>
    public long CollisionBacklog => (long)rc_collision_backlog();

    /// <summary>Hands pending render messages to the handler, up to about maxBytes.</summary>
    public void DrainRender(long maxBytes, MessageHandler handler)
    {
        long done = 0;
        uint type, bytes;
        byte* payload;
        while (done < maxBytes && rc_render_peek(&type, &payload, &bytes) != 0)
        {
            handler(type, payload, bytes);
            done += (long)rc_render_consume();
        }
    }

    private readonly Proto.WorldEntity[] entityBuffer = new Proto.WorldEntity[256];
    private bool hasSelection;
    private Vector3 selMin, selMax;

    /// <summary>Seqlock copy of the world-entities table; false on a torn read (keep last frame's).</summary>
    public bool ReadWorldEntities(List<Proto.WorldEntity> output)
    {
        uint count, has;
        float* sel = stackalloc float[6];
        fixed (Proto.WorldEntity* p = entityBuffer)
        {
            if (rc_read_world_entities(p, (uint)entityBuffer.Length, &count, &has, sel) == 0)
            {
                return false;
            }
        }
        output.Clear();
        for (int i = 0; i < count; i++)
        {
            output.Add(entityBuffer[i]);
        }
        hasSelection = has != 0;
        selMin = new Vector3(sel[0], sel[1], sel[2]);
        selMax = new Vector3(sel[3], sel[4], sel[5]);
        return true;
    }

    /// <summary>The block-selection box from the last world-entities read (Minecraft coords).</summary>
    public bool ReadSelection(out Vector3 min, out Vector3 max)
    {
        min = selMin;
        max = selMax;
        return hasSelection;
    }

    // ---- overlay triple buffer ----

    public bool AcquireOverlayFrame()
    {
        if (rc_overlay_acquire() == 0)
        {
            return false;
        }
        fixed (Proto.OverlaySlotHdr* p = &front)
        {
            rc_overlay_front_header(p);
        }
        return true;
    }

    public void ResetOverlay() => rc_overlay_reset();

    public byte* FrontPixels => rc_overlay_front_pixels();

    public ref readonly Proto.OverlaySlotHdr FrontHeader => ref front;
}
