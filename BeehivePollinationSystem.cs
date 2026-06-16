using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Groundwork;

internal static class BeehivePollinationSystem
{
    private const string TendedFarmingLevelKey = "Groundwork_BeehiveTendedFarmingLevel";
    private const float PollinationCacheLifetimeSeconds = 3f;
    private const float PollinationCachePruneIntervalSeconds = 30f;
    private const float PollinationCacheMaxIdleSeconds = 60f;
    private const float AssignmentCachePositionEpsilonSqr = 0.0001f;
    private const float UnloadedCatchupEffectiveness = 0.5f;
    private const float UnloadedCatchupThresholdSeconds = 30f;
    private const float UnloadedCatchupDaylightShare = 0.5f;
    private static readonly Collider[] PollinationHits = new Collider[256];
    private static readonly Collider[] AssignmentHits = new Collider[128];
    private static readonly Dictionary<Beehive, PollinationCache> PollinationCaches = [];
    private static readonly Dictionary<Component, AssignmentCache> AssignmentCaches = [];
    private static readonly Dictionary<Component, long> LoadedSinceTicksByTarget = [];
    private static readonly List<PollinationTargetCandidate> PollinationCandidates = [];
    private static readonly List<Beehive> StalePollinationCacheHives = [];
    private static readonly List<Component> StaleAssignmentCacheTargets = [];
    private static readonly List<Component> StaleLoadedSinceTargets = [];
    private static readonly HashSet<Plant> SeenPlants = [];
    private static readonly HashSet<Pickable> SeenPickables = [];
    private static readonly HashSet<Beehive> SeenHives = [];
    private static Player? _placingPlayer;
    private static int _pollinationMask;
    private static float _nextPollinationCachePruneAt;

    internal readonly struct PollinationSummary(int count, int maxCount, float honeyMultiplier)
    {
        internal readonly int Count = count;
        internal readonly int MaxCount = maxCount;
        internal readonly float HoneyMultiplier = honeyMultiplier;
    }

    private readonly struct PollinationTargetCandidate(Plant? plant, Pickable? pickable, float horizontalDistance, float heightDistance, int instanceId)
    {
        internal readonly Plant? Plant = plant;
        internal readonly Pickable? Pickable = pickable;
        internal readonly float HorizontalDistance = horizontalDistance;
        internal readonly float HeightDistance = heightDistance;
        internal readonly int InstanceId = instanceId;
    }

    private sealed class PollinationCache
    {
        internal readonly HashSet<Plant> Plants = [];
        internal readonly HashSet<Pickable> Pickables = [];
        internal float RefreshedAt = -PollinationCacheMaxIdleSeconds;
        internal int MaxCount;
        internal float Radius;
        internal bool CanPollinate;
        internal bool RequireDaylight;

        internal int Count => Plants.Count + Pickables.Count;

        internal bool IsFresh(float now, int maxCount, float radius, bool canPollinate, bool requireDaylight)
        {
            return now - RefreshedAt <= PollinationCacheLifetimeSeconds &&
                   MaxCount == maxCount &&
                   Mathf.Approximately(Radius, radius) &&
                   CanPollinate == canPollinate &&
                   RequireDaylight == requireDaylight;
        }

        internal void Clear()
        {
            Plants.Clear();
            Pickables.Clear();
        }
    }

    private sealed class AssignmentCache
    {
        internal Beehive? AssignedHive;
        internal float RefreshedAt = -PollinationCacheMaxIdleSeconds;
        internal float Radius;
        internal Vector3 TargetPosition;
        internal bool RequireDaylight;

        internal bool IsFresh(float now, float radius, Vector3 targetPosition, bool requireDaylight)
        {
            return now - RefreshedAt <= PollinationCacheLifetimeSeconds &&
                   Mathf.Approximately(Radius, radius) &&
                   (TargetPosition - targetPosition).sqrMagnitude <= AssignmentCachePositionEpsilonSqr &&
                   RequireDaylight == requireDaylight;
        }

        internal void Set(Beehive? assignedHive, float now, float radius, Vector3 targetPosition, bool requireDaylight)
        {
            AssignedHive = assignedHive;
            RefreshedAt = now;
            Radius = radius;
            TargetPosition = targetPosition;
            RequireDaylight = requireDaylight;
        }
    }

    // Beehive hover text and harvest bookkeeping.
    internal static void AppendHoverText(Beehive beehive, ref string hoverText)
    {
        if (beehive == null ||
            !IsValid(beehive) ||
            !PrivateArea.CheckAccess(beehive.transform.position, 0f, flash: false))
        {
            return;
        }

        int honeyLevel = GetHoneyLevel(beehive);
        int maxHoney = GetEffectiveMaxHoney(beehive);
        int farmingCapacityBonus = GetFarmingCapacityBonusHoney(beehive);
        ReplaceHoverHeader(beehive, ref hoverText, honeyLevel, maxHoney, farmingCapacityBonus);

        if (TryGetCoverPercentage(beehive, out float coverPercentage))
        {
            float coverMultiplier = GetCoverProductionMultiplier(beehive, coverPercentage);
            AppendLine(
                ref hoverText,
                Colorize(GroundworkLocalization.Format(
                    "groundwork_beehive_cover",
                    "Cover: {0} ({1})",
                    FormatPercent(coverPercentage),
                    FormatMultiplier(coverMultiplier))));
        }

        PollinationSummary summary = GetPollinationSummary(beehive);
        if (summary.MaxCount > 0)
        {
            AppendLine(
                ref hoverText,
                Colorize(GroundworkLocalization.Format(
                    "groundwork_beehive_pollination",
                    "Pollination: {0}/{1} ({2})",
                    summary.Count,
                    summary.MaxCount,
                    FormatMultiplier(summary.HoneyMultiplier))));
        }

        AppendCurrentHoneyRateLine(
            ref hoverText,
            GetNightProductionMultiplier(unloadedCatchup: false),
            "groundwork_beehive_night_rate",
            "Night: {0}",
            EnvironmentEffectSystem.GetBeehiveRainHoneyRate(unloadedCatchup: false),
            "groundwork_beehive_rain_rate",
            "Rain: {0}");

        string nextHoney = FormatNextHoney(beehive, honeyLevel, maxHoney, out float honeyRateMultiplier);
        string nextHoneyLine = honeyRateMultiplier > 0.001f
            ? GroundworkLocalization.Format(
                "groundwork_beehive_next_honey_rate",
                "Next honey: {0} (Honey rate {1})",
                nextHoney,
                FormatMultiplier(honeyRateMultiplier))
            : GroundworkLocalization.Format(
                "groundwork_beehive_next_honey",
                "Next honey: {0}",
                nextHoney);
        AppendLine(ref hoverText, Colorize(nextHoneyLine));
    }

    internal static void StoreTendedFarmingLevel(Beehive beehive, long sender)
    {
        StoreTendedFarmingLevel(beehive, ResolveSenderFarmingLevel(sender));
    }

    internal static void RaiseFarmingSkillForHarvest(long sender, int harvestedHoney)
    {
        Player? player = ResolveSenderPlayer(sender);
        if (player != null)
        {
            RaiseFarmingSkillForHarvest(player, harvestedHoney);
        }
    }

    internal static void RegisterBeehiveHarvest(Beehive beehive, Player player, int harvestedHoney)
    {
        if (beehive == null || player == null || harvestedHoney <= 0)
        {
            return;
        }

        StoreTendedFarmingLevel(beehive, player);
        RaiseFarmingSkillForHarvest(player, harvestedHoney);
    }

    private static void StoreTendedFarmingLevel(Beehive beehive, Player player)
    {
        StoreTendedFarmingLevel(beehive, Mathf.Clamp(player.GetSkillLevel(Skills.SkillType.Farming), 0f, 100f));
    }

    private static void StoreTendedFarmingLevel(Beehive beehive, float farmingLevel)
    {
        ZDO? zdo = GetZdo(beehive);
        if (zdo == null || !beehive.m_nview.IsOwner())
        {
            return;
        }

        zdo.Set(TendedFarmingLevelKey, Mathf.Clamp(farmingLevel, 0f, 100f));
    }

    private static void RaiseFarmingSkillForHarvest(Player player, int harvestedHoney)
    {
        float skillGainPerHoney = GroundworkToolsDomain.BeehiveFarmingSkillGainPerHoney;
        if (player == null || harvestedHoney <= 0 || skillGainPerHoney <= 0f)
        {
            return;
        }

        player.RaiseSkill(Skills.SkillType.Farming, harvestedHoney * skillGainPerHoney);
    }

    internal static void BeginPlacePiece(Player player, Piece piece)
    {
        _placingPlayer = player != null &&
                         piece != null &&
                         GroundworkToolsDomain.BeehiveCapacityFarmingLevelsPerBonusHoney > 0 &&
                         piece.GetComponentInChildren<Beehive>(includeInactive: true) != null
            ? player
            : null;
    }

    internal static void EndPlacePiece()
    {
        _placingPlayer = null;
    }

    internal static void TryStoreBuilderFarmingLevel(Beehive beehive)
    {
        Player? player = _placingPlayer;
        ZDO? zdo = GetZdo(beehive);
        if (player == null ||
            zdo == null ||
            !beehive.m_nview.IsOwner() ||
            GroundworkToolsDomain.BeehiveCapacityFarmingLevelsPerBonusHoney <= 0 ||
            zdo.GetFloat(TendedFarmingLevelKey, -1f) >= 0f)
        {
            return;
        }

        zdo.Set(TendedFarmingLevelKey, Mathf.Clamp(player.GetSkillLevel(Skills.SkillType.Farming), 0f, 100f));
    }

    internal static int GetEffectiveMaxHoney(Beehive beehive, bool preserveStoredHoney = true)
    {
        int baseMax = Mathf.Max(1, beehive.m_maxHoney);
        int effectiveMax = baseMax + GetFarmingCapacityBonusHoney(beehive);

        if (preserveStoredHoney)
        {
            effectiveMax = Mathf.Max(effectiveMax, GetHoneyLevel(beehive));
        }

        return Mathf.Max(baseMax, effectiveMax);
    }

    internal static float GetProductionSpeedMultiplier(Beehive beehive, bool unloadedCatchup = false)
    {
        if (beehive == null || !IsValid(beehive))
        {
            return 1f;
        }

        float multiplier = 1f;
        if (TryGetCoverPercentage(beehive, out float coverPercentage))
        {
            float coverMultiplier = GetCoverProductionMultiplier(beehive, coverPercentage);
            multiplier *= unloadedCatchup ? ApplyUnloadedCatchupEffectiveness(coverMultiplier) : coverMultiplier;
        }

        float pollinationMultiplier = GetPollinationSummary(beehive, requireDaylight: !unloadedCatchup).HoneyMultiplier;
        multiplier *= unloadedCatchup ? ApplyUnloadedCatchupEffectiveness(pollinationMultiplier) : pollinationMultiplier;
        multiplier *= GetNightProductionMultiplier(unloadedCatchup);
        multiplier *= EnvironmentEffectSystem.GetBeehiveRainHoneyRate(unloadedCatchup);
        return Mathf.Max(0f, multiplier);
    }

    internal static bool ShouldUseUnloadedProductionCatchup(Beehive beehive)
    {
        ZDO? zdo = GetZdo(beehive);
        return zdo != null && GetSecondsSinceLastUpdate(zdo) > UnloadedCatchupThresholdSeconds;
    }

    internal static void TrackLoadedTarget(Component target)
    {
        if (target == null || ZNet.instance == null)
        {
            return;
        }

        LoadedSinceTicksByTarget[target] = ZNet.instance.GetTime().Ticks;
    }

    internal static void TryModifyPlantGrowTime(Plant plant, ref float growTime)
    {
        if (plant == null || growTime <= 0f)
        {
            return;
        }

        float multiplier = GetPlantGrowthMultiplierForTarget(plant);
        if (multiplier > 1.001f)
        {
            float totalElapsed = Mathf.Max(0f, (float)plant.TimeSincePlanted());
            float catchupElapsed = GetUnloadedCatchupElapsed(plant, totalElapsed);
            growTime = ApplyCatchupDurationMultiplier(growTime, multiplier, catchupElapsed);
        }
    }

    internal static bool TryModifyForagingRespawnSeconds(Pickable pickable, ref float respawnSeconds)
    {
        if (pickable == null || respawnSeconds <= 0f)
        {
            return false;
        }

        float multiplier = GetForagingRespawnMultiplierForTarget(pickable);
        if (multiplier <= 1.001f)
        {
            return false;
        }

        long pickedTicks = GetPickedTimeTicks(pickable);
        float totalElapsed = pickedTicks > 0 ? GetSecondsSinceTicks(pickedTicks) : 0f;
        float catchupElapsed = GetUnloadedCatchupElapsed(pickable, totalElapsed, pickedTicks);
        respawnSeconds = ApplyCatchupDurationMultiplier(respawnSeconds, multiplier, catchupElapsed);
        return true;
    }

    internal static float GetPlantGrowthMultiplierForHover(Plant plant)
    {
        return plant != null ? GetPlantGrowthMultiplierForTarget(plant) : 1f;
    }

    internal static float GetForagingRespawnMultiplierForHover(Pickable pickable)
    {
        return pickable != null && FarmingSkillSystem.IsForagingTarget(pickable)
            ? GetForagingRespawnMultiplierForTarget(pickable)
            : 1f;
    }

    private static string FormatNextHoney(Beehive beehive, int honeyLevel, int maxHoney, out float honeyRateMultiplier)
    {
        honeyRateMultiplier = 0f;
        if (honeyLevel >= maxHoney)
        {
            return GroundworkLocalization.Text("groundwork_beehive_status_full", "full");
        }

        if (!IsBiomeAllowed(beehive))
        {
            return GroundworkLocalization.Text("groundwork_beehive_status_wrong_biome", "wrong biome");
        }

        if (!TryGetCoverPercentage(beehive, out float coverPercentage) ||
            !HasFreeSpace(beehive, coverPercentage))
        {
            return GroundworkLocalization.Text("groundwork_beehive_status_blocked", "blocked");
        }

        if (EnvironmentEffectSystem.IsLoadedBeehiveProductionPausedByWetEnvironment())
        {
            return GroundworkLocalization.Text("groundwork_beehive_status_paused_by_rain", "paused by rain");
        }

        ZDO? zdo = GetZdo(beehive);
        if (zdo == null)
        {
            return GroundworkLocalization.Text("groundwork_beehive_status_unknown", "unknown");
        }

        float multiplier = GetProductionSpeedMultiplier(beehive);
        if (multiplier <= 0.001f)
        {
            return GroundworkLocalization.Text("groundwork_beehive_status_paused_by_night", "paused by night");
        }

        honeyRateMultiplier = multiplier;
        float effectiveSecondsPerHoney = beehive.m_secPerUnit / Mathf.Max(0.001f, multiplier);
        float product = zdo.GetFloat(ZDOVars.s_product);
        float elapsed = GetSecondsSinceLastUpdate(zdo);
        float remainingSeconds = Mathf.Max(0f, effectiveSecondsPerHoney - product - elapsed);
        return FormatSeconds(remainingSeconds);
    }

    // Pollination assignment and cache refresh.
    private static PollinationSummary GetPollinationSummary(Beehive beehive, bool requireDaylight = true)
    {
        int maxCount = GroundworkToolsDomain.BeehivePollinationMaxPlants;
        float radius = GroundworkToolsDomain.BeehivePollinationRadius;
        if (beehive == null ||
            maxCount <= 0 ||
            radius <= 0f)
        {
            return new PollinationSummary(0, 0, 1f);
        }

        if (!CanHivePollinate(beehive, requireDaylight))
        {
            return new PollinationSummary(0, maxCount, 1f);
        }

        PollinationCache? cache = GetPollinationCache(beehive, maxCount, radius, canPollinate: true, requireDaylight);
        int count = cache?.Count ?? 0;
        float honeyMultiplier = 1f + count * GroundworkToolsDomain.BeehivePollinationHoneySpeedBonusPercentPerTarget / 100f;
        return new PollinationSummary(count, maxCount, honeyMultiplier);
    }

    private static PollinationCache? GetPollinationCache(Beehive beehive, int maxCount, float radius, bool canPollinate, bool requireDaylight = true)
    {
        if (beehive == null || maxCount <= 0 || radius <= 0f)
        {
            return null;
        }

        float now = Time.realtimeSinceStartup;
        PrunePollinationCaches(now);
        if (!PollinationCaches.TryGetValue(beehive, out PollinationCache? cache))
        {
            cache = new PollinationCache();
            PollinationCaches[beehive] = cache;
        }

        if (!cache.IsFresh(now, maxCount, radius, canPollinate, requireDaylight))
        {
            RefreshPollinationCache(beehive, cache, maxCount, radius, canPollinate, requireDaylight, now);
        }

        return cache;
    }

    private static void RefreshPollinationCache(Beehive beehive, PollinationCache cache, int maxCount, float radius, bool canPollinate, bool requireDaylight, float now)
    {
        cache.Clear();
        cache.RefreshedAt = now;
        cache.MaxCount = maxCount;
        cache.Radius = radius;
        cache.CanPollinate = canPollinate;
        cache.RequireDaylight = requireDaylight;
        if (!canPollinate)
        {
            return;
        }

        int hitCount = Physics.OverlapSphereNonAlloc(
            beehive.transform.position,
            radius,
            PollinationHits,
            GetPollinationMask(),
            QueryTriggerInteraction.UseGlobal);

        SeenPlants.Clear();
        SeenPickables.Clear();
        PollinationCandidates.Clear();
        try
        {
            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = PollinationHits[i];
                if (hit == null)
                {
                    continue;
                }

                Plant? plant = hit.GetComponentInParent<Plant>();
                if (plant != null)
                {
                    if (SeenPlants.Add(plant) &&
                        IsGrowingTarget(plant) &&
                        IsAssignedToHive(plant, beehive, radius, requireDaylight))
                    {
                        AddPollinationCandidate(plant, null, plant.transform.position, beehive.transform.position, plant.GetInstanceID());
                    }

                    continue;
                }

                Pickable? pickable = hit.GetComponentInParent<Pickable>();
                if (pickable != null &&
                    SeenPickables.Add(pickable) &&
                    IsGrowingTarget(pickable) &&
                    IsAssignedToHive(pickable, beehive, radius, requireDaylight))
                {
                    AddPollinationCandidate(null, pickable, pickable.transform.position, beehive.transform.position, pickable.GetInstanceID());
                }
            }

            PollinationCandidates.Sort(static (left, right) =>
            {
                return ComparePollinationDistance(
                    left.HorizontalDistance,
                    left.HeightDistance,
                    left.InstanceId,
                    right.HorizontalDistance,
                    right.HeightDistance,
                    right.InstanceId);
            });

            for (int i = 0; i < PollinationCandidates.Count && i < maxCount; i++)
            {
                PollinationTargetCandidate candidate = PollinationCandidates[i];
                if (candidate.Plant != null)
                {
                    cache.Plants.Add(candidate.Plant);
                }
                else if (candidate.Pickable != null)
                {
                    cache.Pickables.Add(candidate.Pickable);
                }
            }
        }
        finally
        {
            SeenPlants.Clear();
            SeenPickables.Clear();
            PollinationCandidates.Clear();
        }
    }

    // Plant and foraging growth multipliers.
    private static float GetPlantGrowthMultiplierForTarget(Plant plant)
    {
        if (!GroundworkToolsDomain.BeehivePollinationFeatureEnabled ||
            plant == null)
        {
            return 1f;
        }

        float radius = GroundworkToolsDomain.BeehivePollinationRadius;
        int maxCount = GroundworkToolsDomain.BeehivePollinationMaxPlants;
        Beehive? hive = FindAssignedHive(plant, radius);
        if (hive == null || maxCount <= 0)
        {
            return 1f;
        }

        PollinationCache? cache = GetPollinationCache(hive, maxCount, radius, canPollinate: true);
        return cache != null && cache.Plants.Contains(plant)
            ? GetHivePlantGrowthMultiplier(hive)
            : 1f;
    }

    private static float GetForagingRespawnMultiplierForTarget(Pickable pickable)
    {
        if (!GroundworkToolsDomain.BeehivePollinationFeatureEnabled ||
            pickable == null ||
            !IsGrowingTarget(pickable))
        {
            return 1f;
        }

        float radius = GroundworkToolsDomain.BeehivePollinationRadius;
        int maxCount = GroundworkToolsDomain.BeehivePollinationMaxPlants;
        Beehive? hive = FindAssignedHive(pickable, radius);
        if (hive == null || maxCount <= 0)
        {
            return 1f;
        }

        PollinationCache? cache = GetPollinationCache(hive, maxCount, radius, canPollinate: true);
        return cache != null && cache.Pickables.Contains(pickable)
            ? GetHiveForagingRespawnMultiplier(hive)
            : 1f;
    }

    private static bool IsAssignedToHive(Component target, Beehive candidate, float radius, bool requireDaylight)
    {
        return FindAssignedHive(target, radius, requireDaylight) == candidate;
    }

    private static void AddPollinationCandidate(Plant? plant, Pickable? pickable, Vector3 targetPosition, Vector3 hivePosition, int instanceId)
    {
        PollinationCandidates.Add(new PollinationTargetCandidate(
            plant,
            pickable,
            GetHorizontalSqrDistance(targetPosition, hivePosition),
            GetHeightDistance(targetPosition, hivePosition),
            instanceId));
    }

    private static Beehive? FindAssignedHive(Component target, float radius, bool requireDaylight = true)
    {
        if (target == null || radius <= 0f)
        {
            return null;
        }

        float now = Time.realtimeSinceStartup;
        PrunePollinationCaches(now);
        Vector3 targetPosition = target.transform.position;
        if (!AssignmentCaches.TryGetValue(target, out AssignmentCache? cache))
        {
            cache = new AssignmentCache();
            AssignmentCaches[target] = cache;
        }

        if (cache.IsFresh(now, radius, targetPosition, requireDaylight))
        {
            if (cache.AssignedHive == null || CanHivePollinate(cache.AssignedHive, requireDaylight))
            {
                return cache.AssignedHive;
            }
        }

        Beehive? assignedHive = FindAssignedHiveUncached(targetPosition, radius, requireDaylight);
        cache.Set(assignedHive, now, radius, targetPosition, requireDaylight);
        return assignedHive;
    }

    private static Beehive? FindAssignedHiveUncached(Vector3 targetPosition, float radius, bool requireDaylight)
    {
        if (radius <= 0f)
        {
            return null;
        }

        int hitCount = Physics.OverlapSphereNonAlloc(
            targetPosition,
            radius,
            AssignmentHits,
            GetPollinationMask(),
            QueryTriggerInteraction.UseGlobal);

        Beehive? bestHive = null;
        float bestHorizontalDistance = float.MaxValue;
        float bestHeightDistance = float.MaxValue;
        int bestInstanceId = int.MaxValue;
        SeenHives.Clear();
        try
        {
            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = AssignmentHits[i];
                if (hit == null)
                {
                    continue;
                }

                Beehive? hive = hit.GetComponentInParent<Beehive>();
                if (hive == null ||
                    !SeenHives.Add(hive) ||
                    !CanHivePollinate(hive, requireDaylight))
                {
                    continue;
                }

                float horizontalDistance = GetHorizontalSqrDistance(hive.transform.position, targetPosition);
                float heightDistance = GetHeightDistance(hive.transform.position, targetPosition);
                int instanceId = hive.GetInstanceID();
                if (ComparePollinationDistance(
                        horizontalDistance,
                        heightDistance,
                        instanceId,
                        bestHorizontalDistance,
                        bestHeightDistance,
                        bestInstanceId) < 0)
                {
                    bestHorizontalDistance = horizontalDistance;
                    bestHeightDistance = heightDistance;
                    bestHive = hive;
                    bestInstanceId = instanceId;
                }
            }
        }
        finally
        {
            SeenHives.Clear();
        }

        return bestHive;
    }

    private static int ComparePollinationDistance(
        float leftHorizontalDistance,
        float leftHeightDistance,
        int leftInstanceId,
        float rightHorizontalDistance,
        float rightHeightDistance,
        int rightInstanceId)
    {
        if (!Mathf.Approximately(leftHorizontalDistance, rightHorizontalDistance))
        {
            return leftHorizontalDistance.CompareTo(rightHorizontalDistance);
        }

        if (!Mathf.Approximately(leftHeightDistance, rightHeightDistance))
        {
            return leftHeightDistance.CompareTo(rightHeightDistance);
        }

        return leftInstanceId.CompareTo(rightInstanceId);
    }

    private static float GetHorizontalSqrDistance(Vector3 left, Vector3 right)
    {
        float x = left.x - right.x;
        float z = left.z - right.z;
        return x * x + z * z;
    }

    private static float GetHeightDistance(Vector3 left, Vector3 right)
    {
        return Mathf.Abs(left.y - right.y);
    }

    private static void PrunePollinationCaches(float now)
    {
        if (now < _nextPollinationCachePruneAt)
        {
            return;
        }

        _nextPollinationCachePruneAt = now + PollinationCachePruneIntervalSeconds;
        StalePollinationCacheHives.Clear();
        foreach (KeyValuePair<Beehive, PollinationCache> entry in PollinationCaches)
        {
            Beehive hive = entry.Key;
            PollinationCache cache = entry.Value;
            if (hive == null ||
                !IsValid(hive) ||
                now - cache.RefreshedAt > PollinationCacheMaxIdleSeconds)
            {
                StalePollinationCacheHives.Add(hive!);
            }
        }

        foreach (Beehive hive in StalePollinationCacheHives)
        {
            PollinationCaches.Remove(hive);
        }

        StalePollinationCacheHives.Clear();

        StaleAssignmentCacheTargets.Clear();
        foreach (KeyValuePair<Component, AssignmentCache> entry in AssignmentCaches)
        {
            Component target = entry.Key;
            AssignmentCache cache = entry.Value;
            if (target == null || now - cache.RefreshedAt > PollinationCacheMaxIdleSeconds)
            {
                StaleAssignmentCacheTargets.Add(target!);
            }
        }

        foreach (Component target in StaleAssignmentCacheTargets)
        {
            AssignmentCaches.Remove(target);
        }

        StaleAssignmentCacheTargets.Clear();

        StaleLoadedSinceTargets.Clear();
        foreach (KeyValuePair<Component, long> entry in LoadedSinceTicksByTarget)
        {
            Component target = entry.Key;
            if (target == null)
            {
                StaleLoadedSinceTargets.Add(target!);
            }
        }

        foreach (Component target in StaleLoadedSinceTargets)
        {
            LoadedSinceTicksByTarget.Remove(target);
        }

        StaleLoadedSinceTargets.Clear();
    }

    // Unloaded catch-up helpers.
    private static float ApplyUnloadedCatchupEffectiveness(float multiplier)
    {
        return 1f + (Mathf.Max(1f, multiplier) - 1f) * UnloadedCatchupEffectiveness;
    }

    private static float ApplyCatchupDurationMultiplier(float durationSeconds, float loadedMultiplier, float catchupElapsedSeconds)
    {
        float fullMultiplier = Mathf.Max(1f, loadedMultiplier);
        if (durationSeconds <= 0f || fullMultiplier <= 1.001f)
        {
            return durationSeconds;
        }

        float catchupElapsed = Mathf.Max(0f, catchupElapsedSeconds);
        if (catchupElapsed <= 0.001f)
        {
            return durationSeconds / fullMultiplier;
        }

        float catchupMultiplier = ApplyUnloadedCatchupEffectiveness(fullMultiplier);
        float remainingProgress = durationSeconds - catchupElapsed * catchupMultiplier;
        if (remainingProgress <= 0f)
        {
            return Mathf.Max(0f, catchupElapsed - 0.001f);
        }

        return catchupElapsed + remainingProgress / fullMultiplier;
    }

    private static float GetUnloadedCatchupElapsed(Component target, float totalElapsedSeconds, long eventStartTicks = 0L)
    {
        if (target == null || ZNet.instance == null || totalElapsedSeconds <= 0f)
        {
            return 0f;
        }

        long nowTicks = ZNet.instance.GetTime().Ticks;
        if (!LoadedSinceTicksByTarget.TryGetValue(target, out long loadedSinceTicks))
        {
            loadedSinceTicks = nowTicks;
            LoadedSinceTicksByTarget[target] = loadedSinceTicks;
        }

        if (eventStartTicks > 0L)
        {
            loadedSinceTicks = Math.Max(loadedSinceTicks, eventStartTicks);
        }

        float loadedElapsed = GetSecondsBetweenTicks(loadedSinceTicks, nowTicks);
        loadedElapsed = Mathf.Clamp(loadedElapsed, 0f, totalElapsedSeconds);
        return Mathf.Max(0f, totalElapsedSeconds - loadedElapsed);
    }

    private static float GetSecondsSinceTicks(long ticks)
    {
        return ZNet.instance != null
            ? GetSecondsBetweenTicks(ticks, ZNet.instance.GetTime().Ticks)
            : 0f;
    }

    private static float GetSecondsBetweenTicks(long startTicks, long endTicks)
    {
        if (startTicks <= 0L || endTicks <= startTicks)
        {
            return 0f;
        }

        return (float)Math.Max(0.0, TimeSpan.FromTicks(endTicks - startTicks).TotalSeconds);
    }

    private static long GetPickedTimeTicks(Pickable pickable)
    {
        ZNetView? nview = pickable != null ? pickable.GetComponent<ZNetView>() : null;
        ZDO? zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
        return zdo?.GetLong(ZDOVars.s_pickedTime, 0L) ?? 0L;
    }

    private static float GetHivePlantGrowthMultiplier(Beehive beehive)
    {
        return GetHiveEmptyScaledMultiplier(beehive, GroundworkToolsDomain.BeehivePollinationPlantGrowSpeedFactor);
    }

    private static float GetHiveForagingRespawnMultiplier(Beehive beehive)
    {
        return GetHiveEmptyScaledMultiplier(beehive, GroundworkToolsDomain.BeehivePollinationForagingRespawnSpeedFactor);
    }

    private static float GetHiveEmptyScaledMultiplier(Beehive beehive, float maxMultiplier)
    {
        if (!CanHivePollinate(beehive))
        {
            return 1f;
        }

        int maxHoney = GetEffectiveMaxHoney(beehive);
        float emptyFactor = maxHoney > 0
            ? 1f - Mathf.Clamp01((float)GetHoneyLevel(beehive) / maxHoney)
            : 0f;
        return Mathf.Lerp(1f, Mathf.Max(1f, maxMultiplier), emptyFactor);
    }

    private static bool CanHivePollinate(Beehive beehive, bool requireDaylight = true)
    {
        if (!GroundworkToolsDomain.BeehivePollinationFeatureEnabled ||
            beehive == null ||
            !IsValid(beehive) ||
            (requireDaylight && IsNight()) ||
            EnvironmentEffectSystem.IsWetEnvironment() ||
            GetHoneyLevel(beehive) >= GetEffectiveMaxHoney(beehive) ||
            !IsBiomeAllowed(beehive))
        {
            return false;
        }

        return !TryGetCoverPercentage(beehive, out float coverPercentage) || HasFreeSpace(beehive, coverPercentage);
    }

    private static bool IsNight()
    {
        return EnvMan.instance != null && !EnvMan.IsDaylight();
    }

    private static bool IsGrowingTarget(Plant plant)
    {
        return plant != null && plant.GetStatus() == Plant.Status.Healthy;
    }

    private static bool IsGrowingTarget(Pickable pickable)
    {
        return pickable != null &&
               !pickable.CanBePicked() &&
               FarmingSkillSystem.IsForagingTarget(pickable);
    }

    private static float GetCoverProductionMultiplier(Beehive beehive, float coverPercentage)
    {
        if (beehive == null || beehive.m_maxCover <= 0f || coverPercentage >= beehive.m_maxCover)
        {
            return 1f;
        }

        float openness = 1f - Mathf.Clamp01(coverPercentage / Mathf.Max(0.0001f, beehive.m_maxCover));
        return Mathf.Lerp(1f, GroundworkToolsDomain.BeehiveCoverMaxSpeedMultiplier, openness * openness);
    }

    private static float GetNightProductionMultiplier(bool unloadedCatchup)
    {
        float nightRate = GroundworkToolsDomain.BeehiveNightHoneyRate;
        if (nightRate >= 0.999f)
        {
            return 1f;
        }

        if (unloadedCatchup)
        {
            return UnloadedCatchupDaylightShare + (1f - UnloadedCatchupDaylightShare) * nightRate;
        }

        return EnvMan.instance != null && !EnvMan.IsDaylight()
            ? nightRate
            : 1f;
    }

    private static void AppendCurrentHoneyRateLine(
        ref string hoverText,
        float nightMultiplier,
        string nightToken,
        string nightFallback,
        float rainMultiplier,
        string rainToken,
        string rainFallback)
    {
        List<string> parts = [];
        if (nightMultiplier < 0.999f)
        {
            parts.Add(GroundworkLocalization.Format(nightToken, nightFallback, FormatMultiplier(nightMultiplier)));
        }

        if (rainMultiplier < 0.999f)
        {
            parts.Add(GroundworkLocalization.Format(rainToken, rainFallback, FormatMultiplier(rainMultiplier)));
        }

        if (parts.Count == 0)
        {
            return;
        }

        AppendLine(ref hoverText, Colorize(string.Join("  ", parts)));
    }

    private static void ReplaceHoverHeader(Beehive beehive, ref string hoverText, int honeyLevel, int maxHoney, int farmingCapacityBonus)
    {
        if (string.IsNullOrEmpty(hoverText))
        {
            return;
        }

        string beehiveName = LocalizeVanillaText(beehive.m_name, "Beehive");
        string honeyName = LocalizeVanillaText(
            beehive.m_honeyItem?.m_itemData.m_shared.m_name,
            "Honey");
        string header = farmingCapacityBonus > 0
            ? GroundworkLocalization.Format(
                "groundwork_beehive_header_farming",
                "{0} ({1} {2}/{3}, Max +{4})",
                beehiveName,
                honeyName,
                honeyLevel,
                maxHoney,
                farmingCapacityBonus)
            : GroundworkLocalization.Format(
                "groundwork_beehive_header",
                "{0} ({1} {2}/{3})",
                beehiveName,
                honeyName,
                honeyLevel,
                maxHoney);

        int lineBreakIndex = hoverText.IndexOf('\n');
        hoverText = lineBreakIndex >= 0
            ? header + hoverText[lineBreakIndex..]
            : header;
    }

    private static string LocalizeVanillaText(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string raw = value!;
        string? localizedValue = Localization.instance?.Localize(raw);
        if (string.IsNullOrWhiteSpace(localizedValue))
        {
            return raw.StartsWith("$", StringComparison.Ordinal) ? fallback : raw;
        }

        string localized = localizedValue!;
        return raw.StartsWith("$", StringComparison.Ordinal) && string.Equals(localized, raw, StringComparison.Ordinal)
            ? fallback
            : localized;
    }

    private static bool HasFreeSpace(Beehive beehive, float coverPercentage)
    {
        return beehive.m_maxCover <= 0f || coverPercentage < beehive.m_maxCover;
    }

    private static bool IsBiomeAllowed(Beehive beehive)
    {
        return (Heightmap.FindBiome(beehive.transform.position) & beehive.m_biome) != 0;
    }

    private static bool TryGetCoverPercentage(Beehive beehive, out float coverPercentage)
    {
        coverPercentage = 0f;
        if (beehive == null || beehive.m_coverPoint == null)
        {
            return false;
        }

        Cover.GetCoverForPoint(beehive.m_coverPoint.position, out coverPercentage, out _);
        coverPercentage = Mathf.Clamp01(coverPercentage);
        return true;
    }

    private static float GetSecondsSinceLastUpdate(ZDO zdo)
    {
        long ticks = zdo.GetLong(ZDOVars.s_lastTime, 0L);
        if (ticks <= 0L || ZNet.instance == null)
        {
            return 0f;
        }

        double seconds = (ZNet.instance.GetTime() - new DateTime(ticks)).TotalSeconds;
        return (float)Math.Max(0.0, seconds);
    }

    private static float GetTendedFarmingLevel(Beehive beehive)
    {
        return Mathf.Clamp(GetZdo(beehive)?.GetFloat(TendedFarmingLevelKey, 0f) ?? 0f, 0f, 100f);
    }

    private static int GetFarmingCapacityBonusHoney(Beehive beehive)
    {
        int levelsPerBonusHoney = GroundworkToolsDomain.BeehiveCapacityFarmingLevelsPerBonusHoney;
        if (levelsPerBonusHoney <= 0)
        {
            return 0;
        }

        return Mathf.Max(0, Mathf.FloorToInt(GetTendedFarmingLevel(beehive) / levelsPerBonusHoney));
    }

    private static float ResolveSenderFarmingLevel(long sender)
    {
        Player? senderPlayer = ResolveSenderPlayer(sender);
        if (senderPlayer != null)
        {
            return Mathf.Clamp(senderPlayer.GetSkillLevel(Skills.SkillType.Farming), 0f, 100f);
        }

        Player? localPlayer = Player.m_localPlayer;
        return localPlayer != null ? Mathf.Clamp(localPlayer.GetSkillLevel(Skills.SkillType.Farming), 0f, 100f) : 0f;
    }

    private static Player? ResolveSenderPlayer(long sender)
    {
        foreach (Player player in Player.GetAllPlayers())
        {
            ZNetView? nview = ((Character)player).m_nview;
            ZDO? zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo != null && zdo.m_uid.UserID == sender)
            {
                return player;
            }
        }

        return null;
    }

    private static int GetHoneyLevel(Beehive beehive)
    {
        return GetZdo(beehive)?.GetInt(ZDOVars.s_level) ?? 0;
    }

    private static ZDO? GetZdo(Beehive beehive)
    {
        if (beehive == null || beehive.m_nview == null || !beehive.m_nview.IsValid())
        {
            return null;
        }

        return beehive.m_nview.GetZDO();
    }

    private static bool IsValid(Beehive beehive)
    {
        return beehive.m_nview != null && beehive.m_nview.IsValid();
    }

    private static int GetPollinationMask()
    {
        if (_pollinationMask == 0)
        {
            _pollinationMask = LayerMask.GetMask("item", "Default", "Default_small", "piece", "piece_nonsolid");
        }

        return _pollinationMask;
    }

    private static void AppendLine(ref string text, string line)
    {
        text = string.IsNullOrEmpty(text) ? line : text + "\n" + line;
    }

    private static string Colorize(string text)
    {
        return "<color=#a8e6a1>" + text + "</color>";
    }

    private static string FormatPercent(float value)
    {
        return Mathf.RoundToInt(Mathf.Clamp01(value) * 100f).ToString(CultureInfo.InvariantCulture) + "%";
    }

    private static string FormatMultiplier(float value)
    {
        return "x" + value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    // Formatting and vanilla hover header replacement.
    private static string FormatSeconds(float seconds)
    {
        return GroundworkLocalization.FormatDuration(seconds);
    }
}

// Harmony patches.
[HarmonyPatch(typeof(Beehive), nameof(Beehive.GetHoverText))]
internal static class BeehiveGetHoverTextPollinationPatch
{
    private static void Postfix(Beehive __instance, ref string __result)
    {
        BeehivePollinationSystem.AppendHoverText(__instance, ref __result);
    }
}

[HarmonyPatch(typeof(Beehive), nameof(Beehive.Awake))]
internal static class BeehiveAwakePollinationPatch
{
    private static void Postfix(Beehive __instance)
    {
        BeehivePollinationSystem.TryStoreBuilderFarmingLevel(__instance);
    }
}

[HarmonyPatch(typeof(Beehive), "UpdateBees")]
internal static class BeehiveUpdateBeesPollinationPatch
{
    private static bool Prefix(Beehive __instance, ref float __state)
    {
        __state = 0f;
        bool unloadedCatchup = BeehivePollinationSystem.ShouldUseUnloadedProductionCatchup(__instance);
        if (EnvironmentEffectSystem.TryPauseBeehiveProduction(__instance, unloadedCatchup))
        {
            return false;
        }

        __state = __instance.m_secPerUnit;
        float multiplier = BeehivePollinationSystem.GetProductionSpeedMultiplier(__instance, unloadedCatchup);
        if (multiplier <= 0.001f)
        {
            EnvironmentEffectSystem.PauseBeehiveProduction(__instance);
            return false;
        }

        if (Mathf.Abs(multiplier - 1f) > 0.001f && __instance.m_secPerUnit > 0f)
        {
            __instance.m_secPerUnit /= multiplier;
        }

        return true;
    }

    private static void Postfix(Beehive __instance, float __state)
    {
        if (__state > 0f)
        {
            __instance.m_secPerUnit = __state;
        }
    }
}

[HarmonyPatch(typeof(Beehive), "IncreseLevel")]
internal static class BeehiveIncreaseLevelPollinationPatch
{
    private static void Prefix(Beehive __instance, ref int __state)
    {
        __state = __instance.m_maxHoney;
        __instance.m_maxHoney = BeehivePollinationSystem.GetEffectiveMaxHoney(__instance);
    }

    private static void Postfix(Beehive __instance, int __state)
    {
        if (__state > 0)
        {
            __instance.m_maxHoney = __state;
        }
    }
}

[HarmonyPatch(typeof(Beehive), "RPC_Extract")]
internal static class BeehiveRpcExtractPollinationPatch
{
    private static void Prefix(Beehive __instance, ref int __state)
    {
        ZDO? zdo = __instance.m_nview != null && __instance.m_nview.IsValid()
            ? __instance.m_nview.GetZDO()
            : null;
        __state = Mathf.Max(0, zdo?.GetInt(ZDOVars.s_level) ?? 0);
    }

    private static void Postfix(Beehive __instance, long caller, int __state)
    {
        if (__state > 0)
        {
            BeehivePollinationSystem.StoreTendedFarmingLevel(__instance, caller);
            BeehivePollinationSystem.RaiseFarmingSkillForHarvest(caller, __state);
        }
    }
}

[HarmonyPatch(typeof(Pickable), nameof(Pickable.Awake))]
internal static class PickableAwakePollinationPatch
{
    private static void Postfix(Pickable __instance)
    {
        BeehivePollinationSystem.TrackLoadedTarget(__instance);
    }
}
