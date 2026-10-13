using System.Collections.Generic;
using System.Linq;
using RiskOfCraft.Game;
using RoR2;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace RiskOfCraft.Render;

/// <summary>
/// Dresses a custom stage in Minecraft blocks: the stage's terrain materials get this player's own
/// Minecraft block textures (sent by the guest on request, design/gameplay.json custom_stages), at
/// one texture per metre, and RoR2's ramp fog takes the stage's colour. Hopoo's triplanar terrain
/// shader maps by world position, so the blocks line up across the whole stage.
/// </summary>
internal sealed class Retexture
{
    private const uint TopId = 1001, SideId = 1002, AccentId = 1003;

    private readonly Atlas atlas;
    private Tables2.CustomStage? pending;
    private float requestedAt;
    private readonly List<(Material material, Material original, Renderer renderer, int index)> swapped = new();
    private bool loggedShaders;

    public Retexture(Atlas atlas)
    {
        this.atlas = atlas;
    }

    public void Begin(Tables2.CustomStage stage)
    {
        pending = stage;
        requestedAt = Time.unscaledTime;
        Commands.Push(Commands.Format("send_texture", ("id", TopId), ("texture", stage.TopTexture)));
        Commands.Push(Commands.Format("send_texture", ("id", SideId), ("texture", stage.SideTexture)));
        Commands.Push(Commands.Format("send_texture", ("id", AccentId), ("texture", stage.AccentTexture)));
        Fog(stage.Fog, stage.FogStrength);
        SkyLight(stage);
    }

    /// <summary>
    /// A dimension's own light: a soft directional light over the whole stage, so the Minecraft blocks
    /// read like Minecraft's lit Nether and End instead of RoR2's dark cave and void. It lives in the
    /// stage's scene and goes with it.
    /// </summary>
    private static void SkyLight(Tables2.CustomStage stage)
    {
        if (stage.SelfLight <= 0f)
        {
            return;
        }
        var go = new GameObject("RiskOfCraft sky light");
        go.transform.rotation = Quaternion.Euler(55f, 35f, 0f);
        Light light = go.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = stage.Light;
        light.intensity = stage.SelfLight * 2f;
        light.shadows = LightShadows.None;
        var fill = new GameObject("RiskOfCraft sky fill");
        fill.transform.rotation = Quaternion.Euler(-40f, 215f, 0f);
        Light under = fill.AddComponent<Light>();
        under.type = LightType.Directional;
        under.color = stage.Light;
        under.intensity = stage.SelfLight;
        under.shadows = LightShadows.None;
        Plugin.Log.LogInfo($"retexture: {stage.Title} sky light {light.intensity:F2} + fill {under.intensity:F2}");
    }

    /// <summary>Stage over: nothing to undo (the scene and its materials go with it).</summary>
    public void Reset()
    {
        pending = null;
        swapped.Clear();
    }

    public void Update()
    {
        if (pending == null)
        {
            return;
        }
        Texture2D? top = atlas.Texture(TopId), side = atlas.Texture(SideId), accent = atlas.Texture(AccentId);
        if (top == null || side == null || accent == null)
        {
            if (Time.unscaledTime - requestedAt > 20f)
            {
                Plugin.Log.LogWarning("retexture: Minecraft's block textures didn't arrive; the stage keeps RoR2's look");
                pending = null;
            }
            return;
        }
        foreach (Texture2D t in new[] { top, side, accent })
        {
            t.wrapMode = TextureWrapMode.Repeat;
            t.filterMode = FilterMode.Point;
        }
        Apply(pending, top, side, accent);
        pending = null;
    }

    private void Apply(Tables2.CustomStage stage, Texture2D top, Texture2D side, Texture2D accent)
    {
        int world = LayerIndex.world.intVal;
        var copies = new Dictionary<Material, Material>();
        int renderers = 0;
        var shaderNames = new Dictionary<string, int>();
        foreach (Renderer renderer in Object.FindObjectsOfType<Renderer>())
        {
            if (renderer is not MeshRenderer || renderer.GetComponentInParent<CharacterBody>() != null || renderer.GetComponentInParent<IInteractable>() != null
                || renderer.name.StartsWith("section ") || !Terrain(renderer, world))
            {
                continue;
            }
            Material[] materials = renderer.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < materials.Length; i++)
            {
                Material m = materials[i];
                if (m == null || m.shader == null)
                {
                    continue;
                }
                string shader = m.shader.name;
                shaderNames[shader] = shaderNames.TryGetValue(shader, out int n) ? n + 1 : 1;
                if (!copies.TryGetValue(m, out Material copy))
                {
                    copy = Dress(m, top, side, accent, stage.SelfLight);
                    copies[m] = copy;
                }
                if (copy != m)
                {
                    materials[i] = copy;
                    changed = true;
                }
            }
            if (changed)
            {
                renderer.sharedMaterials = materials;
                renderers++;
            }
        }
        if (!loggedShaders)
        {
            loggedShaders = true;
            foreach (Material m in copies.Keys.Take(6))
            {
                Shader s = m.shader;
                var props = new List<string>();
                for (int p = 0; p < s.GetPropertyCount(); p++)
                {
                    props.Add($"{s.GetPropertyName(p)}:{s.GetPropertyType(p)}");
                }
                Plugin.Log.LogInfo($"retexture: {m.name} ({s.name}) keywords [{string.Join(" ", s.keywordSpace.keywordNames)}] props {string.Join(" ", props.Take(60))}");
            }
        }
        Plugin.Log.LogInfo($"retexture: {stage.Title}: {renderers} terrain renderers in {copies.Count} materials dressed in {stage.TopTexture}/{stage.SideTexture}; shaders {string.Join(", ", shaderNames.Select(p => $"{p.Key}={p.Value}"))}");
    }

    /// <summary>Ground, rock and cliff meshes: on the world layer, or with a collider there, and big.</summary>
    private static bool Terrain(Renderer renderer, int world)
    {
        if (renderer.gameObject.layer == world)
        {
            return true;
        }
        Collider? collider = renderer.GetComponent<Collider>();
        if (collider != null && collider.gameObject.layer == world)
        {
            return true;
        }
        return renderer.bounds.size.magnitude > 12f && (renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null
            && renderer.sharedMaterial.shader.name.Contains("Terrain"));
    }

    /// <summary>A copy of a stage material wearing the Minecraft textures.</summary>
    private static Material Dress(Material original, Texture2D top, Texture2D side, Texture2D accent, float selfLight)
    {
        Shader shader = original.shader;
        bool triplanar = shader.name.Contains("Triplanar") || shader.name.Contains("Snow Topped");
        bool standard = shader.name == "Hopoo Games/Deferred/Standard" || shader.name == "Standard";
        if (!triplanar && !standard)
        {
            return original;
        }
        var copy = new Material(original) { name = original.name + " (Minecraft)" };
        for (int p = 0; p < shader.GetPropertyCount(); p++)
        {
            if (shader.GetPropertyType(p) != UnityEngine.Rendering.ShaderPropertyType.Texture)
            {
                continue;
            }
            string name = shader.GetPropertyName(p);
            string lower = name.ToLowerInvariant();
            if (lower.Contains("normal") || lower.Contains("bump") || lower.Contains("ramp") || lower.Contains("emission") || lower.Contains("emtex") || lower.Contains("mask")
                || lower.Contains("noise") || lower.Contains("splat"))
            {
                if (lower.Contains("normal") || lower.Contains("bump"))
                {
                    // Flat, not null: a null normal map reads as a "black" texture in Hopoo's triplanar
                    // shader, which bends every normal away from the light (the End's islands drew black).
                    copy.SetTexture(name, Texture2D.normalTexture);
                }
                continue; // splatmaps say which channel shows where: keep them
            }
            Texture2D pick = lower.Contains("side") || lower.Contains("blue") ? side : lower.Contains("green") ? accent : top;
            copy.SetTexture(name, pick);
            // One Minecraft texture per metre: triplanar shaders scale world position, the rest UVs.
            copy.SetTextureScale(name, triplanar ? Vector2.one : copy.GetTextureScale(name) * 8f);
        }
        // Triplanar shaders map world position x factor: 1 = one Minecraft texture per metre (block).
        foreach (string scale in new[] { "_TexScale", "_TextureScale", "_Scale", "_TriplanarScale", "_Tiling", "_TextureFactor", "_TriplanarTextureFactor" })
        {
            if (copy.HasProperty(scale))
            {
                copy.SetFloat(scale, 1f);
            }
        }
        if (copy.HasProperty("_Color"))
        {
            copy.SetColor("_Color", Color.white);
        }
        // Dark dimensions: the block texture lights itself a little (Hopoo's emission), the way
        // Minecraft keeps netherrack and end stone readable under a black sky.
        if (selfLight > 0f && copy.HasProperty("_EmTex") && copy.HasProperty("_EmColor"))
        {
            Texture? main = copy.HasProperty("_MainTex") ? copy.GetTexture("_MainTex") : null;
            copy.SetTexture("_EmTex", main != null ? main : top);
            copy.SetTextureScale("_EmTex", copy.HasProperty("_MainTex") ? copy.GetTextureScale("_MainTex") : Vector2.one);
            copy.SetColor("_EmColor", Color.white * selfLight);
            if (copy.HasProperty("_EmPower"))
            {
                copy.SetFloat("_EmPower", 1f);
            }
        }
        // The stage's own vertex colours (A Moment's islands are near black) would tint the blocks.
        foreach (string flag in new[] { "_ColorsOn", "_MixColorsOn", "_VertexColorOn" })
        {
            if (copy.HasProperty(flag))
            {
                copy.SetFloat(flag, 0f);
            }
        }
        foreach (string keyword in copy.shaderKeywords)
        {
            if (keyword.Contains("VERTEX_COLOR") || keyword.Contains("VERTEXCOLOR") || keyword == "COLORS" || keyword == "MIXCOLORS")
            {
                copy.DisableKeyword(keyword);
            }
        }
        if (shader.name.Contains("Terrain Blend") && !loggedTriplanar)
        {
            loggedTriplanar = true;
            var values = new List<string>();
            for (int p = 0; p < shader.GetPropertyCount(); p++)
            {
                string n = shader.GetPropertyName(p);
                values.Add(shader.GetPropertyType(p) switch
                {
                    UnityEngine.Rendering.ShaderPropertyType.Color => $"{n}={copy.GetColor(n)}",
                    UnityEngine.Rendering.ShaderPropertyType.Float or UnityEngine.Rendering.ShaderPropertyType.Range => $"{n}={copy.GetFloat(n):0.###}",
                    UnityEngine.Rendering.ShaderPropertyType.Texture => $"{n}={(copy.GetTexture(n) != null ? copy.GetTexture(n).name : "none")}",
                    _ => n,
                });
            }
            Plugin.Log.LogInfo($"retexture: triplanar {original.name} keywords [{string.Join(" ", copy.shaderKeywords)}] {string.Join(" ", values)}");
        }
        return copy;
    }

    private static bool loggedTriplanar;

    /// <summary>RoR2's ramp fog in the stage's colour.</summary>
    private static void Fog(Color colour, float strength)
    {
        int changed = 0;
        foreach (PostProcessVolume volume in Object.FindObjectsOfType<PostProcessVolume>())
        {
            if (volume.profile == null || !volume.profile.TryGetSettings(out RampFog fog))
            {
                continue;
            }
            fog.fogColorStart.overrideState = fog.fogColorMid.overrideState = fog.fogColorEnd.overrideState = true;
            fog.fogColorStart.value = colour * 0.9f;
            fog.fogColorMid.value = colour;
            fog.fogColorEnd.value = colour * 1.3f;
            // Thinner than the base stage's own fog, so the Minecraft blocks stay readable.
            fog.fogIntensity.overrideState = true;
            fog.fogIntensity.value *= strength;
            fog.fogHeightIntensity.overrideState = true;
            fog.fogHeightIntensity.value *= strength;
            changed++;
        }
        Plugin.Log.LogInfo($"retexture: fog tinted in {changed} post-process volumes (density x{strength:F2})");
    }
}
