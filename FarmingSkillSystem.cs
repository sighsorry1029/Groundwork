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
    private static readonly Collider[] PickupHits = new Collider[200];
    private static readonly HashSet<Pickable> SeenPickables = new();
    private static Player? _placingPlayer;
    private static bool _rangePicking;
    private static int _suppressRangePickup;
    private static int _pickupMask;

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

    internal static bool IsForagingTarget(Pickable? pickable)
    {
        return pickable != null &&
               pickable.m_respawnTimeMinutes > 0f &&
               DropsEdibleItem(pickable);
    }

    internal static void ApplyForagingBonusEffectFallbacks(ZNetScene scene)
    {
        EffectList? fallback = ResolveForagingBonusEffectFallback(scene);
        if (fallback == null || !fallback.HasEffects())
        {
            return;
        }

        int patched = 0;
        foreach (GameObject prefab in EnumerateScenePrefabs(scene))
        {
            if (prefab == null)
            {
                continue;
            }

            Pickable[] pickables = prefab.GetComponentsInChildren<Pickable>(includeInactive: true);
            foreach (Pickable pickable in pickables)
            {
                if (!IsForagingTarget(pickable) ||
                    pickable.m_pickRaiseSkill != Skills.SkillType.Farming ||
                    HasEffect(pickable.m_bonusEffect))
                {
                    continue;
                }

                pickable.m_bonusEffect = CloneEffectList(fallback);
                patched++;
            }
        }

        if (patched > 0)
        {
            GroundworkPlugin.ModLogger.LogInfo($"Added fallback Farming bonus effect to {patched} foraging pickable prefab(s) with empty m_bonusEffect.");
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

        int hitCount = Physics.OverlapSphereNonAlloc(
            source.transform.position,
            radius,
            PickupHits,
            GetPickupMask(),
            QueryTriggerInteraction.UseGlobal);

        _rangePicking = true;
        SeenPickables.Clear();
        SeenPickables.Add(source);
        try
        {
            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = PickupHits[i];
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

    internal static bool TryGetForagingRespawnSeconds(Pickable pickable, out float respawnSeconds)
    {
        respawnSeconds = 0f;
        if (pickable == null)
        {
            return false;
        }

        respawnSeconds = Math.Max(0f, pickable.m_respawnTimeMinutes) * 60f;
        if (respawnSeconds <= 0f ||
            !IsForagingTarget(pickable))
        {
            return false;
        }

        bool modified = false;
        float speed = GetForagingRespawnSpeedMultiplierForHover(pickable);
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

        return modified;
    }

    internal static bool ShouldRunVanillaShouldRespawn(Pickable pickable, ref bool result)
    {
        if (!TryGetForagingRespawnSeconds(pickable, out float respawnSeconds) ||
            !TryGetPickableZdo(pickable, requireOwner: false, out ZDO? zdo))
        {
            return true;
        }

        long pickedTime = zdo!.GetLong(ZDOVars.s_pickedTime, 0L);
        if (pickedTime <= 1L)
        {
            result = PassesSpawnCheck(pickable);
            return false;
        }

        double elapsedSeconds = TimeSpan.FromTicks(Math.Max(0L, GetCurrentTicks() - pickedTime)).TotalSeconds;
        result = elapsedSeconds > respawnSeconds && PassesSpawnCheck(pickable);
        return false;
    }

    internal static void TryReplayRangePickBonusEffect(Pickable pickable, int bonus)
    {
        if (!_rangePicking ||
            bonus <= 0 ||
            GroundworkToolsDomain.ForagingPickupMaxRange <= 0 ||
            !IsForagingTarget(pickable))
        {
            return;
        }

        Vector3 position = pickable.transform.position;
        if (pickable.m_bonusEffect != null && pickable.m_bonusEffect.HasEffects())
        {
            pickable.m_bonusEffect.Create(position, Quaternion.identity);
        }
        else if (pickable.m_pickEffector != null && pickable.m_pickEffector.HasEffects())
        {
            pickable.m_pickEffector.Create(position, Quaternion.identity);
        }
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
        if (plant == null || growTime <= 0f)
        {
            return;
        }

        TryModifyPlanterGrowTime(plant, ref growTime);
        BeehivePollinationSystem.TryModifyPlantGrowTime(plant, ref growTime);
        EnvironmentEffectSystem.TryModifyPlantGrowTime(plant, ref growTime);
    }

    private static void TryModifyPlanterGrowTime(Plant plant, ref float growTime)
    {
        float speedFactor = GroundworkToolsDomain.PlantGrowSpeedFactor;
        if (speedFactor <= 0 ||
            growTime <= 0f)
        {
            return;
        }

        float speed = GetPlantGrowSpeedMultiplierForHover(plant);
        if (speed > 1.001f)
        {
            growTime /= speed;
        }
    }

    internal static float GetPlantGrowSpeedMultiplierForHover(Plant plant)
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

    internal static float GetForagingRespawnSpeedMultiplierForHover(Pickable pickable)
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

    private static EffectList CloneEffectList(EffectList source)
    {
        EffectList.EffectData[] sourceEffects = source.m_effectPrefabs ?? [];
        EffectList.EffectData[] clonedEffects = new EffectList.EffectData[sourceEffects.Length];
        for (int i = 0; i < sourceEffects.Length; i++)
        {
            EffectList.EffectData sourceEffect = sourceEffects[i];
            clonedEffects[i] = new EffectList.EffectData
            {
                m_prefab = sourceEffect.m_prefab,
                m_enabled = sourceEffect.m_enabled,
                m_variant = sourceEffect.m_variant,
                m_attach = sourceEffect.m_attach,
                m_follow = sourceEffect.m_follow,
                m_inheritParentRotation = sourceEffect.m_inheritParentRotation,
                m_inheritParentScale = sourceEffect.m_inheritParentScale,
                m_multiplyParentVisualScale = sourceEffect.m_multiplyParentVisualScale,
                m_randomRotation = sourceEffect.m_randomRotation,
                m_scale = sourceEffect.m_scale,
                m_childTransform = sourceEffect.m_childTransform
            };
        }

        return new EffectList { m_effectPrefabs = clonedEffects };
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
    private static void Postfix(Pickable __instance, Humanoid character)
    {
        FarmingSkillSystem.TryPickupNearbyForagingTargets(__instance, character);
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class ZNetSceneAwakeFarmingBonusEffectPatch
{
    private static void Postfix(ZNetScene __instance)
    {
        FarmingSkillSystem.ApplyForagingBonusEffectFallbacks(__instance);
    }
}

[HarmonyPatch(typeof(Pickable), "RPC_Pick")]
internal static class PickableRpcPickForagingSkillPatch
{
    private static void Prefix(Pickable __instance, long sender, int bonus)
    {
        FarmingSkillSystem.RememberForagingPickerSkill(__instance, sender);
        FarmingSkillSystem.TryReplayRangePickBonusEffect(__instance, bonus);
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

[HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
internal static class PlayerPlacePieceFarmingTrackingPatch
{
    private static void Prefix(Player __instance, Piece piece)
    {
        FarmingSkillSystem.BeginPlacePiece(__instance);
        BeehivePollinationSystem.BeginPlacePiece(__instance, piece);
    }

    private static void Finalizer()
    {
        FarmingSkillSystem.EndPlacePiece();
        BeehivePollinationSystem.EndPlacePiece();
    }
}

[HarmonyPatch(typeof(Plant), nameof(Plant.Awake))]
internal static class PlantAwakePlanterSkillPatch
{
    private static void Postfix(Plant __instance)
    {
        BeehivePollinationSystem.TrackLoadedTarget(__instance);
        FarmingSkillSystem.TryStorePlanterSkill(__instance);
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
