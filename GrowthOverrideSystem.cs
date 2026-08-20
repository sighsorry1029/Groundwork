using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using ServerSync;
using UnityEngine;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Groundwork;

internal static class GrowthOverrideSystem
{
    private const string PickablesOverrideFileName = "pickables.yml";
    private const string PickablesReferenceFileName = "pickables.reference.yml";
    private const string PlantsOverrideFileName = "plants.yml";
    private const string PlantsReferenceFileName = "plants.reference.yml";
    private const string SyncedYamlIdentifier = "groundwork_growth_yaml";
    private const double ReloadDebounceMilliseconds = 350d;
    private const float SceneSettleDelaySeconds = 1f;
    private const uint VanillaBiomeBits = 0x37Fu;

    private static readonly FarmingTupleYamlConverter FarmingTupleConverter = new();
    private static readonly BiomeListYamlConverter BiomeListConverter = new();
    private static readonly FieldInfo? PlacementGhostField = typeof(Player).GetField(
        "m_placementGhost",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private delegate bool TryGetBiomeDelegate(string name, out Heightmap.Biome biome);

    private delegate Heightmap.Biome GetNatureDelegate(Heightmap.Biome biome);

    private static TryGetBiomeDelegate? _expandWorldDataTryGetBiome;
    private static GetNatureDelegate? _expandWorldDataGetNature;
    private static bool _expandWorldDataBridgeInitialized;
    private static bool _expandWorldDataBridgeWarningLogged;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .WithTypeConverter(FarmingTupleConverter)
        .WithTypeConverter(BiomeListConverter)
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(FarmingTupleConverter)
        .WithTypeConverter(BiomeListConverter)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .DisableAliases()
        .Build();

    private static Dictionary<string, PickableGrowthOverride> _pickableRules =
        new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, PlantGrowthOverride> _plantRules =
        new(StringComparer.OrdinalIgnoreCase);

    private static GroundworkPlugin? _owner;
    private static ConfigSync? _configSync;
    private static CustomSyncedValue<string>? _syncedYaml;
    private static FileSystemWatcher? _watcher;
    private static System.Timers.Timer? _reloadTimer;
    private static Coroutine? _sceneRefreshCoroutine;
    private static ZNetScene? _scene;
    private static AuthorityMode _authorityMode;
    private static string? _lastAppliedNormalizedYaml;

    private static string ConfigDirectoryPath => GroundworkPlugin.YamlConfigDirectoryPath;

    private static string PickablesOverrideFilePath =>
        Path.Combine(ConfigDirectoryPath, PickablesOverrideFileName);

    private static string PickablesReferenceFilePath =>
        Path.Combine(ConfigDirectoryPath, PickablesReferenceFileName);

    private static string PlantsOverrideFilePath =>
        Path.Combine(ConfigDirectoryPath, PlantsOverrideFileName);

    private static string PlantsReferenceFilePath =>
        Path.Combine(ConfigDirectoryPath, PlantsReferenceFileName);

    private enum AuthorityMode
    {
        Unknown,
        LocalFiles,
        SyncedOnly
    }

    internal readonly struct ResolvedPickableRule
    {
        internal ResolvedPickableRule(
            bool hasRespawnOverride,
            float respawnMinutes,
            bool? foragingTarget,
            bool? bonusYield,
            bool hasMaxChance,
            float maxChanceAtLevel100,
            bool hasBonusAmount,
            int bonusAmount)
        {
            HasRespawnOverride = hasRespawnOverride;
            RespawnMinutes = respawnMinutes;
            ForagingTarget = foragingTarget;
            BonusYield = bonusYield;
            HasMaxChance = hasMaxChance;
            MaxChanceAtLevel100 = maxChanceAtLevel100;
            HasBonusAmount = hasBonusAmount;
            BonusAmount = bonusAmount;
        }

        internal readonly bool HasRespawnOverride;
        internal readonly float RespawnMinutes;
        internal readonly bool? ForagingTarget;
        internal readonly bool? BonusYield;
        internal readonly bool HasMaxChance;
        internal readonly float MaxChanceAtLevel100;
        internal readonly bool HasBonusAmount;
        internal readonly int BonusAmount;
    }

    internal readonly struct ResolvedPlantRule
    {
        internal ResolvedPlantRule(
            bool hasGrowTimeOverride,
            float growSecondsMin,
            float growSecondsMax,
            bool hasBiomeOverride,
            Heightmap.Biome biomeMask)
        {
            HasGrowTimeOverride = hasGrowTimeOverride;
            GrowSecondsMin = growSecondsMin;
            GrowSecondsMax = growSecondsMax;
            HasBiomeOverride = hasBiomeOverride;
            BiomeMask = biomeMask;
        }

        internal readonly bool HasGrowTimeOverride;
        internal readonly float GrowSecondsMin;
        internal readonly float GrowSecondsMax;
        internal readonly bool HasBiomeOverride;
        internal readonly Heightmap.Biome BiomeMask;
    }

    internal readonly struct PlantBiomeOverrideState
    {
        internal PlantBiomeOverrideState(
            Plant plant,
            Heightmap.Biome originalBiome,
            Heightmap.Biome appliedBiome)
        {
            Plant = plant;
            OriginalBiome = originalBiome;
            AppliedBiome = appliedBiome;
        }

        internal Plant? Plant { get; }

        internal Heightmap.Biome OriginalBiome { get; }

        internal Heightmap.Biome AppliedBiome { get; }
    }

    internal readonly struct PieceBiomeOverrideState
    {
        internal PieceBiomeOverrideState(
            Piece piece,
            Heightmap.Biome originalBiome,
            Heightmap.Biome appliedBiome)
        {
            Piece = piece;
            OriginalBiome = originalBiome;
            AppliedBiome = appliedBiome;
        }

        internal Piece? Piece { get; }

        internal Heightmap.Biome OriginalBiome { get; }

        internal Heightmap.Biome AppliedBiome { get; }
    }

    internal static void Initialize(GroundworkPlugin owner, ConfigSync configSync)
    {
        _owner = owner;
        _configSync = configSync;
        _configSync.SourceOfTruthChanged += OnSourceOfTruthChanged;
        _syncedYaml = new CustomSyncedValue<string>(configSync, SyncedYamlIdentifier, "");
        _syncedYaml.ValueChanged += OnSyncedYamlChanged;
        RefreshAuthority(force: true);
    }

    internal static void Shutdown()
    {
        StopSceneRefreshCoroutine();
        DisposeWatcher();

        if (_syncedYaml != null)
        {
            _syncedYaml.ValueChanged -= OnSyncedYamlChanged;
            _syncedYaml = null;
        }

        if (_configSync != null)
        {
            _configSync.SourceOfTruthChanged -= OnSourceOfTruthChanged;
            _configSync = null;
        }

        _pickableRules = new Dictionary<string, PickableGrowthOverride>(StringComparer.OrdinalIgnoreCase);
        _plantRules = new Dictionary<string, PlantGrowthOverride>(StringComparer.OrdinalIgnoreCase);
        _expandWorldDataTryGetBiome = null;
        _expandWorldDataGetNature = null;
        _expandWorldDataBridgeInitialized = false;
        _expandWorldDataBridgeWarningLogged = false;
        _lastAppliedNormalizedYaml = null;
        _scene = null;
        _owner = null;
        _authorityMode = AuthorityMode.Unknown;
    }

    internal static void RefreshAuthority(bool force = false)
    {
        AuthorityMode nextMode = UsesLocalAuthorityFiles()
            ? AuthorityMode.LocalFiles
            : AuthorityMode.SyncedOnly;
        if (!force && nextMode == _authorityMode)
        {
            return;
        }

        _authorityMode = nextMode;
        switch (nextMode)
        {
            case AuthorityMode.LocalFiles:
                SetupWatcher();
                ReloadFromDiskAndSync();
                GroundworkPlugin.ModLogger.LogInfo("Groundwork growth YAML authority mode: LocalFiles.");
                break;
            case AuthorityMode.SyncedOnly:
                DisposeWatcher();
                ApplySyncedYamlText(
                    _syncedYaml?.Value ?? "",
                    "server-synced growth configuration");
                GroundworkPlugin.ModLogger.LogInfo("Groundwork growth YAML authority mode: SyncedOnly.");
                break;
        }
    }

    internal static void OnZNetSceneReady(ZNetScene scene)
    {
        if (scene == null)
        {
            return;
        }

        _scene = scene;
        RefreshAuthority();
        ScheduleSceneSettledRefresh(scene);
    }

    internal static bool TryGetPickableRule(Pickable? pickable, out ResolvedPickableRule resolved)
    {
        resolved = default;
        if (pickable == null ||
            !TryGetPrefabName(pickable, out string prefabName) ||
            !_pickableRules.TryGetValue(prefabName, out PickableGrowthOverride? rule))
        {
            return false;
        }

        float rawRespawnMinutes = pickable.m_respawnTimeMinutes;
        bool hasRespawnOverride = rule.RespawnMinutes.HasValue &&
                                  IsFinitePositive(rawRespawnMinutes);
        resolved = new ResolvedPickableRule(
            hasRespawnOverride,
            hasRespawnOverride ? rule.RespawnMinutes!.Value : rawRespawnMinutes,
            rule.Farming?.ForagingTarget,
            rule.Farming?.BonusYield,
            rule.Farming?.MaxChanceAtLevel100.HasValue == true,
            rule.Farming?.MaxChanceAtLevel100 ?? pickable.m_maxLevelBonusChance,
            rule.Farming?.BonusAmount.HasValue == true,
            rule.Farming?.BonusAmount ?? pickable.m_bonusYieldAmount);
        return true;
    }

    internal static bool TryGetPlantRule(Plant? plant, out ResolvedPlantRule resolved)
    {
        resolved = default;
        if (plant == null ||
            !TryGetPrefabName(plant, out string prefabName) ||
            !_plantRules.TryGetValue(prefabName, out PlantGrowthOverride? rule))
        {
            return false;
        }

        bool hasBiomeOverride = TryResolveBiomeMask(rule, out Heightmap.Biome biomeMask);
        resolved = new ResolvedPlantRule(
            rule.GrowSecondsMin.HasValue && rule.GrowSecondsMax.HasValue,
            rule.GrowSecondsMin ?? plant.m_growTime,
            rule.GrowSecondsMax ?? plant.m_growTimeMax,
            hasBiomeOverride,
            biomeMask);
        return true;
    }

    internal static PlantBiomeOverrideState BeginPlantHealthBiomeOverride(Plant? plant)
    {
        if (plant == null ||
            !TryGetPlantRule(plant, out ResolvedPlantRule rule) ||
            !rule.HasBiomeOverride ||
            plant.m_biome == rule.BiomeMask)
        {
            return default;
        }

        PlantBiomeOverrideState state = new(plant, plant.m_biome, rule.BiomeMask);
        plant.m_biome = rule.BiomeMask;
        return state;
    }

    internal static void EndPlantHealthBiomeOverride(PlantBiomeOverrideState state)
    {
        if (state.Plant != null && state.Plant.m_biome == state.AppliedBiome)
        {
            state.Plant.m_biome = state.OriginalBiome;
        }
    }

    internal static PieceBiomeOverrideState BeginPlacementBiomeOverride(Player? player)
    {
        if (player == null || PlacementGhostField?.GetValue(player) is not GameObject placementGhost)
        {
            return default;
        }

        Piece? piece = placementGhost.GetComponent<Piece>() ??
                       placementGhost.GetComponentInChildren<Piece>(includeInactive: true);
        Plant? plant = placementGhost.GetComponent<Plant>() ??
                       placementGhost.GetComponentInChildren<Plant>(includeInactive: true);
        if (piece == null ||
            plant == null ||
            !TryGetPlantRule(plant, out ResolvedPlantRule rule) ||
            !rule.HasBiomeOverride ||
            piece.m_onlyInBiome == rule.BiomeMask)
        {
            return default;
        }

        PieceBiomeOverrideState state = new(piece, piece.m_onlyInBiome, rule.BiomeMask);
        piece.m_onlyInBiome = rule.BiomeMask;
        return state;
    }

    internal static void EndPlacementBiomeOverride(PieceBiomeOverrideState state)
    {
        if (state.Piece != null && state.Piece.m_onlyInBiome == state.AppliedBiome)
        {
            state.Piece.m_onlyInBiome = state.OriginalBiome;
        }
    }

    internal static bool IsPlantBiomeAllowed(
        Plant? plant,
        Heightmap.Biome liveAllowedBiomes,
        Heightmap? heightmap,
        Vector3 position)
    {
        Heightmap.Biome allowedBiomes = liveAllowedBiomes;
        if (TryGetPlantRule(plant, out ResolvedPlantRule rule) && rule.HasBiomeOverride)
        {
            allowedBiomes = rule.BiomeMask;
        }

        if (allowedBiomes == Heightmap.Biome.None)
        {
            return true;
        }

        Heightmap.Biome currentBiome = heightmap != null
            ? heightmap.GetBiome(position)
            : Heightmap.FindBiome(position);
        currentBiome = ResolveEffectiveNature(currentBiome);
        return (currentBiome & allowedBiomes) != 0;
    }

    private static bool UsesLocalAuthorityFiles()
    {
        if (_configSync?.IsSourceOfTruth != true)
        {
            return false;
        }

        return !ZNet.HasServerHost() ||
               ZNet.instance != null && ZNet.instance.IsServer();
    }

    private static void OnSourceOfTruthChanged(bool _)
    {
        RefreshAuthority(force: true);
    }

    private static void SetupWatcher()
    {
        EnsureOverrideFilesExist();
        if (_watcher != null)
        {
            return;
        }

        _reloadTimer = new System.Timers.Timer(ReloadDebounceMilliseconds)
        {
            AutoReset = false,
            SynchronizingObject = ThreadingHelper.SynchronizingObject
        };
        _reloadTimer.Elapsed += OnReloadTimerElapsed;

        _watcher = new FileSystemWatcher(ConfigDirectoryPath, "*.yml")
        {
            IncludeSubdirectories = false,
            SynchronizingObject = ThreadingHelper.SynchronizingObject
        };
        _watcher.Changed += OnOverrideFileChanged;
        _watcher.Created += OnOverrideFileChanged;
        _watcher.Deleted += OnOverrideFileChanged;
        _watcher.Renamed += OnOverrideFileChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private static void DisposeWatcher()
    {
        if (_watcher != null)
        {
            _watcher.Dispose();
            _watcher = null;
        }

        if (_reloadTimer != null)
        {
            _reloadTimer.Stop();
            _reloadTimer.Elapsed -= OnReloadTimerElapsed;
            _reloadTimer.Dispose();
            _reloadTimer = null;
        }
    }

    private static void OnOverrideFileChanged(object sender, FileSystemEventArgs args)
    {
        if (_authorityMode != AuthorityMode.LocalFiles ||
            _reloadTimer == null ||
            !IsOverrideFileChange(args))
        {
            return;
        }

        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private static void OnReloadTimerElapsed(object sender, System.Timers.ElapsedEventArgs args)
    {
        if (_authorityMode == AuthorityMode.LocalFiles)
        {
            ReloadFromDiskAndSync();
        }
    }

    private static void ReloadFromDiskAndSync()
    {
        if (_authorityMode != AuthorityMode.LocalFiles)
        {
            return;
        }

        try
        {
            EnsureOverrideFilesExist();
            ApplyFileTexts(
                File.ReadAllText(PickablesOverrideFilePath),
                File.ReadAllText(PlantsOverrideFilePath),
                publish: true,
                $"{PickablesOverrideFilePath} and {PlantsOverrideFilePath}");
        }
        catch (Exception exception)
        {
            GroundworkPlugin.ModLogger.LogError(
                $"Could not reload {PickablesOverrideFileName} and {PlantsOverrideFileName}; " +
                "keeping the last-known-good growth configuration. " +
                exception.GetBaseException().Message);
        }
    }

    private static void OnSyncedYamlChanged()
    {
        if (_authorityMode == AuthorityMode.SyncedOnly)
        {
            ApplySyncedYamlText(
                _syncedYaml?.Value ?? "",
                "server-synced growth configuration");
        }
    }

    private static bool IsOverrideFileChange(FileSystemEventArgs args)
    {
        if (IsOverrideFilePath(args.FullPath))
        {
            return true;
        }

        return args is RenamedEventArgs renamed && IsOverrideFilePath(renamed.OldFullPath);
    }

    private static bool IsOverrideFilePath(string path)
    {
        string fileName = Path.GetFileName(path);
        return fileName.Equals(PickablesOverrideFileName, StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals(PlantsOverrideFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyFileTexts(
        string pickablesYaml,
        string plantsYaml,
        bool publish,
        string source)
    {
        if (!TryParseAndNormalizeFiles(
                pickablesYaml,
                plantsYaml,
                out Dictionary<string, PickableGrowthOverride> pickableRules,
                out Dictionary<string, PlantGrowthOverride> plantRules,
                out string normalizedYaml,
                out string error))
        {
            LogParseFailure(source, error);
            return;
        }

        CommitParsedRules(pickableRules, plantRules, normalizedYaml);
        if (publish &&
            _syncedYaml != null &&
            !string.Equals(_syncedYaml.Value ?? "", normalizedYaml, StringComparison.Ordinal))
        {
            _syncedYaml.AssignLocalValue(normalizedYaml);
        }
    }

    private static void ApplySyncedYamlText(string yamlText, string source)
    {
        if (!TryParseAndNormalizeSyncedDocument(
                yamlText,
                out Dictionary<string, PickableGrowthOverride> pickableRules,
                out Dictionary<string, PlantGrowthOverride> plantRules,
                out string normalizedYaml,
                out string error))
        {
            LogParseFailure(source, error);
            return;
        }

        CommitParsedRules(pickableRules, plantRules, normalizedYaml);
    }

    private static void CommitParsedRules(
        Dictionary<string, PickableGrowthOverride> pickableRules,
        Dictionary<string, PlantGrowthOverride> plantRules,
        string normalizedYaml)
    {
        if (string.Equals(normalizedYaml, _lastAppliedNormalizedYaml, StringComparison.Ordinal))
        {
            return;
        }

        _pickableRules = pickableRules;
        _plantRules = plantRules;
        _lastAppliedNormalizedYaml = normalizedYaml;
        PickableRespawnHoverSystem.RefreshLoadedMarkers();
        BeehivePollinationSystem.InvalidateTargetCaches();
    }

    private static void LogParseFailure(string source, string error)
    {
        GroundworkPlugin.ModLogger.LogError(
            $"Could not parse {source}; keeping the last-known-good growth configuration. {error}");
    }

    private static bool TryParseAndNormalizeFiles(
        string pickablesYaml,
        string plantsYaml,
        out Dictionary<string, PickableGrowthOverride> pickableRules,
        out Dictionary<string, PlantGrowthOverride> plantRules,
        out string normalizedYaml,
        out string error)
    {
        pickableRules = new Dictionary<string, PickableGrowthOverride>(StringComparer.OrdinalIgnoreCase);
        plantRules = new Dictionary<string, PlantGrowthOverride>(StringComparer.OrdinalIgnoreCase);
        normalizedYaml = "";
        error = "";

        try
        {
            List<PickableGrowthEntry> pickables = DeserializeRootSequence<PickableGrowthEntry>(
                pickablesYaml,
                PickablesOverrideFileName);
            List<PlantGrowthEntry> plants = DeserializeRootSequence<PlantGrowthEntry>(
                plantsYaml,
                PlantsOverrideFileName);
            NormalizeEntries(
                pickables,
                plants,
                out pickableRules,
                out plantRules,
                out normalizedYaml);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    private static bool TryParseAndNormalizeSyncedDocument(
        string yamlText,
        out Dictionary<string, PickableGrowthOverride> pickableRules,
        out Dictionary<string, PlantGrowthOverride> plantRules,
        out string normalizedYaml,
        out string error)
    {
        pickableRules = new Dictionary<string, PickableGrowthOverride>(StringComparer.OrdinalIgnoreCase);
        plantRules = new Dictionary<string, PlantGrowthOverride>(StringComparer.OrdinalIgnoreCase);
        normalizedYaml = "";
        error = "";

        try
        {
            GrowthOverrideDocument parsed = string.IsNullOrWhiteSpace(yamlText)
                ? new GrowthOverrideDocument()
                : Deserializer.Deserialize<GrowthOverrideDocument>(yamlText) ??
                  throw new InvalidDataException("The synced growth document cannot be null.");
            if (parsed.Pickables == null || parsed.Plants == null)
            {
                throw new InvalidDataException(
                    "The synced growth document must contain non-null pickables and plants sequences.");
            }

            NormalizeEntries(
                parsed.Pickables,
                parsed.Plants,
                out pickableRules,
                out plantRules,
                out normalizedYaml);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    private static List<T> DeserializeRootSequence<T>(string yamlText, string fileName)
    {
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            return [];
        }

        return Deserializer.Deserialize<List<T>>(yamlText) ??
               throw new InvalidDataException($"{fileName} must contain a YAML sequence, not null.");
    }

    private static void NormalizeEntries(
        IEnumerable<PickableGrowthEntry> pickables,
        IEnumerable<PlantGrowthEntry> plants,
        out Dictionary<string, PickableGrowthOverride> pickableRules,
        out Dictionary<string, PlantGrowthOverride> plantRules,
        out string normalizedYaml)
    {
        pickableRules = new Dictionary<string, PickableGrowthOverride>(StringComparer.OrdinalIgnoreCase);
        plantRules = new Dictionary<string, PlantGrowthOverride>(StringComparer.OrdinalIgnoreCase);

        foreach (PickableGrowthEntry raw in pickables)
        {
            PickableGrowthOverride entry = NormalizePickable(raw);
            AddUnique(pickableRules, entry.Prefab, entry, "Pickable");
        }

        foreach (PlantGrowthEntry raw in plants)
        {
            PlantGrowthOverride entry = NormalizePlant(raw);
            AddUnique(plantRules, entry.Prefab, entry, "Plant");
        }

        GrowthOverrideDocument normalized = new()
        {
            Pickables = pickableRules.Values
                .OrderBy(entry => entry.Prefab, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Prefab, StringComparer.Ordinal)
                .Select(ToDocumentEntry)
                .ToList(),
            Plants = plantRules.Values
                .OrderBy(entry => entry.Prefab, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Prefab, StringComparer.Ordinal)
                .Select(ToDocumentEntry)
                .ToList()
        };
        normalizedYaml = CanonicalizeYaml(Serializer.Serialize(normalized));
    }

    private static string CanonicalizeYaml(string yaml)
    {
        return yaml.Replace("\r\n", "\n")
                   .Replace('\r', '\n')
                   .TrimEnd(new[] { '\n' }) + "\n";
    }

    private static PickableGrowthOverride NormalizePickable(PickableGrowthEntry raw)
    {
        if (raw == null)
        {
            throw new InvalidDataException("Pickable entries cannot be null.");
        }

        string tuple = RequireTuple(raw.Prefab, "Pickable");
        string[] tupleParts = tuple.Split(new[] { ',' }, StringSplitOptions.None);
        if (tupleParts.Length is < 1 or > 2)
        {
            throw new InvalidDataException(
                $"Pickable prefab tuple '{tuple}' must be '<prefab>' or '<prefab>, <respawnMinutes>'.");
        }

        string prefab = RequirePrefab(tupleParts[0], "Pickable");
        float? respawnMinutes = null;
        if (tupleParts.Length == 2)
        {
            respawnMinutes = ParsePositiveFloat(
                tupleParts[1],
                $"Pickable '{prefab}' respawnMinutes");
        }

        return new PickableGrowthOverride
        {
            Prefab = prefab,
            RespawnMinutes = respawnMinutes,
            Farming = raw.Farming
        };
    }

    private static PlantGrowthOverride NormalizePlant(PlantGrowthEntry raw)
    {
        if (raw == null)
        {
            throw new InvalidDataException("Plant entries cannot be null.");
        }

        string tuple = RequireTuple(raw.Prefab, "Plant");
        string[] tupleParts = tuple.Split(new[] { ',' }, StringSplitOptions.None);
        if (tupleParts.Length is < 1 or > 2)
        {
            throw new InvalidDataException(
                $"Plant prefab tuple '{tuple}' must be '<prefab>' or " +
                "'<prefab>, <growSecondsMin>~<growSecondsMax>'.");
        }

        string prefab = RequirePrefab(tupleParts[0], "Plant");
        float? growSecondsMin = null;
        float? growSecondsMax = null;
        if (tupleParts.Length == 2)
        {
            string[] rangeParts = tupleParts[1].Split(new[] { '~' }, StringSplitOptions.None);
            if (rangeParts.Length != 2)
            {
                throw new InvalidDataException(
                    $"Plant '{prefab}' grow-time range must be '<growSecondsMin>~<growSecondsMax>'.");
            }

            growSecondsMin = ParsePositiveFloat(
                rangeParts[0],
                $"Plant '{prefab}' growSecondsMin");
            growSecondsMax = ParsePositiveFloat(
                rangeParts[1],
                $"Plant '{prefab}' growSecondsMax");

            if (growSecondsMax < growSecondsMin)
            {
                throw new InvalidDataException(
                    $"Plant '{prefab}' growSecondsMax must be greater than or equal to growSecondsMin.");
            }
        }

        PlantBiomeList? biomes = NormalizeBiomeList(raw.Biomes, prefab);
        if (!growSecondsMin.HasValue && biomes == null)
        {
            throw new InvalidDataException(
                $"Plant '{prefab}' must override grow time, biomes, or both.");
        }

        return new PlantGrowthOverride
        {
            Prefab = prefab,
            GrowSecondsMin = growSecondsMin,
            GrowSecondsMax = growSecondsMax,
            Biomes = biomes
        };
    }

    private static PickableGrowthEntry ToDocumentEntry(PickableGrowthOverride entry)
    {
        return new PickableGrowthEntry
        {
            Prefab = FormatPickableTuple(entry),
            Farming = entry.Farming
        };
    }

    private static PlantGrowthEntry ToDocumentEntry(PlantGrowthOverride entry)
    {
        return new PlantGrowthEntry
        {
            Prefab = entry.GrowSecondsMin.HasValue && entry.GrowSecondsMax.HasValue
                ? $"{entry.Prefab}, {FormatFloat(entry.GrowSecondsMin.Value)}~{FormatFloat(entry.GrowSecondsMax.Value)}"
                : entry.Prefab,
            Biomes = entry.Biomes
        };
    }

    private static PlantBiomeList? NormalizeBiomeList(PlantBiomeList? raw, string prefab)
    {
        if (raw == null)
        {
            return null;
        }

        if (raw.Names == null || raw.Names.Count == 0)
        {
            throw new InvalidDataException(
                $"Plant '{prefab}' biomes must contain at least one biome name.");
        }

        List<string> names = new(raw.Names.Count);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? rawName in raw.Names)
        {
            string name = (rawName ?? "").Trim();
            if (name.Length == 0 || name.Any(char.IsControl))
            {
                throw new InvalidDataException(
                    $"Plant '{prefab}' biome names must be non-empty and cannot contain control characters.");
            }

            if (name.Equals(nameof(Heightmap.Biome.None), StringComparison.OrdinalIgnoreCase) ||
                name.Equals(nameof(Heightmap.Biome.All), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Plant '{prefab}' biome '{name}' is not supported; list explicit biome names instead.");
            }

            if (long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                throw new InvalidDataException(
                    $"Plant '{prefab}' biome values must use names, not numeric masks.");
            }

            if (!seen.Add(name))
            {
                throw new InvalidDataException(
                    $"Plant '{prefab}' contains duplicate biome name '{name}'.");
            }

            names.Add(name);
        }

        return new PlantBiomeList { Names = names };
    }

    private static string FormatPickableTuple(PickableGrowthOverride entry)
    {
        return entry.RespawnMinutes.HasValue
            ? $"{entry.Prefab}, {FormatFloat(entry.RespawnMinutes.Value)}"
            : entry.Prefab;
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string RequireTuple(string? tuple, string kind)
    {
        if (string.IsNullOrWhiteSpace(tuple))
        {
            throw new InvalidDataException($"{kind} entries require a prefab tuple.");
        }

        return tuple!.Trim();
    }

    private static string RequirePrefab(string? prefab, string kind)
    {
        if (string.IsNullOrWhiteSpace(prefab))
        {
            throw new InvalidDataException($"{kind} entries require a prefab name.");
        }

        string normalized = prefab!.Trim();
        if (normalized.Any(char.IsControl))
        {
            throw new InvalidDataException($"{kind} prefab names cannot contain control characters.");
        }

        return normalized;
    }

    private static float ParsePositiveFloat(string? value, string context)
    {
        if (!float.TryParse(
                value?.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float parsed) ||
            !IsFinitePositive(parsed))
        {
            throw new InvalidDataException($"{context} must be a finite value greater than 0.");
        }

        return parsed;
    }

    private static bool TryResolveBiomeMask(
        PlantGrowthOverride rule,
        out Heightmap.Biome biomeMask)
    {
        biomeMask = Heightmap.Biome.None;
        if (rule.Biomes?.Names == null)
        {
            return false;
        }

        foreach (string name in rule.Biomes.Names)
        {
            if (!TryResolveBiomeName(name, out Heightmap.Biome resolved))
            {
                WarnUnresolvedBiome(
                    rule,
                    name,
                    "the name is unknown or Expand World Data has not finished loading its biome map");
                biomeMask = Heightmap.Biome.None;
                return false;
            }

            if (!IsSingleBiomeBit(resolved))
            {
                WarnUnresolvedBiome(
                    rule,
                    name,
                    "the name does not resolve to one biome");
                biomeMask = Heightmap.Biome.None;
                return false;
            }

            Heightmap.Biome effective = resolved;
            EnsureExpandWorldDataBridge();
            if (_expandWorldDataGetNature != null
                    ? !TryGetExpandWorldDataNature(resolved, out effective)
                    : !IsVanillaBiomeBit(resolved))
            {
                WarnUnresolvedBiome(
                    rule,
                    name,
                    "its Expand World Data nature could not be resolved");
                biomeMask = Heightmap.Biome.None;
                return false;
            }

            if (effective == Heightmap.Biome.None)
            {
                WarnUnresolvedBiome(
                    rule,
                    name,
                    "its effective Expand World Data nature is None");
                biomeMask = Heightmap.Biome.None;
                return false;
            }

            biomeMask |= effective;
        }

        return biomeMask != Heightmap.Biome.None;
    }

    private static bool TryResolveBiomeName(string name, out Heightmap.Biome biome)
    {
        EnsureExpandWorldDataBridge();
        if (_expandWorldDataTryGetBiome != null)
        {
            try
            {
                if (_expandWorldDataTryGetBiome(name, out biome))
                {
                    return true;
                }
            }
            catch (Exception exception)
            {
                LogExpandWorldDataBridgeWarning(exception);
            }
        }

        return Enum.TryParse(name, ignoreCase: true, out biome);
    }

    private static Heightmap.Biome ResolveEffectiveNature(Heightmap.Biome biome)
    {
        return TryGetExpandWorldDataNature(biome, out Heightmap.Biome nature)
            ? nature
            : biome;
    }

    private static bool TryGetExpandWorldDataNature(
        Heightmap.Biome biome,
        out Heightmap.Biome nature)
    {
        EnsureExpandWorldDataBridge();
        if (_expandWorldDataGetNature != null)
        {
            try
            {
                nature = _expandWorldDataGetNature(biome);
                return true;
            }
            catch (Exception exception)
            {
                LogExpandWorldDataBridgeWarning(exception);
            }
        }

        nature = biome;
        return false;
    }

    private static void EnsureExpandWorldDataBridge()
    {
        if (_expandWorldDataBridgeInitialized)
        {
            return;
        }

        _expandWorldDataBridgeInitialized = true;
        Type? managerType = Type.GetType(
            "ExpandWorldData.BiomeManager, ExpandWorldData",
            throwOnError: false);
        if (managerType == null)
        {
            return;
        }

        try
        {
            MethodInfo? tryGetBiomeMethod = managerType.GetMethod(
                "TryGetBiome",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(string), typeof(Heightmap.Biome).MakeByRefType()],
                modifiers: null);
            MethodInfo? getNatureMethod = managerType.GetMethod(
                "GetNature",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(Heightmap.Biome)],
                modifiers: null);
            if (tryGetBiomeMethod == null || getNatureMethod == null)
            {
                LogExpandWorldDataBridgeWarning(
                    new MissingMethodException(
                        "ExpandWorldData.BiomeManager.TryGetBiome/GetNature was not found."));
                return;
            }

            _expandWorldDataTryGetBiome = (TryGetBiomeDelegate)tryGetBiomeMethod.CreateDelegate(
                typeof(TryGetBiomeDelegate));
            _expandWorldDataGetNature = (GetNatureDelegate)getNatureMethod.CreateDelegate(
                typeof(GetNatureDelegate));
        }
        catch (Exception exception)
        {
            LogExpandWorldDataBridgeWarning(exception);
        }
    }

    private static void LogExpandWorldDataBridgeWarning(Exception exception)
    {
        if (_expandWorldDataBridgeWarningLogged)
        {
            return;
        }

        _expandWorldDataBridgeWarningLogged = true;
        GroundworkPlugin.ModLogger.LogWarning(
            "Expand World Data biome compatibility is unavailable; " +
            "Groundwork will preserve live biome restrictions for unresolved custom names. " +
            exception.GetBaseException().Message);
    }

    private static void WarnUnresolvedBiome(
        PlantGrowthOverride rule,
        string name,
        string reason)
    {
        rule.WarnedUnresolvedBiomes ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!rule.WarnedUnresolvedBiomes.Add(name))
        {
            return;
        }

        GroundworkPlugin.ModLogger.LogWarning(
            $"Plant '{rule.Prefab}' biome override is not active because '{name}' could not be used: {reason}. " +
            "The complete biome list will be retried and live placement/growth restrictions remain unchanged meanwhile.");
    }

    private static bool IsSingleBiomeBit(Heightmap.Biome biome)
    {
        uint bits = unchecked((uint)(int)biome);
        return bits != 0 && (bits & (bits - 1)) == 0;
    }

    private static bool IsVanillaBiomeBit(Heightmap.Biome biome)
    {
        uint bits = unchecked((uint)(int)biome);
        return IsSingleBiomeBit(biome) && (bits & VanillaBiomeBits) == bits;
    }

    private sealed class FarmingTupleYamlConverter : IYamlTypeConverter
    {
        private const int TupleLength = 4;

        public bool Accepts(Type type)
        {
            return type == typeof(PickableFarmingOverride);
        }

        public object ReadYaml(
            IParser parser,
            Type type,
            ObjectDeserializer rootDeserializer)
        {
            SequenceStart start = parser.Consume<SequenceStart>();
            if (start.Style != SequenceStyle.Flow)
            {
                throw new InvalidDataException(
                    "farming must be a flow tuple: " +
                    "[foragingTarget, bonusYield, maxChanceAtLevel100, bonusAmount].");
            }

            if (!start.Anchor.IsEmpty || !start.Tag.IsEmpty)
            {
                throw new InvalidDataException("farming tuples cannot use YAML anchors or tags.");
            }

            List<Scalar> values = new(TupleLength);
            while (!parser.Accept<SequenceEnd>(out _))
            {
                if (values.Count == TupleLength)
                {
                    throw new InvalidDataException(
                        "farming must contain exactly four scalar values.");
                }

                Scalar scalar = parser.Consume<Scalar>();
                if (!scalar.Anchor.IsEmpty || !scalar.Tag.IsEmpty)
                {
                    throw new InvalidDataException(
                        "farming tuple values cannot use YAML anchors or tags.");
                }

                values.Add(scalar);
            }

            parser.Consume<SequenceEnd>();
            if (values.Count != TupleLength)
            {
                throw new InvalidDataException(
                    "farming must contain exactly four scalar values.");
            }

            return new PickableFarmingOverride
            {
                ForagingTarget = ParseNullableBool(values[0], "foragingTarget"),
                BonusYield = ParseNullableBool(values[1], "bonusYield"),
                MaxChanceAtLevel100 = ParseNullableChance(values[2], "maxChanceAtLevel100"),
                BonusAmount = ParseNullableNonNegativeInt(values[3], "bonusAmount")
            };
        }

        public void WriteYaml(
            IEmitter emitter,
            object? value,
            Type type,
            ObjectSerializer serializer)
        {
            if (value is not PickableFarmingOverride farming)
            {
                throw new InvalidDataException("farming tuple value is missing.");
            }

            emitter.Emit(new SequenceStart(
                AnchorName.Empty,
                TagName.Empty,
                isImplicit: true,
                SequenceStyle.Flow));
            EmitScalar(emitter, FormatNullable(farming.ForagingTarget));
            EmitScalar(emitter, FormatNullable(farming.BonusYield));
            EmitScalar(emitter, FormatNullable(farming.MaxChanceAtLevel100));
            EmitScalar(emitter, FormatNullable(farming.BonusAmount));
            emitter.Emit(new SequenceEnd());
        }

        private static bool? ParseNullableBool(Scalar scalar, string field)
        {
            if (IsPlainNull(scalar))
            {
                return null;
            }

            string value = RequirePlainValue(scalar, field);
            if (!bool.TryParse(value, out bool parsed))
            {
                throw new InvalidDataException(
                    $"farming.{field} must be true, false, or null.");
            }

            return parsed;
        }

        private static float? ParseNullableChance(Scalar scalar, string field)
        {
            if (IsPlainNull(scalar))
            {
                return null;
            }

            string value = RequirePlainValue(scalar, field);
            if (!float.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float parsed) ||
                !IsFinite(parsed) ||
                parsed < 0f ||
                parsed > 1f)
            {
                throw new InvalidDataException(
                    $"farming.{field} must be between 0 and 1, or null.");
            }

            return parsed;
        }

        private static int? ParseNullableNonNegativeInt(Scalar scalar, string field)
        {
            if (IsPlainNull(scalar))
            {
                return null;
            }

            string value = RequirePlainValue(scalar, field);
            if (!int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsed) ||
                parsed < 0)
            {
                throw new InvalidDataException(
                    $"farming.{field} must be 0 or greater, or null.");
            }

            return parsed;
        }

        private static bool IsPlainNull(Scalar scalar)
        {
            return scalar.Style == ScalarStyle.Plain &&
                   (scalar.Value.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                    scalar.Value.Equals("~", StringComparison.Ordinal));
        }

        private static string RequirePlainValue(Scalar scalar, string field)
        {
            if (scalar.Style != ScalarStyle.Plain || string.IsNullOrWhiteSpace(scalar.Value))
            {
                throw new InvalidDataException(
                    $"farming.{field} must be an unquoted scalar value.");
            }

            return scalar.Value.Trim();
        }

        private static string FormatNullable(bool? value)
        {
            return value.HasValue ? (value.Value ? "true" : "false") : "null";
        }

        private static string FormatNullable(float? value)
        {
            return value.HasValue ? FormatFloat(value.Value) : "null";
        }

        private static string FormatNullable(int? value)
        {
            return value?.ToString(CultureInfo.InvariantCulture) ?? "null";
        }

        private static void EmitScalar(IEmitter emitter, string value)
        {
            emitter.Emit(new Scalar(
                AnchorName.Empty,
                TagName.Empty,
                value,
                ScalarStyle.Plain,
                isPlainImplicit: true,
                isQuotedImplicit: false));
        }
    }

    private sealed class BiomeListYamlConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type)
        {
            return type == typeof(PlantBiomeList);
        }

        public object ReadYaml(
            IParser parser,
            Type type,
            ObjectDeserializer rootDeserializer)
        {
            SequenceStart start = parser.Consume<SequenceStart>();
            if (start.Style != SequenceStyle.Flow)
            {
                throw new InvalidDataException(
                    "biomes must be a flow list such as [Mistlands, Meadows].");
            }

            if (!start.Anchor.IsEmpty || !start.Tag.IsEmpty)
            {
                throw new InvalidDataException("biomes cannot use YAML anchors or tags.");
            }

            List<string> names = [];
            while (!parser.Accept<SequenceEnd>(out _))
            {
                Scalar scalar = parser.Consume<Scalar>();
                if (!scalar.Anchor.IsEmpty || !scalar.Tag.IsEmpty)
                {
                    throw new InvalidDataException(
                        "biome names cannot use YAML anchors or tags.");
                }

                names.Add(scalar.Value);
            }

            parser.Consume<SequenceEnd>();
            return new PlantBiomeList { Names = names };
        }

        public void WriteYaml(
            IEmitter emitter,
            object? value,
            Type type,
            ObjectSerializer serializer)
        {
            if (value is not PlantBiomeList biomes)
            {
                throw new InvalidDataException("biomes value is missing.");
            }

            emitter.Emit(new SequenceStart(
                AnchorName.Empty,
                TagName.Empty,
                isImplicit: true,
                SequenceStyle.Flow));
            foreach (string name in biomes.Names)
            {
                serializer(name, typeof(string));
            }

            emitter.Emit(new SequenceEnd());
            if (biomes.ReferencePlacementBiomeMaskDiffers)
            {
                emitter.Emit(new Comment(
                    "Live cultivator placement mask differs; copying biomes replaces it with this growth mask.",
                    isInline: true));
            }
        }
    }

    private static void AddUnique<T>(
        IDictionary<string, T> rules,
        string prefab,
        T entry,
        string kind)
    {
        if (rules.ContainsKey(prefab))
        {
            throw new InvalidDataException($"Duplicate {kind} entry '{prefab}'.");
        }

        rules.Add(prefab, entry);
    }

    private static void ScheduleSceneSettledRefresh(ZNetScene scene)
    {
        if (_owner == null)
        {
            return;
        }

        StopSceneRefreshCoroutine();
        _sceneRefreshCoroutine = _owner.StartCoroutine(RefreshSceneAfterSettle(scene));
    }

    private static IEnumerator RefreshSceneAfterSettle(ZNetScene scene)
    {
        yield return null;
        if (scene == null ||
            !ReferenceEquals(scene, ZNetScene.instance))
        {
            _sceneRefreshCoroutine = null;
            yield break;
        }

        FarmingSkillSystem.RefreshForagingBonusEffectFallback(scene);
        ScytheHarvestSystem.RefreshCultivatedPickables(scene);
        yield return new WaitForSecondsRealtime(SceneSettleDelaySeconds);
        _sceneRefreshCoroutine = null;
        if (scene == null ||
            !ReferenceEquals(scene, ZNetScene.instance))
        {
            yield break;
        }

        FarmingSkillSystem.RefreshForagingBonusEffectFallback(scene);
        ScytheHarvestSystem.RefreshCultivatedPickables(scene);
        if (_authorityMode == AuthorityMode.LocalFiles)
        {
            try
            {
                WriteReferenceIfChanged(scene);
            }
            catch (Exception exception)
            {
                GroundworkPlugin.ModLogger.LogWarning(
                    $"Could not refresh {PickablesReferenceFileName} and " +
                    $"{PlantsReferenceFileName}: {exception.GetBaseException().Message}");
            }
        }
    }

    private static void StopSceneRefreshCoroutine()
    {
        if (_sceneRefreshCoroutine == null)
        {
            return;
        }

        _owner?.StopCoroutine(_sceneRefreshCoroutine);
        _sceneRefreshCoroutine = null;
    }

    private static void WriteReferenceIfChanged(ZNetScene scene)
    {
        GrowthReferenceOwnership.InvalidateModOwners();
        GrowthReferenceSnapshot reference = BuildReferenceSnapshot(scene);
        string pickablesContent = BuildPickablesReferenceContent(reference.Pickables);
        string plantsContent = BuildPlantsReferenceContent(reference.Plants);

        Directory.CreateDirectory(ConfigDirectoryPath);
        if (WriteFileIfChanged(PickablesReferenceFilePath, pickablesContent))
        {
            GroundworkPlugin.ModLogger.LogInfo(
                $"Wrote Pickable reference file: {PickablesReferenceFilePath}");
        }

        if (WriteFileIfChanged(PlantsReferenceFilePath, plantsContent))
        {
            GroundworkPlugin.ModLogger.LogInfo(
                $"Wrote Plant reference file: {PlantsReferenceFilePath}");
        }
    }

    private static bool WriteFileIfChanged(string path, string content)
    {
        if (File.Exists(path) &&
            string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
        {
            return false;
        }

        File.WriteAllText(path, content);
        return true;
    }

    private static string BuildPickablesReferenceContent(
        IReadOnlyCollection<PickableGrowthOverride> pickables)
    {
        StringBuilder builder = new();
        builder.AppendLine($"# Generated by {GroundworkPlugin.ModName}. Do not edit this file directly.");
        builder.AppendLine($"# Copy only entries you want to change into {PickablesOverrideFileName}.");
        builder.AppendLine("# Pickable prefab tuple: <prefab>, <respawnMinutes>.");
        builder.AppendLine("# Farming tuple: [foragingTarget, bonusYield, maxChanceAtLevel100, bonusAmount].");
        builder.AppendLine("# Reference farming booleans are observed automatic/native states; copying them makes those choices explicit.");
        builder.AppendLine("# Invalid live chance or amount values are written as null so copied entries preserve them instead of failing validation.");
        builder.AppendLine("# Owner headings identify the original prefab provider, not mods that later changed its fields.");
        builder.AppendLine("# Pickables with a base respawn time of 0 are omitted because this overlay cannot enable their respawn lifecycle.");
        builder.AppendLine();
        AppendReferenceEntries(builder, pickables, ToDocumentEntry);
        return builder.ToString();
    }

    private static string BuildPlantsReferenceContent(
        IReadOnlyCollection<PlantGrowthOverride> plants)
    {
        StringBuilder builder = new();
        builder.AppendLine($"# Generated by {GroundworkPlugin.ModName}. Do not edit this file directly.");
        builder.AppendLine($"# Copy only entries you want to change into {PlantsOverrideFileName}.");
        builder.AppendLine("# Plant prefab tuple: <prefab>[, <growSecondsMin>~<growSecondsMax>].");
        builder.AppendLine("# biomes reports the live Plant growth mask when it has stable names.");
        builder.AppendLine("# Copying biomes explicitly applies the list to both cultivator placement and Plant health checks.");
        builder.AppendLine("# An inline note marks entries whose live cultivator placement mask differs from the reported growth mask.");
        builder.AppendLine("# Owner headings identify the original prefab provider, not mods that later changed its fields.");
        builder.AppendLine();
        AppendReferenceEntries(builder, plants, ToDocumentEntry);
        return builder.ToString();
    }

    private static void AppendReferenceEntries<TSource, TOutput>(
        StringBuilder builder,
        IReadOnlyCollection<TSource> entries,
        Func<TSource, TOutput> convert)
        where TSource : class
    {
        if (entries.Count == 0)
        {
            builder.AppendLine("[]");
            return;
        }

        bool wroteOwner = false;
        foreach (ReferenceOwnerGroup<TSource> group in GroupReferenceEntries(entries))
        {
            if (wroteOwner)
            {
                builder.AppendLine();
            }

            builder.Append("# ===== ");
            builder.Append(group.OwnerName);
            builder.AppendLine(" =====");
            foreach (TSource entry in group.Entries)
            {
                string entryYaml = CanonicalizeYaml(
                        Serializer.Serialize(new[] { convert(entry) }))
                    .TrimEnd(new[] { '\n' });
                foreach (string line in entryYaml
                             .Split(new[] { '\n' }, StringSplitOptions.None))
                {
                    builder.AppendLine(line);
                }
            }

            wroteOwner = true;
        }
    }

    private static IEnumerable<ReferenceOwnerGroup<T>> GroupReferenceEntries<T>(
        IEnumerable<T> entries)
        where T : class
    {
        return entries
            .Select(entry =>
            {
                string prefab = entry switch
                {
                    PickableGrowthOverride pickable => pickable.Prefab,
                    PlantGrowthOverride plant => plant.Prefab,
                    _ => ""
                };
                return new
                {
                    Entry = entry,
                    Prefab = prefab,
                    Owner = GrowthReferenceOwnership.GetOwnerName(prefab)
                };
            })
            .OrderBy(entry => GetOwnerSortBucket(entry.Owner))
            .ThenBy(entry => entry.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Prefab, StringComparer.OrdinalIgnoreCase)
            .GroupBy(entry => entry.Owner, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ReferenceOwnerGroup<T>(
                group.Key,
                group.Select(entry => entry.Entry).ToList()));
    }

    private static int GetOwnerSortBucket(string ownerName)
    {
        if (ownerName.Equals(
                GrowthReferenceOwnership.ValheimOwnerName,
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return ownerName.Equals(
                GrowthReferenceOwnership.UnknownOwnerName,
                StringComparison.OrdinalIgnoreCase)
            ? 2
            : 1;
    }

    private static GrowthReferenceSnapshot BuildReferenceSnapshot(ZNetScene scene)
    {
        Dictionary<string, PickableGrowthOverride> pickables =
            new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PlantGrowthOverride> plants =
            new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> ambiguousPickables = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> ambiguousPlants = new(StringComparer.OrdinalIgnoreCase);
        List<string> unnamedBiomeReferencePrefabs = [];

        foreach (GameObject prefab in EnumerateScenePrefabs(scene))
        {
            string prefabName = Utils.GetPrefabName(prefab);
            if (string.IsNullOrWhiteSpace(prefabName))
            {
                continue;
            }

            Pickable[] respawningPickables = prefab
                .GetComponentsInChildren<Pickable>(includeInactive: true)
                .Where(pickable => IsFinitePositive(pickable.m_respawnTimeMinutes))
                .ToArray();
            if (respawningPickables.Length > 0 &&
                !ambiguousPickables.Contains(prefabName) &&
                !pickables.ContainsKey(prefabName))
            {
                Pickable pickable = respawningPickables[0];
                if (respawningPickables.Skip(1).Any(other => !HasSameReferenceValues(pickable, other)))
                {
                    ambiguousPickables.Add(prefabName);
                    GroundworkPlugin.ModLogger.LogWarning(
                        $"Omitting Pickable prefab '{prefabName}' from " +
                        $"{PickablesReferenceFileName}: " +
                        "its components have different reference values, while overrides are prefab-wide.");
                }
                else
                {
                    pickables.Add(prefabName, new PickableGrowthOverride
                    {
                        Prefab = prefabName,
                        RespawnMinutes = pickable.m_respawnTimeMinutes,
                        Farming = new PickableFarmingOverride
                        {
                            ForagingTarget = FarmingSkillSystem.IsAutomaticForagingTarget(pickable),
                            BonusYield = pickable.m_pickRaiseSkill == Skills.SkillType.Farming,
                            MaxChanceAtLevel100 =
                                IsFinite(pickable.m_maxLevelBonusChance) &&
                                pickable.m_maxLevelBonusChance >= 0f &&
                                pickable.m_maxLevelBonusChance <= 1f
                                    ? pickable.m_maxLevelBonusChance
                                    : null,
                            BonusAmount = pickable.m_bonusYieldAmount >= 0
                                ? pickable.m_bonusYieldAmount
                                : null
                        }
                    });
                }
            }

            Plant[] growingPlants = prefab
                .GetComponentsInChildren<Plant>(includeInactive: true)
                .Where(plant =>
                    IsFinitePositive(plant.m_growTime) &&
                    IsFinitePositive(plant.m_growTimeMax))
                .ToArray();
            if (growingPlants.Length > 0 &&
                !ambiguousPlants.Contains(prefabName) &&
                !plants.ContainsKey(prefabName))
            {
                Plant plant = growingPlants[0];
                if (growingPlants.Any(other => other.m_growTimeMax < other.m_growTime))
                {
                    ambiguousPlants.Add(prefabName);
                    GroundworkPlugin.ModLogger.LogWarning(
                        $"Omitting Plant prefab '{prefabName}' from " +
                        $"{PlantsReferenceFileName}: " +
                        "its live grow-time maximum is lower than its minimum.");
                }
                else if (growingPlants.Skip(1).Any(other => !HasSameReferenceValues(plant, other)))
                {
                    ambiguousPlants.Add(prefabName);
                    GroundworkPlugin.ModLogger.LogWarning(
                        $"Omitting Plant prefab '{prefabName}' from " +
                        $"{PlantsReferenceFileName}: " +
                        "its components have different grow-time ranges, while overrides are prefab-wide.");
                }
                else
                {
                    PlantBiomeList? referenceBiomes = null;
                    if (!TryBuildReferenceBiomeList(
                            prefab,
                            growingPlants,
                            out referenceBiomes,
                            out string biomeError,
                            out bool unnamedBiomeBit))
                    {
                        if (unnamedBiomeBit)
                        {
                            unnamedBiomeReferencePrefabs.Add(prefabName);
                        }
                        else
                        {
                            GroundworkPlugin.ModLogger.LogWarning(
                                $"Omitting biomes for Plant prefab '{prefabName}' from " +
                                $"{PlantsReferenceFileName}: {biomeError}");
                        }
                    }

                    plants.Add(prefabName, new PlantGrowthOverride
                    {
                        Prefab = prefabName,
                        GrowSecondsMin = plant.m_growTime,
                        GrowSecondsMax = plant.m_growTimeMax,
                        Biomes = referenceBiomes
                    });
                }
            }
        }

        if (unnamedBiomeReferencePrefabs.Count > 0)
        {
            const int exampleLimit = 5;
            string examples = string.Join(", ", unnamedBiomeReferencePrefabs.Take(exampleLimit));
            string suffix = unnamedBiomeReferencePrefabs.Count > exampleLimit ? ", ..." : "";
            GroundworkPlugin.ModLogger.LogDebug(
                $"Omitted biome metadata from {PlantsReferenceFileName} for " +
                $"{unnamedBiomeReferencePrefabs.Count} Plant prefab(s) whose live biome mask contains " +
                $"an unnamed bit. Live restrictions remain unchanged. Examples: {examples}{suffix}");
        }

        return new GrowthReferenceSnapshot
        {
            Pickables = pickables.Values
                .OrderBy(entry => entry.Prefab, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Plants = plants.Values
                .OrderBy(entry => entry.Prefab, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static IEnumerable<GameObject> EnumerateScenePrefabs(ZNetScene scene)
    {
        HashSet<string> seenNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameObject prefab in scene.m_namedPrefabs.Values)
        {
            if (TryAddPrefabName(prefab, seenNames))
            {
                yield return prefab;
            }
        }

        foreach (GameObject prefab in scene.m_prefabs)
        {
            if (TryAddPrefabName(prefab, seenNames))
            {
                yield return prefab;
            }
        }

        foreach (GameObject prefab in scene.m_nonNetViewPrefabs)
        {
            if (TryAddPrefabName(prefab, seenNames))
            {
                yield return prefab;
            }
        }
    }

    private static bool TryAddPrefabName(GameObject? prefab, ISet<string> seenNames)
    {
        if (prefab == null)
        {
            return false;
        }

        string prefabName = Utils.GetPrefabName(prefab);
        return !string.IsNullOrWhiteSpace(prefabName) &&
               seenNames.Add(prefabName);
    }

    private static bool TryBuildReferenceBiomeList(
        GameObject prefab,
        IReadOnlyList<Plant> plants,
        out PlantBiomeList? biomes,
        out string error,
        out bool unnamedBiomeBit)
    {
        biomes = null;
        error = "";
        unnamedBiomeBit = false;
        if (plants.Count == 0)
        {
            error = "it has no growing Plant components.";
            return false;
        }

        Heightmap.Biome referenceMask = plants[0].m_biome;
        if (plants.Skip(1).Any(plant => plant.m_biome != referenceMask))
        {
            error = "its Plant components have different growth biome masks.";
            return false;
        }

        bool placementBiomeMaskDiffers = false;
        foreach (Plant plant in plants)
        {
            Piece? piece = plant.GetComponentInParent<Piece>() ??
                           prefab.GetComponentInChildren<Piece>(includeInactive: true);
            if (piece != null && piece.m_onlyInBiome != referenceMask)
            {
                placementBiomeMaskDiffers = true;
            }
        }

        if (referenceMask == Heightmap.Biome.None)
        {
            error = "its live biome mask is None and cannot be represented as an allowed-name list.";
            return false;
        }

        List<string> names = [];
        uint remainingBits = unchecked((uint)(int)referenceMask);
        for (uint bit = 1; bit != 0; bit <<= 1)
        {
            if ((remainingBits & bit) == 0)
            {
                continue;
            }

            Heightmap.Biome biome = (Heightmap.Biome)(int)bit;
            string? name = Enum.GetName(typeof(Heightmap.Biome), biome);
            if (string.IsNullOrWhiteSpace(name) ||
                name.Equals(nameof(Heightmap.Biome.None), StringComparison.OrdinalIgnoreCase) ||
                name.Equals(nameof(Heightmap.Biome.All), StringComparison.OrdinalIgnoreCase) ||
                name.Any(char.IsControl))
            {
                unnamedBiomeBit = true;
                error = $"biome bit {unchecked((int)bit)} has no stable name.";
                return false;
            }

            names.Add(name);
            remainingBits &= ~bit;
        }

        if (remainingBits != 0 || names.Count == 0)
        {
            error = "its biome mask could not be converted to stable names.";
            return false;
        }

        biomes = new PlantBiomeList
        {
            Names = names,
            ReferencePlacementBiomeMaskDiffers = placementBiomeMaskDiffers
        };
        return true;
    }

    private static bool HasSameReferenceValues(Pickable first, Pickable second)
    {
        return Mathf.Approximately(first.m_respawnTimeMinutes, second.m_respawnTimeMinutes) &&
               FarmingSkillSystem.IsAutomaticForagingTarget(first) ==
               FarmingSkillSystem.IsAutomaticForagingTarget(second) &&
               (first.m_pickRaiseSkill == Skills.SkillType.Farming) ==
               (second.m_pickRaiseSkill == Skills.SkillType.Farming) &&
               Mathf.Approximately(first.m_maxLevelBonusChance, second.m_maxLevelBonusChance) &&
               first.m_bonusYieldAmount == second.m_bonusYieldAmount;
    }

    private static bool HasSameReferenceValues(Plant first, Plant second)
    {
        return Mathf.Approximately(first.m_growTime, second.m_growTime) &&
               Mathf.Approximately(first.m_growTimeMax, second.m_growTimeMax);
    }

    private static bool TryGetPrefabName(Component component, out string prefabName)
    {
        ZNetView? nview = component.GetComponentInParent<ZNetView>();
        GameObject root = nview != null ? nview.gameObject : component.transform.root.gameObject;
        prefabName = Utils.GetPrefabName(root);
        return !string.IsNullOrWhiteSpace(prefabName);
    }

    private static bool IsFinitePositive(float value)
    {
        return IsFinite(value) && value > 0f;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static void EnsureOverrideFilesExist()
    {
        Directory.CreateDirectory(ConfigDirectoryPath);
        if (!File.Exists(PickablesOverrideFilePath))
        {
            File.WriteAllText(
                PickablesOverrideFilePath,
                DefaultPickablesOverrideTemplate());
        }

        if (!File.Exists(PlantsOverrideFilePath))
        {
            File.WriteAllText(
                PlantsOverrideFilePath,
                DefaultPlantsOverrideTemplate());
        }
    }

    private static string DefaultPickablesOverrideTemplate()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "# Groundwork Pickable respawn and Farming overrides.",
            $"# Copy exact prefab names and observed values from {PickablesReferenceFileName}.",
            "# This root document is a YAML sequence. Legacy mapping fields are not supported.",
            "#",
            "# Full schema:",
            "# - prefab: <prefab>[, <respawnMinutes>] # optional positive respawn minutes; omit it to keep the live value.",
            "#   farming: [foragingTarget, bonusYield, maxChanceAtLevel100, bonusAmount]",
            "#                                      # optional tuple; when present it must contain exactly four values.",
            "#                                      # use null in any position to keep automatic/live behavior.",
            "#",
            "# Farming tuple positions:",
            "# 1. foragingTarget: true enables Groundwork range/scythe harvest, respawn scaling, pollination, rain, hover, and bonus-effect fallback.",
            "#                    false opts out; null keeps edible-drop automatic detection.",
            "# 2. bonusYield: true temporarily uses Farming for vanilla skill gain and bonus-yield rolls.",
            "#                false or null does not add that behavior and never disables a native Farming bonus.",
            "# 3. maxChanceAtLevel100: 0..1 probability at Farming 100; null keeps the live prefab value.",
            "# 4. bonusAmount: non-negative extra item count on success; null keeps the live prefab value.",
            "#",
            "# A positive base respawn time is still required. A prefab whose live value is 0 cannot be made respawning here.",
            "# On a successful configured or native Farming bonus roll, Groundwork supplies fallback VFX/SFX when m_bonusEffect is empty.",
            "",
            "- prefab: Pickable_Dandelion # prefab only: keep the live respawn time",
            "  farming: [true, true, 0.25, 1] # target, bonus, chance at Farming 100, extra amount",
            "- prefab: Pickable_Thistle # prefab only: keep the live respawn time",
            "  farming: [true, true, 0.25, 1] # target, bonus, chance at Farming 100, extra amount",
        }) + Environment.NewLine;
    }

    private static string DefaultPlantsOverrideTemplate()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "# Groundwork Plant grow-time and biome overrides.",
            $"# Copy exact prefab names and observed values from {PlantsReferenceFileName}.",
            "# This root document is a YAML sequence. Legacy mapping fields are not supported.",
            "#",
            "# Full schema:",
            "# - prefab: <prefab>[, <growSecondsMin>~<growSecondsMax>] # optional positive seconds; omit the range to keep live grow time.",
            "#   biomes: [<biome>, ...] # optional non-empty name list; applies to cultivator placement and Plant growth health.",
            "#                              # omit biomes to keep live biome restrictions; None, All, and numeric masks are not accepted.",
            "#                              # heat/cold, cultivated-ground, roof, and spacing checks remain unchanged.",
            "#",
            "# Replace [] with entries that override grow time, biomes, or both.",
            "[]",
            "# Examples:",
            "# - prefab: Beech_Sapling, 3000~5000 # time only; keep live biome restrictions.",
            "# - prefab: sapling_jotunpuffs # biome only; keep live grow time.",
            "#   biomes: [Mistlands] # For an EWD custom biome with nature: Mistlands, list the vanilla nature name Mistlands rather than the custom biome name.",
            "#                         # This allows vanilla Mistlands and every EWD custom biome in that nature group; individual members cannot be selected separately.",
            "# - prefab: MyMod_Sapling # independent EWD custom-biome example.",
            "#   biomes: [MyIndependentBiome] # Use the EWD `biome:` name only for an independent custom biome with no valid nature or terrain alias."
        }) + Environment.NewLine;
    }
}

internal sealed class GrowthReferenceSnapshot
{
    public List<PickableGrowthOverride> Pickables { get; set; } = [];

    public List<PlantGrowthOverride> Plants { get; set; } = [];
}

internal sealed class ReferenceOwnerGroup<T>
{
    internal ReferenceOwnerGroup(string ownerName, List<T> entries)
    {
        string sanitizedOwnerName = new((ownerName ?? "")
            .Where(character => !char.IsControl(character))
            .ToArray());
        OwnerName = string.IsNullOrWhiteSpace(sanitizedOwnerName)
            ? GrowthReferenceOwnership.UnknownOwnerName
            : sanitizedOwnerName.Trim();
        Entries = entries;
    }

    internal string OwnerName { get; }

    internal List<T> Entries { get; }
}

internal sealed class GrowthOverrideDocument
{
    [YamlMember(Order = 1)]
    public List<PickableGrowthEntry> Pickables { get; set; } = [];

    [YamlMember(Order = 2)]
    public List<PlantGrowthEntry> Plants { get; set; } = [];
}

internal sealed class PickableGrowthEntry
{
    [YamlMember(Order = 1)]
    public string? Prefab { get; set; }

    [YamlMember(Order = 2)]
    public PickableFarmingOverride? Farming { get; set; }
}

internal sealed class PlantGrowthEntry
{
    [YamlMember(Order = 1)]
    public string? Prefab { get; set; }

    [YamlMember(Order = 2)]
    public PlantBiomeList? Biomes { get; set; }
}

internal sealed class PickableGrowthOverride
{
    public string Prefab { get; set; } = "";

    public float? RespawnMinutes { get; set; }

    public PickableFarmingOverride? Farming { get; set; }
}

internal sealed class PickableFarmingOverride
{
    public bool? ForagingTarget { get; set; }

    public bool? BonusYield { get; set; }

    public float? MaxChanceAtLevel100 { get; set; }

    public int? BonusAmount { get; set; }
}

internal sealed class PlantGrowthOverride
{
    public string Prefab { get; set; } = "";

    public float? GrowSecondsMin { get; set; }

    public float? GrowSecondsMax { get; set; }

    public PlantBiomeList? Biomes { get; set; }

    internal HashSet<string>? WarnedUnresolvedBiomes { get; set; }
}

internal sealed class PlantBiomeList
{
    public List<string> Names { get; set; } = [];

    internal bool ReferencePlacementBiomeMaskDiffers { get; set; }
}
