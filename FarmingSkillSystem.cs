using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Groundwork;

internal static class FarmingSkillSystem
{
    private const string ForagingPickerSkillKey = "Groundwork_ForagingPickerFarmingSkill";
    private const string PlantPlanterSkillKey = "Groundwork_PlanterFarmingSkill";
    private const string PreferredBonusEffectSourcePrefab = "Pickable_Fiddlehead";
    private static readonly HashSet<Pickable> SeenPickables = new();
    private static Player? _placingPlayer;
    private static bool _rangePicking;
    private static int _suppressRangePickup;
    private static int _pickupMask;
    private static EffectList? _foragingBonusEffectFallback;

    internal readonly struct RangePickupSuppression : IDisposable
    {
        public void Dispose()
        {
            if (_suppressRangePickup > 0)
            {
                _suppressRangePickup--;
            }
        }
    }

    internal readonly struct PickableRespawnTiming(
        float totalSeconds,
        double elapsedSeconds,
        float remainingSeconds,
        bool modified,
        bool hasNetworkState,
        bool hasPickedTime)
    {
        internal readonly float TotalSeconds = totalSeconds;
        internal readonly double ElapsedSeconds = elapsedSeconds;
        internal readonly float RemainingSeconds = remainingSeconds;
        internal readonly bool Modified = modified;
        internal readonly bool HasNetworkState = hasNetworkState;
        internal readonly bool HasPickedTime = hasPickedTime;
    }

    internal sealed class PickableInteractFarmingState
    {
        private readonly Pickable _pickable;
        private readonly Skills.SkillType _pickRaiseSkill;
        private readonly float _maxLevelBonusChance;
        private readonly int _bonusYieldAmount;
        private readonly EffectList _bonusEffect;
        private bool _restored;

        internal PickableInteractFarmingState(Pickable pickable)
        {
            _pickable = pickable;
            _pickRaiseSkill = pickable.m_pickRaiseSkill;
            _maxLevelBonusChance = pickable.m_maxLevelBonusChance;
            _bonusYieldAmount = pickable.m_bonusYieldAmount;
            _bonusEffect = pickable.m_bonusEffect;
        }

        internal void Restore()
        {
            if (_restored)
            {
                return;
            }

            _restored = true;
            if (_pickable == null)
            {
                return;
            }

            _pickable.m_pickRaiseSkill = _pickRaiseSkill;
            _pickable.m_maxLevelBonusChance = _maxLevelBonusChance;
            _pickable.m_bonusYieldAmount = _bonusYieldAmount;
            _pickable.m_bonusEffect = _bonusEffect;
        }
    }

    internal static void Shutdown()
    {
        SeenPickables.Clear();
        _placingPlayer = null;
        _rangePicking = false;
        _suppressRangePickup = 0;
        _pickupMask = 0;
        _foragingBonusEffectFallback = null;
    }

    internal static bool IsForagingTarget(Pickable? pickable)
    {
        if (pickable == null)
        {
            return false;
        }

        float respawnMinutes = pickable.m_respawnTimeMinutes;
        bool? configuredTarget = null;
        if (GrowthOverrideSystem.TryGetPickableRule(
                pickable,
                out GrowthOverrideSystem.ResolvedPickableRule rule))
        {
            if (rule.HasRespawnOverride)
            {
                respawnMinutes = rule.RespawnMinutes;
            }

            configuredTarget = rule.ForagingTarget;
        }

        return respawnMinutes > 0f &&
               (configuredTarget ?? DropsEdibleItem(pickable));
    }

    internal static bool IsAutomaticForagingTarget(Pickable? pickable)
    {
        return pickable != null &&
               pickable.m_respawnTimeMinutes > 0f &&
               DropsEdibleItem(pickable);
    }

    internal static void RefreshForagingBonusEffectFallback(ZNetScene scene)
    {
        if (scene == null)
        {
            return;
        }

        _foragingBonusEffectFallback = ResolveForagingBonusEffectFallback(scene);
    }

    internal static PickableInteractFarmingState? BeginFarmingInteract(
        Pickable pickable,
        Humanoid character)
    {
        if (pickable == null ||
            character is not Player ||
            !IsForagingTarget(pickable))
        {
            return null;
        }

        bool hasRule = GrowthOverrideSystem.TryGetPickableRule(
            pickable,
            out GrowthOverrideSystem.ResolvedPickableRule rule);
        bool configuredBonus = hasRule && rule.BonusYield == true;
        bool nativeFarmingBonus = pickable.m_pickRaiseSkill == Skills.SkillType.Farming;
        if (!configuredBonus && !nativeFarmingBonus)
        {
            return null;
        }

        if (configuredBonus &&
            pickable.m_pickRaiseSkill != Skills.SkillType.None &&
            pickable.m_pickRaiseSkill != Skills.SkillType.Farming)
        {
            return null;
        }

        Skills.SkillType pickRaiseSkill = configuredBonus
            ? Skills.SkillType.Farming
            : pickable.m_pickRaiseSkill;
        float maxChance = configuredBonus && rule.HasMaxChance
            ? rule.MaxChanceAtLevel100
            : pickable.m_maxLevelBonusChance;
        int bonusAmount = configuredBonus && rule.HasBonusAmount
            ? rule.BonusAmount
            : pickable.m_bonusYieldAmount;
        EffectList bonusEffect = pickable.m_bonusEffect;
        if (!HasEffect(bonusEffect))
        {
            bonusEffect = _foragingBonusEffectFallback ?? new EffectList();
        }

        if (pickRaiseSkill == pickable.m_pickRaiseSkill &&
            Mathf.Approximately(maxChance, pickable.m_maxLevelBonusChance) &&
            bonusAmount == pickable.m_bonusYieldAmount &&
            ReferenceEquals(bonusEffect, pickable.m_bonusEffect))
        {
            return null;
        }

        PickableInteractFarmingState state = new(pickable);
        try
        {
            pickable.m_pickRaiseSkill = pickRaiseSkill;
            pickable.m_maxLevelBonusChance = maxChance;
            pickable.m_bonusYieldAmount = bonusAmount;
            pickable.m_bonusEffect = bonusEffect;
            return state;
        }
        catch
        {
            state.Restore();
            throw;
        }
    }

    internal static void TryPickupNearbyForagingTargets(Pickable source, Humanoid character)
    {
        if (_rangePicking ||
            _suppressRangePickup > 0 ||
            character is not Player player ||
            !IsForagingTarget(source))
        {
            return;
        }

        float maxRange = GroundworkToolsDomain.ForagingPickupMaxRange;
        if (maxRange <= 0)
        {
            return;
        }

        float radius = player.GetSkillFactor(Skills.SkillType.Farming) * maxRange;
        if (radius <= 0.05f)
        {
            return;
        }

        Collider[] pickupHits = Physics.OverlapSphere(
            source.transform.position,
            radius,
            GetPickupMask(),
            QueryTriggerInteraction.UseGlobal);

        _rangePicking = true;
        SeenPickables.Clear();
        SeenPickables.Add(source);
        try
        {
            for (int i = 0; i < pickupHits.Length; i++)
            {
                Collider hit = pickupHits[i];
                if (hit == null)
                {
                    continue;
                }

                Pickable? pickable = hit.GetComponentInParent<Pickable>();
                if (pickable == null ||
                    !SeenPickables.Add(pickable) ||
                    !IsForagingTarget(pickable) ||
                    !pickable.CanBePicked())
                {
                    continue;
                }

                pickable.Interact(player, repeat: false, alt: false);
            }
        }
        finally
        {
            SeenPickables.Clear();
            _rangePicking = false;
        }
    }

    internal static RangePickupSuppression SuppressRangePickup()
    {
        _suppressRangePickup++;
        return new RangePickupSuppression();
    }

    internal static void RememberForagingPickerSkill(Pickable pickable, long sender)
    {
        if (!GroundworkToolsDomain.ForagingFeatureEnabled ||
            !IsForagingTarget(pickable) ||
            !TryGetPickableZdo(pickable, requireOwner: true, out ZDO? zdo))
        {
            return;
        }

        zdo!.Set(ForagingPickerSkillKey, ResolveSenderFarmingSkill(sender));
    }

    internal static void EnsureForagingPickerSkill(Pickable pickable, bool picked)
    {
        if (!picked ||
            !IsForagingTarget(pickable) ||
            !TryGetPickableZdo(pickable, requireOwner: true, out ZDO? zdo))
        {
            return;
        }

        if (zdo!.GetFloat(ForagingPickerSkillKey, -1f) >= 0f)
        {
            return;
        }

        zdo.Set(ForagingPickerSkillKey, ResolveLocalFarmingSkill());
    }

    internal static bool TryGetPickableRespawnTiming(Pickable pickable, out PickableRespawnTiming timing)
    {
        timing = default;
        if (pickable == null)
        {
            return false;
        }

        float respawnMinutes = Math.Max(0f, pickable.m_respawnTimeMinutes);
        bool modified = false;
        if (GrowthOverrideSystem.TryGetPickableRule(
                pickable,
                out GrowthOverrideSystem.ResolvedPickableRule rule) &&
            rule.HasRespawnOverride)
        {
            modified = !Mathf.Approximately(respawnMinutes, rule.RespawnMinutes);
            respawnMinutes = rule.RespawnMinutes;
        }

        float respawnSeconds = respawnMinutes * 60f;
        if (respawnSeconds <= 0f)
        {
            return false;
        }

        if (IsForagingTarget(pickable))
        {
            float speed = GetForagingRespawnSpeedMultiplier(pickable);
            if (speed > 1.001f)
            {
                respawnSeconds /= speed;
                modified = true;
            }

            if (BeehivePollinationSystem.TryModifyForagingRespawnSeconds(pickable, ref respawnSeconds))
            {
                modified = true;
            }

            if (EnvironmentEffectSystem.TryModifyForagingRespawnSeconds(pickable, ref respawnSeconds))
            {
                modified = true;
            }
        }

        if (!TryGetPickableZdo(pickable, requireOwner: false, out ZDO? zdo))
        {
            timing = new PickableRespawnTiming(
                respawnSeconds,
                0.0,
                respawnSeconds,
                modified,
                hasNetworkState: false,
                hasPickedTime: false);
            return true;
        }

        long pickedTime = zdo!.GetLong(ZDOVars.s_pickedTime, 0L);
        if (pickedTime <= 1L)
        {
            timing = new PickableRespawnTiming(
                respawnSeconds,
                0.0,
                respawnSeconds,
                modified,
                hasNetworkState: true,
                hasPickedTime: false);
            return true;
        }

        double elapsedSeconds = TimeSpan.FromTicks(Math.Max(0L, GetCurrentTicks() - pickedTime)).TotalSeconds;
        float remainingSeconds = Math.Max(0f, respawnSeconds - (float)elapsedSeconds);
        timing = new PickableRespawnTiming(
            respawnSeconds,
            elapsedSeconds,
            remainingSeconds,
            modified,
            hasNetworkState: true,
            hasPickedTime: true);
        return true;
    }

    internal static bool ShouldRunVanillaShouldRespawn(Pickable pickable, ref bool result)
    {
        if (!TryGetPickableRespawnTiming(pickable, out PickableRespawnTiming timing) ||
            !timing.Modified ||
            !timing.HasNetworkState)
        {
            return true;
        }

        result = (!timing.HasPickedTime || timing.ElapsedSeconds > timing.TotalSeconds) &&
                 PassesSpawnCheck(pickable);
        return false;
    }

    internal static void BeginPlacePiece(Player player)
    {
        if (!GroundworkToolsDomain.PlantGrowFeatureEnabled)
        {
            _placingPlayer = null;
            return;
        }

        _placingPlayer = player;
    }

    internal static void EndPlacePiece()
    {
        _placingPlayer = null;
    }

    internal static void TryStorePlanterSkill(Plant plant)
    {
        Player? player = _placingPlayer;
        if (player == null ||
            !GroundworkToolsDomain.PlantGrowFeatureEnabled ||
            !TryGetPlantZdo(plant, requireOwner: true, out ZDO? zdo))
        {
            return;
        }

        zdo!.Set(PlantPlanterSkillKey, player.GetSkillFactor(Skills.SkillType.Farming));
    }

    internal static void TryModifyGrowTime(Plant plant, ref float growTime)
    {
        if (plant == null)
        {
            return;
        }

        ApplyConfiguredPlantGrowTime(plant, ref growTime);
        if (growTime <= 0f)
        {
            return;
        }

        TryModifyPlanterGrowTime(plant, ref growTime);
        BeehivePollinationSystem.TryModifyPlantGrowTime(plant, ref growTime);
        EnvironmentEffectSystem.TryModifyPlantGrowTime(plant, ref growTime);
    }

    private static void ApplyConfiguredPlantGrowTime(Plant plant, ref float growTime)
    {
        if (!GrowthOverrideSystem.TryGetPlantRule(
                plant,
                out GrowthOverrideSystem.ResolvedPlantRule rule) ||
            !rule.HasGrowTimeOverride)
        {
            return;
        }

        UnityEngine.Random.State randomState = UnityEngine.Random.state;
        float seedFraction;
        try
        {
            UnityEngine.Random.InitState(plant.m_seed);
            seedFraction = UnityEngine.Random.value;
        }
        finally
        {
            UnityEngine.Random.state = randomState;
        }

        growTime = Mathf.Lerp(rule.GrowSecondsMin, rule.GrowSecondsMax, seedFraction);
    }

    private static void TryModifyPlanterGrowTime(Plant plant, ref float growTime)
    {
        float speedFactor = GroundworkToolsDomain.PlantGrowSpeedFactor;
        if (speedFactor <= 0 ||
            growTime <= 0f)
        {
            return;
        }

        float speed = GetPlantGrowSpeedMultiplier(plant);
        if (speed > 1.001f)
        {
            growTime /= speed;
        }
    }

    internal static float GetPlantGrowSpeedMultiplier(Plant plant)
    {
        float speedFactor = GroundworkToolsDomain.PlantGrowSpeedFactor;
        if (plant == null ||
            speedFactor <= 0f ||
            !TryGetPlantZdo(plant, requireOwner: false, out ZDO? zdo))
        {
            return 1f;
        }

        float skillFactor = Mathf.Clamp01(zdo!.GetFloat(PlantPlanterSkillKey, 0f));
        return ResolveSkillSpeedMultiplier(speedFactor, skillFactor);
    }

    internal static float GetForagingRespawnSpeedMultiplier(Pickable pickable)
    {
        float speedFactor = GroundworkToolsDomain.ForagingRespawnSpeedFactor;
        if (pickable == null ||
            speedFactor <= 0f ||
            !IsForagingTarget(pickable))
        {
            return 1f;
        }

        return ResolveSkillSpeedMultiplier(speedFactor, ResolveForagingPickerSkill(pickable));
    }

    private static bool DropsEdibleItem(Pickable pickable)
    {
        if (IsEdibleItemPrefab(pickable.m_itemPrefab))
        {
            return true;
        }

        if (pickable.m_extraDrops?.m_drops == null)
        {
            return false;
        }

        foreach (DropTable.DropData drop in pickable.m_extraDrops.m_drops)
        {
            if (IsEdibleItemPrefab(drop.m_item))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEdibleItemPrefab(GameObject? itemPrefab)
    {
        ItemDrop? itemDrop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
        if (itemDrop?.m_itemData?.m_shared == null)
        {
            return false;
        }

        ItemDrop.ItemData.SharedData shared = itemDrop.m_itemData.m_shared;
        return shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable &&
               (shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f);
    }

    private static EffectList? ResolveForagingBonusEffectFallback(ZNetScene scene)
    {
        Pickable? preferred = scene.GetPrefab(PreferredBonusEffectSourcePrefab)?.GetComponentInChildren<Pickable>(includeInactive: true);
        if (HasEffect(preferred?.m_bonusEffect))
        {
            return preferred!.m_bonusEffect;
        }

        foreach (GameObject prefab in EnumerateScenePrefabs(scene))
        {
            if (prefab == null)
            {
                continue;
            }

            Pickable[] pickables = prefab.GetComponentsInChildren<Pickable>(includeInactive: true);
            foreach (Pickable pickable in pickables)
            {
                if (IsForagingTarget(pickable) &&
                    pickable.m_pickRaiseSkill == Skills.SkillType.Farming &&
                    HasEffect(pickable.m_bonusEffect))
                {
                    return pickable.m_bonusEffect;
                }
            }
        }

        return BuildPickEffectFallback(scene);
    }

    private static EffectList BuildPickEffectFallback(ZNetScene scene)
    {
        List<EffectList.EffectData> effects = new();
        AddEffectIfFound(scene, effects, "sfx_pickable_pick");
        AddEffectIfFound(scene, effects, "vfx_pickable_pick");
        return new EffectList { m_effectPrefabs = effects.ToArray() };
    }

    private static void AddEffectIfFound(ZNetScene scene, List<EffectList.EffectData> effects, string prefabName)
    {
        GameObject? effectPrefab = scene.GetPrefab(prefabName);
        if (effectPrefab == null)
        {
            return;
        }

        effects.Add(new EffectList.EffectData
        {
            m_prefab = effectPrefab,
            m_enabled = true,
            m_variant = -1
        });
    }

    private static IEnumerable<GameObject> EnumerateScenePrefabs(ZNetScene scene)
    {
        foreach (GameObject prefab in scene.m_prefabs)
        {
            yield return prefab;
        }

        foreach (GameObject prefab in scene.m_nonNetViewPrefabs)
        {
            yield return prefab;
        }
    }

    private static bool HasEffect(EffectList? effectList)
    {
        return effectList != null && effectList.HasEffects();
    }

    private static float ResolveSenderFarmingSkill(long sender)
    {
        foreach (Player player in Player.GetAllPlayers())
        {
            ZNetView? nview = ((Character)player).m_nview;
            ZDO? zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo != null && zdo.m_uid.UserID == sender)
            {
                return player.GetSkillFactor(Skills.SkillType.Farming);
            }
        }

        return ResolveLocalFarmingSkill();
    }

    private static float ResolveLocalFarmingSkill()
    {
        Player? player = Player.m_localPlayer;
        return player != null ? player.GetSkillFactor(Skills.SkillType.Farming) : 0f;
    }

    private static float ResolveForagingPickerSkill(Pickable pickable)
    {
        return TryGetPickableZdo(pickable, requireOwner: false, out ZDO? zdo)
            ? Mathf.Clamp01(zdo!.GetFloat(ForagingPickerSkillKey, ResolveLocalFarmingSkill()))
            : ResolveLocalFarmingSkill();
    }

    private static float ResolveSkillSpeedMultiplier(float speedFactor, float skillFactor)
    {
        return Mathf.Lerp(1f, Mathf.Max(1f, speedFactor), Mathf.Clamp01(skillFactor));
    }

    private static long GetCurrentTicks()
    {
        return ZNet.instance != null
            ? ZNet.instance.GetTime().Ticks
            : DateTime.Now.Ticks;
    }

    private static bool PassesSpawnCheck(Pickable pickable)
    {
        return pickable.m_spawnCheck == null || pickable.m_spawnCheck(pickable);
    }

    private static bool TryGetPickableZdo(Pickable pickable, bool requireOwner, out ZDO? zdo)
    {
        zdo = null;
        ZNetView? nview = pickable.m_nview;
        if (nview == null || !nview.IsValid() || (requireOwner && !nview.IsOwner()))
        {
            return false;
        }

        zdo = nview.GetZDO();
        return zdo != null;
    }

    private static bool TryGetPlantZdo(Plant plant, bool requireOwner, out ZDO? zdo)
    {
        zdo = null;
        ZNetView? nview = plant.m_nview;
        if (nview == null || !nview.IsValid() || (requireOwner && !nview.IsOwner()))
        {
            return false;
        }

        zdo = nview.GetZDO();
        return zdo != null;
    }

    private static int GetPickupMask()
    {
        if (_pickupMask == 0)
        {
            _pickupMask = LayerMask.GetMask("item", "Default_small", "piece_nonsolid", "piece");
        }

        return _pickupMask;
    }
}

[HarmonyPatch(typeof(Pickable), nameof(Pickable.Interact))]
internal static class PickableInteractForagingPickupPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(
        Pickable __instance,
        Humanoid character,
        out FarmingSkillSystem.PickableInteractFarmingState? __state)
    {
        __state = FarmingSkillSystem.BeginFarmingInteract(__instance, character);
    }

    [HarmonyPriority(Priority.First)]
    private static void Postfix(
        Pickable __instance,
        Humanoid character,
        FarmingSkillSystem.PickableInteractFarmingState? __state)
    {
        __state?.Restore();
        FarmingSkillSystem.TryPickupNearbyForagingTargets(__instance, character);
    }

    private static Exception? Finalizer(
        FarmingSkillSystem.PickableInteractFarmingState? __state,
        Exception? __exception)
    {
        __state?.Restore();
        return __exception;
    }
}

[HarmonyPatch(typeof(Pickable), "RPC_Pick")]
internal static class PickableRpcPickForagingSkillPatch
{
    private static void Prefix(Pickable __instance, long sender)
    {
        FarmingSkillSystem.RememberForagingPickerSkill(__instance, sender);
    }
}

[HarmonyPatch(typeof(Pickable), nameof(Pickable.SetPicked))]
internal static class PickableSetPickedForagingSkillPatch
{
    private static void Postfix(Pickable __instance, bool picked)
    {
        FarmingSkillSystem.EnsureForagingPickerSkill(__instance, picked);
    }
}

[HarmonyPatch(typeof(Pickable), nameof(Pickable.ShouldRespawn))]
internal static class PickableShouldRespawnForagingPatch
{
    private static bool Prefix(Pickable __instance, ref bool __result)
    {
        return FarmingSkillSystem.ShouldRunVanillaShouldRespawn(__instance, ref __result);
    }
}

[HarmonyPatch(typeof(Plant), "GetGrowTime")]
internal static class PlantGetGrowTimeGroundworkPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Plant __instance, ref float __result)
    {
        FarmingSkillSystem.TryModifyGrowTime(__instance, ref __result);
    }
}
