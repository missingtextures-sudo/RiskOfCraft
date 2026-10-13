using System.Collections.Generic;
using RoR2;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace RiskOfCraft.Render;

/// <summary>
/// Steve's RoR2 equipment as a Minecraft-style slot left of Minecraft's hotbar and offhand slot (RoR2's own
/// equipment icons are hidden with its skill bar): the equipment's icon and name, a cooldown shade that
/// shrinks like Minecraft's item cooldown with the seconds left, the charges when it has more than
/// one, and the key that uses it. Sized in Minecraft GUI pixels from the guest's GUI scale, so it
/// lines up with the hotbar at any resolution.
/// </summary>
internal sealed class EquipmentHud
{
    private GUIStyle? text;
    private readonly Dictionary<EquipmentIndex, AsyncOperationHandle<Sprite>> icons = new();

    /// <summary>RoR2 loads equipment icons on demand (pickupIconSpriteReference): load once, then reuse.</summary>
    private Sprite? Icon(EquipmentDef def)
    {
        if (def.pickupIconSprite != null)
        {
            return def.pickupIconSprite;
        }
        if (!icons.TryGetValue(def.equipmentIndex, out AsyncOperationHandle<Sprite> handle))
        {
            if (def.pickupIconSpriteReference == null || !def.pickupIconSpriteReference.RuntimeKeyIsValid())
            {
                return null;
            }
            handle = Addressables.LoadAssetAsync<Sprite>(def.pickupIconSpriteReference.RuntimeKey);
            icons[def.equipmentIndex] = handle;
        }
        return handle.IsValid() && handle.IsDone && handle.Status == AsyncOperationStatus.Succeeded ? handle.Result : null;
    }

    /// <summary>Call from OnGUI (repaint) while Minecraft's HUD is on screen.</summary>
    public void Draw(CharacterBody body, uint guiScale, string key)
    {
        if (Event.current.type != EventType.Repaint || body.inventory == null)
        {
            return;
        }
        EquipmentIndex index = body.inventory.currentEquipmentIndex;
        EquipmentDef? def = index != EquipmentIndex.None ? EquipmentCatalog.GetEquipmentDef(index) : null;
        if (def == null)
        {
            return;
        }
        float s = Mathf.Max(1f, guiScale);
        float size = 22f * s;
        // Minecraft's hotbar is 182 GUI pixels wide at the bottom centre, with the offhand slot (29)
        // to its left: the equipment slot goes just left of that, where Minecraft draws nothing.
        var slot = new Rect(Screen.width * 0.5f - 91f * s - 29f * s - 6f * s - size, Screen.height - size - 1f * s, size, size);
        Fill(slot, new Color(0.08f, 0.08f, 0.08f, 0.75f));
        Fill(new Rect(slot.x, slot.y, slot.width, s), new Color(0.55f, 0.55f, 0.55f, 0.9f));
        Fill(new Rect(slot.x, slot.y, s, slot.height), new Color(0.55f, 0.55f, 0.55f, 0.9f));
        Fill(new Rect(slot.x, slot.yMax - s, slot.width, s), new Color(0.2f, 0.2f, 0.2f, 0.9f));
        Fill(new Rect(slot.xMax - s, slot.y, s, slot.height), new Color(0.2f, 0.2f, 0.2f, 0.9f));
        Rect inner = new(slot.x + 3f * s, slot.y + 3f * s, slot.width - 6f * s, slot.height - 6f * s);
        // RoR2's icons are sprites, often packed in an atlas: draw the sprite's own part of its texture.
        Sprite? sprite = Icon(def);
        if (sprite != null && sprite.texture != null)
        {
            Texture2D atlas = sprite.texture;
            Rect r = sprite.textureRect;
            GUI.DrawTextureWithTexCoords(inner, atlas, new Rect(r.x / atlas.width, r.y / atlas.height, r.width / atlas.width, r.height / atlas.height), alphaBlend: true);
        }
        else if (def.pickupIconTexture != null)
        {
            GUI.DrawTexture(inner, def.pickupIconTexture, ScaleMode.ScaleToFit, alphaBlend: true);
        }

        EquipmentSlot equipment = body.equipmentSlot;
        int stock = equipment != null ? equipment.stock : 0;
        int maxStock = equipment != null ? equipment.maxStock : 1;
        float left = equipment != null ? equipment.cooldownTimer : 0f;
        if (left > 0f && !float.IsInfinity(left))
        {
            float full = Mathf.Max(left, def.cooldown * body.inventory.CalculateEquipmentCooldownScale());
            float part = Mathf.Clamp01(left / Mathf.Max(full, 0.01f));
            if (stock == 0)
            {
                // Minecraft's cooldown: a white shade over the item, shrinking from the top.
                Fill(new Rect(inner.x, inner.yMax - inner.height * part, inner.width, inner.height * part), new Color(1f, 1f, 1f, 0.45f));
            }
            Label(inner, Mathf.CeilToInt(left).ToString(), TextAnchor.MiddleCenter, 9f * s, Color.white);
        }
        if (maxStock > 1)
        {
            Label(new Rect(slot.x, slot.y + 2f * s, slot.width - 2f * s, slot.height - 2f * s), stock.ToString(), TextAnchor.LowerRight, 8f * s, Color.white);
        }
        // Which equipment and its key, beside the slot (clear of Minecraft's hunger bar).
        string name = Language.GetString(def.nameToken);
        Label(new Rect(slot.xMax - 200f * s, slot.y - 17f * s, 200f * s, 8f * s), name, TextAnchor.LowerRight, 5.5f * s, new Color(1f, 1f, 0.45f));
        Label(new Rect(slot.xMax - 200f * s, slot.y - 9f * s, 200f * s, 8f * s), $"[{key}] to use", TextAnchor.LowerRight, 5f * s, new Color(0.85f, 0.85f, 0.85f));
    }

    private static void Fill(Rect rect, Color color)
    {
        Color old = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = old;
    }

    private void Label(Rect rect, string value, TextAnchor anchor, float fontSize, Color color)
    {
        text ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow };
        text.alignment = anchor;
        text.fontSize = Mathf.RoundToInt(fontSize);
        text.normal.textColor = new Color(0.15f, 0.15f, 0.15f, 0.9f);
        float shadow = Mathf.Max(1f, fontSize / 8f);
        GUI.Label(new Rect(rect.x + shadow, rect.y + shadow, rect.width, rect.height), value, text);
        text.normal.textColor = color;
        GUI.Label(rect, value, text);
    }
}
