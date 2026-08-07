using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;

namespace Groundwork;

internal static class PickableRespawnHoverSystem
{
    internal static void AppendHoverText(Pickable pickable, ref string hoverText)
    {
        if (pickable == null)
        {
            return;
        }

        if (pickable.CanBePicked() ||
            !FarmingSkillSystem.TryGetPickableRespawnTiming(
                pickable,
                out FarmingSkillSystem.PickableRespawnTiming timing) ||
            !timing.HasPickedTime ||
            timing.RemainingSeconds <= 0.01f)
        {
            return;
        }

        float farmingMultiplier = FarmingSkillSystem.GetForagingRespawnSpeedMultiplier(pickable);
        float pollinationMultiplier = BeehivePollinationSystem.GetForagingRespawnMultiplierForHover(pickable);
        float rainMultiplier = EnvironmentEffectSystem.GetWetForagingRespawnSpeedMultiplier(pickable);
        AppendEffectHoverLines(
            ref hoverText,
            timing.RemainingSeconds,
            farmingMultiplier,
            pollinationMultiplier,
            rainMultiplier);
    }

    internal static void AppendPlantHoverText(Plant plant, ref string hoverText)
    {
        if (plant == null ||
            plant.GetStatus() != Plant.Status.Healthy)
        {
            return;
        }

        if (!FarmingSkillSystem.TryGetPlantRemainingGrowthSeconds(
                plant,
                out float remainingSeconds) ||
            remainingSeconds <= 0.01f)
        {
            return;
        }

        float farmingMultiplier = FarmingSkillSystem.GetPlantGrowSpeedMultiplier(plant);
        float pollinationMultiplier = BeehivePollinationSystem.GetPlantGrowthMultiplierForHover(plant);
        float rainMultiplier = EnvironmentEffectSystem.GetWetPlantGrowSpeedMultiplier();
        AppendEffectHoverLines(
            ref hoverText,
            remainingSeconds,
            farmingMultiplier,
            pollinationMultiplier,
            rainMultiplier);
    }

    private static void AppendEffectHoverLines(
        ref string hoverText,
        float remainingSeconds,
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

        AppendLine(ref hoverText, Colorize(GroundworkLocalization.FormatDuration(remainingSeconds)));
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
