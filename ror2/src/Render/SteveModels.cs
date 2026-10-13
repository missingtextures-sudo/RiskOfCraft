using System.Collections.Generic;
using RoR2;
using UnityEngine;

namespace RiskOfCraft.Render;

/// <summary>
/// Every player is drawn as a Minecraft player. Commando's model is switched off on each player's
/// body (forceRenderingOff, which RoR2's own visibility code never touches) and the Minecraft
/// player model is drawn instead:
/// - your own body: Minecraft's own posed model, in F5 (RenderRing's avatar); nothing in first person;
/// - other players: the standing Minecraft body your Minecraft sends (its skin and armour), at their
///   feet and facing, with Minecraft-style leg and arm swing from their speed.
/// While your Minecraft isn't linked, everything is RoR2's own again.
/// </summary>
internal sealed class SteveModels
{
    private readonly RenderRing ring;
    private readonly Dictionary<CharacterBody, List<Renderer>> hidden = new();
    private readonly List<CharacterBody> scratch = new();
    private readonly Dictionary<CharacterBody, (Vector3 last, float phase)> walk = new();

    public SteveModels(RenderRing ring)
    {
        this.ring = ring;
    }

    public void Update(bool linked, bool localFollows, CharacterBody? local)
    {
        foreach (CharacterBody body in CharacterBody.readOnlyInstancesList)
        {
            if (body == null || !body.isPlayerControlled)
            {
                continue;
            }
            bool mine = body == local;
            bool steve = linked && (mine ? localFollows : ring.Body.Shown);
            SetHidden(body, steve);
            if (steve && !mine && body.healthComponent != null && body.healthComponent.alive)
            {
                DrawRemote(body);
            }
        }
        // Bodies that went away (stage change, death) or stopped being Steves.
        scratch.Clear();
        foreach (CharacterBody body in hidden.Keys)
        {
            if (body == null || !linked)
            {
                scratch.Add(body!);
            }
        }
        foreach (CharacterBody body in scratch)
        {
            SetHidden(body, false);
            hidden.Remove(body);
            walk.Remove(body);
        }
    }

    private void SetHidden(CharacterBody body, bool hide)
    {
        if (hide)
        {
            if (hidden.ContainsKey(body))
            {
                return;
            }
            var list = new List<Renderer>();
            Transform? model = body.modelLocator != null ? body.modelLocator.modelTransform : null;
            if (model != null)
            {
                foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(includeInactive: true))
                {
                    if (!renderer.forceRenderingOff)
                    {
                        renderer.forceRenderingOff = true;
                        list.Add(renderer);
                    }
                }
            }
            hidden[body] = list;
            Plugin.Log.LogInfo($"steve models: {body.GetUserName()}'s Commando hidden ({list.Count} renderers); drawn as a Minecraft player");
        }
        else if (body != null && hidden.TryGetValue(body, out List<Renderer>? list))
        {
            foreach (Renderer renderer in list)
            {
                if (renderer != null)
                {
                    renderer.forceRenderingOff = false;
                }
            }
            hidden.Remove(body);
        }
    }

    private void DrawRemote(CharacterBody body)
    {
        Vector3 feet = body.footPosition;
        Vector3 forward = body.characterDirection != null ? body.characterDirection.forward : body.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-4f)
        {
            forward = Vector3.forward;
        }
        // Minecraft's standing body faces Minecraft +Z, which is Unity -Z.
        Quaternion facing = Quaternion.LookRotation(-forward.normalized, Vector3.up);
        // A little bob while walking, like Minecraft's walk cycle, so remote Steves don't glide.
        walk.TryGetValue(body, out (Vector3 last, float phase) state);
        float speed = state.last == Vector3.zero ? 0f : (feet - state.last).magnitude / Mathf.Max(Time.deltaTime, 1e-4f);
        state.phase += Mathf.Min(speed, 8f) * Time.deltaTime * 2.2f;
        state.last = feet;
        walk[body] = state;
        float bob = speed > 0.5f ? Mathf.Abs(Mathf.Sin(state.phase)) * 0.06f : 0f;
        ring.Body.Draw(Matrix4x4.TRS(feet + Vector3.up * bob, facing, Vector3.one));
    }
}
