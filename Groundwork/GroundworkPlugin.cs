using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using LocalizationManager;
using ServerSync;
using UnityEngine;

namespace Groundwork;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInIncompatibility("advize.PlantEverything")]
[BepInDependency(JewelcraftingGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(ZenBeehiveGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(ExpandWorldDataGuid, BepInDependency.DependencyFlags.SoftDependency)]
public class GroundworkPlugin : BaseUnityPlugin
{
    internal const string ModName = "Groundwork";
    internal const string ModVersion = "1.1.12";
    internal const string Author = "sighsorry";
    private const string ModGUID = $"{Author}.{ModName}";
    private const string JewelcraftingGuid = "org.bepinex.plugins.jewelcrafting";
    private const string ZenBeehiveGuid = "ZenDragon.ZenBeehive";
    internal const string ExpandWorldDataGuid = "expand_world_data";
    private const string TerrainToolsYamlFileName = "Groundwork.yml";
    private const string SyncedTerrainToolsYamlIdentifier = "groundwork_yaml";
    private const float YamlReloadDebounceSeconds = 0.35f;

    internal static readonly string YamlConfigDirectoryPath = Path.Combine(Paths.ConfigPath, ModName);
    private static readonly string TerrainToolsYamlFilePath =
        Path.Combine(YamlConfigDirectoryPath, TerrainToolsYamlFileName);
    private static CustomSyncedValue<string>? _syncedTerrainToolsYaml;
    private static IReadOnlyList<NormalizedTerrainToolConfig> _terrainTools = Array.Empty<NormalizedTerrainToolConfig>();
    private static bool _suppressSyncedYamlChanged;
    private static YamlAuthorityMode _yamlAuthorityMode;
    private int _configurationManagerOrder;

    private readonly Harmony _harmony = new(ModGUID);
    private FileSystemWatcher? _watcher;
    private Coroutine? _yamlReloadCoroutine;

    internal static GroundworkPlugin? Instance { get; private set; }

    public static readonly ManualLogSource ModLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
    internal static readonly ConfigSync ConfigSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };
    internal static PluginSettings Settings { get; } = new();

    internal static IReadOnlyList<NormalizedTerrainToolConfig> TerrainTools => _terrainTools;

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public enum HoverHintMode
    {
        Off,
        Compact,
        Detailed
    }

    public enum FarmingRangeHarvestTargetMode
    {
        ForagingOnly,
        ForagingAndCrops
    }

    public enum TerrainToolRangePreviewMode
    {
        Vanilla,
        Grid
    }

    private enum YamlAuthorityMode
    {
        LocalFiles,
        SyncedOnly
    }

    public void Awake()
    {
        Instance = this;
        Localizer.Load();

        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        try
        {
            Settings.Bind(this);
            _ = ConfigSync.AddLockingConfigEntry(Settings.General.LockConfiguration);
            InitializeSyncedYamlValue();
            GrowthOverrideSystem.Initialize(this, ConfigSync);
            GroundworkConfigLoader.EnsureLocalFileExists(YamlConfigDirectoryPath, TerrainToolsYamlFilePath);
            RefreshYamlAuthorityMode(force: true);

            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);

            Config.Save();
        }
        finally
        {
            Config.SaveOnConfigSet = saveOnSet;
        }
    }

    public void OnDestroy()
    {
        RunShutdownStep("Harmony patches", _harmony.UnpatchSelf);
        RunShutdownStep(nameof(PlayerPlacePieceGroundworkPatch), PlayerPlacePieceGroundworkPatch.ClearContext);
        RunShutdownStep(nameof(FarmingSkillTooltipPosition), FarmingSkillTooltipPosition.Clear);
        RunShutdownStep(nameof(GrowthOverrideSystem), GrowthOverrideSystem.Shutdown);
        RunShutdownStep(nameof(CultivationSystem), CultivationSystem.Shutdown);
        RunShutdownStep(nameof(PickedVisualSystem), PickedVisualSystem.Shutdown);
        RunShutdownStep(nameof(MassPlantingSystem), MassPlantingSystem.Shutdown);
        RunShutdownStep(nameof(TerrainToolRangeSystem), TerrainToolRangeSystem.Shutdown);
        RunShutdownStep(nameof(PickaxeTerrainScalingSystem), PickaxeTerrainScalingSystem.Shutdown);
        RunShutdownStep(nameof(TerrainDigFloatingTextSystem), TerrainDigFloatingTextSystem.Clear);
        RunShutdownStep(nameof(PickableRespawnHoverSystem), PickableRespawnHoverSystem.Shutdown);
        RunShutdownStep(nameof(FarmingSkillSystem), FarmingSkillSystem.Shutdown);
        RunShutdownStep(nameof(ZenBeehiveCompatSystem), ZenBeehiveCompatSystem.Shutdown);
        RunShutdownStep(nameof(BeehivePollinationSystem), BeehivePollinationSystem.Shutdown);
        RunShutdownStep(nameof(ScytheHarvestSystem), ScytheHarvestSystem.Shutdown);
        RunShutdownStep(nameof(ScytheHandleUnlockSystem), ScytheHandleUnlockSystem.Shutdown);
        RunShutdownStep(nameof(ScytheToolCompatSystem), ScytheToolCompatSystem.Shutdown);
        RunShutdownStep(nameof(CameraZoomInputSuppressionSystem), CameraZoomInputSuppressionSystem.Shutdown);
        RunShutdownStep(nameof(GroundworkInputIcons), GroundworkInputIcons.Shutdown);
        RunShutdownStep(nameof(Localizer), Localizer.Unload);
        RunShutdownStep("synced YAML state", DisposeSyncedYamlValue);
        RunShutdownStep("YAML file watcher", DisposeWatcher);
        _terrainTools = Array.Empty<NormalizedTerrainToolConfig>();
        _suppressSyncedYamlChanged = false;
        _yamlAuthorityMode = default;
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    private static void RunShutdownStep(string name, Action shutdown)
    {
        try
        {
            shutdown();
        }
        catch (Exception ex)
        {
            ModLogger.LogWarning(
                $"Could not fully clean up {name} while unloading Groundwork: {ex.GetBaseException().Message}");
        }
    }

    internal static void ApplyToObjectDb(ObjectDB objectDb)
    {
        if (objectDb == null)
        {
            return;
        }

        TerrainToolRangeSystem.RestoreObjectDb(objectDb);
        bool scytheItemTypesChanged = ScytheToolCompatSystem.ApplyToObjectDb(objectDb);
        TerrainToolRangeSystem.ApplyToObjectDb(objectDb, TerrainTools);
        CultivationSystem.ApplyToObjectDb(objectDb);
        if (scytheItemTypesChanged)
        {
            ScytheToolCompatSystem.NotifyJewelcraftingEffectRecalcIfPresent();
        }
    }

    internal static void TryApplyPendingConfig()
    {
        Instance?.RefreshYamlAuthorityMode();
        GrowthOverrideSystem.RefreshAuthority();
    }

    private void SetupWatcher()
    {
        if (_watcher != null)
        {
            return;
        }

        Directory.CreateDirectory(YamlConfigDirectoryPath);
        _watcher = new FileSystemWatcher(YamlConfigDirectoryPath, TerrainToolsYamlFileName);
        _watcher.Changed += OnYamlFileChanged;
        _watcher.Created += OnYamlFileChanged;
        _watcher.Renamed += OnYamlFileChanged;
        _watcher.IncludeSubdirectories = false;
        _watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnYamlFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_yamlAuthorityMode != YamlAuthorityMode.LocalFiles)
        {
            return;
        }

        ScheduleLocalYamlReload();
    }

    private void ScheduleLocalYamlReload()
    {
        CancelPendingYamlReload();
        _yamlReloadCoroutine = StartCoroutine(ReloadLocalYamlAfterDelay());
    }

    private IEnumerator ReloadLocalYamlAfterDelay()
    {
        yield return new WaitForSecondsRealtime(YamlReloadDebounceSeconds);
        _yamlReloadCoroutine = null;
        if (_yamlAuthorityMode == YamlAuthorityMode.LocalFiles)
        {
            ReloadLocalYaml();
        }
    }

    private void CancelPendingYamlReload()
    {
        if (_yamlReloadCoroutine == null)
        {
            return;
        }

        StopCoroutine(_yamlReloadCoroutine);
        _yamlReloadCoroutine = null;
    }

    private void ReloadLocalYaml()
    {
        if (_yamlAuthorityMode != YamlAuthorityMode.LocalFiles)
        {
            return;
        }

        GroundworkConfigLoader.EnsureLocalFileExists(YamlConfigDirectoryPath, TerrainToolsYamlFilePath);
        string yamlText = File.ReadAllText(TerrainToolsYamlFilePath);
        if (_syncedTerrainToolsYaml != null)
        {
            _suppressSyncedYamlChanged = true;
            try
            {
                _syncedTerrainToolsYaml.AssignLocalValue(yamlText);
            }
            finally
            {
                _suppressSyncedYamlChanged = false;
            }
        }

        ApplyYamlText(yamlText);
    }

    private void OnSyncedYamlChanged()
    {
        if (_suppressSyncedYamlChanged)
        {
            return;
        }

        ApplyYamlText(_syncedTerrainToolsYaml?.Value ?? "");
    }

    private void RefreshYamlAuthorityMode(bool force = false)
    {
        YamlAuthorityMode nextMode = ZNet.instance != null && !ZNet.instance.IsServer()
            ? YamlAuthorityMode.SyncedOnly
            : YamlAuthorityMode.LocalFiles;
        if (!force && nextMode == _yamlAuthorityMode)
        {
            return;
        }

        _yamlAuthorityMode = nextMode;
        switch (nextMode)
        {
            case YamlAuthorityMode.LocalFiles:
                SetupWatcher();
                ReloadLocalYaml();
                ModLogger.LogInfo("Groundwork YAML authority mode: LocalFiles.");
                break;
            case YamlAuthorityMode.SyncedOnly:
                DisposeWatcher();
                ApplyYamlText(_syncedTerrainToolsYaml?.Value ?? "");
                ModLogger.LogInfo("Groundwork YAML authority mode: SyncedOnly.");
                break;
        }
    }

    private static void ApplyYamlText(string yamlText)
    {
        if (!GroundworkConfigLoader.TryParseTerrainToolsYaml(yamlText, out IReadOnlyList<NormalizedTerrainToolConfig>? configs))
        {
            return;
        }

        _terrainTools = configs ?? Array.Empty<NormalizedTerrainToolConfig>();
        if (ObjectDB.instance != null)
        {
            // Both YAML domains can target the same Piece. Cultivation must run last,
            // including after restoring costs captured by an older terrain configuration.
            ApplyToObjectDb(ObjectDB.instance);
        }
    }

    private void InitializeSyncedYamlValue()
    {
        DisposeSyncedYamlValue();
        _syncedTerrainToolsYaml = new CustomSyncedValue<string>(ConfigSync, SyncedTerrainToolsYamlIdentifier, "");
        _syncedTerrainToolsYaml.ValueChanged += OnSyncedYamlChanged;
    }

    private void DisposeSyncedYamlValue()
    {
        if (_syncedTerrainToolsYaml != null)
        {
            _syncedTerrainToolsYaml.ValueChanged -= OnSyncedYamlChanged;
            _syncedTerrainToolsYaml = null;
        }
    }

    private void DisposeWatcher()
    {
        CancelPendingYamlReload();
        if (_watcher == null)
        {
            return;
        }

        _watcher.Dispose();
        _watcher = null;
    }

    internal sealed class PluginSettings
    {
        internal GeneralSettings General { get; } = new();

        internal TerrainToolSettings TerrainTools { get; } = new();

        internal FarmingSettings Farming { get; } = new();

        internal void Bind(GroundworkPlugin plugin)
        {
            General.Bind(plugin);
            TerrainTools.Bind(plugin);
            Farming.Bind(plugin);
        }
    }

    internal sealed class GeneralSettings
    {
        internal ConfigEntry<Toggle> LockConfiguration = null!;

        internal void Bind(GroundworkPlugin plugin)
        {
            const string group = "1 - General";
            LockConfiguration = plugin.config(group, "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
        }
    }

    internal sealed class TerrainToolSettings
    {
        internal ConfigEntry<float> TerrainToolRangeStep = null!;
        internal ConfigEntry<TerrainToolRangePreviewMode> DefaultPreviewMode = null!;
        internal ConfigEntry<KeyboardShortcut> TerrainToolPreviewToggleHotkey = null!;
        internal ConfigEntry<Toggle> ToolHud = null!;
        internal ConfigEntry<Toggle> TerrainHeightHint = null!;
        internal ConfigEntry<Toggle> PavedRoadSmoothHeight = null!;
        internal ConfigEntry<KeyboardShortcut> ToolWheelModifierHotkey = null!;

        internal void Bind(GroundworkPlugin plugin)
        {
            const string group = "2 - Terrain Tools";
            TerrainToolRangeStep = plugin.config(group, "Terrain Tool Range Step", 0.5f, new ConfigDescription("Range adjustment step for terrain tool pieces configured in Groundwork.yml. Hoe/Cultivator use meters, and Pickaxe terrainDig uses scale units.", new AcceptableValueRange<float>(0.05f, 5f)), synchronizedSetting: false);
            DefaultPreviewMode = plugin.config(group, "Terrain Tool Default Preview Mode", TerrainToolRangePreviewMode.Vanilla, "Default Hoe/Cultivator terrain range preview mode. Vanilla scales the existing placement ghost visuals. Grid hides those visuals and draws the exact radius plus terrain grid candidate markers.", synchronizedSetting: false);
            TerrainToolPreviewToggleHotkey = plugin.config(group, "Terrain Tool Preview Toggle Hotkey", new KeyboardShortcut(KeyCode.G), new ConfigDescription("Local hotkey for toggling Hoe/Cultivator terrain modifying pieces between Vanilla and Grid preview while placing.", new AcceptableShortcuts()), synchronizedSetting: false);
            ToolHud = plugin.config(group, "Tool HUD", Toggle.On, "If on, Hoe/Cultivator terrain range HUD, Pickaxe terrain dig scale key hint, and Pickaxe terrain dig tooltip are shown.", synchronizedSetting: false);
            TerrainHeightHint = plugin.config(group, "Terrain Height Hint", Toggle.On, "Show the standing-height and Shift+Click guidance above the build panel while using Level Ground or Paved Road. Independent of Tool HUD; Paved Road requires Paved Road Smooth Height.", synchronizedSetting: false);
            PavedRoadSmoothHeight = plugin.config(group, "Paved Road Smooth Height", Toggle.On, "If on, Paved Road applies its vanilla smooth height operation. Turn off to keep only the paved paint effect.", synchronizedSetting: false);
            ToolWheelModifierHotkey = plugin.config(group, "Tool Wheel Modifier Hotkey", new KeyboardShortcut(KeyCode.LeftAlt), new ConfigDescription("Local hotkey held while using mouse wheel for Groundwork tool features, including mass planting count, Hoe/Cultivator terrain range, and pickaxe terrainDig scale.", new AcceptableShortcuts()), synchronizedSetting: false);
        }
    }

    internal sealed class FarmingSettings
    {
        internal ConfigEntry<Toggle> MassPlantingEnabled = null!;
        internal ConfigEntry<float> MassPlantSpacingFactor = null!;
        internal ConfigEntry<float> MassPlantSkillGainFactor = null!;
        internal ConfigEntry<KeyboardShortcut> ToggleGridPlantingHotkey = null!;
        internal ConfigEntry<float> ForagingPickupMaxRange = null!;
        internal ConfigEntry<FarmingRangeHarvestTargetMode> RangeHarvestTargets = null!;
        internal ConfigEntry<Toggle> NaturalPickableRemoval = null!;
        internal ConfigEntry<string> NaturalPickableRemovalPrefabs = null!;
        internal ConfigEntry<string> ScytheHandleRequiredGlobalKey = null!;
        internal ConfigEntry<float> ForagingRespawnSpeedFactor = null!;
        internal ConfigEntry<float> PlantGrowSpeedFactor = null!;
        internal ConfigEntry<HoverHintMode> CropHoverHint = null!;
        internal ConfigEntry<HoverHintMode> ForagingHoverHint = null!;
        internal ConfigEntry<HoverHintMode> BeehiveHoverHint = null!;
        internal ConfigEntry<int> BeehiveCapacityFarmingLevelsPerBonusHoney = null!;
        internal ConfigEntry<float> BeehiveFarmingSkillGainPerHoney = null!;
        internal ConfigEntry<float> BeehiveCoverMaxSpeedMultiplier = null!;
        internal ConfigEntry<float> BeehiveNightHoneyRate = null!;
        internal ConfigEntry<float> BeehiveRainHoneyRate = null!;
        internal ConfigEntry<float> BeehivePollinationRadius = null!;
        internal ConfigEntry<int> BeehivePollinationMaxPlants = null!;
        internal ConfigEntry<Toggle> BeehivePollinationPreview = null!;
        internal ConfigEntry<float> BeehivePollinationPlantGrowSpeedFactor = null!;
        internal ConfigEntry<float> BeehivePollinationForagingRespawnSpeedFactor = null!;
        internal ConfigEntry<float> BeehivePollinationHoneySpeedBonusPercentPerTarget = null!;
        internal ConfigEntry<float> WetEnvironmentPlantGrowSpeedFactor = null!;
        internal ConfigEntry<float> WetEnvironmentForagingRespawnSpeedFactor = null!;

        internal void Bind(GroundworkPlugin plugin)
        {
            const string massPlantingGroup = "3 - Mass Planting";
            const string plantsAndForagingGroup = "4 - Plants and Foraging";
            const string beehivesGroup = "5 - Beehives";
            const string pollinationGroup = "6 - Pollination";
            const string scytheGroup = "7 - Scythe";

            MassPlantingEnabled = plugin.config(massPlantingGroup, "Mass Planting Enabled", Toggle.On, "If on, mass planting unlocks by Farming level: 0-19 off, 20-39 plants 5, 40-59 plants 10, 60-79 plants 15, 80-99 plants 20, and 100 plants 25. Grid planting is always available.", synchronizedSetting: true);
            ToggleGridPlantingHotkey = plugin.config(massPlantingGroup, "Toggle Grid Planting Hotkey", new KeyboardShortcut(KeyCode.G), new ConfigDescription("Local hotkey for toggling world-grid snapping while a plant piece is selected.", new AcceptableShortcuts()), synchronizedSetting: false);
            MassPlantSpacingFactor = plugin.config(massPlantingGroup, "Mass Plant Spacing Factor", 1.0f, new ConfigDescription("Multiplier for automatic mass-plant spacing. Base spacing is the selected Plant prefab growRadius * 2, so modded crops with their own growRadius are spaced automatically.", new AcceptableValueRange<float>(0.25f, 3f)), synchronizedSetting: true);
            MassPlantSkillGainFactor = plugin.config(massPlantingGroup, "Mass Plant Skill Gain Factor", 0.5f, new ConfigDescription("Additional Farming skill gain for mass planting. Vanilla grants one build-skill raise for the click; this adds (extra planted crops * factor). 0 keeps only the vanilla one-click skill gain.", new AcceptableValueRange<float>(0f, 5f)), synchronizedSetting: true);

            PlantGrowSpeedFactor = plugin.config(plantsAndForagingGroup, "Plant Grow Speed Factor", 2.5f, new ConfigDescription("Grow speed factor at Farming skill 100 for placed Plant prefabs. Newly planted crops store the planter's Farming skill. 0 disables this feature.", new AcceptableValueRange<float>(0f, 10f)), synchronizedSetting: true);
            ForagingPickupMaxRange = plugin.config(plantsAndForagingGroup, "Foraging Pickup Max Range", 5f, new ConfigDescription("Maximum nearby pickup range in meters at Farming skill 100 for targets selected by Farming Range Harvest Targets. 0 disables this feature.", new AcceptableValueRange<float>(0f, 10f)), synchronizedSetting: true);
            RangeHarvestTargets = plugin.config(plantsAndForagingGroup, "Farming Range Harvest Targets", FarmingRangeHarvestTargetMode.ForagingOnly, "Controls which Pickables the Farming-scaled nearby pickup affects. ForagingOnly includes positive-respawn targets that drop edible food or are explicitly enabled in BepInEx/config/Groundwork/pickables.yml. ForagingAndCrops also includes mature Pickables produced by Plant grown prefabs. Scythe harvesting is unaffected.", synchronizedSetting: true);
            NaturalPickableRemoval = plugin.config(plantsAndForagingGroup, "Natural Pickable Removal", Toggle.Off, "If on, the Cultivator can permanently remove natural Pickables whose exact prefab names are listed in Natural Pickable Removal Prefabs. Removal gives no items or harvest.", synchronizedSetting: true);
            NaturalPickableRemovalPrefabs = plugin.config(plantsAndForagingGroup, "Natural Pickable Removal Prefabs", "Pickable_Branch, Pickable_Flint, Pickable_Dandelion, Pickable_Mushroom, Pickable_Thistle", "Comma-separated exact prefab names eligible for Natural Pickable Removal. This list has no effect while that setting is off.", synchronizedSetting: true);
            ForagingRespawnSpeedFactor = plugin.config(plantsAndForagingGroup, "Foraging Respawn Speed Factor", 5f, new ConfigDescription("Respawn speed factor at Farming skill 100 for foraging-style pickables. Targets need a positive base respawn time and must either drop edible food or be explicitly enabled in BepInEx/config/Groundwork/pickables.yml. 0 disables this feature.", new AcceptableValueRange<float>(0f, 20f)), synchronizedSetting: true);
            WetEnvironmentPlantGrowSpeedFactor = plugin.config(plantsAndForagingGroup, "Rain Plant Grow Speed Factor", 2f, new ConfigDescription("Plant growth speed factor while the current environment is wet. 1 disables this bonus.", new AcceptableValueRange<float>(1f, 10f)), synchronizedSetting: true);
            WetEnvironmentForagingRespawnSpeedFactor = plugin.config(plantsAndForagingGroup, "Rain Foraging Respawn Speed Factor", 2f, new ConfigDescription("Foraging respawn speed factor while the current environment is wet. Applies to automatic edible targets and prefabs explicitly enabled in BepInEx/config/Groundwork/pickables.yml. 1 disables this bonus.", new AcceptableValueRange<float>(1f, 10f)), synchronizedSetting: true);
            CropHoverHint = plugin.config(plantsAndForagingGroup, "Crop Hover Hint", HoverHintMode.Detailed, "Controls Groundwork details added to growing crop hover text. Off keeps vanilla text, Compact adds only remaining growth time, and Detailed also adds active Farming, pollination, and rain multipliers.", synchronizedSetting: false);
            ForagingHoverHint = plugin.config(plantsAndForagingGroup, "Foraging Hover Hint", HoverHintMode.Detailed, "Controls Groundwork details shown for picked respawning forage targets. Off keeps only the target name when Groundwork must provide a hidden hover proxy, Compact adds only remaining respawn time, and Detailed also adds active Farming, pollination, and rain multipliers. Pollination and target discovery remain active in every mode.", synchronizedSetting: false);

            BeehiveHoverHint = plugin.config(beehivesGroup, "Beehive Hover Hint", HoverHintMode.Detailed, "Controls Groundwork details added to beehive hover text. Off keeps vanilla text, Compact adds capacity, production modifiers, and next-honey timing, and Detailed also explains how the configured bonuses work. Honey production, growth bonuses, and Farming effects remain active in every mode.", synchronizedSetting: false);
            BeehiveCapacityFarmingLevelsPerBonusHoney = plugin.config(beehivesGroup, "Beehive Capacity Farming Levels Per Bonus Honey", 20, new ConfigDescription("Farming levels required for each +1 beehive honey capacity. 0 disables the capacity bonus.", new AcceptableValueRange<int>(0, 100)), synchronizedSetting: true);
            BeehiveFarmingSkillGainPerHoney = plugin.config(beehivesGroup, "Beehive Farming Skill Gain Per Honey", 0.25f, new ConfigDescription("Farming skill gain for each honey harvested from a beehive. 0 disables this bonus.", new AcceptableValueRange<float>(0f, 5f)), synchronizedSetting: true);
            BeehiveCoverMaxSpeedMultiplier = plugin.config(beehivesGroup, "Beehive Cover Max Speed Multiplier", 3f, new ConfigDescription("Honey production multiplier at 0% cover. The bonus scales linearly from x1 at the beehive max cover threshold to this value when fully open.", new AcceptableValueRange<float>(1f, 10f)), synchronizedSetting: true);
            BeehiveNightHoneyRate = plugin.config(beehivesGroup, "Beehive Night Honey Rate", 0.5f, new ConfigDescription("Honey production rate at night. 1 is the vanilla value, 0.5 makes night production half speed, and 0 pauses night production. Unloaded catch-up uses an average day/night rate.", new AcceptableValueRange<float>(0f, 1f)), synchronizedSetting: true);
            BeehiveRainHoneyRate = plugin.config(beehivesGroup, "Beehive Rain Honey Rate", 0.5f, new ConfigDescription("Honey production rate while the current environment is wet. 1 is the vanilla value, 0.5 makes rain production half speed, and 0 pauses rain production. Rain is not accumulated during unloaded catch-up.", new AcceptableValueRange<float>(0f, 1f)), synchronizedSetting: true);
            BeehivePollinationPreview = plugin.config(pollinationGroup, "Beehive Pollination Preview", Toggle.On, "If on, hovering a beehive draws both its centered terrain range guide and marker highlights around assigned Plant and foraging Pickable targets. Raising the beehive shrinks the range guide, and slopes change it by direction. If the ground directly below is outside the sphere, the centered guide is hidden. The range is gray while unavailable or paused; assigned target markers are gray while rain or night pauses pollination. Target colliders and assignment still determine the actual affected objects.", synchronizedSetting: false);
            BeehivePollinationRadius = plugin.config(pollinationGroup, "Beehive Pollination Radius", 3f, new ConfigDescription("Radius in meters for beehive pollination bonuses. 0 disables plant and foraging growth bonuses from beehives.", new AcceptableValueRange<float>(0f, 20f)), synchronizedSetting: true);
            BeehivePollinationMaxPlants = plugin.config(pollinationGroup, "Beehive Pollination Max Plants", 24, new ConfigDescription("Maximum nearby growing plants or foraging targets counted by one beehive for pollination.", new AcceptableValueRange<int>(0, 100)), synchronizedSetting: true);
            BeehivePollinationPlantGrowSpeedFactor = plugin.config(pollinationGroup, "Beehive Pollination Plant Grow Speed Factor", 2f, new ConfigDescription("Plant growth speed factor near an empty active beehive. The bonus fades to x1 as the beehive fills with honey.", new AcceptableValueRange<float>(1f, 10f)), synchronizedSetting: true);
            BeehivePollinationForagingRespawnSpeedFactor = plugin.config(pollinationGroup, "Beehive Pollination Foraging Respawn Speed Factor", 4f, new ConfigDescription("Foraging respawn speed factor near an empty active beehive. The bonus fades to x1 as the beehive fills with honey.", new AcceptableValueRange<float>(1f, 10f)), synchronizedSetting: true);
            BeehivePollinationHoneySpeedBonusPercentPerTarget = plugin.config(pollinationGroup, "Beehive Pollination Honey Speed Bonus Percent Per Target", 10f, new ConfigDescription("Additional honey production speed percent per counted pollination target. For example, 10 means each target adds +10%, so 24 targets gives Honey rate x3.4.", new AcceptableValueRange<float>(0f, 100f)), synchronizedSetting: true);

            ScytheHandleRequiredGlobalKey = plugin.config(scytheGroup, "Scythe Handle Required Global Key", "defeated_bonemass", "Global key required for the Bog Witch to sell ScytheHandle. Vanilla uses defeated_dragon; Groundwork defaults to defeated_bonemass. For per-player progression and discovery of additional global keys, see YouAreNotWorthy.", synchronizedSetting: true);
        }
    }

    private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
    {
        object[] tags = description.Tags
            .Concat([new ConfigurationManagerAttributes { Order = -_configurationManagerOrder++ }])
            .ToArray();
        ConfigDescription extendedDescription = new(description.Description + (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"), description.AcceptableValues, tags);
        ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);
        SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
        syncedConfigEntry.SynchronizedConfig = synchronizedSetting;
        return configEntry;
    }

    private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true)
    {
        return config(group, name, value, new ConfigDescription(description), synchronizedSetting);
    }

    private class AcceptableShortcuts() : AcceptableValueBase(typeof(KeyboardShortcut))
    {
        public override object Clamp(object value) => value;
        public override bool IsValid(object value) => true;

        public override string ToDescriptionString() => $"# Acceptable values: {string.Join(", ", UnityInput.Current.SupportedKeyCodes)}";
    }

    private class ConfigurationManagerAttributes
    {
        [UsedImplicitly] public int? Order = null!;
    }
}

public static class KeyboardExtensions
{
    public static bool IsKeyDown(this KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None &&
               Input.GetKeyDown(shortcut.MainKey) &&
               shortcut.Modifiers.All(Input.GetKey);
    }

    public static bool IsKeyHeld(this KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None &&
               Input.GetKey(shortcut.MainKey) &&
               shortcut.Modifiers.All(Input.GetKey);
    }
}
