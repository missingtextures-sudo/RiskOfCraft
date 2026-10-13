using System.Collections.Generic;
using RoR2.UI;
using UnityEngine;

namespace RiskOfCraft.Game;

/// <summary>
/// RoR2's HUD parts that Minecraft's HUD replaces. For the whole of a linked run: the health, level
/// and XP bars, the skill and sprint icons (Steve has a hotbar, not skills) and RoR2's equipment
/// icons (RiskOfCraft draws its own equipment slot by the hotbar, see EquipmentHud). While Minecraft
/// owns the player, the buffs and RoR2's crosshair too. Money, items, the objective, the teleporter
/// and boss bars stay, since Minecraft has none of those.
/// </summary>
internal sealed class HudHider
{
    private readonly List<GameObject> hiddenObjects = new();
    private readonly List<GameObject> hiddenWhileOwned = new();
    private bool hidingCore, hidingOwned;
    private float nextScan;

    /// <param name="inRun">a linked run is on: Steve's HUD replaces RoR2's bars and skills</param>
    /// <param name="minecraftOwns">Minecraft owns the player: buffs and the crosshair go as well</param>
    public void Update(bool inRun, bool minecraftOwns)
    {
        bool rescan = Time.unscaledTime > nextScan;
        if (inRun && (!hidingCore || rescan))
        {
            hidingCore = true;
            foreach (HUD hud in HUD.readOnlyInstanceList)
            {
                if (hud == null)
                {
                    continue;
                }
                Hide(hud.healthBar != null ? hud.healthBar.gameObject : null, hiddenObjects);
                Hide(hud.expBar != null ? hud.expBar.gameObject : null, hiddenObjects);
                Hide(hud.levelText != null ? hud.levelText.gameObject : null, hiddenObjects);
                Hide(hud.sprintIcon != null ? hud.sprintIcon.gameObject : null, hiddenObjects);
                Hide(hud.equipmentOverflowIcon != null ? hud.equipmentOverflowIcon.gameObject : null, hiddenObjects);
                Hide(KeyItemSlot(hud), hiddenObjects); // Seekers' key item slot ("F"): an empty box beside the hotbar
                foreach (SkillIcon icon in hud.skillIcons ?? System.Array.Empty<SkillIcon>())
                {
                    Hide(icon != null ? icon.gameObject : null, hiddenObjects);
                }
                foreach (EquipmentIcon icon in hud.equipmentIcons ?? System.Array.Empty<EquipmentIcon>())
                {
                    Hide(icon != null ? icon.gameObject : null, hiddenObjects);
                }
            }
        }
        else if (!inRun && hidingCore)
        {
            hidingCore = false;
            Restore(hiddenObjects);
        }

        if (minecraftOwns && (!hidingOwned || rescan))
        {
            hidingOwned = true;
            foreach (HUD hud in HUD.readOnlyInstanceList)
            {
                Hide(hud != null && hud.buffDisplay != null ? hud.buffDisplay.gameObject : null, hiddenWhileOwned);
            }
            foreach (CrosshairManager crosshair in Object.FindObjectsOfType<CrosshairManager>())
            {
                Hide(crosshair.container != null ? crosshair.container.gameObject : null, hiddenWhileOwned);
            }
        }
        else if (!minecraftOwns && hidingOwned)
        {
            hidingOwned = false;
            Restore(hiddenWhileOwned);
        }
        if (rescan)
        {
            nextScan = Time.unscaledTime + 1f;
        }
    }

    private static GameObject? KeyItemSlot(HUD hud)
    {
        Transform? t = hud.keyItemIcon != null ? hud.keyItemIcon.transform : null;
        while (t != null && t.name != "KeyItemSlot")
        {
            t = t.parent;
        }
        return t != null ? t.gameObject : hud.keyItemIcon != null ? hud.keyItemIcon.gameObject : null;
    }

    private static void Hide(GameObject? go, List<GameObject> into)
    {
        if (go != null && go.activeSelf)
        {
            go.SetActive(false);
            into.Add(go);
        }
    }

    private static void Restore(List<GameObject> hidden)
    {
        foreach (GameObject go in hidden)
        {
            if (go != null)
            {
                go.SetActive(true);
            }
        }
        hidden.Clear();
    }
}
