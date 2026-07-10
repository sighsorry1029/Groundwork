using HarmonyLib;

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
