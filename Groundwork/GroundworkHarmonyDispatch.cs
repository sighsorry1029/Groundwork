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
    private static void Postfix(Player __instance)
    {
        MassPlantingSystem.TrySnapPlacementGhost(__instance);
        MassPlantingSystem.UpdatePlacementPreview(__instance);
        TerrainToolRangeSystem.ApplyCurrentRangeToGhost(__instance);
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
        ScytheHarvestSystem.RefreshCultivatedPickables(__instance);
        FarmingSkillSystem.ApplyForagingBonusEffectFallbacks(__instance);
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
