using UnityEngine;

namespace RiskOfCraft.Render;

/// <summary>
/// Steve on the lobby's character pad: the survivor mannequin RoR2 builds from Commando's display
/// prefab keeps its place, animations and sounds, but while Minecraft has sent the player's own
/// model (skin and armour) Commando's renderers are off and that model is drawn standing there.
/// Without a model yet (Minecraft still starting), Commando shows as before.
/// </summary>
internal sealed class SteveMannequin : MonoBehaviour
{
    private Renderer[] renderers = System.Array.Empty<Renderer>();
    private bool hidden;

    private void Start() => renderers = GetComponentsInChildren<Renderer>(true);

    /// <summary>A loadout or skin change rebuilt or re-skinned the mannequin: look again.</summary>
    public void Refresh()
    {
        renderers = GetComponentsInChildren<Renderer>(true);
        hidden = false;
    }

    private void LateUpdate()
    {
        CapturedModel? steve = Plugin.Instance != null && Plugin.Instance.Blocks != null ? Plugin.Instance.Blocks.Body : null;
        bool show = steve != null && steve.Shown && Plugin.Instance!.Linked;
        if (show != hidden)
        {
            hidden = show;
            foreach (Renderer r in renderers)
            {
                if (r != null)
                {
                    r.forceRenderingOff = show;
                }
            }
        }
        if (show)
        {
            // Minecraft's model faces the other way from a RoR2 mannequin: turned round to face the camera.
            steve!.Draw(Matrix4x4.TRS(transform.position, transform.rotation * Quaternion.Euler(0f, 180f, 0f), Vector3.one));
        }
    }
}
