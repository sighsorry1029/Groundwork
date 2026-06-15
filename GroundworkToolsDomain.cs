using BepInEx.Configuration;
using UnityEngine;

namespace Groundwork;

internal static class GroundworkToolsDomain
{
    private static GroundworkPlugin.TerrainToolSettings TerrainTools => GroundworkPlugin.Settings.TerrainTools;

    private static GroundworkPlugin.FarmingSettings Farming => GroundworkPlugin.Settings.Farming;

    internal static bool MassPlantingEnabled =>
        Farming.MassPlantingEnabled?.Value.IsOn() == true;

    internal static KeyboardShortcut ToolWheelModifierHotkey =>
        TerrainTools.ToolWheelModifierHotkey?.Value ?? new KeyboardShortcut(KeyCode.None);

    internal static KeyboardShortcut ToggleGridPlantingHotkey =>
        Farming.ToggleGridPlantingHotkey?.Value ?? new KeyboardShortcut(KeyCode.None);

    internal static float MassPlantSpacingFactor =>
        Mathf.Clamp(Farming.MassPlantSpacingFactor?.Value ?? 1f, 0.25f, 3f);

    internal static float MassPlantSkillGainFactor =>
        Mathf.Clamp(Farming.MassPlantSkillGainFactor?.Value ?? 0f, 0f, 5f);

    internal static float TerrainToolRangeStep =>
        Mathf.Max(0.05f, TerrainTools.TerrainToolRangeStep?.Value ?? 1f);

    internal static GroundworkPlugin.TerrainToolRangePreviewMode TerrainToolDefaultPreviewMode =>
        TerrainTools.DefaultPreviewMode?.Value ?? GroundworkPlugin.TerrainToolRangePreviewMode.Vanilla;

    internal static KeyboardShortcut TerrainToolPreviewToggleHotkey =>
        TerrainTools.TerrainToolPreviewToggleHotkey?.Value ?? new KeyboardShortcut(KeyCode.None);

    internal static bool PavedRoadSmoothHeight =>
        TerrainTools.PavedRoadSmoothHeight?.Value.IsOn() == true;

    internal static bool ToolHudEnabled =>
        TerrainTools.ToolHud?.Value.IsOn() == true;

    internal static float ForagingPickupMaxRange =>
        Mathf.Max(0f, Farming.ForagingPickupMaxRange?.Value ?? 0f);

    internal static float ForagingRespawnSpeedFactor =>
        Mathf.Max(0f, Farming.ForagingRespawnSpeedFactor?.Value ?? 0f);

    internal static float PlantGrowSpeedFactor =>
        Mathf.Max(0f, Farming.PlantGrowSpeedFactor?.Value ?? 0f);

    internal static int BeehiveCapacityFarmingLevelsPerBonusHoney =>
        Mathf.Clamp(Farming.BeehiveCapacityFarmingLevelsPerBonusHoney?.Value ?? 0, 0, 100);

    internal static float BeehiveFarmingSkillGainPerHoney =>
        Mathf.Max(0f, Farming.BeehiveFarmingSkillGainPerHoney?.Value ?? 0f);

    internal static float BeehiveCoverMaxSpeedMultiplier =>
        Mathf.Max(1f, Farming.BeehiveCoverMaxSpeedMultiplier?.Value ?? 1f);

    internal static float BeehiveNightHoneyRate =>
        Mathf.Clamp(Farming.BeehiveNightHoneyRate?.Value ?? 1f, 0f, 1f);

    internal static float BeehiveRainHoneyRate =>
        Mathf.Clamp(Farming.BeehiveRainHoneyRate?.Value ?? 0f, 0f, 1f);

    internal static float BeehivePollinationRadius =>
        Mathf.Max(0f, Farming.BeehivePollinationRadius?.Value ?? 0f);

    internal static int BeehivePollinationMaxPlants =>
        Mathf.Max(0, Farming.BeehivePollinationMaxPlants?.Value ?? 0);

    internal static float BeehivePollinationPlantGrowSpeedFactor =>
        Mathf.Max(1f, Farming.BeehivePollinationPlantGrowSpeedFactor?.Value ?? 1f);

    internal static float BeehivePollinationForagingRespawnSpeedFactor =>
        Mathf.Max(1f, Farming.BeehivePollinationForagingRespawnSpeedFactor?.Value ?? 1f);

    internal static float BeehivePollinationHoneySpeedBonusPercentPerTarget =>
        Mathf.Max(0f, Farming.BeehivePollinationHoneySpeedBonusPercentPerTarget?.Value ?? 0f);

    internal static float WetEnvironmentPlantGrowSpeedFactor =>
        Mathf.Max(1f, Farming.WetEnvironmentPlantGrowSpeedFactor?.Value ?? 1f);

    internal static float WetEnvironmentForagingRespawnSpeedFactor =>
        Mathf.Max(1f, Farming.WetEnvironmentForagingRespawnSpeedFactor?.Value ?? 1f);

    internal static bool ForagingFeatureEnabled =>
        ForagingPickupMaxRange > 0 || ForagingRespawnSpeedFactor > 0;

    internal static bool PlantGrowFeatureEnabled =>
        PlantGrowSpeedFactor > 0;

    internal static bool BeehivePollinationFeatureEnabled =>
        BeehivePollinationRadius > 0f &&
        BeehivePollinationMaxPlants > 0 &&
        (BeehivePollinationPlantGrowSpeedFactor > 1.001f ||
         BeehivePollinationForagingRespawnSpeedFactor > 1.001f ||
         BeehivePollinationHoneySpeedBonusPercentPerTarget > 0.001f);
}
