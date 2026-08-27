using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Groundwork;

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
internal static class ObjectDbAwakeGroundworkPatch
{
    private static void Postfix(ObjectDB __instance)
    {
        GroundworkPlugin.ApplyToObjectDb(__instance);
    }
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
internal static class ObjectDbCopyOtherDbGroundworkPatch
{
    private static void Postfix(ObjectDB __instance)
    {
        GroundworkPlugin.ApplyToObjectDb(__instance);
    }
}

[HarmonyPatch(typeof(Player), "Update")]
internal static class PlayerUpdateGroundworkPatch
{
    private static void Prefix(Player __instance)
    {
        MassPlantingSystem.BeginPlayerUpdateInput(__instance);
    }

    private static void Postfix(Player __instance)
    {
        MassPlantingSystem.EndPlayerUpdateInputSuppression();
        try
        {
            GroundworkPlugin.TryApplyPendingConfig();
            if (__instance == Player.m_localPlayer)
            {
                MassPlantingSystem.UpdateInput(__instance);
                TerrainToolRangeSystem.UpdateInput(__instance);
                TerrainDigFloatingTextSystem.Update();
                BeehivePollinationSystem.UpdateHoverPreview(__instance);
            }

            PickaxeTerrainScalingSystem.UpdateInput(__instance);
        }
        finally
        {
            MassPlantingSystem.ClearPlayerUpdateInput();
        }
    }

    private static void Finalizer()
    {
        MassPlantingSystem.ClearPlayerUpdateInput();
    }
}

[HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
internal static class PlayerUpdatePlacementGhostGroundworkPatch
{
    private static void Prefix(
        Player __instance,
        out GrowthOverrideSystem.PieceBiomeOverrideState __state)
    {
        __state = GrowthOverrideSystem.BeginPlacementBiomeOverride(__instance);
    }

    private static void Postfix(Player __instance)
    {
        MassPlantingSystem.TrySnapPlacementGhost(__instance);
        MassPlantingSystem.UpdatePlacementPreview(__instance);
        TerrainToolRangeSystem.ApplyCurrentRangeToGhost(__instance);
    }

    private static Exception? Finalizer(
        GrowthOverrideSystem.PieceBiomeOverrideState __state,
        Exception? __exception)
    {
        GrowthOverrideSystem.EndPlacementBiomeOverride(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
internal static class PlayerTryPlacePieceGroundworkPatch
{
    private static bool Prefix(Player __instance, Piece piece, ref bool __result)
    {
        TerrainToolRangeSystem.BeginTryPlacePiece(__instance, piece);
        return MassPlantingSystem.TryInterceptPlace(__instance, piece, ref __result);
    }

    private static System.Exception? Finalizer(
        Player __instance,
        Piece piece,
        bool __result,
        System.Exception? __exception)
    {
        TerrainToolRangeSystem.EndTryPlacePiece(__instance, piece, __exception == null && __result);
        return __exception;
    }
}

[HarmonyPatch(typeof(Player), "Start")]
internal static class PlayerStartGroundworkPatch
{
    private static void Postfix(Player __instance)
    {
        ScytheToolCompatSystem.NotifyPendingJewelcraftingEffectRecalcIfNeeded(__instance);
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class ZNetSceneAwakeGroundworkPatch
{
    private static void Postfix(ZNetScene __instance)
    {
        GrowthOverrideSystem.OnZNetSceneReady(__instance);
        ScytheHarvestSystem.RefreshCultivatedPickables(__instance);
        FarmingSkillSystem.RefreshForagingBonusEffectFallback(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
internal static class PlayerPlacePieceGroundworkPatch
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
internal static class PlantAwakeGroundworkPatch
{
    private static void Postfix(Plant __instance)
    {
        MassPlantingSystem.TrySynchronizePendingPlant(__instance);
        BeehivePollinationSystem.TrackLoadedTarget(__instance);
        FarmingSkillSystem.TryStorePlanterSkill(__instance);
    }
}

[HarmonyPatch(typeof(Plant), "UpdateHealth")]
internal static class PlantUpdateHealthGroundworkPatch
{
    private static void Prefix(
        Plant __instance,
        out GrowthOverrideSystem.PlantBiomeOverrideState __state)
    {
        __state = GrowthOverrideSystem.BeginPlantHealthBiomeOverride(__instance);
    }

    private static Exception? Finalizer(
        GrowthOverrideSystem.PlantBiomeOverrideState __state,
        Exception? __exception)
    {
        GrowthOverrideSystem.EndPlantHealthBiomeOverride(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(Hud), "SetupPieceInfo")]
internal static class HudSetupPieceInfoGroundworkPatch
{
    private static void Prefix(Piece? piece, out PieceDescriptionState? __state)
    {
        __state = piece != null ? new PieceDescriptionState(piece, piece.m_description) : null;
        TerrainToolRangeSystem.AppendPieceDescription(piece);
        MassPlantingSystem.AppendPieceDescription(piece);
    }

    private static void Finalizer(PieceDescriptionState? __state)
    {
        if (__state?.Piece != null)
        {
            __state.Piece.m_description = __state.Description;
        }
    }

    private sealed class PieceDescriptionState(Piece piece, string description)
    {
        internal Piece Piece { get; } = piece;
        internal string Description { get; } = description;
    }
}

internal static class FarmingSkillTooltipText
{
    internal const string HeadingToken = "$groundwork_skill_farming_heading";
    internal const string MassPlantingToken = "$groundwork_skill_farming_mass_planting";
    internal const string PlantGrowthToken = "$groundwork_skill_farming_plant_growth";
    internal const string ForagingRangeToken = "$groundwork_skill_farming_foraging_range";
    internal const string ForagingRespawnToken = "$groundwork_skill_farming_foraging_respawn";
    internal const string ForagingBothToken = "$groundwork_skill_farming_foraging_both";
    internal const string BonusYieldToken = "$groundwork_skill_farming_bonus_yield";
    internal const string BeehiveCapacityToken = "$groundwork_skill_farming_beehive_capacity";

    internal static string Append(
        string? original,
        bool massPlantingEnabled,
        bool plantGrowthEnabled,
        bool foragingRangeEnabled,
        bool foragingRespawnEnabled,
        bool beehiveCapacityEnabled)
    {
        original ??= string.Empty;
        if (original.IndexOf(HeadingToken, StringComparison.Ordinal) >= 0)
        {
            return original;
        }

        List<string> lines = [HeadingToken];
        if (massPlantingEnabled)
        {
            lines.Add(MassPlantingToken);
        }

        if (plantGrowthEnabled)
        {
            lines.Add(PlantGrowthToken);
        }

        if (foragingRangeEnabled && foragingRespawnEnabled)
        {
            lines.Add(ForagingBothToken);
        }
        else if (foragingRangeEnabled)
        {
            lines.Add(ForagingRangeToken);
        }
        else if (foragingRespawnEnabled)
        {
            lines.Add(ForagingRespawnToken);
        }

        lines.Add(BonusYieldToken);
        if (beehiveCapacityEnabled)
        {
            lines.Add(BeehiveCapacityToken);
        }

        string section = string.Join("\n", lines);
        return original.Length > 0
            ? original + "\n\n" + section
            : section;
    }

    internal static bool MatchesSkillDescription(
        string? tooltipText,
        string? skillDescription)
    {
        return !string.IsNullOrWhiteSpace(tooltipText) &&
               !string.IsNullOrWhiteSpace(skillDescription) &&
               tooltipText!.IndexOf(skillDescription!, StringComparison.Ordinal) >= 0;
    }
}

[HarmonyPatch(typeof(SkillsDialog), nameof(SkillsDialog.Setup))]
internal static class FarmingSkillTooltipPatch
{
    private static bool _failureLogged;

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter("randyknapp.mods.epicloot")]
    private static void Postfix(SkillsDialog __instance, Player player)
    {
        if (__instance == null || player == null)
        {
            return;
        }

        try
        {
            List<Skills.Skill>? skills = player.GetSkills()?.GetSkillList();
            if (skills == null)
            {
                return;
            }

            Skills.Skill? farmingSkill = null;
            int farmingIndex = -1;
            for (int index = 0; index < skills.Count; index++)
            {
                Skills.Skill skill = skills[index];
                if (skill?.m_info?.m_skill == Skills.SkillType.Farming)
                {
                    farmingSkill = skill;
                    farmingIndex = index;
                    break;
                }
            }

            if (farmingSkill?.m_info == null)
            {
                return;
            }

            UITooltip? tooltip = FindFarmingTooltip(
                __instance,
                farmingIndex,
                farmingSkill.m_info.m_description);
            if (tooltip == null)
            {
                return;
            }

            string text = FarmingSkillTooltipText.Append(
                tooltip.m_text,
                GroundworkToolsDomain.MassPlantingEnabled,
                GroundworkToolsDomain.PlantGrowSpeedFactor > 1.001f,
                GroundworkToolsDomain.ForagingPickupMaxRange > 0.001f,
                GroundworkToolsDomain.ForagingRespawnSpeedFactor > 1.001f,
                GroundworkToolsDomain.BeehiveCapacityFarmingLevelsPerBonusHoney > 0);
            if (!string.Equals(text, tooltip.m_text, StringComparison.Ordinal))
            {
                tooltip.Set(
                    tooltip.m_topic,
                    text,
                    tooltip.m_anchor,
                    tooltip.m_fixedPosition);
            }
        }
        catch (Exception exception)
        {
            if (_failureLogged)
            {
                return;
            }

            _failureLogged = true;
            GroundworkPlugin.ModLogger.LogWarning(
                "Could not extend the Farming skill tooltip: " +
                exception.GetBaseException().Message);
        }
    }

    private static UITooltip? FindFarmingTooltip(
        SkillsDialog dialog,
        int farmingIndex,
        string farmingDescription)
    {
        if (dialog.m_elements != null &&
            farmingIndex >= 0 &&
            farmingIndex < dialog.m_elements.Count)
        {
            UITooltip? indexedTooltip = dialog.m_elements[farmingIndex]?
                .GetComponentInChildren<UITooltip>();
            if (indexedTooltip != null &&
                FarmingSkillTooltipText.MatchesSkillDescription(
                    indexedTooltip.m_text,
                    farmingDescription))
            {
                return indexedTooltip;
            }
        }

        InventoryGui? inventory = dialog.GetComponentInParent<InventoryGui>();
        if (inventory == null)
        {
            return null;
        }

        UITooltip[] candidates = inventory.GetComponentsInChildren<UITooltip>(true);
        foreach (UITooltip candidate in candidates)
        {
            if (candidate != null &&
                candidate.gameObject.activeInHierarchy &&
                FarmingSkillTooltipText.MatchesSkillDescription(
                    candidate.m_text,
                    farmingDescription))
            {
                return candidate;
            }
        }

        foreach (UITooltip candidate in candidates)
        {
            if (candidate != null &&
                FarmingSkillTooltipText.MatchesSkillDescription(
                    candidate.m_text,
                    farmingDescription))
            {
                return candidate;
            }
        }

        return null;
    }
}

[HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
internal static class GameCameraUpdateCameraGroundworkPatch
{
    private static void Prefix()
    {
        CameraZoomInputSuppressionSystem.BeginGameCameraUpdate();
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var inputGetAxis = AccessTools.Method(typeof(Input), nameof(Input.GetAxis), [typeof(string)]);
        var inputGetAxisForCamera = AccessTools.Method(
            typeof(CameraZoomInputSuppressionSystem),
            nameof(CameraZoomInputSuppressionSystem.GetAxisForCamera));

        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(inputGetAxis))
            {
                instruction.operand = inputGetAxisForCamera;
                yield return instruction;
                continue;
            }

            yield return instruction;
        }
    }

    private static void Finalizer()
    {
        CameraZoomInputSuppressionSystem.EndGameCameraUpdate();
        TerrainToolRangeSystem.ClearCameraZoomSuppression();
        PickaxeTerrainScalingSystem.ClearCameraZoomSuppression();
    }
}

internal static class CameraZoomInputSuppressionSystem
{
    private static bool InsideGameCameraUpdate { get; set; }

    internal static void Shutdown()
    {
        InsideGameCameraUpdate = false;
    }

    internal static void BeginGameCameraUpdate()
    {
        InsideGameCameraUpdate = true;
    }

    internal static void EndGameCameraUpdate()
    {
        InsideGameCameraUpdate = false;
    }

    internal static float GetAxisForCamera(string axisName)
    {
        float value = Input.GetAxis(axisName);
        return IsMouseScrollAxis(axisName) && ShouldSuppressCameraZoomInput() ? 0f : value;
    }

    internal static bool ShouldBlockZInputMouseScrollWheel()
    {
        return InsideGameCameraUpdate && ShouldSuppressCameraZoomInput();
    }

    private static bool ShouldSuppressCameraZoomInput()
    {
        return TerrainToolRangeSystem.ShouldSuppressCameraZoomInput() ||
               MassPlantingSystem.ShouldSuppressCameraZoomInput() ||
               PickaxeTerrainScalingSystem.ShouldSuppressCameraZoomInput();
    }

    private static bool IsMouseScrollAxis(string axisName)
    {
        return axisName.Equals("Mouse ScrollWheel", StringComparison.OrdinalIgnoreCase) ||
               axisName.Equals("Mouse Scroll Wheel", StringComparison.OrdinalIgnoreCase);
    }
}

[HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel))]
internal static class ZInputGetMouseScrollWheelGroundworkPatch
{
    private static bool Prefix(ref float __result)
    {
        if (MassPlantingSystem.ShouldBlockPlayerUpdateMouseScrollWheel())
        {
            __result = 0f;
            return false;
        }

        if (!CameraZoomInputSuppressionSystem.ShouldBlockZInputMouseScrollWheel())
        {
            return true;
        }

        __result = 0f;
        return false;
    }
}

[HarmonyPatch(typeof(KeyHints), "Awake")]
internal static class KeyHintsAwakeGroundworkPatch
{
    private static void Postfix(KeyHints __instance)
    {
        try
        {
            MassPlantingSystem.InitializeBuildHints(__instance);
        }
        finally
        {
            PickaxeTerrainScalingSystem.InitializeKeyHints(__instance);
        }
    }
}

[HarmonyPatch(typeof(KeyHints), "UpdateHints")]
internal static class KeyHintsUpdateGroundworkPatch
{
    private static void Postfix(KeyHints __instance)
    {
        try
        {
            MassPlantingSystem.UpdateBuildHint(__instance);
        }
        finally
        {
            PickaxeTerrainScalingSystem.UpdateKeyHint(__instance);
        }
    }
}
