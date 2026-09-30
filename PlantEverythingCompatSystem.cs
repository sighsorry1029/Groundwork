using System;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace Groundwork;

internal static class PlantEverythingCompatSystem
{
    internal const string PluginGuid = "advize.PlantEverything";
    internal static bool IsActive { get; private set; }

    internal static void Initialize(Harmony harmony)
    {
        IsActive = Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin);
        if (!IsActive)
        {
            return;
        }

        if (!PatchHoverTimers(harmony, plugin.Instance.GetType().Assembly))
        {
            GroundworkPlugin.ModLogger.LogWarning(
                "Could not locate PlantEverything's hover timer patches. " +
                "Disable its plant timers to avoid duplicate or inaccurate growth timers.");
        }

        GroundworkPlugin.ModLogger.LogInfo(
            "PlantEverything compatibility active: cultivation.yml, Groundwork Cultivator removal, " +
            "and harvested visuals are disabled. Growth bonuses remain active. Existing cultivation files are preserved.");
    }

    internal static bool PatchHoverTimers(Harmony harmony, Assembly assembly)
    {
        Type? patches = assembly.GetType("Advize_PlantEverything.HoverTextPatches");
        MethodInfo? pickable = FindHoverPostfix(patches, typeof(Pickable));
        MethodInfo? plant = FindHoverPostfix(patches, typeof(Plant));
        if (pickable == null || plant == null)
        {
            return false;
        }

        // Keep PE's configuration and patches intact. Our prefixes are removed by UnpatchSelf,
        // restoring its timers when Groundwork unloads, including when our hover modes are Off.
        HarmonyMethod prefix = new(typeof(PlantEverythingCompatSystem), nameof(AllowPlantEverythingTimer));
        harmony.Patch(pickable, prefix: prefix);
        harmony.Patch(plant, prefix: prefix);
        return true;
    }

    private static MethodInfo? FindHoverPostfix(Type? patches, Type target)
    {
        MethodInfo? method = patches?.GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic,
            null, new[] { target, typeof(string).MakeByRefType() }, null);
        return method?.ReturnType == typeof(void) ? method : null;
    }

    private static bool AllowPlantEverythingTimer() => !IsActive;

    internal static void Shutdown() => IsActive = false;
}
