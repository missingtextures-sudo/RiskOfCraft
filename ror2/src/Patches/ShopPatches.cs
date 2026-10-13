using HarmonyLib;
using RiskOfCraft.Game;
using RoR2;
using UnityEngine.Networking;

namespace RiskOfCraft.Patches;

/// <summary>
/// RiskOfCraft's Bazaar terminals (LunarShop.Offer) sell a Minecraft item: they show no RoR2
/// pickup, remember who paid, and give the item to that player's Minecraft instead of dropping one.
/// </summary>
[HarmonyPatch]
internal static class ShopPatches
{
    [HarmonyPatch(typeof(ShopTerminalBehavior), nameof(ShopTerminalBehavior.GenerateNewPickupServer), typeof(bool))]
    [HarmonyPrefix]
    private static bool NoRoR2Pickup(ShopTerminalBehavior __instance)
    {
        if (__instance.GetComponent<LunarShop.Offer>() == null)
        {
            return true;
        }
        __instance.SetPickupIndex(PickupIndex.none, newHidden: true);
        return false;
    }

    [HarmonyPatch(typeof(PurchaseInteraction), nameof(PurchaseInteraction.OnInteractionBegin))]
    [HarmonyPrefix]
    private static void RememberBuyer(PurchaseInteraction __instance, Interactor activator)
    {
        if (NetworkServer.active && activator != null && __instance.TryGetComponent(out LunarShop.Offer offer) && __instance.CanBeAffordedByInteractor(activator))
        {
            offer.Buyer = activator.GetComponent<CharacterBody>();
        }
    }

    [HarmonyPatch(typeof(ShopTerminalBehavior), nameof(ShopTerminalBehavior.DropPickup), typeof(bool))]
    [HarmonyPrefix]
    private static bool GiveMinecraftItem(ShopTerminalBehavior __instance)
    {
        if (!NetworkServer.active || !__instance.TryGetComponent(out LunarShop.Offer offer))
        {
            return true;
        }
        LunarShop.Deliver(offer);
        __instance.NetworkhasBeenPurchased = true;
        return false;
    }
}
