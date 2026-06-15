using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;

namespace Groundwork;

internal static class PickableRespawnHoverSystem
{
    internal static void AppendHoverText(Pickable pickable, ref string hoverText)
    {
        if (pickable == null ||
            !GroundworkToolsDomain.Enabled)
        {
            return;
        }

        if (!ResolveRemainingSeconds(pickable, out float remainingSeconds) ||
            remainingSeconds <= 0.01f)
        {
            return;
        }

        float farmingMultiplier = FarmingSkillSystem.GetForagingRespawnSpeedMultiplierForHover(pickable);
        float pollinationMultiplier = BeehivePollinationSystem.GetForagingRespawnMultiplierForHover(pickable);
        float rainMultiplier = EnvironmentEffectSystem.GetWetForagingRespawnSpeedMultiplier(pickable);
        AppendEffectHoverLines(
            ref hoverText,
            remainingSeconds,
            "groundwork_respawn_total",
            "{0} (respawn {1})",
            farmingMultiplier,
            pollinationMultiplier,
            rainMultiplier);
    }

    internal static void AppendPlantHoverText(Plant plant, ref string hoverText)
    {
        if (plant == null ||
            !GroundworkToolsDomain.Enabled ||
            plant.GetStatus() != Plant.Status.Healthy)
        {
            return;
        }

        float remainingSeconds = Math.Max(0f, plant.GetGrowTime() - (float)plant.TimeSincePlanted());
        if (remainingSeconds <= 0.01f)
        {
            return;
        }

        float farmingMultiplier = FarmingSkillSystem.GetPlantGrowSpeedMultiplierForHover(plant);
        float pollinationMultiplier = BeehivePollinationSystem.GetPlantGrowthMultiplierForHover(plant);
        float rainMultiplier = EnvironmentEffectSystem.GetWetPlantGrowSpeedMultiplier();
        AppendEffectHoverLines(
            ref hoverText,
            remainingSeconds,
            "groundwork_growth_total",
            "{0} (growth {1})",
            farmingMultiplier,
            pollinationMultiplier,
            rainMultiplier);
    }

    private static bool ResolveRemainingSeconds(Pickable pickable, out float remainingSeconds)
    {
        remainingSeconds = 0f;
        if (pickable.CanBePicked())
        {
            return true;
        }

        float respawnSeconds = Math.Max(0f, pickable.m_respawnTimeMinutes) * 60f;
        if (respawnSeconds <= 0f)
        {
            return false;
        }

        if (FarmingSkillSystem.TryGetForagingRespawnSeconds(pickable, out float foragingRespawnSeconds))
        {
            respawnSeconds = foragingRespawnSeconds;
        }

        if (!TryGetPickableZdo(pickable, out ZDO? zdo))
        {
            remainingSeconds = respawnSeconds;
            return true;
        }

        long pickedTime = zdo!.GetLong(ZDOVars.s_pickedTime, 0L);
        if (pickedTime <= 1L)
        {
            remainingSeconds = respawnSeconds;
            return true;
        }

        double elapsedSeconds = TimeSpan.FromTicks(Math.Max(0L, GetCurrentTicks() - pickedTime)).TotalSeconds;
        remainingSeconds = Math.Max(0f, respawnSeconds - (float)elapsedSeconds);
        return true;
    }

    private static long GetCurrentTicks()
    {
        return ZNet.instance != null
            ? ZNet.instance.GetTime().Ticks
            : DateTime.Now.Ticks;
    }

    private static bool TryGetPickableZdo(Pickable pickable, out ZDO? zdo)
    {
        zdo = null;
        ZNetView? nview = pickable.m_nview;
        if (nview == null || !nview.IsValid())
        {
            return false;
        }

        zdo = nview.GetZDO();
        return zdo != null;
    }

    private static void AppendEffectHoverLines(
        ref string hoverText,
        float remainingSeconds,
        string totalToken,
        string totalFallback,
        float farmingMultiplier,
        float pollinationMultiplier,
        float rainMultiplier)
    {
        List<string> parts = [];
        AddMultiplierPart(parts, farmingMultiplier, "groundwork_factor_farming", "farming {0}");
        AddMultiplierPart(parts, pollinationMultiplier, "groundwork_factor_pollination", "pollination {0}");
        AddMultiplierPart(parts, rainMultiplier, "groundwork_factor_rain", "rain {0}");

        if (parts.Count > 0)
        {
            AppendLine(ref hoverText, Colorize(string.Join(" ", parts)));
        }

        float totalMultiplier = farmingMultiplier * pollinationMultiplier * rainMultiplier;
        string duration = GroundworkLocalization.FormatDuration(remainingSeconds);
        string durationLine = totalMultiplier > 1.001f
            ? GroundworkLocalization.Format(totalToken, totalFallback, duration, FormatMultiplier(totalMultiplier))
            : duration;
        AppendLine(ref hoverText, Colorize(durationLine));
    }

    private static void AddMultiplierPart(List<string> parts, float multiplier, string token, string fallback)
    {
        if (multiplier <= 1.001f)
        {
            return;
        }

        parts.Add(GroundworkLocalization.Format(token, fallback, FormatMultiplier(multiplier)));
    }

    private static void AppendLine(ref string text, string line)
    {
        text = string.IsNullOrEmpty(text) ? line : text + "\n" + line;
    }

    private static string Colorize(string text)
    {
        return "<color=#a8e6a1>" + text + "</color>";
    }

    private static string FormatMultiplier(float multiplier)
    {
        return "x" + multiplier.ToString("0.##", CultureInfo.InvariantCulture);
    }
}

[HarmonyPatch(typeof(Pickable), nameof(Pickable.GetHoverText))]
internal static class PickableGetHoverTextRespawnHoverPatch
{
    private static void Postfix(Pickable __instance, ref string __result)
    {
        PickableRespawnHoverSystem.AppendHoverText(__instance, ref __result);
    }
}

[HarmonyPatch(typeof(Plant), nameof(Plant.GetHoverText))]
internal static class PlantGetHoverTextGrowthHoverPatch
{
    private static void Postfix(Plant __instance, ref string __result)
    {
        PickableRespawnHoverSystem.AppendPlantHoverText(__instance, ref __result);
    }
}
