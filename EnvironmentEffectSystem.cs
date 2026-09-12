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
            GameAccess.BeehiveView(beehive) == null ||
            !GameAccess.BeehiveView(beehive).IsValid())
        {
            return false;
        }

        PauseBeehiveProduction(beehive);

        return true;
    }

    internal static void PauseBeehiveProduction(Beehive beehive)
    {
        if (beehive == null ||
            GameAccess.BeehiveView(beehive) == null ||
            !GameAccess.BeehiveView(beehive).IsValid())
        {
            return;
        }

        if (beehive.m_beeEffect != null && beehive.m_beeEffect.activeSelf)
        {
            beehive.m_beeEffect.SetActive(false);
        }

        if (GameAccess.BeehiveView(beehive).IsOwner() && ZNet.instance != null)
        {
            ZDO? zdo = GameAccess.BeehiveView(beehive).GetZDO();
            zdo?.Set(ZDOVars.s_lastTime, ZNet.instance.GetTime().Ticks);
        }
    }

}
