using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Groundwork;

internal static class ScytheToolCompatSystem
{
    private const string JewelcraftingAssemblyName = "Jewelcrafting";
    private static readonly Dictionary<ItemDrop.ItemData.SharedData, ItemDrop.ItemData.ItemType> OriginalItemTypes = new();
    private static bool _loggedJewelcraftingRecalcFailure;
    private static bool _pendingJewelcraftingRecalc;

    internal static bool ApplyToObjectDb(ObjectDB objectDb)
    {
        int changedCount = 0;
        foreach (GameObject itemPrefab in objectDb.m_items)
        {
            if (itemPrefab == null)
            {
                continue;
            }

            ItemDrop? itemDrop = itemPrefab.GetComponent<ItemDrop>();
            ItemDrop.ItemData.SharedData? sharedData = itemDrop?.m_itemData?.m_shared;
            if (!IsScytheLike(sharedData) ||
                sharedData!.m_itemType == ItemDrop.ItemData.ItemType.Tool)
            {
                continue;
            }

            OriginalItemTypes.TryAdd(sharedData, sharedData.m_itemType);
            sharedData.m_itemType = ItemDrop.ItemData.ItemType.Tool;
            changedCount++;
        }

        if (changedCount > 0)
        {
            GroundworkPlugin.ModLogger.LogInfo($"Treated {changedCount} scythe/Farming item prefab(s) as ItemType.Tool.");
        }

        return changedCount > 0;
    }

    internal static bool ShouldCountAsWeapon(ItemDrop.ItemData? item)
    {
        return item?.m_shared != null &&
               item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool &&
               IsScytheLike(item.m_shared);
    }

    internal static void NotifyJewelcraftingEffectRecalcIfPresent()
    {
        Assembly? jewelcraftingAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(
                assembly.GetName().Name,
                JewelcraftingAssemblyName,
                StringComparison.OrdinalIgnoreCase));
        if (jewelcraftingAssembly == null)
        {
            return;
        }

        if (!TryGetReadyLocalPlayer(out Player player))
        {
            _pendingJewelcraftingRecalc = true;
            return;
        }

        try
        {
            if (!InvokeJewelcraftingEffectRecalc(jewelcraftingAssembly, player))
            {
                _pendingJewelcraftingRecalc = false;
                LogJewelcraftingRecalcFailure(
                    "no supported effect recalculation entry point was found");
                return;
            }

            _pendingJewelcraftingRecalc = false;
        }
        catch (Exception ex)
        {
            _pendingJewelcraftingRecalc = false;
            LogJewelcraftingRecalcFailure(ex.GetBaseException().Message);
        }
    }

    internal static void NotifyPendingJewelcraftingEffectRecalcIfNeeded(Player player)
    {
        if (!_pendingJewelcraftingRecalc || player != Player.m_localPlayer)
        {
            return;
        }

        NotifyJewelcraftingEffectRecalcIfPresent();
    }

    private static bool TryGetReadyLocalPlayer(out Player player)
    {
        player = Player.m_localPlayer;
        ZNetView? nview = player != null ? ((Character)player).m_nview : null;
        return player != null && nview != null && nview.IsValid();
    }

    private static bool InvokeJewelcraftingEffectRecalc(Assembly jewelcraftingAssembly, Player player)
    {
        Type? trackerType = jewelcraftingAssembly.GetType("Jewelcrafting.GemEffects.TrackEquipmentChanges");
        MethodInfo? calculateEffectsMethod = trackerType?.GetMethod(
            "CalculateEffects",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(Player)],
            modifiers: null);
        if (calculateEffectsMethod != null)
        {
            calculateEffectsMethod.Invoke(null, new object[] { player });
            return true;
        }

        Type? apiType = jewelcraftingAssembly.GetType("Jewelcrafting.API");
        MethodInfo? recalcMethod = apiType?.GetMethod(
            "InvokeEffectRecalc",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        if (recalcMethod == null)
        {
            return false;
        }

        recalcMethod.Invoke(null, null);
        return true;
    }

    internal static void Shutdown()
    {
        int restoredCount = 0;
        foreach (KeyValuePair<ItemDrop.ItemData.SharedData, ItemDrop.ItemData.ItemType> state in OriginalItemTypes)
        {
            if (state.Key != null && state.Key.m_itemType == ItemDrop.ItemData.ItemType.Tool)
            {
                state.Key.m_itemType = state.Value;
                restoredCount++;
            }
        }

        OriginalItemTypes.Clear();
        if (restoredCount > 0)
        {
            NotifyJewelcraftingEffectRecalcIfPresent();
        }

        _pendingJewelcraftingRecalc = false;
        _loggedJewelcraftingRecalcFailure = false;
    }

    private static void LogJewelcraftingRecalcFailure(string reason)
    {
        if (_loggedJewelcraftingRecalcFailure)
        {
            return;
        }

        _loggedJewelcraftingRecalcFailure = true;
        GroundworkPlugin.ModLogger.LogWarning(
            $"Could not notify Jewelcrafting to recalculate gem effects: {reason}.");
    }

    private static bool IsScytheLike(ItemDrop.ItemData.SharedData? sharedData)
    {
        if (sharedData == null || sharedData.m_skillType != Skills.SkillType.Farming)
        {
            return false;
        }

        Attack? primaryAttack = sharedData.m_attack;
        return string.Equals(sharedData.m_animationState.ToString(), "Scythe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(primaryAttack?.m_attackAnimation, "scything", StringComparison.OrdinalIgnoreCase) ||
               primaryAttack?.m_harvest == true;
    }
}

[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.IsWeapon))]
internal static class ItemDataIsWeaponScytheToolCompatPatch
{
    private static void Postfix(ItemDrop.ItemData __instance, ref bool __result)
    {
        if (!__result && ScytheToolCompatSystem.ShouldCountAsWeapon(__instance))
        {
            __result = true;
        }
    }
}
