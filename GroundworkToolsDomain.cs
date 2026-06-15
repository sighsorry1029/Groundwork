using BepInEx.Configuration;
using UnityEngine;

namespace Groundwork;

internal static class GroundworkToolsDomain
{
    private static GroundworkPlugin.GeneralSettings General => GroundworkPlugin.Settings.General;

    private static GroundworkPlugin.FarmingSettings Farming => GroundworkPlugin.Settings.Farming;

    internal static bool Enabled => true;

    internal static bool ScytheHarvestImprovementsEnabled => true;

    internal static bool TreatScythesAsToolsEnabled => true;

    internal static bool MassPlantingEnabled =>
        Enabled && Farming.MassPlantingEnabled?.Value.IsOn() == true;

    internal static KeyboardShortcut ToolWheelModifierHotkey =>
        General.ToolWheelModifierHotkey?.Value ?? new KeyboardShortcut(KeyCode.None);

    internal static KeyboardShortcut ToggleGridPlantingHotkey =>
        Farming.ToggleGridPlantingHotkey?.Value ?? new KeyboardShortcut(KeyCode.None);

    internal static float MassPlantSpacingFactor =>
        Enabled ? Mathf.Clamp(Farming.MassPlantSpacingFactor?.Value ?? 1f, 0.25f, 3f) : 1f;

    internal static float MassPlantSkillGainFactor =>
        Enabled ? Mathf.Clamp(Farming.MassPlantSkillGainFactor?.Value ?? 0f, 0f, 5f) : 0f;

    internal static bool TerrainToolRangeAndCostEnabled => true;

    internal static float TerrainToolRangeStep =>
        Enabled ? Mathf.Max(0.05f, General.TerrainToolRangeStep?.Value ?? 1f) : 1f;

    internal static GroundworkPlugin.TerrainToolRangePreviewMode TerrainToolDefaultPreviewMode =>
        Farming.DefaultPreviewMode?.Value ?? GroundworkPlugin.TerrainToolRangePreviewMode.Vanilla;

    internal static KeyboardShortcut TerrainToolPreviewToggleHotkey =>
        Farming.TerrainToolPreviewToggleHotkey?.Value ?? new KeyboardShortcut(KeyCode.None);

    internal static bool PavedRoadSmoothHeight =>
        Farming.PavedRoadSmoothHeight?.Value.IsOn() == true;

    internal static bool ToolHudEnabled =>
        Enabled && General.ToolHud?.Value.IsOn() == true;

    internal static bool HarvestSweepEnabled => Enabled;

    internal static float ForagingPickupMaxRange =>
        Enabled ? Mathf.Max(0f, Farming.ForagingPickupMaxRange?.Value ?? 0f) : 0f;

    internal static float ForagingRespawnSpeedFactor =>
        Enabled ? Mathf.Max(0f, Farming.ForagingRespawnSpeedFactor?.Value ?? 0f) : 0f;

    internal static float PlantGrowSpeedFactor =>
        Enabled ? Mathf.Max(0f, Farming.PlantGrowSpeedFactor?.Value ?? 0f) : 0f;

    internal static int BeehiveCapacityFarmingLevelsPerBonusHoney =>
        Enabled ? Mathf.Clamp(Farming.BeehiveCapacityFarmingLevelsPerBonusHoney?.Value ?? 0, 0, 100) : 0;

    internal static float BeehiveFarmingSkillGainPerHoney =>
        Enabled ? Mathf.Max(0f, Farming.BeehiveFarmingSkillGainPerHoney?.Value ?? 0f) : 0f;

    internal static float BeehiveCoverMaxSpeedMultiplier =>
        Enabled ? Mathf.Max(1f, Farming.BeehiveCoverMaxSpeedMultiplier?.Value ?? 1f) : 1f;

    internal static float BeehiveNightHoneyRate =>
        Enabled ? Mathf.Clamp(Farming.BeehiveNightHoneyRate?.Value ?? 1f, 0f, 1f) : 1f;

    internal static float BeehiveRainHoneyRate =>
        Enabled ? Mathf.Clamp(Farming.BeehiveRainHoneyRate?.Value ?? 0f, 0f, 1f) : 1f;

    internal static float BeehivePollinationRadius =>
        Enabled ? Mathf.Max(0f, Farming.BeehivePollinationRadius?.Value ?? 0f) : 0f;

    internal static int BeehivePollinationMaxPlants =>
        Enabled ? Mathf.Max(0, Farming.BeehivePollinationMaxPlants?.Value ?? 0) : 0;

    internal static float BeehivePollinationPlantGrowSpeedFactor =>
        Enabled ? Mathf.Max(1f, Farming.BeehivePollinationPlantGrowSpeedFactor?.Value ?? 1f) : 1f;

    internal static float BeehivePollinationForagingRespawnSpeedFactor =>
        Enabled ? Mathf.Max(1f, Farming.BeehivePollinationForagingRespawnSpeedFactor?.Value ?? 1f) : 1f;

    internal static float BeehivePollinationHoneySpeedBonusPercentPerTarget =>
        Enabled ? Mathf.Max(0f, Farming.BeehivePollinationHoneySpeedBonusPercentPerTarget?.Value ?? 0f) : 0f;

    internal static float WetEnvironmentPlantGrowSpeedFactor =>
        Enabled ? Mathf.Max(1f, Farming.WetEnvironmentPlantGrowSpeedFactor?.Value ?? 1f) : 1f;

    internal static float WetEnvironmentForagingRespawnSpeedFactor =>
        Enabled ? Mathf.Max(1f, Farming.WetEnvironmentForagingRespawnSpeedFactor?.Value ?? 1f) : 1f;

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
