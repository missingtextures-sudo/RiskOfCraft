using System.Collections.Generic;
using RiskOfCraft.Link;
using RiskOfCraft.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace RiskOfCraft.Render;

/// <summary>
/// The light entities Minecraft lists in the world-entities table instead of capturing as
/// geometry: arrows in flight and stuck, tridents, dropped and thrown items. Each is a few quads
/// built here from its atlas rectangles, all in one mesh per frame. Block cracks and contact
/// shadows in the same table are not drawn.
/// </summary>
internal sealed unsafe class WorldEntities
{
    private const uint Arrow = 1, Item = 2, Trident = 3, Block = 4;
    // Minecraft's arrow model: 16 x 5 texels at 0.9 / 16 blocks per texel, shifted 4 texels back.
    private const float Texel = 0.05625f;

    private readonly HostLink link;
    private readonly Atlas atlas;
    private readonly List<Proto.WorldEntity> entities = new();
    private readonly List<Vector3> positions = new();
    private readonly List<Vector2> uvs = new();
    private readonly List<Color32> colours = new();
    private readonly List<int> indices = new();
    private Mesh? mesh;
    private bool logged;

    public WorldEntities(HostLink link, Atlas atlas)
    {
        this.link = link;
        this.atlas = atlas;
    }

    public void Draw()
    {
        if (!atlas.Usable || !link.ReadWorldEntities(entities) || entities.Count == 0)
        {
            return;
        }
        positions.Clear();
        uvs.Clear();
        colours.Clear();
        indices.Clear();
        foreach (Proto.WorldEntity e in entities)
        {
            Vector3 at = Coords.ToUnity(e.x, e.y, e.z);
            switch (e.kind)
            {
                case Arrow:
                    AddArrow(at, e);
                    break;
                case Item:
                case Trident:
                    AddSprite(at, e);
                    break;
                case Block:
                    AddCube(at, e);
                    break;
            }
        }
        if (indices.Count == 0)
        {
            return;
        }
        if (mesh == null)
        {
            mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            mesh.MarkDynamic();
        }
        mesh.Clear();
        mesh.SetVertices(positions);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(colours);
        mesh.SetTriangles(indices, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        Material? material = atlas.Material(0, Surface.Cutout, twoSided: true); // sprites: holes all over
        if (material != null)
        {
            Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0, null, 0, null, ShadowCastingMode.On, receiveShadows: true);
        }
        if (!logged)
        {
            logged = true;
            Plugin.Log.LogInfo($"entities: drawing Minecraft's arrows and items ({entities.Count} this frame)");
        }
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float u0, float v0, float u1, float v1, Color32 colour)
    {
        int first = positions.Count;
        positions.Add(a);
        positions.Add(b);
        positions.Add(c);
        positions.Add(d);
        uvs.Add(new Vector2(u0, v1));
        uvs.Add(new Vector2(u1, v1));
        uvs.Add(new Vector2(u1, v0));
        uvs.Add(new Vector2(u0, v0));
        for (int i = 0; i < 4; i++)
        {
            colours.Add(colour);
        }
        indices.Add(first);
        indices.Add(first + 1);
        indices.Add(first + 2);
        indices.Add(first);
        indices.Add(first + 2);
        indices.Add(first + 3);
    }

    // Two crossed side views along the flight direction, plus the square back plate.
    private void AddArrow(Vector3 at, Proto.WorldEntity e)
    {
        float yaw = e.yaw * Mathf.Deg2Rad, pitch = e.pitch * Mathf.Deg2Rad;
        // Minecraft's arrow heading (x = sin yaw, z = cos yaw), with Z flipped for Unity.
        var forward = new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch), -Mathf.Cos(yaw) * Mathf.Cos(pitch));
        Vector3 side = Vector3.Cross(Mathf.Abs(forward.y) > 0.99f ? Vector3.right : Vector3.up, forward).normalized;
        Vector3 up = Vector3.Cross(forward, side);
        Vector3 tail = at - forward * (12f * Texel), tip = at + forward * (4f * Texel);
        var white = new Color32(255, 255, 255, 255);
        for (int k = 0; k < 2; k++)
        {
            Vector3 across = (k == 0 ? side + up : side - up).normalized * (2.5f * Texel);
            Quad(tail - across, tip - across, tip + across, tail + across, e.uv[0], e.uv[1], e.uv[2], e.uv[3], white);
        }
        Vector3 back = at - forward * (11f * Texel);
        Vector3 s = side * (2.5f * Texel), u = up * (2.5f * Texel);
        Quad(back - s - u, back + s - u, back + s + u, back - s + u, e.uv[4], e.uv[5], e.uv[6], e.uv[7], white);
    }

    // A dropped item, thrown item or trident: its icon as a flat sprite turning about the vertical.
    private void AddSprite(Vector3 at, Proto.WorldEntity e)
    {
        float size = e.scale > 0f ? e.scale : 0.5f;
        Vector3 right = Quaternion.Euler(0f, e.yaw, 0f) * Vector3.right * (size * 0.5f);
        Vector3 up = Vector3.up * size;
        Quad(at - right, at + right, at + right + up, at - right + up, e.uv[0], e.uv[1], e.uv[2], e.uv[3], new Color32(255, 255, 255, 255));
    }

    // A dropped block: a small spinning cube with the block's side, top and bottom textures.
    private void AddCube(Vector3 at, Proto.WorldEntity e)
    {
        float size = e.scale > 0f ? e.scale : 0.25f;
        Quaternion spin = Quaternion.Euler(0f, e.yaw, 0f);
        Vector3 x = spin * Vector3.right * (size * 0.5f), z = spin * Vector3.forward * (size * 0.5f), y = Vector3.up * size;
        var white = new Color32(255, 255, 255, 255);
        Color32 tint = e.tint != 0 ? new Color32((byte)e.tint, (byte)(e.tint >> 8), (byte)(e.tint >> 16), 255) : white;
        Quad(at - x - z, at + x - z, at + x - z + y, at - x - z + y, e.uv[0], e.uv[1], e.uv[2], e.uv[3], white);
        Quad(at + x - z, at + x + z, at + x + z + y, at + x - z + y, e.uv[0], e.uv[1], e.uv[2], e.uv[3], white);
        Quad(at + x + z, at - x + z, at - x + z + y, at + x + z + y, e.uv[0], e.uv[1], e.uv[2], e.uv[3], white);
        Quad(at - x + z, at - x - z, at - x - z + y, at - x + z + y, e.uv[0], e.uv[1], e.uv[2], e.uv[3], white);
        Quad(at - x - z + y, at + x - z + y, at + x + z + y, at - x + z + y, e.uv[4], e.uv[5], e.uv[6], e.uv[7], tint);
        Quad(at - x + z, at + x + z, at + x - z, at - x - z, e.uv[8], e.uv[9], e.uv[10], e.uv[11], white);
    }
}
