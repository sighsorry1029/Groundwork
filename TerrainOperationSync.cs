using System;
using HarmonyLib;

namespace Groundwork;

// 1.0.7 sends only the TerrainOp prefab hash. Preserve the six per-operation values
// Groundwork adjusts; keep every other setting (including new paint curves) on the prefab.
internal static class TerrainOperationSync
{
    private const int Marker = 0x47575431; // GWT1: versioned extension, not a saved-world format.
    private const int PayloadBytes = 4 + 5 * 4 + 1;
    private static readonly Func<object, object> Clone = AccessTools.MethodDelegate<Func<object, object>>(
        AccessTools.DeclaredMethod(typeof(object), "MemberwiseClone", Type.EmptyTypes));

    internal static void Write(TerrainOp.Settings settings, ZPackage package)
    {
        package.Write(Marker);
        package.Write(settings.m_levelRadius);
        package.Write(settings.m_raiseRadius);
        package.Write(settings.m_raiseDelta);
        package.Write(settings.m_smoothRadius);
        package.Write(settings.m_paintRadius);
        package.Write(settings.m_smooth);
    }

    internal static TerrainOp.Settings? Read(TerrainOp.Settings? prefabSettings, ZPackage package)
    {
        int position = package.GetPos();
        if (prefabSettings == null || package.Size() - position < 4)
        {
            return prefabSettings;
        }

        if (package.ReadInt() != Marker)
        {
            package.SetPos(position); // Leave extensions owned by another mod untouched.
            return prefabSettings;
        }

        // A recognized but incomplete/invalid operation must not fall back to a different size.
        if (package.Size() - position < PayloadBytes)
        {
            return null;
        }

        float level = package.ReadSingle();
        float raise = package.ReadSingle();
        float delta = package.ReadSingle();
        float smooth = package.ReadSingle();
        float paint = package.ReadSingle();
        bool smoothing = package.ReadBool();
        if (!ValidRadius(level) || !ValidRadius(raise) || !ValidRadius(smooth) || !ValidRadius(paint) ||
            float.IsNaN(delta) || float.IsInfinity(delta))
        {
            return null;
        }

        if (level == prefabSettings.m_levelRadius && raise == prefabSettings.m_raiseRadius &&
            delta == prefabSettings.m_raiseDelta && smooth == prefabSettings.m_smoothRadius &&
            paint == prefabSettings.m_paintRadius && smoothing == prefabSettings.m_smooth)
        {
            return prefabSettings;
        }

        // Deserialize now returns shared prefab settings. Never mutate them while receiving an RPC.
        var result = (TerrainOp.Settings)Clone(prefabSettings);
        result.m_levelRadius = level;
        result.m_raiseRadius = raise;
        result.m_raiseDelta = delta;
        result.m_smoothRadius = smooth;
        result.m_paintRadius = paint;
        result.m_smooth = smoothing;
        return result;
    }

    private static bool ValidRadius(float value) => value >= 0f && !float.IsInfinity(value);
}

[HarmonyPatch(typeof(TerrainOp.Settings), nameof(TerrainOp.Settings.Serialize))]
internal static class TerrainSettingsSerializeGroundworkPatch
{
    private static void Postfix(TerrainOp.Settings __instance, ZPackage pkg) => TerrainOperationSync.Write(__instance, pkg);
}

[HarmonyPatch(typeof(TerrainOp.Settings), nameof(TerrainOp.Settings.Deserialize))]
internal static class TerrainSettingsDeserializeGroundworkPatch
{
    private static void Postfix(ZPackage pkg, ref TerrainOp.Settings? __result) => __result = TerrainOperationSync.Read(__result, pkg);
}
