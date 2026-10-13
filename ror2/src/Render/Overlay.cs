using System;
using RiskOfCraft.Link;
using UnityEngine;

namespace RiskOfCraft.Render;

/// <summary>
/// Minecraft's HUD and screens as one full-screen texture over Risk of Rain 2's final image.
/// Minecraft renders at RoR2's resolution (the host state carries it) and publishes frames through
/// the overlay triple buffer; the newest one is uploaded only when the dirty flag is set.
/// Mirrors skse/src/Overlay.cpp; the sender is fabric/.../client/FrameExporter.java.
/// </summary>
internal sealed unsafe class Overlay
{
    private readonly HostLink link;
    private Texture2D? texture;
    private Texture2D? cursor;
    private bool topDown;
    private int frames;
    private float lastLog;

    public Overlay(HostLink link)
    {
        this.link = link;
    }

    public bool HasFrame => texture != null;

    /// <summary>Swaps in Minecraft's newest frame, if there is one. Without one the last frame stays up.</summary>
    public void Update()
    {
        if (!link.AcquireOverlayFrame())
        {
            return;
        }
        Proto.OverlaySlotHdr header = link.FrontHeader;
        int width = (int)header.width, height = (int)header.height;
        if (width <= 0 || height <= 0 || width > Proto.MaxOverlayW || height > Proto.MaxOverlayH)
        {
            return;
        }
        if (texture == null || texture.width != width || texture.height != height)
        {
            if (texture != null)
            {
                UnityEngine.Object.Destroy(texture);
            }
            texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false, linear: false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            Plugin.Log.LogInfo($"overlay: {width}x{height} (RoR2 is {Screen.width}x{Screen.height})");
        }
        // Unity's first texture row is the bottom one, which is what a bottom-up frame starts with.
        topDown = (header.flags & 1) == 0;
        texture.LoadRawTextureData((IntPtr)link.FrontPixels, width * height * 4);
        texture.Apply(updateMipmaps: false);
        frames++;
        if (Time.unscaledTime - lastLog > 10f)
        {
            Plugin.Log.LogInfo($"overlay: {frames / Mathf.Max(Time.unscaledTime - lastLog, 0.001f):F0} frames/s from Minecraft, frame id {header.frameId}");
            lastLog = Time.unscaledTime;
            frames = 0;
        }
    }

    /// <summary>Forgets the shown frame (link lost): nothing stale comes back when Minecraft returns.</summary>
    public void Clear()
    {
        if (texture != null)
        {
            UnityEngine.Object.Destroy(texture);
            texture = null;
        }
    }

    /// <summary>Call from OnGUI, which Unity draws after RoR2's own UI.</summary>
    public void Draw(bool showCursor, Vector2 cursorPosition)
    {
        if (Event.current.type != EventType.Repaint || texture == null)
        {
            return;
        }
        var screen = new Rect(0, 0, Screen.width, Screen.height);
        GUI.DrawTextureWithTexCoords(screen, texture, topDown ? new Rect(0, 1, 1, -1) : new Rect(0, 0, 1, 1), alphaBlend: true);
        if (showCursor)
        {
            cursor ??= MakeCursor();
            // Cursor events are in overlay pixels; the overlay fills the screen.
            float scale = Mathf.Max(2f, Screen.height / 540f);
            GUI.DrawTexture(new Rect(cursorPosition.x, cursorPosition.y, cursor.width * scale, cursor.height * scale), cursor);
        }
    }

    /// <summary>A plain arrow: Minecraft's screens don't draw a pointer of their own.</summary>
    private static Texture2D MakeCursor()
    {
        const int size = 16;
        var arrow = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
        var pixels = new Color32[size * size];
        for (int row = 0; row < size; row++) // row 0 = top of the arrow
        {
            for (int x = 0; x < size; x++)
            {
                bool inside = x <= row * 0.7f && row < 12;
                bool edge = inside && (x == 0 || x + 1 > row * 0.7f || row == 11);
                Color32 colour = inside ? (edge ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255)) : new Color32(0, 0, 0, 0);
                pixels[(size - 1 - row) * size + x] = colour;
            }
        }
        arrow.SetPixels32(pixels);
        arrow.Apply();
        return arrow;
    }
}
