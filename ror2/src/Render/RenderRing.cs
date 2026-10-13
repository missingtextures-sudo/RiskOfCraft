using BepInEx.Configuration;
using RiskOfCraft.Link;
using RiskOfCraft.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace RiskOfCraft.Render;

/// <summary>
/// Consumes Minecraft's render ring: the block atlas, section meshes, entity textures, the
/// player's own model, the player's standing body, and the scene of other entities and particles.
/// Lights, solids and dug cells are not used; their messages are skipped by length.
/// </summary>
internal sealed unsafe class RenderRing
{
    private const long BytesPerFrame = 24L << 20;

    private readonly HostLink link;
    private readonly Atlas atlas;
    private readonly BlockSections sections;
    private readonly CapturedModel avatar;
    private readonly CapturedModel scene;
    private readonly WorldEntities entities;
    private Vector3 sceneOrigin;
    private readonly HostLink.MessageHandler handler;
    private Mesh? outline;
    private Material? outlineMaterial;
    private int unknownLogged;
    private float lastLog;
    private int messages;

    public RenderRing(HostLink link, ConfigFile config)
    {
        this.link = link;
        atlas = new Atlas(config);
        sections = new BlockSections(atlas);
        avatar = new CapturedModel(atlas, "avatar");
        Body = new CapturedModel(atlas, "standing body");
        scene = new CapturedModel(atlas, "scene");
        entities = new WorldEntities(link, atlas);
        handler = Handle;
    }

    /// <summary>The local player's standing body (skin, armour), facing +Z at the feet: drawn on every other Steve.</summary>
    public CapturedModel Body { get; }

    public Atlas Atlas => atlas;

    public BlockSections Sections => sections;

    public delegate void PayloadHandler(byte* payload, uint bytes);

    /// <summary>Dug messages: which blocks the player dug out of RoR2's ground.</summary>
    public PayloadHandler? Dug;

    /// <summary>Drains the ring every frame (Minecraft blocks when it fills) and draws when <paramref name="visible"/>.</summary>
    public void Update(bool visible, Vector3 feet, bool firstPerson)
    {
        link.DrainRender(BytesPerFrame, handler);
        sections.SetVisible(visible);
        if (!visible)
        {
            return;
        }
        if (!firstPerson)
        {
            avatar.Draw(Matrix4x4.Translate(feet)); // F5: the player's own posed model
        }
        scene.Draw(Matrix4x4.Translate(sceneOrigin));
        entities.Draw();
        DrawSelection();
        if (Time.unscaledTime - lastLog > 30f && messages > 0)
        {
            Plugin.Log.LogInfo($"blocks: {sections.Count} sections live, {messages} render messages in the last {Time.unscaledTime - lastLog:F0} s");
            lastLog = Time.unscaledTime;
            messages = 0;
        }
    }

    /// <summary>Link lost: nothing of Minecraft's stays on screen, and a returning Minecraft resends everything.</summary>
    public void Reset()
    {
        sections.Clear();
        avatar.Hide();
        scene.Hide();
    }

    private void Handle(uint type, byte* payload, uint bytes)
    {
        messages++;
        switch (type)
        {
            case (uint)Proto.RenMsg.ClearAll:
                sections.Clear();
                avatar.Hide();
                scene.Hide();
                break;
            case (uint)Proto.RenMsg.Atlas when bytes >= sizeof(Proto.RenAtlas):
            {
                var header = (Proto.RenAtlas*)payload;
                if (bytes >= (ulong)sizeof(Proto.RenAtlas) + (ulong)header->width * header->height * 4)
                {
                    atlas.SetTexture(0, (int)header->width, (int)header->height, payload + sizeof(Proto.RenAtlas));
                }
                break;
            }
            case (uint)Proto.RenMsg.AtlasRegion when bytes >= sizeof(Proto.RenAtlasRegion):
            {
                var header = (Proto.RenAtlasRegion*)payload;
                if (bytes >= (ulong)sizeof(Proto.RenAtlasRegion) + (ulong)header->width * header->height * 4)
                {
                    atlas.SetRegion((int)header->x, (int)header->y, (int)header->width, (int)header->height, payload + sizeof(Proto.RenAtlasRegion));
                }
                break;
            }
            case (uint)Proto.RenMsg.Texture when bytes >= sizeof(Proto.RenTexture):
            {
                var header = (Proto.RenTexture*)payload;
                if (header->id != 0 && bytes >= (ulong)sizeof(Proto.RenTexture) + (ulong)header->width * header->height * 4)
                {
                    atlas.SetTexture(header->id, (int)header->width, (int)header->height, payload + sizeof(Proto.RenTexture));
                }
                break;
            }
            case (uint)Proto.RenMsg.Section when bytes >= sizeof(Proto.RenSection):
            {
                var header = (Proto.RenSection*)payload;
                if (bytes >= (ulong)sizeof(Proto.RenSection) + (ulong)header->vertexCount * (ulong)sizeof(Proto.RenVertex))
                {
                    sections.SetSection(*header, (Proto.RenVertex*)(payload + sizeof(Proto.RenSection)));
                }
                break;
            }
            case (uint)Proto.RenMsg.Avatar when bytes >= sizeof(Proto.RenAvatar):
            case (uint)Proto.RenMsg.Ragdoll when bytes >= sizeof(Proto.RenAvatar):
            {
                var header = (Proto.RenAvatar*)payload;
                ulong need = (ulong)sizeof(Proto.RenAvatar) + (ulong)header->batchCount * (ulong)sizeof(Proto.RenBatch) + (ulong)header->vertexCount * (ulong)sizeof(Proto.RenVertex);
                if (bytes >= need)
                {
                    var batches = (Proto.RenBatch*)(payload + sizeof(Proto.RenAvatar));
                    (type == (uint)Proto.RenMsg.Avatar ? avatar : Body).Set(header->batchCount, header->vertexCount, batches, (Proto.RenVertex*)(batches + header->batchCount));
                }
                break;
            }
            case (uint)Proto.RenMsg.Scene when bytes >= sizeof(Proto.RenScene):
            {
                // Every other entity (TNT, boats, chests, ...) and all particles, relative to a block near the camera.
                var header = (Proto.RenScene*)payload;
                ulong need = (ulong)sizeof(Proto.RenScene) + (ulong)header->batchCount * (ulong)sizeof(Proto.RenBatch) + (ulong)header->vertexCount * (ulong)sizeof(Proto.RenVertex);
                if (bytes >= need)
                {
                    var batches = (Proto.RenBatch*)(payload + sizeof(Proto.RenScene));
                    sceneOrigin = Coords.ToUnity(header->originX, header->originY, header->originZ);
                    scene.Set(header->batchCount, header->vertexCount, batches, (Proto.RenVertex*)(batches + header->batchCount));
                }
                break;
            }
            case (uint)Proto.RenMsg.Dug:
                Dug?.Invoke(payload, bytes);
                break;
            case (uint)Proto.RenMsg.Lights:
            case (uint)Proto.RenMsg.Solids:
                break;
            default:
                if (unknownLogged++ < 5)
                {
                    Plugin.Log.LogWarning($"blocks: skipping unknown render message type {type} ({bytes} bytes)");
                }
                break;
        }
    }

    /// <summary>The outline around the block Minecraft's crosshair is on, from the world-entities table.</summary>
    private void DrawSelection()
    {
        if (!link.ReadSelection(out Vector3 min, out Vector3 max))
        {
            return;
        }
        if (outline == null)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default");
            if (shader == null)
            {
                return;
            }
            outlineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, color = new Color(0f, 0f, 0f, 0.8f) };
            outline = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                corners[i] = new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
            }
            outline.vertices = corners;
            outline.SetIndices(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 }, MeshTopology.Lines, 0);
        }
        // Minecraft box to Unity box: Z flips, so the far Z becomes the near one.
        Vector3 a = Coords.ToUnity(min.x, min.y, max.z);
        Vector3 b = Coords.ToUnity(max.x, max.y, min.z);
        const float grow = 0.004f;
        Matrix4x4 matrix = Matrix4x4.TRS(a - Vector3.one * grow, Quaternion.identity, b - a + Vector3.one * (2f * grow));
        Graphics.DrawMesh(outline, matrix, outlineMaterial, 0, null, 0, null, ShadowCastingMode.Off, receiveShadows: false);
    }
}
