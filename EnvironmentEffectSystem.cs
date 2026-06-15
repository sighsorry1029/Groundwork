using UnityEngine;

namespace Groundwork;

internal static class EnvironmentEffectSystem
{
    internal static bool IsWetEnvironment()
    {
        return EnvMan.instance != null && EnvMan.IsWet();
    }

    internal static float GetBeehiveRainHoneyRate(bool unloadedCatchup)
    {
        return !unloadedCatchup && IsWetEnvironment()
            ? GroundworkToolsDomain.BeehiveRainHoneyRate
            : 1f;
    }

    internal static bool IsLoadedBeehiveProductionPausedByWetEnvironment()
    {
        return IsWetEnvironment() && GroundworkToolsDomain.BeehiveRainHoneyRate <= 0.001f;
    }

    internal static float GetWetPlantGrowSpeedMultiplier()
    {
        return IsWetEnvironment() ? GroundworkToolsDomain.WetEnvironmentPlantGrowSpeedFactor : 1f;
    }

    internal static float GetWetForagingRespawnSpeedMultiplier(Pickable pickable)
    {
        return IsWetEnvironment() && FarmingSkillSystem.IsForagingTarget(pickable)
            ? GroundworkToolsDomain.WetEnvironmentForagingRespawnSpeedFactor
            : 1f;
    }

    internal static bool TryPauseBeehiveProduction(Beehive beehive, bool unloadedCatchup)
    {
        if (unloadedCatchup ||
            !IsLoadedBeehiveProductionPausedByWetEnvironment() ||
            beehive == null ||
            beehive.m_nview == null ||
            !beehive.m_nview.IsValid())
        {
            return false;
        }

        PauseBeehiveProduction(beehive);

        return true;
    }

    internal static void PauseBeehiveProduction(Beehive beehive)
    {
        if (beehive == null ||
            beehive.m_nview == null ||
            !beehive.m_nview.IsValid())
        {
            return;
        }

        if (beehive.m_beeEffect != null && beehive.m_beeEffect.activeSelf)
        {
            beehive.m_beeEffect.SetActive(false);
        }

        if (beehive.m_nview.IsOwner() && ZNet.instance != null)
        {
            ZDO? zdo = beehive.m_nview.GetZDO();
            zdo?.Set(ZDOVars.s_lastTime, ZNet.instance.GetTime().Ticks);
        }
    }

    internal static void TryModifyPlantGrowTime(Plant plant, ref float growTime)
    {
        float speedFactor = GetWetPlantGrowSpeedMultiplier();
        if (plant == null ||
            growTime <= 0f ||
            speedFactor <= 1.001f)
        {
            return;
        }

        growTime /= speedFactor;
    }

    internal static bool TryModifyForagingRespawnSeconds(Pickable pickable, ref float respawnSeconds)
    {
        float speedFactor = GetWetForagingRespawnSpeedMultiplier(pickable);
        if (pickable == null ||
            respawnSeconds <= 0f ||
            speedFactor <= 1.001f)
        {
            return false;
        }

        respawnSeconds /= speedFactor;
        return true;
    }
}
