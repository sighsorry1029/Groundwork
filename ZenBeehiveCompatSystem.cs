using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace Groundwork;

internal static class ZenBeehiveCompatSystem
{
    internal const string ZenBeehiveGuid = "ZenDragon.ZenBeehive";
    private static Beehive? _openBeehive;
    private static int _lastHoneyLevel;

    private static bool IsLoaded => Chainloader.PluginInfos.ContainsKey(ZenBeehiveGuid);

    internal static void BeginBeehiveContainer(Container? container)
    {
        if (!IsLoaded ||
            container == null ||
            !container.TryGetComponent(out Beehive beehive))
        {
            EndBeehiveContainer();
            return;
        }

        if (_openBeehive != null && _openBeehive != beehive)
        {
            CheckForHarvest();
        }

        _openBeehive = beehive;
        _lastHoneyLevel = GetHoneyLevel(beehive);
    }

    internal static void EndBeehiveContainer()
    {
        CheckForHarvest();
        _openBeehive = null;
        _lastHoneyLevel = 0;
    }

    internal static void CheckForHarvest()
    {
        Beehive? beehive = _openBeehive;
        if (!IsLoaded || beehive == null || !IsValid(beehive))
        {
            _openBeehive = null;
            _lastHoneyLevel = 0;
            return;
        }

        int currentHoneyLevel = GetHoneyLevel(beehive);
        int harvestedHoney = _lastHoneyLevel - currentHoneyLevel;
        Player? player = Player.m_localPlayer;
        if (harvestedHoney > 0 && player != null)
        {
            BeehivePollinationSystem.RegisterBeehiveHarvest(beehive, player, harvestedHoney);
        }

        _lastHoneyLevel = currentHoneyLevel;
    }

    internal static void RefreshContainerAmountText(InventoryGrid? grid)
    {
        Beehive? beehive = _openBeehive;
        InventoryGui? inventoryGui = InventoryGui.instance;
        if (!IsLoaded ||
            beehive == null ||
            grid == null ||
            inventoryGui == null ||
            grid != inventoryGui.ContainerGrid ||
            grid.m_elements.Count == 0)
        {
            return;
        }

        Inventory inventory = grid.GetInventory();
        if (inventory == null || inventory.NrOfItems() <= 0)
        {
            return;
        }

        ItemDrop.ItemData item = inventory.GetItem(0);
        if (item == null)
        {
            return;
        }

        grid.m_elements[0].m_amount.text = $"{item.m_stack}/{BeehivePollinationSystem.GetEffectiveMaxHoney(beehive)}";
    }

    private static int GetHoneyLevel(Beehive beehive)
    {
        ZNetView? nview = beehive.m_nview;
        ZDO? zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
        return Mathf.Max(0, zdo?.GetInt(ZDOVars.s_level) ?? 0);
    }

    private static bool IsValid(Beehive beehive)
    {
        return beehive.m_nview != null && beehive.m_nview.IsValid();
    }
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show), typeof(Container), typeof(int))]
[HarmonyAfter(ZenBeehiveCompatSystem.ZenBeehiveGuid)]
internal static class InventoryGuiShowZenBeehiveCompatPatch
{
    private static void Postfix(Container container)
    {
        ZenBeehiveCompatSystem.BeginBeehiveContainer(container);
    }
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
[HarmonyAfter(ZenBeehiveCompatSystem.ZenBeehiveGuid)]
internal static class InventoryGuiHideZenBeehiveCompatPatch
{
    private static void Postfix()
    {
        ZenBeehiveCompatSystem.EndBeehiveContainer();
    }
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnTakeAll))]
[HarmonyAfter(ZenBeehiveCompatSystem.ZenBeehiveGuid)]
internal static class InventoryGuiOnTakeAllZenBeehiveCompatPatch
{
    private static void Postfix()
    {
        ZenBeehiveCompatSystem.CheckForHarvest();
    }
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnDropOutside))]
[HarmonyAfter(ZenBeehiveCompatSystem.ZenBeehiveGuid)]
internal static class InventoryGuiOnDropOutsideZenBeehiveCompatPatch
{
    private static void Postfix()
    {
        ZenBeehiveCompatSystem.CheckForHarvest();
    }
}

[HarmonyPatch(
    typeof(InventoryGui),
    nameof(InventoryGui.OnSelectedItem),
    typeof(InventoryGrid),
    typeof(ItemDrop.ItemData),
    typeof(Vector2i),
    typeof(InventoryGrid.Modifier))]
[HarmonyAfter(ZenBeehiveCompatSystem.ZenBeehiveGuid)]
internal static class InventoryGuiOnSelectedItemZenBeehiveCompatPatch
{
    private static void Postfix()
    {
        ZenBeehiveCompatSystem.CheckForHarvest();
    }
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.StackAll), typeof(Inventory), typeof(bool))]
[HarmonyAfter(ZenBeehiveCompatSystem.ZenBeehiveGuid)]
internal static class InventoryStackAllZenBeehiveCompatPatch
{
    private static void Postfix()
    {
        ZenBeehiveCompatSystem.CheckForHarvest();
    }
}

[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UseItem), typeof(Inventory), typeof(ItemDrop.ItemData), typeof(bool))]
[HarmonyAfter(ZenBeehiveCompatSystem.ZenBeehiveGuid)]
internal static class HumanoidUseItemZenBeehiveCompatPatch
{
    private static void Postfix()
    {
        ZenBeehiveCompatSystem.CheckForHarvest();
    }
}

[HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.UpdateGui), typeof(Player), typeof(ItemDrop.ItemData))]
[HarmonyAfter(ZenBeehiveCompatSystem.ZenBeehiveGuid)]
internal static class InventoryGridUpdateGuiZenBeehiveCompatPatch
{
    private static void Postfix(InventoryGrid __instance)
    {
        ZenBeehiveCompatSystem.RefreshContainerAmountText(__instance);
    }
}
