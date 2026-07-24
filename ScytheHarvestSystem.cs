using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Groundwork;

internal static class ScytheHarvestSystem
{
    private const int MaxHarvestHitBufferSize = 2048;
    private static Collider[] HarvestHits = new Collider[256];
    private static readonly HashSet<Pickable> SeenPickables = new();
    private static readonly HashSet<Destructible> SeenDestructibles = new();
    private static readonly HashSet<string> CultivatedPickablePrefabNames = new(StringComparer.Ordinal);
    private static int _harvestMask;
    private static ZNetScene? _cultivatedPickableScene;
    private static int _cultivatedPickableScenePrefabCount = -1;
    private static bool _reportedHarvestSearchSaturation;

    internal static bool ShouldOverrideVanillaHarvest(Attack attack)
    {
        return attack is
               {
                   m_harvest: true,
                   m_character: not null,
                   m_weapon.m_shared: not null
               } &&
               (Object)(object)attack.m_character == (Object)(object)Player.m_localPlayer &&
               attack.m_weapon.m_shared.m_skillType == Skills.SkillType.Farming &&
               attack.m_harvestRadiusMaxLevel > 0f;
    }

    internal static void HarvestCrops(Attack attack)
    {
        if (!ShouldOverrideVanillaHarvest(attack))
        {
            return;
        }

        Player? player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        Vector3 center = ResolveHarvestCenter(attack);
        float radius = Mathf.Lerp(
            attack.m_harvestRadius,
            attack.m_harvestRadiusMaxLevel,
            player.GetSkillFactor(Skills.SkillType.Farming));

        int hitCount = QueryHarvestHits(center, radius);

        SeenPickables.Clear();
        SeenDestructibles.Clear();
        try
        {
            using (FarmingSkillSystem.SuppressRangePickup())
            {
                for (int i = 0; i < hitCount; i++)
                {
                    Collider hit = HarvestHits[i];
                    if (hit == null)
                    {
                        continue;
                    }

                    Pickable? pickable = hit.GetComponentInParent<Pickable>();
                    if (pickable != null)
                    {
                        TryHarvestPickable(player, pickable);
                        continue;
                    }

                    Plant? plant = hit.GetComponentInParent<Plant>();
                    Destructible? destructible = plant != null && plant.GetStatus() != Plant.Status.Healthy
                        ? hit.GetComponentInParent<Destructible>()
                        : null;
                    if (destructible != null && SeenDestructibles.Add(destructible))
                    {
                        destructible.Destroy();
                    }
                }
            }
        }
        finally
        {
            SeenPickables.Clear();
            SeenDestructibles.Clear();
        }
    }

    private static void TryHarvestPickable(Player player, Pickable pickable)
    {
        if (!SeenPickables.Add(pickable) ||
            !CanScytheHarvest(pickable) ||
            !pickable.CanBePicked())
        {
            return;
        }

        pickable.Interact(player, repeat: false, alt: false);
    }

    internal static bool CanScytheHarvest(Pickable? pickable)
    {
        return pickable != null &&
               (pickable.m_harvestable ||
               FarmingSkillSystem.IsForagingTarget(pickable) ||
               IsCultivatedPickable(pickable));
    }

    internal static void RefreshCultivatedPickables(ZNetScene scene)
    {
        CultivatedPickablePrefabNames.Clear();
        _cultivatedPickableScene = scene;
        _cultivatedPickableScenePrefabCount = scene.m_namedPrefabs.Count;

        foreach (GameObject prefab in scene.m_namedPrefabs.Values)
        {
            if (prefab == null)
            {
                continue;
            }

            foreach (Plant plant in prefab.GetComponentsInChildren<Plant>(includeInactive: true))
            {
                if (plant.m_grownPrefabs == null)
                {
                    continue;
                }

                foreach (GameObject grownPrefab in plant.m_grownPrefabs)
                {
                    if (grownPrefab != null && grownPrefab.GetComponentInChildren<Pickable>(includeInactive: true) != null)
                    {
                        CultivatedPickablePrefabNames.Add(Utils.GetPrefabName(grownPrefab));
                    }
                }
            }
        }
    }

    internal static void Shutdown()
    {
        SeenPickables.Clear();
        SeenDestructibles.Clear();
        CultivatedPickablePrefabNames.Clear();
        Array.Clear(HarvestHits, 0, HarvestHits.Length);
        HarvestHits = new Collider[256];
        _cultivatedPickableScene = null;
        _cultivatedPickableScenePrefabCount = -1;
        _harvestMask = 0;
        _reportedHarvestSearchSaturation = false;
    }

    private static bool IsCultivatedPickable(Pickable pickable)
    {
        ZNetScene? scene = ZNetScene.instance;
        if (scene == null)
        {
            return false;
        }

        if (_cultivatedPickableScene != scene ||
            _cultivatedPickableScenePrefabCount != scene.m_namedPrefabs.Count)
        {
            RefreshCultivatedPickables(scene);
        }

        ZNetView? nview = pickable.GetComponentInParent<ZNetView>();
        GameObject prefabRoot = nview != null ? nview.gameObject : pickable.gameObject;
        return CultivatedPickablePrefabNames.Contains(Utils.GetPrefabName(prefabRoot));
    }

    private static Vector3 ResolveHarvestCenter(Attack attack)
    {
        Character character = attack.m_character;
        Transform origin = ResolveAttackOrigin(attack, character);
        Vector3 attackDir = ResolveMeleeAttackDirection(attack, character, origin);
        return origin.position +
               Vector3.up * attack.m_attackHeight +
               character.transform.right * attack.m_attackOffset +
               attackDir * attack.m_attackRange;
    }

    private static Transform ResolveAttackOrigin(Attack attack, Character character)
    {
        if (!string.IsNullOrEmpty(attack.m_attackOriginJoint))
        {
            Transform? visual = character.GetVisual()?.transform;
            if (visual != null)
            {
                Transform? joint = Utils.FindChild(visual, attack.m_attackOriginJoint);
                if (joint != null)
                {
                    return joint;
                }
            }
        }

        return character.transform;
    }

    private static Vector3 ResolveMeleeAttackDirection(Attack attack, Character character, Transform origin)
    {
        Vector3 forward = character.transform.forward;
        Vector3 aimDir = character is Humanoid humanoid
            ? humanoid.GetAimDir(origin.position)
            : forward;
        aimDir.x = forward.x;
        aimDir.z = forward.z;
        if (aimDir.sqrMagnitude < 0.001f)
        {
            return forward;
        }

        aimDir.Normalize();
        return Vector3.RotateTowards(
            forward,
            aimDir,
            Mathf.Deg2Rad * attack.m_maxYAngle,
            10f);
    }

    private static int GetHarvestMask()
    {
        if (_harvestMask == 0)
        {
            _harvestMask = LayerMask.GetMask("piece", "piece_nonsolid", "item", "Default", "Default_small");
        }

        return _harvestMask;
    }

    private static int QueryHarvestHits(Vector3 center, float radius)
    {
        while (true)
        {
            int hitCount = Physics.OverlapSphereNonAlloc(
                center,
                radius,
                HarvestHits,
                GetHarvestMask(),
                QueryTriggerInteraction.UseGlobal);
            if (hitCount < HarvestHits.Length)
            {
                return hitCount;
            }

            if (HarvestHits.Length >= MaxHarvestHitBufferSize)
            {
                if (!_reportedHarvestSearchSaturation)
                {
                    _reportedHarvestSearchSaturation = true;
                    GroundworkPlugin.ModLogger.LogWarning(
                        $"Scythe harvest search reached the {HarvestHits.Length}-collider maximum buffer capacity. " +
                        "Results may be truncated in this unusually dense area.");
                }

                return hitCount;
            }

            Array.Resize(ref HarvestHits, Math.Min(HarvestHits.Length * 2, MaxHarvestHitBufferSize));
        }
    }
}

[HarmonyPatch(typeof(Attack), nameof(Attack.DoMeleeAttack))]
internal static class AttackDoMeleeAttackScytheHarvestPatch
{
    private static void Prefix(Attack __instance, out bool __state)
    {
        __state = ScytheHarvestSystem.ShouldOverrideVanillaHarvest(__instance);
        if (__state)
        {
            __instance.m_harvest = false;
        }
    }

    private static void Postfix(Attack __instance, bool __state)
    {
        if (!__state)
        {
            return;
        }

        __instance.m_harvest = true;
        ScytheHarvestSystem.HarvestCrops(__instance);
    }

    private static Exception? Finalizer(Attack __instance, bool __state, Exception? __exception)
    {
        if (__state)
        {
            __instance.m_harvest = true;
        }

        return __exception;
    }
}
