using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using RiskOfCraft.Game;
using RiskOfCraft.Link;
using RiskOfCraft.Player;
using UnityEngine;

namespace RiskOfCraft.Input;

/// <summary>
/// Reads the raw keyboard and mouse and re-encodes them for Minecraft's input ring: keys as SDL
/// scancodes, mouse buttons as SDL numbers (1 left, 2 middle, 3 right, 4/5 extra). The host
/// integrates the mouse into the authoritative look, with Minecraft's own sensitivity curve.
/// A few keys stay with Risk of Rain 2 (design/gameplay.json keybinds): interact, equipment, Esc.
/// The receiving end is fabric/.../client/InputBridge.java.
/// </summary>
internal sealed class InputBridge
{
    private readonly HostLink link;
    private readonly CameraDriver camera;
    private readonly KeyCode interactKey;
    private readonly KeyCode equipmentKey;
    private readonly KeyCode menuKey;
    private readonly ConfigEntry<float> mouseScale;
    private readonly KeyCode[] keys;

    private readonly HashSet<KeyCode> held = new();
    private readonly List<KeyCode> scratch = new();
    private readonly bool[] buttons = new bool[6];
    private bool routing;
    private bool screenOpen;
    private float cursorX, cursorY;
    private float sensitivity = 0.5f;
    private KeyCode repeatKey = KeyCode.None;
    private float repeatAt;
    private bool textBroken;

    public InputBridge(HostLink link, CameraDriver camera, ConfigFile config)
    {
        this.link = link;
        this.camera = camera;
        interactKey = Parse(config.Bind("Keys", "Interact", Tables.KeyInteract, "RoR2's interact: chests, shrines, the teleporter, printers. Hold where RoR2 wants a hold.").Value, KeyCode.G);
        equipmentKey = Parse(config.Bind("Keys", "Equipment", Tables.KeyEquipment, "Uses your RoR2 equipment.").Value, KeyCode.R);
        menuKey = Parse(config.Bind("Keys", "MinecraftMenu", Tables.KeyMcMenu, "Opens Minecraft's pause and options menu. Esc opens RoR2's menu, or closes an open Minecraft screen first.").Value, KeyCode.O);
        mouseScale = config.Bind("Keys", "MouseScale", 1.0f, "Multiplies mouse movement before Minecraft's sensitivity curve (Minecraft's own sensitivity setting also applies).");
        keys = (KeyCode[])Enum.GetValues(typeof(KeyCode));
    }

    private static KeyCode Parse(string name, KeyCode fallback) => Enum.TryParse(name, true, out KeyCode key) ? key : fallback;

    /// <summary>True while a Minecraft screen (inventory, chat, pause) is open: the overlay cursor is shown.</summary>
    public bool ScreenOpen => routing && screenOpen;
    public Vector2 Cursor => new(cursorX, cursorY);
    public string InteractKeyName => interactKey.ToString();
    public string EquipmentKeyName => equipmentKey.ToString();
    public bool InteractHeld { get; private set; }
    public bool EquipmentHeld { get; private set; }

    /// <summary>Dev harness: hold interact for a while without a keyboard.</summary>
    public float DevInteractUntil;

    /// <summary>Dev harness: hold the equipment key until this time.</summary>
    public float DevEquipmentUntil;

    public void Update(Ownership ownership, in Proto.GuestState guest)
    {
        bool route = ownership.State == Owner.MinecraftOwns && Application.isFocused;
        if (route != routing)
        {
            routing = route;
            ReleaseAll();
            Plugin.Log.LogInfo(route ? "input: keyboard and mouse go to Minecraft" : "input: released (release-all sent)");
        }
        if (!route)
        {
            ReleaseOsCursor();
            // Unfocused, the player's keys don't count; the dev harness's held keys still do.
            bool owns = ownership.State == Owner.MinecraftOwns;
            InteractHeld = owns && Time.unscaledTime < DevInteractUntil;
            EquipmentHeld = owns && Time.unscaledTime < DevEquipmentUntil;
            return;
        }
        if (guest.sensitivity > 0f)
        {
            sensitivity = guest.sensitivity;
        }

        bool nowOpen = (guest.flags & (uint)Proto.GuestFlags.ScreenOpen) != 0;
        if (nowOpen && !screenOpen)
        {
            cursorX = Screen.width * 0.5f;
            cursorY = Screen.height * 0.5f;
            link.PushInput(Proto.InputType.Cursor, 0, (int)cursorX, (int)cursorY);
            OpenOsCursor();
        }
        else if (!nowOpen && screenOpen)
        {
            ReleaseOsCursor();
        }
        screenOpen = nowOpen;

        Vector2 delta = MouseDelta() * mouseScale.Value;
        if (screenOpen)
        {
            if (UsingOsCursor)
            {
                // Windows' own cursor (its speed and acceleration, as on the desktop), free while the
                // screen is open: its position is Minecraft's cursor. Unity's origin is bottom left.
                Vector3 mouse = UnityEngine.Input.mousePosition;
                float x = Mathf.Clamp(mouse.x, 0f, Screen.width - 1), y = Mathf.Clamp(Screen.height - 1 - mouse.y, 0f, Screen.height - 1);
                if ((int)x != (int)cursorX || (int)y != (int)cursorY)
                {
                    cursorX = x;
                    cursorY = y;
                    link.PushInput(Proto.InputType.Cursor, 0, (int)cursorX, (int)cursorY);
                }
            }
            else if (delta != Vector2.zero)
            {
                // Overlay pixels, origin top left; the mouse delta has Y up.
                cursorX = Mathf.Clamp(cursorX + delta.x, 0f, Screen.width - 1);
                cursorY = Mathf.Clamp(cursorY - delta.y, 0f, Screen.height - 1);
                link.PushInput(Proto.InputType.Cursor, 0, (int)cursorX, (int)cursorY);
            }
            foreach (char c in TypedText())
            {
                if (c >= ' ' && c != 127)
                {
                    link.PushInput(Proto.InputType.Text, 0, c);
                }
            }
        }
        else
        {
            // Minecraft's own mouse curve (MouseHandler.turnPlayer): (s * 0.6 + 0.2)^3 * 8, times 0.15 degrees.
            float s = sensitivity * 0.6f + 0.2f;
            float factor = s * s * s * 8f * 0.15f;
            camera.Yaw = Mathf.DeltaAngle(0f, camera.Yaw + delta.x * factor);
            camera.Pitch = Mathf.Clamp(camera.Pitch - delta.y * factor, -90f, 90f);
        }

        PumpKeys();
        for (int b = 0; b < 5; b++)
        {
            // Unity: 0 left, 1 right, 2 middle, 3/4 extra. SDL: 1 left, 2 middle, 3 right, 4/5 extra.
            int sdl = b switch { 0 => 1, 1 => 3, 2 => 2, _ => b + 1 };
            PumpButton(sdl, UnityEngine.Input.GetMouseButton(b));
        }
        float scroll = UnityEngine.Input.mouseScrollDelta.y;
        if (scroll != 0f)
        {
            link.PushInput(Proto.InputType.Scroll, 0, scroll > 0f ? 120 : -120);
        }
    }

    /// <summary>Raw mouse movement in counts this frame (Rewired, which RoR2 reads its mouse with).</summary>
    private readonly List<RoR2.UI.MPEventSystem> cursorOpened = new();

    /// <summary>True while RoR2 has freed the Windows cursor for an open Minecraft screen.</summary>
    public bool UsingOsCursor => cursorOpened.Count > 0 && UnityEngine.Cursor.lockState != CursorLockMode.Locked;

    /// <summary>A Minecraft screen opened: RoR2 frees and shows the Windows cursor (as its own menus do).</summary>
    private void OpenOsCursor()
    {
        if (cursorOpened.Count > 0)
        {
            return;
        }
        foreach (RoR2.UI.MPEventSystem events in RoR2.UI.MPEventSystem.readOnlyInstancesList)
        {
            if (events != null)
            {
                events.cursorOpenerCount++;
                cursorOpened.Add(events);
            }
        }
    }

    private void ReleaseOsCursor()
    {
        foreach (RoR2.UI.MPEventSystem events in cursorOpened)
        {
            if (events != null && events.cursorOpenerCount > 0)
            {
                events.cursorOpenerCount--;
            }
        }
        cursorOpened.Clear();
    }

    private static Vector2 MouseDelta()
    {
        Rewired.Mouse? mouse = Rewired.ReInput.isReady ? Rewired.ReInput.controllers.Mouse : null;
        if (mouse != null)
        {
            return new Vector2(mouse.GetAxisRaw(0), mouse.GetAxisRaw(1));
        }
        return new Vector2(UnityEngine.Input.GetAxisRaw("Mouse X"), UnityEngine.Input.GetAxisRaw("Mouse Y")) * 10f;
    }

    private string TypedText()
    {
        if (textBroken)
        {
            return "";
        }
        try
        {
            return UnityEngine.Input.inputString;
        }
        catch (Exception e)
        {
            textBroken = true;
            Plugin.Log.LogWarning($"input: no text input from Unity ({e.Message}); typing in Minecraft's chat is off");
            return "";
        }
    }

    private void PumpKeys()
    {
        bool interactDown = Time.unscaledTime < DevInteractUntil;
        bool equipmentDown = Time.unscaledTime < DevEquipmentUntil;
        foreach (KeyCode key in keys)
        {
            if (key == KeyCode.None || key >= KeyCode.Mouse0)
            {
                continue; // mouse and joystick "keys" are not keyboard keys
            }
            bool down = UnityEngine.Input.GetKey(key);
            if (!down && !held.Contains(key))
            {
                continue;
            }
            // With a Minecraft screen up every key is Minecraft's, so typing works and Esc closes it.
            if (!screenOpen)
            {
                if (key == KeyCode.Escape)
                {
                    continue; // RoR2's pause menu
                }
                if (key == interactKey)
                {
                    interactDown |= down;
                    continue;
                }
                if (key == equipmentKey)
                {
                    equipmentDown |= down;
                    continue;
                }
                if (key == menuKey)
                {
                    if (down && held.Add(key))
                    {
                        ReleaseKeys();
                        held.Add(key);
                        link.PushInput(Proto.InputType.OpenMenu);
                    }
                    else if (!down)
                    {
                        held.Remove(key);
                    }
                    continue;
                }
            }
            if (!Scancodes.FromKey.TryGetValue(key, out ushort scancode))
            {
                continue;
            }
            if (down && held.Add(key))
            {
                link.PushInput(Proto.InputType.Key, scancode, 1);
                repeatKey = key;
                repeatAt = Time.unscaledTime + 0.4f;
            }
            else if (!down && held.Remove(key))
            {
                link.PushInput(Proto.InputType.Key, scancode, 0);
                if (repeatKey == key)
                {
                    repeatKey = KeyCode.None;
                }
            }
        }
        // Key repeat for Minecraft's text fields (held backspace, arrows): a second "down" is a repeat there.
        if (screenOpen && repeatKey != KeyCode.None && Time.unscaledTime >= repeatAt && Scancodes.FromKey.TryGetValue(repeatKey, out ushort repeat))
        {
            repeatAt = Time.unscaledTime + 0.04f;
            link.PushInput(Proto.InputType.Key, repeat, 1);
        }
        InteractHeld = interactDown;
        EquipmentHeld = equipmentDown;
    }

    private void PumpButton(int sdlButton, bool down)
    {
        if (down != buttons[sdlButton])
        {
            buttons[sdlButton] = down;
            link.PushInput(Proto.InputType.MouseButton, (ushort)sdlButton, down ? 1 : 0);
        }
    }

    private void ReleaseKeys()
    {
        scratch.Clear();
        scratch.AddRange(held);
        foreach (KeyCode key in scratch)
        {
            if (Scancodes.FromKey.TryGetValue(key, out ushort scancode))
            {
                link.PushInput(Proto.InputType.Key, scancode, 0);
            }
        }
        held.Clear();
        repeatKey = KeyCode.None;
    }

    /// <summary>Input focus left Minecraft (RoR2 menu, cutscene, alt-tab, link loss): lift everything.</summary>
    public void ReleaseAll()
    {
        held.Clear();
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i] = false;
        }
        repeatKey = KeyCode.None;
        screenOpen = false;
        link.PushInput(Proto.InputType.ReleaseAll);
    }
}
