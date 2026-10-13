using System.Collections.Generic;
using UnityEngine;

namespace RiskOfCraft.Input;

/// <summary>
/// Unity KeyCodes to SDL scancodes (USB HID usage ids), which is what Minecraft 26.x reads.
/// </summary>
internal static class Scancodes
{
    public const ushort Escape = 41;
    public const ushort F5 = 62;

    public static readonly Dictionary<KeyCode, ushort> FromKey = Build();

    private static Dictionary<KeyCode, ushort> Build()
    {
        var map = new Dictionary<KeyCode, ushort>();
        for (int i = 0; i < 26; i++)
        {
            map[KeyCode.A + i] = (ushort)(4 + i); // a..z = 4..29
        }
        for (int i = 0; i < 9; i++)
        {
            map[KeyCode.Alpha1 + i] = (ushort)(30 + i); // 1..9 = 30..38
        }
        map[KeyCode.Alpha0] = 39;
        map[KeyCode.Return] = 40;
        map[KeyCode.Escape] = Escape;
        map[KeyCode.Backspace] = 42;
        map[KeyCode.Tab] = 43;
        map[KeyCode.Space] = 44;
        map[KeyCode.Minus] = 45;
        map[KeyCode.Equals] = 46;
        map[KeyCode.LeftBracket] = 47;
        map[KeyCode.RightBracket] = 48;
        map[KeyCode.Backslash] = 49;
        map[KeyCode.Semicolon] = 51;
        map[KeyCode.Quote] = 52;
        map[KeyCode.BackQuote] = 53;
        map[KeyCode.Comma] = 54;
        map[KeyCode.Period] = 55;
        map[KeyCode.Slash] = 56;
        map[KeyCode.CapsLock] = 57;
        for (int i = 0; i < 12; i++)
        {
            map[KeyCode.F1 + i] = (ushort)(58 + i); // F1..F12 = 58..69
        }
        map[KeyCode.Print] = 70;
        map[KeyCode.ScrollLock] = 71;
        map[KeyCode.Pause] = 72;
        map[KeyCode.Insert] = 73;
        map[KeyCode.Home] = 74;
        map[KeyCode.PageUp] = 75;
        map[KeyCode.Delete] = 76;
        map[KeyCode.End] = 77;
        map[KeyCode.PageDown] = 78;
        map[KeyCode.RightArrow] = 79;
        map[KeyCode.LeftArrow] = 80;
        map[KeyCode.DownArrow] = 81;
        map[KeyCode.UpArrow] = 82;
        map[KeyCode.Numlock] = 83;
        map[KeyCode.KeypadDivide] = 84;
        map[KeyCode.KeypadMultiply] = 85;
        map[KeyCode.KeypadMinus] = 86;
        map[KeyCode.KeypadPlus] = 87;
        map[KeyCode.KeypadEnter] = 88;
        for (int i = 0; i < 9; i++)
        {
            map[KeyCode.Keypad1 + i] = (ushort)(89 + i); // keypad 1..9 = 89..97
        }
        map[KeyCode.Keypad0] = 98;
        map[KeyCode.KeypadPeriod] = 99;
        map[KeyCode.LeftControl] = 224;
        map[KeyCode.LeftShift] = 225;
        map[KeyCode.LeftAlt] = 226;
        map[KeyCode.LeftWindows] = 227;
        map[KeyCode.RightControl] = 228;
        map[KeyCode.RightShift] = 229;
        map[KeyCode.RightAlt] = 230;
        map[KeyCode.RightWindows] = 231;
        return map;
    }
}
