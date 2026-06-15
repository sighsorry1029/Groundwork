using HarmonyLib;

namespace Groundwork;

internal static class GroundworkHarmonyDispatch
{
    internal static void PlayerUpdatePostfix(Player player)
    {
        if (player == Player.m_localPlayer)
        {
            MassPlantingSystem.RefreshBuildHintUi();
            MassPlantingSystem.UpdateInput(player);
            TerrainToolRangeSystem.UpdateInput(player);
            PickaxeTerrainScalingSystem.RefreshKeyHintUi();
            TerrainDigFloatingTextSystem.Update();
        }

        PickaxeTerrainScalingSystem.UpdateInput(player);
    }

    internal static void PlayerUpdatePlacementGhostPostfix(Player player)
    {
        MassPlantingSystem.TrySnapPlacementGhost(player);
        MassPlantingSystem.UpdatePlacementPreview(player);
        TerrainToolRangeSystem.ApplyCurrentRangeToGhost(player);
    }

    internal static bool PlayerTryPlacePiecePrefix(Player player, Piece piece, ref bool result)
    {
        TerrainToolRangeSystem.BeginTryPlacePiece(player, piece);
        return MassPlantingSystem.TryInterceptPlace(player, piece, ref result);
    }

    internal static void PlayerTryPlacePiecePostfix(Player player, Piece piece, bool result)
    {
        TerrainToolRangeSystem.EndTryPlacePiece(player, piece, result);
    }
}

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
            GroundworkHarmonyDispatch.PlayerUpdatePostfix(__instance);
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
        GroundworkHarmonyDispatch.PlayerUpdatePlacementGhostPostfix(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
internal static class PlayerTryPlacePieceGroundworkPatch
{
    private static bool Prefix(Player __instance, Piece piece, ref bool __result)
    {
        return GroundworkHarmonyDispatch.PlayerTryPlacePiecePrefix(__instance, piece, ref __result);
    }

    private static void Postfix(Player __instance, Piece piece, bool __result)
    {
        GroundworkHarmonyDispatch.PlayerTryPlacePiecePostfix(__instance, piece, __result);
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
