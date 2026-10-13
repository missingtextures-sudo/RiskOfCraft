using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace RiskOfCraft.Render;

/// <summary>How a face is drawn, decided per triangle from its texture's alpha (see <see cref="Atlas.Classify"/>).</summary>
internal enum Surface
{
    /// <summary>Every texel opaque.</summary>
    Solid,

    /// <summary>Holes in it (leaves, a helmet's face opening, an arrow's sprite).</summary>
    Cutout,

    /// <summary>Minecraft's translucent layer (stained glass, water).</summary>
    Translucent,

    /// <summary>Nothing to see (a skin's unused hat layer): not drawn at all.</summary>
    Empty,
}

/// <summary>
/// Minecraft's textures and the materials that draw them in Risk of Rain 2. BepInEx can't compile
/// shaders, so they are borrowed from the game: Hopoo's deferred standard shader (the one RoR2's
/// own stages use) with its vertex-colour and cutout keywords, so blocks are lit, shadowed and
/// fogged by the stage like any rock. If that shader isn't loaded yet, an unlit vertex-colour
/// shader stands in, with Minecraft's own face shading baked into the vertex colour.
/// </summary>
internal sealed unsafe class Atlas
{
    private static readonly string[] LitCandidates = { "Hopoo Games/Deferred/Standard" };
    private static readonly string[] UnlitCandidates = { "Sprites/Default", "UI/Default", "Legacy Shaders/Particles/Alpha Blended" };

    private readonly ConfigEntry<string> wanted;

    /// <summary>Dev: draw with another shader from now on (empty: back to the default choice).</summary>
    public void ForceShader(string name)
    {
        wanted.Value = name;
        lit = null;
        unlit = null;
        searched = false;
        foreach (Material m in materials.Values)
        {
            UnityEngine.Object.Destroy(m);
        }
        materials.Clear();
        if (name.StartsWith("unlit:"))
        {
            unlit = Shader.Find(name.Substring(6));
            searched = true;
            wanted.Value = "";
        }
        Plugin.Log.LogInfo($"blocks: shader now {(Find() != null ? Find()!.name : "none")}");
    }
    private readonly float brightness;
    private Shader? lit;
    private Shader? standard;
    private Shader? legacyCutout;
    private readonly HashSet<ulong> stopgapKeys = new();
    private int nextModelShaderSearch;

    /// <summary>A shader by name among those loaded (Shader.Find misses ones loaded from asset bundles).</summary>
    private static Shader? Loaded(string name) =>
        Resources.FindObjectsOfTypeAll<Shader>().FirstOrDefault(s => s.name == name && s.isSupported) ?? Shader.Find(name);
    private Shader? unlit;
    private bool searched;
    private bool shadersLogged;
    private readonly Dictionary<uint, Texture2D> textures = new(); // 0 = the block atlas
    private readonly Dictionary<uint, (int width, int height, byte[] alpha)> alphas = new();
    private readonly Dictionary<ulong, Material> materials = new();
    private readonly Dictionary<(int, int), Texture2D> stamps = new();

    public Atlas(ConfigFile config)
    {
        wanted = config.Bind("Render", "BlockShader", "",
            "Shader for Minecraft's blocks and models. Empty picks Hopoo's deferred standard shader (lit by RoR2), or an unlit one.");
        brightness = config.Bind("Render", "BlockBrightness", 1.0f,
            "Scales the colour of Minecraft's blocks under RoR2's lights.").Value;
    }

    /// <summary>True when blocks are lit by RoR2 (Minecraft's face shading must not be baked in).</summary>
    public bool Lit => Find() == lit && lit != null;

    public bool Usable => Find() != null;

    private Shader? Find()
    {
        if (lit != null)
        {
            return lit;
        }
        if (!searched || (unlit != null && Time.frameCount % 120 == 0))
        {
            searched = true;
            Shader[] all = Resources.FindObjectsOfTypeAll<Shader>();
            if (!shadersLogged && all.Length > 50)
            {
                shadersLogged = true;
                Plugin.Log.LogInfo($"blocks: {all.Length} shaders loaded: {string.Join(", ", all.Select(s => s.name).Distinct().OrderBy(n => n).Take(400))}");
            }
            if (wanted.Value.Length > 0)
            {
                lit = all.FirstOrDefault(s => s.name == wanted.Value && s.isSupported) ?? Shader.Find(wanted.Value);
            }
            lit ??= all.FirstOrDefault(s => LitCandidates.Contains(s.name) && s.isSupported);
            if (lit != null)
            {
                Plugin.Log.LogInfo($"blocks: drawing with RoR2's shader '{lit.name}' (lit)");
                foreach (Material m in materials.Values)
                {
                    UnityEngine.Object.Destroy(m);
                }
                materials.Clear();
                return lit;
            }
            if (unlit == null)
            {
                foreach (string name in UnlitCandidates)
                {
                    unlit = Shader.Find(name);
                    if (unlit != null)
                    {
                        Plugin.Log.LogInfo($"blocks: RoR2's lit shader isn't loaded yet; drawing with '{unlit.name}' (unlit, Minecraft shading)");
                        break;
                    }
                }
            }
        }
        return unlit;
    }

    /// <summary>kRenAtlas / kRenTexture: a whole texture, RGBA8, top row first.</summary>
    public void SetTexture(uint id, int width, int height, byte* pixels)
    {
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
        {
            return;
        }
        if (!textures.TryGetValue(id, out Texture2D? texture) || texture.width != width || texture.height != height)
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
            textures[id] = texture;
            foreach (KeyValuePair<ulong, Material> pair in materials)
            {
                if ((uint)(pair.Key >> 40) == id)
                {
                    pair.Value.mainTexture = texture;
                }
            }
            Plugin.Log.LogInfo(id == 0 ? $"blocks: atlas {width}x{height}" : $"blocks: texture {id} {width}x{height}");
        }
        var alpha = new byte[width * height];
        for (int i = 0; i < alpha.Length; i++)
        {
            alpha[i] = pixels[i * 4 + 3];
        }
        alphas[id] = (width, height, alpha);
        // Loaded as sent: Unity's row 0 is then Minecraft's top row, so Minecraft's v is used unchanged.
        texture.LoadRawTextureData((IntPtr)pixels, width * height * 4);
        texture.Apply(updateMipmaps: false);
    }

    public Texture2D? Texture(uint id) => textures.TryGetValue(id, out Texture2D? t) ? t : null;

    /// <summary>Dev: every texture id received.</summary>
    public IEnumerable<uint> TextureIds => textures.Keys;

    /// <summary>kRenAtlasRegion: an animated sprite's current frame.</summary>
    public void SetRegion(int x, int y, int width, int height, byte* pixels)
    {
        if (!textures.TryGetValue(0, out Texture2D? texture) || width <= 0 || height <= 0 || x < 0 || y < 0
            || x + width > texture.width || y + height > texture.height)
        {
            return;
        }
        // A small texture per sprite size, copied into the atlas on the GPU: re-uploading the whole
        // atlas for every animated sprite every frame would move hundreds of megabytes.
        if (!stamps.TryGetValue((width, height), out Texture2D? stamp))
        {
            stamp = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false, linear: false) { hideFlags = HideFlags.HideAndDontSave };
            stamps[(width, height)] = stamp;
        }
        stamp.LoadRawTextureData((IntPtr)pixels, width * height * 4);
        stamp.Apply(updateMipmaps: false);
        Graphics.CopyTexture(stamp, 0, 0, 0, 0, width, height, texture, 0, 0, x, y);
    }

    /// <summary>
    /// Looks at the texels a triangle covers: all opaque is <see cref="Surface.Solid"/>, none is
    /// <see cref="Surface.Empty"/>, a mix is <see cref="Surface.Cutout"/>.
    /// </summary>
    public Surface Classify(uint id, float u0, float v0, float u1, float v1, float u2, float v2)
    {
        if (!alphas.TryGetValue(id, out (int width, int height, byte[] alpha) map))
        {
            return Surface.Solid;
        }
        const int steps = 5;
        bool anyOpaque = false, anyClear = false;
        for (int i = 0; i <= steps; i++)
        {
            for (int j = 0; j <= steps - i; j++)
            {
                float a = (i + 0.05f * steps) / (steps * 1.15f), b = (j + 0.05f * steps) / (steps * 1.15f), c = 1f - a - b;
                int x = Mathf.Clamp((int)((u0 * a + u1 * b + u2 * c) * map.width), 0, map.width - 1);
                int y = Mathf.Clamp((int)((v0 * a + v1 * b + v2 * c) * map.height), 0, map.height - 1);
                if (map.alpha[y * map.width + x] < 26)
                {
                    anyClear = true;
                }
                else
                {
                    anyOpaque = true;
                }
            }
        }
        return !anyOpaque ? Surface.Empty : anyClear ? Surface.Cutout : Surface.Solid;
    }

    /// <summary>
    /// Minecraft's vertex colour for this host: tint and ambient occlusion as sent, plus Minecraft's
    /// fixed face shading and light level when the shader is unlit (a lit shader shades it itself).
    /// </summary>
    public Color32 Shade(uint rgba, uint light, uint flags)
    {
        float r = (byte)rgba, g = (byte)(rgba >> 8), b = (byte)(rgba >> 16);
        float k = brightness;
        if (!Lit)
        {
            int normal = (int)((flags >> 4) & 7); // Direction ordinal + 1: 1 down, 2 up, 3 north, 4 south, 5 west, 6 east
            float face = normal switch { 1 => 0.5f, 2 => 1.0f, 3 or 4 => 0.8f, 5 or 6 => 0.6f, _ => 1.0f };
            int level = (int)Math.Max(light & 0xFF, (light >> 8) & 0xFF);
            k *= face * (0.25f + 0.75f * level / 15f);
        }
        return new Color32((byte)Mathf.Min(255f, r * k), (byte)Mathf.Min(255f, g * k), (byte)Mathf.Min(255f, b * k), (byte)(rgba >> 24));
    }

    /// <summary>The material for a texture and surface kind, culled or two-sided.</summary>
    public Material? Material(uint textureId, Surface surface, bool twoSided, Color32? colour = null)
    {
        bool stopgap = false;
        Shader? shader = Find();
        if (shader == null || surface == Surface.Empty)
        {
            return null;
        }
        // Skins and armour (entity textures): Unity's Standard shader. Hopoo's deferred shader
        // drew Graphics.DrawMesh models behind nearer blocks (measured); skins need no vertex tint.
        bool model = textureId != 0 && shader == lit;
        if (model)
        {
            if ((standard == null || legacyCutout == null) && Time.frameCount >= nextModelShaderSearch)
            {
                nextModelShaderSearch = Time.frameCount + 120;
                standard ??= Loaded("Standard");
                legacyCutout ??= Loaded("Legacy Shaders/Transparent/Cutout/Diffuse");
                if (legacyCutout != null)
                {
                    // The stand-in materials go; the next model frame gets the real cutout ones.
                    foreach (ulong k in stopgapKeys)
                    {
                        if (materials.TryGetValue(k, out Material old))
                        {
                            UnityEngine.Object.Destroy(old);
                            materials.Remove(k);
                        }
                    }
                    stopgapKeys.Clear();
                }
            }
            // Parts with see-through texels (a skin's sleeves and trouser layer, a helmet's face
            // opening): RoR2's build of Standard has no alpha-test variant, so those texels drew
            // black. The legacy cutout shader has its alpha test built in; until it is loaded,
            // Hopoo's shader with its cutout keyword stands in (never Standard).
            Shader? pick = surface == Surface.Solid ? standard : legacyCutout;
            if (pick != null)
            {
                shader = pick;
            }
            else if (surface != Surface.Solid)
            {
                stopgap = true;
            }
        }
        Color32 c = colour ?? new Color32(255, 255, 255, 255);
        // Near-white tints share one material; real tints (dyes) get their own.
        uint tintKey = c.r > 245 && c.g > 245 && c.b > 245 ? 0u : ((uint)c.r << 16) | ((uint)c.g << 8) | c.b;
        ulong key = ((ulong)textureId << 40) | ((ulong)tintKey << 8) | (ulong)surface | (twoSided ? 4ul : 0ul);
        if (materials.TryGetValue(key, out Material? material))
        {
            return material;
        }
        material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = false };
        textures.TryGetValue(textureId, out Texture2D? texture);
        material.mainTexture = texture;
        material.color = Color.white;
        if (model && shader == legacyCutout)
        {
            material.color = tintKey == 0 ? Color.white : (Color)c;
            material.SetFloat("_Cutoff", 0.1f);
            material.renderQueue = (int)RenderQueue.AlphaTest;
        }
        else if (shader == standard && model)
        {
            material.color = tintKey == 0 ? Color.white : (Color)c;
            material.SetFloat("_Glossiness", 0f);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Cull", twoSided ? (float)CullMode.Off : (float)CullMode.Back);
            if (surface != Surface.Solid)
            {
                material.SetFloat("_Mode", 1f); // cutout
                material.EnableKeyword("_ALPHATEST_ON");
                material.SetFloat("_Cutoff", 0.1f);
                material.renderQueue = (int)RenderQueue.AlphaTest;
            }
        }
        else if (shader == lit)
        {
            // Hopoo's standard: vertex colour on (tint and AO), no specular, cutout for holes.
            material.SetFloat("_VertexColorOn", 1f);
            material.EnableKeyword("VERTEXCOLOR");
            material.SetFloat("_Smoothness", 0f);
            material.SetFloat("_SpecularStrength", 0f);
            material.SetFloat("_Cull", twoSided ? (float)CullMode.Off : (float)CullMode.Back);
            if (surface != Surface.Solid)
            {
                material.SetFloat("_EnableCutout", 1f);
                material.EnableKeyword("CUTOUT");
                material.SetFloat("_Cutoff", 0.1f);
            }
        }
        else
        {
            material.SetFloat("_Cull", twoSided ? (float)CullMode.Off : (float)CullMode.Back);
            material.renderQueue = surface == Surface.Translucent ? (int)RenderQueue.Transparent : (int)RenderQueue.AlphaTest;
        }
        if (stopgap)
        {
            stopgapKeys.Add(key);
        }
        materials[key] = material;
        if (model)
        {
            Plugin.Log.LogInfo($"models: material for texture {textureId} ({surface}{(twoSided ? ", two-sided" : "")}) uses {shader.name}");
        }
        return material;
    }
}
