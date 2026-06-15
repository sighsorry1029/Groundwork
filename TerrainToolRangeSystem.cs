using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Groundwork;

internal static class TerrainToolRangeSystem
{
    private const int CustomPreviewSegmentCount = 96;
    private const float CustomPreviewYOffset = 0.06f;
    private const float CustomPreviewLineWidth = 0.045f;
    private const float CustomGridPreviewMarkerSize = 0.16f;
    private const int CustomGridPreviewMaxMarkers = 2048;
    private const float CustomGridPreviewRadiusEpsilon = 0.001f;
    private static readonly int PreviewColorProperty = Shader.PropertyToID("_Color");
    private static readonly int PreviewTintColorProperty = Shader.PropertyToID("_TintColor");
    private static readonly int PreviewBaseColorProperty = Shader.PropertyToID("_BaseColor");
    private static readonly int PreviewEmissionColorProperty = Shader.PropertyToID("_EmissionColor");
    private static readonly int[] PreviewColorProperties =
    [
        PreviewTintColorProperty,
        PreviewColorProperty,
        PreviewBaseColorProperty,
        PreviewEmissionColorProperty
    ];
    private static readonly Color FallbackPreviewRingColor = new(1f, 0.95f, 0.78f, 0.86f);
    private static readonly ConditionalWeakTable<ObjectDB, ObjectDbState> ObjectDbStates = new();
    private static readonly Dictionary<Piece, TerrainToolRule> RulesByPiece = new();
    private static readonly Dictionary<string, List<TerrainToolRuleTemplate>> RuleTemplatesByTool = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, float> CurrentRanges = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Player, PendingPlacementCost> PendingPlacementCosts = new();
    private static readonly Dictionary<Player, TerrainToolRule> ActivePlacementRules = new();
    private static readonly HashSet<string> ReportedWarnings = new(StringComparer.OrdinalIgnoreCase);
    private static TerrainToolRule? ActiveRangeRule;
    private static GroundworkPlugin.TerrainToolRangePreviewMode? ActivePreviewMode;
    private static GameObject? CachedTerrainOpsGhost;
    private static TerrainOp[] CachedTerrainOps = Array.Empty<TerrainOp>();
    private static readonly HashSet<string> AimHeightHintPiecePrefabNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mud_road_v2",
        "paved_road_v2"
    };
    private const string AimHeightHint = "Uses your standing height by default.\nShift+Click to use the aimed ground height instead.";
    private static GameObject? ScaledPlacementGhost;
    private static Vector3 ScaledPlacementGhostBaseScale;
    private static readonly List<PreviewTransformScaleState> ScaledPreviewTransforms = [];
    private static readonly List<ParticleSystem> ScaledPreviewParticleSystems = [];
    private static float ScaledPlacementGhostLastVisualScale = -1f;
    private static GameObject? CustomRangePreviewObject;
    private static LineRenderer? CustomRangePreviewLine;
    private static Material? CustomRangePreviewMaterial;
    private static GameObject? CustomGridPreviewObject;
    private static MeshFilter? CustomGridPreviewMeshFilter;
    private static MeshRenderer? CustomGridPreviewRenderer;
    private static Mesh? CustomGridPreviewMesh;
    private static GameObject? CustomRangePreviewGhost;
    private static Color CustomRangePreviewColor = FallbackPreviewRingColor;
    private static int CustomGridPreviewSignature;
    private static bool CustomGridPreviewSignatureValid;
    private static readonly List<Heightmap> CustomRangePreviewHeightmaps = [];
    private static readonly List<Heightmap> CustomGridPreviewHeightmaps = [];
    private static readonly List<Vector3> CustomGridPreviewVertices = [];
    private static readonly List<int> CustomGridPreviewIndices = [];
    private static readonly List<Color> CustomGridPreviewColors = [];
    private static readonly List<PreviewVisualVisibilityState> HiddenPreviewVisuals = [];
    private static GameObject? RangeLabelObject;
    private static TextMeshProUGUI? RangeLabelText;

    internal static bool Enabled =>
        GroundworkToolsDomain.TerrainToolRangeAndCostEnabled;

    internal static void RestoreObjectDb(ObjectDB objectDb)
    {
        if (objectDb == null)
        {
            return;
        }

        ObjectDbState state = ObjectDbStates.GetValue(objectDb, _ => new ObjectDbState());
        RestoreOriginalCosts(state);
        ClearRuntimeState();
    }

    internal static void ApplyToObjectDb(ObjectDB objectDb, IReadOnlyList<NormalizedTerrainToolConfig> configs)
    {
        if (objectDb == null)
        {
            return;
        }

        ObjectDbState state = ObjectDbStates.GetValue(objectDb, _ => new ObjectDbState());
        ClearRuntimeState();

        if (!Enabled || configs.Count == 0)
        {
            return;
        }

        foreach (NormalizedTerrainToolConfig config in configs)
        {
            TerrainToolRuleTemplate template = new(config);
            if (!RuleTemplatesByTool.TryGetValue(template.ToolPrefabName, out List<TerrainToolRuleTemplate>? templates))
            {
                templates = new List<TerrainToolRuleTemplate>();
                RuleTemplatesByTool[template.ToolPrefabName] = templates;
            }

            templates.Add(template);
        }

        foreach (string toolPrefabName in RuleTemplatesByTool.Keys)
        {
            GameObject? toolPrefab = objectDb.GetItemPrefab(toolPrefabName);
            PieceTable? pieceTable = toolPrefab?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces;
            if (pieceTable != null)
            {
                ApplyToPieceTable(objectDb, state, toolPrefabName, pieceTable);
            }
        }
    }

    internal static void ApplyToPlayerBuildPieces(Player player)
    {
        if (!Enabled || player == null || ObjectDB.instance == null)
        {
            return;
        }

        string toolPrefabName = ResolveCurrentToolPrefabName(player);
        if (string.IsNullOrWhiteSpace(toolPrefabName) ||
            !RuleTemplatesByTool.ContainsKey(toolPrefabName) ||
            player.m_buildPieces == null)
        {
            return;
        }

        ObjectDbState state = ObjectDbStates.GetValue(ObjectDB.instance, _ => new ObjectDbState());
        ApplyToPieceTable(ObjectDB.instance, state, toolPrefabName, player.m_buildPieces);
    }

    internal static void UpdateInput(Player player)
    {
        if (player == null || player != Player.m_localPlayer)
        {
            return;
        }

        if (!Enabled || !player.InPlaceMode() || !TryGetSelectedRule(player, out TerrainToolRule rule))
        {
            ClearActiveRangeRule();
            ClearRangePreview();
            return;
        }

        CleanupExpiredPendingCosts();

        if (!rule.RangeEnabled)
        {
            ClearActiveRangeRule();
            ClearRangePreview();
            return;
        }

        SetActiveRangeRule(rule);
        TryTogglePreviewMode();

        float scroll = ZInput.GetMouseScrollWheel();
        if (Mathf.Abs(scroll) < 0.01f || !IsRangeModifierHeld())
        {
            return;
        }

        float currentRange = GetCurrentRange(rule);
        float step = GroundworkToolsDomain.TerrainToolRangeStep;
        float nextRange = Mathf.Clamp(currentRange + Math.Sign(scroll) * step, rule.MinRange, rule.MaxRange);
        nextRange = Mathf.Round(nextRange / step) * step;
        nextRange = Mathf.Clamp(nextRange, rule.MinRange, rule.MaxRange);
        if (Mathf.Abs(nextRange - currentRange) < 0.001f)
        {
            return;
        }

        CurrentRanges[rule.Id] = nextRange;
        ApplyCurrentCostToPiece(rule);
        SuppressCameraZoomThisFrame = true;
    }

    internal static void ApplyCurrentRangeToGhost(Player player)
    {
        if (player == null || player != Player.m_localPlayer)
        {
            return;
        }

        if (!Enabled || player.m_placementGhost == null || !TryGetSelectedRule(player, out TerrainToolRule rule) || !rule.RangeEnabled)
        {
            ClearActiveRangeRule();
            ClearRangePreview();
            return;
        }

        SetActiveRangeRule(rule);

        GameObject ghost = player.m_placementGhost;
        TerrainOp[] terrainOps = GetCachedTerrainOps(ghost);
        float range = GetCurrentRange(rule);
        ApplyRangeToTerrainOps(terrainOps, range, rule);
        if (IsGridRangePreviewEnabled())
        {
            ResetScaledPlacementGhost();
            ApplyCustomRangePreview(ghost, terrainOps, range);
        }
        else
        {
            ClearCustomRangePreview();
            ApplyRangeToGhostVisual(ghost, rule, range);
        }

        UpdateRangeLabel(rule, ghost, terrainOps, range);
    }

    internal static PieceInfoState? PreparePieceInfo(Piece piece)
    {
        if (!Enabled || piece == null || !RulesByPiece.TryGetValue(piece, out TerrainToolRule rule))
        {
            return null;
        }

        ApplyCurrentCostToPiece(rule);
        if (!rule.RangeEnabled)
        {
            return null;
        }

        PieceInfoState state = new(piece);
        float range = GetCurrentRange(rule);
        string shortcut = FormatRangeModifierShortcut();
        string rangeHint = shortcut.Length == 0
            ? GroundworkLocalization.Text("groundwork_terrain_range_hotkey_unbound", "Terrain range adjustment hotkey is unbound")
            : GroundworkLocalization.Format("groundwork_terrain_range_adjust_hint", "{0} + Mouse Wheel: adjust terrain range", shortcut);
        string rangeInfo = "<color=orange>" + rangeHint + "</color>\n" +
                           GroundworkLocalization.Format(
                               "groundwork_terrain_current_range",
                               "Current range: {0}m",
                               FormatRange(range));
        if (AimHeightHintPiecePrefabNames.Contains(rule.PiecePrefabName) &&
            ShouldKeepPavedRoadSmoothHeight(rule))
        {
            rangeInfo = $"{rangeInfo}\n{GroundworkLocalization.Text("groundwork_terrain_aim_height_hint", AimHeightHint)}";
        }

        rangeInfo = $"{rangeInfo}\n{FormatPreviewToggleHint()}";
        piece.m_description = string.IsNullOrWhiteSpace(piece.m_description)
            ? rangeInfo
            : $"{piece.m_description}\n\n{rangeInfo}";
        return state;
    }

    internal static void RestorePieceInfo(PieceInfoState? state)
    {
        state?.Restore();
    }

    internal static bool ShouldSuppressCameraZoom()
    {
        if (!ShouldSuppressCameraZoomInput() ||
            Mathf.Abs(ZInput.GetMouseScrollWheel()) < 0.01f)
        {
            return SuppressCameraZoomThisFrame;
        }

        return true;
    }

    internal static bool ShouldSuppressCameraZoomInput()
    {
        if (!Enabled ||
            Player.m_localPlayer == null ||
            !IsRangeModifierHeld() ||
            !TryGetSelectedRule(Player.m_localPlayer, out TerrainToolRule rule) ||
            !rule.RangeEnabled)
        {
            return SuppressCameraZoomThisFrame;
        }

        return true;
    }

    internal static void BeginTryPlacePiece(Player player, Piece piece)
    {
        if (!Enabled || player == null || piece == null || !RulesByPiece.TryGetValue(piece, out TerrainToolRule rule))
        {
            return;
        }

        ActivePlacementRules[player] = rule;
    }

    internal static void EndTryPlacePiece(Player player, Piece piece, bool placed)
    {
        if (player == null)
        {
            return;
        }

        if (placed && piece != null && RulesByPiece.TryGetValue(piece, out TerrainToolRule rule))
        {
            PendingPlacementCosts[player] = new PendingPlacementCost(rule, GetCurrentRange(rule), Time.frameCount);
            InvalidateCustomGridPreview();
        }

        ActivePlacementRules.Remove(player);
    }

    internal static TerrainOpSettingsState? PrepareTerrainOp(TerrainOp terrainOp)
    {
        if (terrainOp == null || Player.m_localPlayer == null ||
            !ActivePlacementRules.TryGetValue(Player.m_localPlayer, out TerrainToolRule rule) ||
            !rule.RangeEnabled)
        {
            return null;
        }

        TerrainOpSettingsState state = TerrainOpSettingsState.Capture(terrainOp.m_settings);
        ApplyTerrainOpOverrides(terrainOp.m_settings, rule);
        ApplyRangeToSettings(terrainOp.m_settings, GetCurrentRange(rule));
        return state;
    }

    internal static void RestoreTerrainOp(TerrainOp terrainOp, TerrainOpSettingsState? state)
    {
        if (terrainOp == null || state == null)
        {
            return;
        }

        state.Restore(terrainOp.m_settings);
    }

    internal static void CheckDynamicStamina(Character character, ref bool result)
    {
        if (!result ||
            !Enabled ||
            character is not Player player ||
            player != Player.m_localPlayer ||
            !IsPlacementButtonDown() ||
            !TryGetSelectedRule(player, out TerrainToolRule rule) ||
            !rule.RangeEnabled)
        {
            return;
        }

        float requiredStamina = player.GetBuildStamina() * rule.GetStaminaCostMultiplier(GetCurrentRange(rule));
        if (player.GetStamina() + 0.001f < requiredStamina)
        {
            result = false;
        }
    }

    internal static void CheckDynamicRequirements(Player player, Piece piece, Player.RequirementMode mode, ref bool result)
    {
        if (!result ||
            !Enabled ||
            mode != Player.RequirementMode.CanBuild ||
            player == null ||
            piece == null ||
            player.NoCostCheat() ||
            ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()) ||
            !RulesByPiece.TryGetValue(piece, out TerrainToolRule rule))
        {
            return;
        }

        ItemDrop.ItemData? rightItem = player.GetRightItem();
        if (rightItem?.m_shared?.m_useDurability != true)
        {
            return;
        }

        float requiredDurability = GetScaledPlaceDurability(player, rightItem, rule, GetCurrentRange(rule));
        if (requiredDurability > 0f && rightItem.m_durability + 0.001f < requiredDurability)
        {
            result = false;
        }
    }

    internal static void ApplyDynamicBuildStamina(Player player, ref float stamina)
    {
        if (!Enabled ||
            player == null ||
            !PendingPlacementCosts.TryGetValue(player, out PendingPlacementCost pendingCost) ||
            !pendingCost.IsCurrentFrame)
        {
            return;
        }

        stamina *= pendingCost.Rule.GetStaminaCostMultiplier(pendingCost.Range);
    }

    internal static void ApplyDynamicPlaceDurability(Player player, ItemDrop.ItemData tool, ref float durability)
    {
        if (!Enabled ||
            player == null ||
            tool == null ||
            !PendingPlacementCosts.TryGetValue(player, out PendingPlacementCost pendingCost) ||
            !pendingCost.IsCurrentFrame)
        {
            return;
        }

        durability *= pendingCost.Rule.GetDurabilityMultiplier(pendingCost.Range);
        PendingPlacementCosts.Remove(player);
    }

    private static void ApplyToPieceTable(ObjectDB objectDb, ObjectDbState state, string toolPrefabName, PieceTable pieceTable)
    {
        if (!RuleTemplatesByTool.TryGetValue(toolPrefabName, out List<TerrainToolRuleTemplate>? templates))
        {
            return;
        }

        foreach (TerrainToolRuleTemplate template in templates)
        {
            Piece? piece = FindPiece(pieceTable, template.PiecePrefabName);
            if (piece == null)
            {
                continue;
            }

            if (!state.OriginalCosts.ContainsKey(piece))
            {
                state.OriginalCosts[piece] = CloneRequirements(piece.m_resources);
            }

            Piece.Requirement[] baseRequirements = template.HasCostOverride
                ? BuildRequirements(objectDb, template)
                : CloneRequirements(state.OriginalCosts[piece]);
            piece.m_resources = CloneRequirements(baseRequirements);

            TerrainToolRule rule = TerrainToolRule.Create(template, piece, DetectBaseRange(piece), baseRequirements);
            RulesByPiece[piece] = rule;
            CurrentRanges[rule.Id] = Mathf.Clamp(CurrentRanges.TryGetValue(rule.Id, out float current) ? current : rule.DefaultRange, rule.MinRange, rule.MaxRange);
            ApplyCurrentCostToPiece(rule);
        }
    }

    private static Piece? FindPiece(PieceTable pieceTable, string piecePrefabName)
    {
        foreach (GameObject piecePrefab in pieceTable.m_pieces)
        {
            if (piecePrefab == null)
            {
                continue;
            }

            Piece? piece = piecePrefab.GetComponent<Piece>();
            if (piece == null)
            {
                continue;
            }

            if (piecePrefab.name.Equals(piecePrefabName, StringComparison.OrdinalIgnoreCase) ||
                piece.name.Equals(piecePrefabName, StringComparison.OrdinalIgnoreCase) ||
                piece.m_name.Equals(piecePrefabName, StringComparison.OrdinalIgnoreCase))
            {
                return piece;
            }
        }

        return null;
    }

    private static Piece.Requirement[] BuildRequirements(ObjectDB objectDb, TerrainToolRuleTemplate template)
    {
        List<Piece.Requirement> requirements = new();
        foreach ((string itemPrefabName, int amount) in template.Cost)
        {
            if (amount <= 0)
            {
                continue;
            }

            ItemDrop? itemDrop = objectDb.GetItemPrefab(itemPrefabName)?.GetComponent<ItemDrop>();
            if (itemDrop == null)
            {
                WarnOnce($"missing_cost_item:{template.Id}:{itemPrefabName}",
                    $"Terrain tool '{template.Id}' skipped cost item '{itemPrefabName}': item prefab was not found in ObjectDB.");
                continue;
            }

            requirements.Add(new Piece.Requirement
            {
                m_resItem = itemDrop,
                m_amount = amount,
                m_recover = false
            });
        }

        return requirements.ToArray();
    }

    private static void ApplyCurrentCostToPiece(TerrainToolRule rule)
    {
        if (rule.Piece == null)
        {
            return;
        }

        rule.Piece.m_resources = BuildScaledRequirements(rule.BaseRequirements, rule.GetMaterialCostMultiplier(GetCurrentRange(rule)));
    }

    private static Piece.Requirement[] BuildScaledRequirements(Piece.Requirement[] baseRequirements, float multiplier)
    {
        if (baseRequirements.Length == 0)
        {
            return Array.Empty<Piece.Requirement>();
        }

        Piece.Requirement[] scaled = new Piece.Requirement[baseRequirements.Length];
        for (int index = 0; index < baseRequirements.Length; index++)
        {
            Piece.Requirement requirement = baseRequirements[index];
            scaled[index] = new Piece.Requirement
            {
                m_resItem = requirement.m_resItem,
                m_amount = Mathf.CeilToInt(requirement.m_amount * Mathf.Max(1f, multiplier)),
                m_amountPerLevel = requirement.m_amountPerLevel,
                m_extraAmountOnlyOneIngredient = requirement.m_extraAmountOnlyOneIngredient,
                m_recover = requirement.m_recover
            };
        }

        return scaled;
    }

    private static void ClearRuntimeState()
    {
        RulesByPiece.Clear();
        RuleTemplatesByTool.Clear();
        PendingPlacementCosts.Clear();
        ActivePlacementRules.Clear();
        ActiveRangeRule = null;
        ActivePreviewMode = null;
        ClearRangePreview();
    }

    private static void RestoreOriginalCosts(ObjectDbState state)
    {
        foreach ((Piece piece, Piece.Requirement[] requirements) in state.OriginalCosts)
        {
            if (piece != null)
            {
                piece.m_resources = CloneRequirements(requirements);
            }
        }
    }

    private static Piece.Requirement[] CloneRequirements(Piece.Requirement[]? requirements)
    {
        if (requirements == null || requirements.Length == 0)
        {
            return Array.Empty<Piece.Requirement>();
        }

        Piece.Requirement[] clone = new Piece.Requirement[requirements.Length];
        for (int index = 0; index < requirements.Length; index++)
        {
            Piece.Requirement requirement = requirements[index];
            clone[index] = new Piece.Requirement
            {
                m_resItem = requirement.m_resItem,
                m_amount = requirement.m_amount,
                m_amountPerLevel = requirement.m_amountPerLevel,
                m_extraAmountOnlyOneIngredient = requirement.m_extraAmountOnlyOneIngredient,
                m_recover = requirement.m_recover
            };
        }

        return clone;
    }

    private static bool TryGetSelectedRule(Player player, out TerrainToolRule rule)
    {
        rule = null!;
        Piece? piece = player.m_buildPieces != null ? player.m_buildPieces.GetSelectedPiece() : null;
        return piece != null && RulesByPiece.TryGetValue(piece, out rule);
    }

    private static string ResolveCurrentToolPrefabName(Player player)
    {
        ItemDrop.ItemData? rightItem = player.GetRightItem();
        return rightItem?.m_dropPrefab != null ? rightItem.m_dropPrefab.name : "";
    }

    private static float GetCurrentRange(TerrainToolRule rule)
    {
        return CurrentRanges.TryGetValue(rule.Id, out float range)
            ? Mathf.Clamp(range, rule.MinRange, rule.MaxRange)
            : rule.DefaultRange;
    }

    private static void SetActiveRangeRule(TerrainToolRule rule)
    {
        if (ActiveRangeRule != null && ActiveRangeRule.Id.Equals(rule.Id, StringComparison.OrdinalIgnoreCase))
        {
            ActiveRangeRule = rule;
            ActivePreviewMode ??= GetDefaultPreviewMode();
            return;
        }

        ClearActiveRangeRule();
        ActiveRangeRule = rule;
        ActivePreviewMode = GetDefaultPreviewMode();
    }

    private static void ClearActiveRangeRule()
    {
        if (ActiveRangeRule == null)
        {
            return;
        }

        ResetCurrentRangeToDefault(ActiveRangeRule);
        ActiveRangeRule = null;
        ActivePreviewMode = null;
    }

    private static void ResetCurrentRangeToDefault(TerrainToolRule rule)
    {
        CurrentRanges[rule.Id] = rule.VanillaRange;
        ApplyCurrentCostToPiece(rule);
    }

    private static GroundworkPlugin.TerrainToolRangePreviewMode GetDefaultPreviewMode()
    {
        return GroundworkToolsDomain.TerrainToolDefaultPreviewMode;
    }

    private static GroundworkPlugin.TerrainToolRangePreviewMode GetCurrentPreviewMode()
    {
        ActivePreviewMode ??= GetDefaultPreviewMode();
        return ActivePreviewMode.Value;
    }

    private static void TryTogglePreviewMode()
    {
        if (!CanHandlePreviewToggleInput() ||
            !GroundworkToolsDomain.TerrainToolPreviewToggleHotkey.IsKeyDown())
        {
            return;
        }

        ActivePreviewMode = GetCurrentPreviewMode() == GroundworkPlugin.TerrainToolRangePreviewMode.Grid
            ? GroundworkPlugin.TerrainToolRangePreviewMode.Vanilla
            : GroundworkPlugin.TerrainToolRangePreviewMode.Grid;
        ClearRangePreview();
    }

    private static bool CanHandlePreviewToggleInput()
    {
        return !Hud.IsPieceSelectionVisible() &&
               !Hud.InRadial() &&
               !InventoryGui.IsVisible() &&
               !Menu.IsVisible() &&
               !Console.IsVisible() &&
               (Chat.instance == null || !Chat.instance.HasFocus());
    }

    private static float DetectBaseRange(Piece piece)
    {
        float radius = 0f;
        foreach (TerrainOp terrainOp in piece.GetComponentsInChildren<TerrainOp>(includeInactive: true))
        {
            radius = Mathf.Max(radius, DetectRepresentativeRange(terrainOp.m_settings));
        }

        return radius;
    }

    private static void ApplyRangeToTerrainOps(IEnumerable<TerrainOp> terrainOps, float range, TerrainToolRule rule)
    {
        foreach (TerrainOp terrainOp in terrainOps)
        {
            if (terrainOp != null)
            {
                ApplyTerrainOpOverrides(terrainOp.m_settings, rule);
                ApplyRangeToSettings(terrainOp.m_settings, range);
            }
        }
    }

    private static TerrainOp[] GetCachedTerrainOps(GameObject ghost)
    {
        if (ghost == null)
        {
            ClearCachedTerrainOps();
            return Array.Empty<TerrainOp>();
        }

        if (CachedTerrainOpsGhost == ghost)
        {
            return CachedTerrainOps;
        }

        CachedTerrainOpsGhost = ghost;
        CachedTerrainOps = ghost.GetComponentsInChildren<TerrainOp>(includeInactive: true);
        InvalidateCustomGridPreview();
        return CachedTerrainOps;
    }

    private static void ClearCachedTerrainOps()
    {
        CachedTerrainOpsGhost = null;
        CachedTerrainOps = Array.Empty<TerrainOp>();
    }

    private static void ApplyTerrainOpOverrides(TerrainOp.Settings settings, TerrainToolRule rule)
    {
        if (settings == null || rule == null)
        {
            return;
        }

        if (!ShouldKeepPavedRoadSmoothHeight(rule))
        {
            settings.m_smooth = false;
        }
    }

    private static bool ShouldKeepPavedRoadSmoothHeight(TerrainToolRule rule)
    {
        return !rule.PiecePrefabName.Equals("paved_road_v2", StringComparison.OrdinalIgnoreCase) ||
               GroundworkToolsDomain.PavedRoadSmoothHeight;
    }

    private static void ApplyRangeToSettings(TerrainOp.Settings settings, float range)
    {
        float representativeRange = DetectRepresentativeRange(settings);
        if (representativeRange <= 0.001f)
        {
            return;
        }

        float scale = Mathf.Max(0f, range) / representativeRange;
        if (settings.m_levelRadius > 0f)
        {
            settings.m_levelRadius *= scale;
        }

        if (settings.m_raiseRadius > 0f)
        {
            settings.m_raiseRadius *= scale;
        }

        if (settings.m_smoothRadius > 0f)
        {
            settings.m_smoothRadius *= scale;
        }

        if (settings.m_paintRadius > 0f)
        {
            settings.m_paintRadius *= scale;
        }
    }

    private static float DetectRepresentativeRange(TerrainOp.Settings settings)
    {
        if (settings.m_level && settings.m_levelRadius > 0f)
        {
            return settings.m_levelRadius;
        }

        if (settings.m_raise && settings.m_raiseRadius > 0f)
        {
            return settings.m_raiseRadius;
        }

        if (settings.m_smooth && settings.m_smoothRadius > 0f)
        {
            return settings.m_smoothRadius;
        }

        if (settings.m_paintRadius > 0f)
        {
            return settings.m_paintRadius;
        }

        return Mathf.Max(settings.m_levelRadius, settings.m_raiseRadius, settings.m_smoothRadius, settings.m_paintRadius);
    }

    private static bool IsGridRangePreviewEnabled()
    {
        return GetCurrentPreviewMode() == GroundworkPlugin.TerrainToolRangePreviewMode.Grid;
    }

    private static void ApplyRangeToGhostVisual(GameObject ghost, TerrainToolRule rule, float range)
    {
        if (ghost == null)
        {
            ResetScaledPlacementGhost();
            return;
        }

        if (ScaledPlacementGhost != ghost)
        {
            ResetScaledPlacementGhost();
            ScaledPlacementGhost = ghost;
            ScaledPlacementGhostBaseScale = ghost.transform.localScale;
            CapturePreviewVisuals(ghost);
        }

        float scale = rule.GetVisualRangeScale(range);
        ghost.transform.localScale = ScaledPlacementGhostBaseScale;
        if (ScaledPreviewTransforms.Count == 0)
        {
            ghost.transform.localScale = new Vector3(
                ScaledPlacementGhostBaseScale.x * scale,
                ScaledPlacementGhostBaseScale.y,
                ScaledPlacementGhostBaseScale.z * scale);
        }
        else
        {
            foreach (PreviewTransformScaleState transformState in ScaledPreviewTransforms)
            {
                transformState.Apply(scale);
            }
        }

        if (Mathf.Abs(ScaledPlacementGhostLastVisualScale - scale) > 0.001f)
        {
            RestartPreviewParticles();
            ScaledPlacementGhostLastVisualScale = scale;
        }
    }

    private static void CapturePreviewVisuals(GameObject ghost)
    {
        ScaledPreviewTransforms.Clear();
        ScaledPreviewParticleSystems.Clear();

        foreach (Transform transform in ghost.GetComponentsInChildren<Transform>(includeInactive: true))
        {
            if (transform == null || !transform.name.Equals("_GhostOnly", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CaptureGhostOnlyVisualTransform(transform);
        }

        ScaledPreviewParticleSystems.AddRange(ghost.GetComponentsInChildren<ParticleSystem>(includeInactive: true));
    }

    private static void CaptureGhostOnlyVisualTransform(Transform ghostOnlyTransform)
    {
        if (HasScalablePreviewVisual(ghostOnlyTransform))
        {
            ScaledPreviewTransforms.Add(new PreviewTransformScaleState(ghostOnlyTransform));
            return;
        }

        foreach (Transform child in ghostOnlyTransform.GetComponentsInChildren<Transform>(includeInactive: true))
        {
            if (child == null || child == ghostOnlyTransform || !HasScalablePreviewVisual(child))
            {
                continue;
            }

            ScaledPreviewTransforms.Add(new PreviewTransformScaleState(child));
        }
    }

    private static bool HasScalablePreviewVisual(Transform transform)
    {
        if (transform.GetComponent<ParticleSystem>() != null)
        {
            return true;
        }

        Renderer? renderer = transform.GetComponent<Renderer>();
        return renderer != null && renderer.enabled;
    }

    private static void ResetScaledPlacementGhost()
    {
        foreach (PreviewTransformScaleState transformState in ScaledPreviewTransforms)
        {
            transformState.Restore();
        }

        if (ScaledPlacementGhost != null)
        {
            ScaledPlacementGhost.transform.localScale = ScaledPlacementGhostBaseScale;
        }

        ScaledPlacementGhost = null;
        ScaledPlacementGhostBaseScale = Vector3.one;
        ScaledPreviewTransforms.Clear();
        ScaledPreviewParticleSystems.Clear();
        ScaledPlacementGhostLastVisualScale = -1f;
    }

    private static void RestartPreviewParticles()
    {
        foreach (ParticleSystem particleSystem in ScaledPreviewParticleSystems)
        {
            if (particleSystem == null || !particleSystem.gameObject.activeInHierarchy)
            {
                continue;
            }

            particleSystem.Clear(withChildren: false);
            particleSystem.Play(withChildren: false);
        }
    }

    private static void ApplyCustomRangePreview(GameObject ghost, IReadOnlyList<TerrainOp> terrainOps, float range)
    {
        if (ghost == null)
        {
            ClearCustomRangePreview();
            return;
        }

        if (CustomRangePreviewGhost != ghost)
        {
            RestoreHiddenPreviewVisuals();
            CustomRangePreviewGhost = ghost;
            CustomRangePreviewColor = SamplePreviewRingColor(ghost);
            HideVanillaPreviewVisuals(ghost);
        }

        KeepVanillaPreviewVisualsHidden();

        LineRenderer? lineRenderer = EnsureCustomRangePreviewLine();
        if (lineRenderer == null)
        {
            return;
        }

        Vector3 center = ResolveRangePreviewWorldPoint(ghost, terrainOps);
        float previewRange = Mathf.Max(0.01f, range);
        CustomRangePreviewShape shape = ResolveCustomRangePreviewShape(terrainOps);
        Color ringColor = CustomRangePreviewColor;
        ringColor.a = Mathf.Clamp(ringColor.a <= 0.001f ? 0.28f : ringColor.a * 0.45f, 0.16f, 0.55f);
        UpdateCustomRangePreview(
            lineRenderer,
            center,
            previewRange,
            shape,
            ringColor);
        UpdateCustomGridPreview(terrainOps, CustomRangePreviewColor);
        lineRenderer.gameObject.SetActive(true);
    }

    private static LineRenderer? EnsureCustomRangePreviewLine()
    {
        if (CustomRangePreviewLine != null && CustomRangePreviewObject != null)
        {
            return CustomRangePreviewLine;
        }

        CustomRangePreviewObject = new GameObject("Groundwork_TerrainToolRangePreview");
        CustomRangePreviewObject.hideFlags = HideFlags.DontSave;
        CustomRangePreviewLine = CustomRangePreviewObject.AddComponent<LineRenderer>();
        Material? material = GetCustomRangePreviewMaterial();
        if (material != null)
        {
            CustomRangePreviewLine.material = material;
        }

        CustomRangePreviewLine.useWorldSpace = true;
        CustomRangePreviewLine.positionCount = CustomPreviewSegmentCount + 1;
        CustomRangePreviewLine.widthMultiplier = CustomPreviewLineWidth;
        CustomRangePreviewLine.numCapVertices = 2;
        CustomRangePreviewLine.numCornerVertices = 2;
        CustomRangePreviewLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        CustomRangePreviewLine.receiveShadows = false;
        CustomRangePreviewObject.SetActive(false);
        return CustomRangePreviewLine;
    }

    private static Material? GetCustomRangePreviewMaterial()
    {
        if (CustomRangePreviewMaterial != null)
        {
            return CustomRangePreviewMaterial;
        }

        Shader shader = Shader.Find("Sprites/Default") ??
                        Shader.Find("Legacy Shaders/Particles/Alpha Blended") ??
                        Shader.Find("Standard");
        if (shader == null)
        {
            return null;
        }

        CustomRangePreviewMaterial = new Material(shader)
        {
            color = Color.white,
            hideFlags = HideFlags.DontSave
        };
        return CustomRangePreviewMaterial;
    }

    private static MeshRenderer? EnsureCustomGridPreview()
    {
        if (CustomGridPreviewRenderer != null && CustomGridPreviewMeshFilter != null && CustomGridPreviewObject != null)
        {
            return CustomGridPreviewRenderer;
        }

        CustomGridPreviewObject = new GameObject("Groundwork_TerrainToolGridPreview");
        CustomGridPreviewObject.hideFlags = HideFlags.DontSave;
        CustomGridPreviewMeshFilter = CustomGridPreviewObject.AddComponent<MeshFilter>();
        CustomGridPreviewRenderer = CustomGridPreviewObject.AddComponent<MeshRenderer>();
        Material? material = GetCustomRangePreviewMaterial();
        if (material != null)
        {
            CustomGridPreviewRenderer.sharedMaterial = material;
        }

        CustomGridPreviewRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        CustomGridPreviewRenderer.receiveShadows = false;
        CustomGridPreviewMesh = new Mesh
        {
            name = "Groundwork_TerrainToolGridPreviewMesh",
            hideFlags = HideFlags.DontSave
        };
        CustomGridPreviewMesh.MarkDynamic();
        CustomGridPreviewMeshFilter.sharedMesh = CustomGridPreviewMesh;
        CustomGridPreviewObject.SetActive(false);
        return CustomGridPreviewRenderer;
    }

    private static void UpdateCustomRangePreview(LineRenderer lineRenderer, Vector3 center, float radius, CustomRangePreviewShape shape, Color color)
    {
        color.a = Mathf.Clamp(color.a <= 0.001f ? FallbackPreviewRingColor.a : color.a, 0.2f, 1f);
        lineRenderer.startColor = color;
        lineRenderer.endColor = color;
        lineRenderer.widthMultiplier = CustomPreviewLineWidth;

        if (shape == CustomRangePreviewShape.Square)
        {
            lineRenderer.positionCount = 5;
            lineRenderer.SetPosition(0, center + new Vector3(-radius, 0f, -radius));
            lineRenderer.SetPosition(1, center + new Vector3(-radius, 0f, radius));
            lineRenderer.SetPosition(2, center + new Vector3(radius, 0f, radius));
            lineRenderer.SetPosition(3, center + new Vector3(radius, 0f, -radius));
            lineRenderer.SetPosition(4, center + new Vector3(-radius, 0f, -radius));
            return;
        }

        lineRenderer.positionCount = CustomPreviewSegmentCount + 1;
        for (int index = 0; index <= CustomPreviewSegmentCount; index++)
        {
            float angle = index / (float)CustomPreviewSegmentCount * Mathf.PI * 2f;
            Vector3 point = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            lineRenderer.SetPosition(index, point);
        }
    }

    private static void UpdateCustomGridPreview(IReadOnlyList<TerrainOp> terrainOps, Color color)
    {
        MeshRenderer? renderer = EnsureCustomGridPreview();
        if (renderer == null || CustomGridPreviewMesh == null || CustomGridPreviewObject == null)
        {
            return;
        }

        if (!ShouldRebuildCustomGridPreview(terrainOps, color))
        {
            if (CustomGridPreviewMesh.vertexCount > 0)
            {
                CustomGridPreviewObject.SetActive(true);
            }

            return;
        }

        CustomGridPreviewVertices.Clear();
        CustomGridPreviewIndices.Clear();
        CustomGridPreviewColors.Clear();
        CustomGridPreviewHeightmaps.Clear();

        Color markerColor = color;
        markerColor.a = Mathf.Clamp(markerColor.a <= 0.001f ? 0.28f : markerColor.a * 0.45f, 0.16f, 0.5f);

        foreach (TerrainOp terrainOp in terrainOps)
        {
            if (terrainOp == null ||
                !TryResolveRepresentativeOperation(
                    terrainOp,
                    out Vector3 center,
                    out float radius,
                    out CustomRangePreviewShape shape,
                    out bool usePaintGrid,
                    out bool includeBoundary))
            {
                continue;
            }

            AddCustomGridPreviewOperation(center, radius, shape, usePaintGrid, includeBoundary, markerColor);

            if (CustomGridPreviewVertices.Count / 4 >= CustomGridPreviewMaxMarkers)
            {
                break;
            }
        }

        CustomGridPreviewMesh.Clear();
        if (CustomGridPreviewVertices.Count == 0)
        {
            CustomGridPreviewObject.SetActive(false);
            return;
        }

        CustomGridPreviewMesh.SetVertices(CustomGridPreviewVertices);
        CustomGridPreviewMesh.SetColors(CustomGridPreviewColors);
        CustomGridPreviewMesh.SetIndices(CustomGridPreviewIndices, MeshTopology.Triangles, 0);
        CustomGridPreviewMesh.RecalculateBounds();
        CustomGridPreviewObject.SetActive(true);
    }

    private static bool ShouldRebuildCustomGridPreview(IReadOnlyList<TerrainOp> terrainOps, Color color)
    {
        int signature = BuildCustomGridPreviewSignature(terrainOps, color);
        if (CustomGridPreviewSignatureValid && signature == CustomGridPreviewSignature)
        {
            return false;
        }

        CustomGridPreviewSignature = signature;
        CustomGridPreviewSignatureValid = true;
        return true;
    }

    private static int BuildCustomGridPreviewSignature(IReadOnlyList<TerrainOp> terrainOps, Color color)
    {
        HashCode hash = new();
        hash.Add(Mathf.RoundToInt(color.r * 255f));
        hash.Add(Mathf.RoundToInt(color.g * 255f));
        hash.Add(Mathf.RoundToInt(color.b * 255f));
        hash.Add(Mathf.RoundToInt(color.a * 255f));

        foreach (TerrainOp terrainOp in terrainOps)
        {
            if (terrainOp == null ||
                !TryResolveRepresentativeOperation(
                    terrainOp,
                    out Vector3 center,
                    out float radius,
                    out CustomRangePreviewShape shape,
                    out bool usePaintGrid,
                    out bool includeBoundary))
            {
                continue;
            }

            hash.Add(Mathf.RoundToInt(radius / CustomGridPreviewRadiusEpsilon));
            hash.Add(shape);
            hash.Add(usePaintGrid);
            hash.Add(includeBoundary);
            AddCustomGridPreviewOperationSignature(center, radius, usePaintGrid, ref hash);
        }

        return hash.ToHashCode();
    }

    private static void AddCustomGridPreviewOperationSignature(Vector3 center, float radius, bool usePaintGrid, ref HashCode hash)
    {
        CustomGridPreviewHeightmaps.Clear();
        Heightmap.FindHeightmap(center, radius + 1f, CustomGridPreviewHeightmaps);
        hash.Add(CustomGridPreviewHeightmaps.Count);
        foreach (Heightmap heightmap in CustomGridPreviewHeightmaps)
        {
            if (heightmap == null)
            {
                hash.Add(0);
                continue;
            }

            Vector3 gridCenter = center;
            hash.Add(heightmap.GetInstanceID());
            hash.Add(Mathf.RoundToInt(heightmap.m_scale * 1000f));
            if (usePaintGrid)
            {
                gridCenter.x -= 0.5f;
                gridCenter.z -= 0.5f;
                heightmap.WorldToVertexMask(gridCenter, out int paintCenterX, out int paintCenterY);
                hash.Add(paintCenterX);
                hash.Add(paintCenterY);
            }
            else
            {
                heightmap.WorldToVertex(gridCenter, out int centerX, out int centerY);
                hash.Add(centerX);
                hash.Add(centerY);
            }
        }
    }

    private static void InvalidateCustomGridPreview()
    {
        CustomGridPreviewSignatureValid = false;
    }

    private static void AddCustomGridPreviewOperation(
        Vector3 center,
        float radius,
        CustomRangePreviewShape shape,
        bool usePaintGrid,
        bool includeBoundary,
        Color color)
    {
        if (radius <= 0.001f || CustomGridPreviewVertices.Count / 4 >= CustomGridPreviewMaxMarkers)
        {
            return;
        }

        CustomGridPreviewHeightmaps.Clear();
        Heightmap.FindHeightmap(center, radius + 1f, CustomGridPreviewHeightmaps);
        foreach (Heightmap heightmap in CustomGridPreviewHeightmaps)
        {
            if (heightmap == null)
            {
                continue;
            }

            Vector3 gridCenter = center;
            if (usePaintGrid)
            {
                gridCenter.x -= 0.5f;
                gridCenter.z -= 0.5f;
                heightmap.WorldToVertexMask(gridCenter, out int paintCenterX, out int paintCenterY);
                AddCustomGridPreviewHeightmapMarkers(heightmap, paintCenterX, paintCenterY, radius, CustomRangePreviewShape.Circle, includeBoundary, color);
            }
            else
            {
                heightmap.WorldToVertex(gridCenter, out int centerX, out int centerY);
                AddCustomGridPreviewHeightmapMarkers(heightmap, centerX, centerY, radius, shape, includeBoundary, color);
            }

            if (CustomGridPreviewVertices.Count / 4 >= CustomGridPreviewMaxMarkers)
            {
                break;
            }
        }
    }

    private static void AddCustomGridPreviewHeightmapMarkers(
        Heightmap heightmap,
        int centerX,
        int centerY,
        float radius,
        CustomRangePreviewShape shape,
        bool includeBoundary,
        Color color)
    {
        float vertexRadius = radius / Mathf.Max(0.001f, heightmap.m_scale);
        int indexRadius = Mathf.CeilToInt(vertexRadius);
        int width = heightmap.m_width + 1;
        Vector2 vertexCenter = new(centerX, centerY);
        for (int y = centerY - indexRadius; y <= centerY + indexRadius; y++)
        {
            for (int x = centerX - indexRadius; x <= centerX + indexRadius; x++)
            {
                if (x < 0 || y < 0 || x >= width || y >= width)
                {
                    continue;
                }

                if (shape == CustomRangePreviewShape.Circle)
                {
                    float distance = Vector2.Distance(vertexCenter, new Vector2(x, y));
                    if (includeBoundary ? distance > vertexRadius : distance >= vertexRadius)
                    {
                        continue;
                    }
                }

                AddCustomGridPreviewMarker(heightmap, x, y, color);
                if (CustomGridPreviewVertices.Count / 4 >= CustomGridPreviewMaxMarkers)
                {
                    break;
                }
            }

            if (CustomGridPreviewVertices.Count / 4 >= CustomGridPreviewMaxMarkers)
            {
                break;
            }
        }
    }

    private static void AddCustomGridPreviewMarker(Heightmap heightmap, int x, int y, Color color)
    {
        Vector3 center = heightmap.transform.TransformPoint(heightmap.CalcVertex(x, y)) +
                         Vector3.up * CustomPreviewYOffset;
        int vertexStart = CustomGridPreviewVertices.Count;
        float halfSize = CustomGridPreviewMarkerSize * 0.5f;
        CustomGridPreviewVertices.Add(center + new Vector3(-halfSize, 0f, -halfSize));
        CustomGridPreviewVertices.Add(center + new Vector3(-halfSize, 0f, halfSize));
        CustomGridPreviewVertices.Add(center + new Vector3(halfSize, 0f, halfSize));
        CustomGridPreviewVertices.Add(center + new Vector3(halfSize, 0f, -halfSize));
        CustomGridPreviewColors.Add(color);
        CustomGridPreviewColors.Add(color);
        CustomGridPreviewColors.Add(color);
        CustomGridPreviewColors.Add(color);
        CustomGridPreviewIndices.Add(vertexStart);
        CustomGridPreviewIndices.Add(vertexStart + 1);
        CustomGridPreviewIndices.Add(vertexStart + 2);
        CustomGridPreviewIndices.Add(vertexStart);
        CustomGridPreviewIndices.Add(vertexStart + 2);
        CustomGridPreviewIndices.Add(vertexStart + 3);
    }

    private static CustomRangePreviewShape ResolveCustomRangePreviewShape(IReadOnlyList<TerrainOp> terrainOps)
    {
        foreach (TerrainOp terrainOp in terrainOps)
        {
            if (terrainOp != null &&
                TryResolveRepresentativeOperation(
                    terrainOp,
                    out _,
                    out _,
                    out CustomRangePreviewShape shape,
                    out _,
                    out _))
            {
                return shape;
            }
        }

        return CustomRangePreviewShape.Circle;
    }

    private static bool IsRepresentativeRadius(float radius, float representativeRange)
    {
        return radius > 0.001f && Mathf.Abs(radius - representativeRange) <= 0.001f;
    }

    private enum CustomRangePreviewShape
    {
        Circle,
        Square
    }

    private static Vector3 ResolveRangePreviewWorldPoint(GameObject ghost, IReadOnlyList<TerrainOp> terrainOps)
    {
        if (TryResolveSnappedRangePreviewWorldPoint(terrainOps, CustomPreviewYOffset, out Vector3 snappedWorldPoint))
        {
            return snappedWorldPoint;
        }

        foreach (TerrainOp terrainOp in terrainOps)
        {
            if (terrainOp != null)
            {
                return terrainOp.transform.position + Vector3.up * CustomPreviewYOffset;
            }
        }

        return ghost != null
            ? ghost.transform.position + Vector3.up * CustomPreviewYOffset
            : Vector3.up * CustomPreviewYOffset;
    }

    private static bool TryResolveSnappedRangePreviewWorldPoint(IReadOnlyList<TerrainOp> terrainOps, float yOffset, out Vector3 worldPoint)
    {
        foreach (TerrainOp terrainOp in terrainOps)
        {
            if (terrainOp == null ||
                !TryResolveRepresentativeOperation(
                    terrainOp,
                    out Vector3 center,
                    out float radius,
                    out CustomRangePreviewShape shape,
                    out bool usePaintGrid,
                    out bool includeBoundary))
            {
                continue;
            }

            if (TrySnapRangePreviewCenter(center, radius, shape, usePaintGrid, includeBoundary, yOffset, out worldPoint))
            {
                return true;
            }
        }

        worldPoint = default;
        return false;
    }

    private static bool TryResolveRepresentativeOperation(
        TerrainOp terrainOp,
        out Vector3 center,
        out float radius,
        out CustomRangePreviewShape shape,
        out bool usePaintGrid,
        out bool includeBoundary)
    {
        TerrainOp.Settings settings = terrainOp.m_settings;
        Vector3 position = terrainOp.transform.position;
        float representativeRange = DetectRepresentativeRange(settings);

        if (representativeRange <= 0.001f)
        {
            center = default;
            radius = 0f;
            shape = CustomRangePreviewShape.Circle;
            usePaintGrid = false;
            includeBoundary = true;
            return false;
        }

        if (settings.m_level && IsRepresentativeRadius(settings.m_levelRadius, representativeRange))
        {
            center = position + Vector3.up * settings.m_levelOffset;
            radius = settings.m_levelRadius;
            shape = settings.m_square ? CustomRangePreviewShape.Square : CustomRangePreviewShape.Circle;
            usePaintGrid = false;
            includeBoundary = true;
            return true;
        }

        if (settings.m_raise && IsRepresentativeRadius(settings.m_raiseRadius, representativeRange))
        {
            center = position;
            radius = settings.m_raiseRadius;
            shape = settings.m_square ? CustomRangePreviewShape.Square : CustomRangePreviewShape.Circle;
            usePaintGrid = false;
            includeBoundary = settings.m_square;
            return true;
        }

        if (settings.m_paintCleared && settings.m_paintRadius > 0.001f)
        {
            center = position;
            radius = settings.m_paintRadius;
            shape = CustomRangePreviewShape.Circle;
            usePaintGrid = true;
            includeBoundary = false;
            return true;
        }

        if (settings.m_smooth && IsRepresentativeRadius(settings.m_smoothRadius, representativeRange))
        {
            center = position + Vector3.up * settings.m_levelOffset;
            radius = settings.m_smoothRadius;
            shape = settings.m_square ? CustomRangePreviewShape.Square : CustomRangePreviewShape.Circle;
            usePaintGrid = false;
            includeBoundary = false;
            return true;
        }

        center = default;
        radius = 0f;
        shape = CustomRangePreviewShape.Circle;
        usePaintGrid = false;
        includeBoundary = true;
        return false;
    }

    private static bool TrySnapRangePreviewCenter(
        Vector3 center,
        float radius,
        CustomRangePreviewShape shape,
        bool usePaintGrid,
        bool includeBoundary,
        float yOffset,
        out Vector3 worldPoint)
    {
        CustomRangePreviewHeightmaps.Clear();
        Heightmap.FindHeightmap(center, radius + 1f, CustomRangePreviewHeightmaps);

        bool found = false;
        Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        foreach (Heightmap heightmap in CustomRangePreviewHeightmaps)
        {
            if (heightmap == null)
            {
                continue;
            }

            Vector3 gridCenter = center;
            if (usePaintGrid)
            {
                gridCenter.x -= 0.5f;
                gridCenter.z -= 0.5f;
                heightmap.WorldToVertexMask(gridCenter, out int paintCenterX, out int paintCenterY);
                found |= AccumulateAffectedGridPreviewBounds(heightmap, paintCenterX, paintCenterY, radius, CustomRangePreviewShape.Circle, includeBoundary, yOffset, ref min, ref max);
            }
            else
            {
                heightmap.WorldToVertex(gridCenter, out int centerX, out int centerY);
                found |= AccumulateAffectedGridPreviewBounds(heightmap, centerX, centerY, radius, shape, includeBoundary, yOffset, ref min, ref max);
            }
        }

        worldPoint = found ? (min + max) * 0.5f : default;
        return found;
    }

    private static bool AccumulateAffectedGridPreviewBounds(
        Heightmap heightmap,
        int centerX,
        int centerY,
        float radius,
        CustomRangePreviewShape shape,
        bool includeBoundary,
        float yOffset,
        ref Vector3 min,
        ref Vector3 max)
    {
        float vertexRadius = radius / Mathf.Max(0.001f, heightmap.m_scale);
        int indexRadius = Mathf.CeilToInt(vertexRadius);
        int width = heightmap.m_width + 1;
        Vector2 vertexCenter = new(centerX, centerY);
        bool found = false;
        for (int y = centerY - indexRadius; y <= centerY + indexRadius; y++)
        {
            for (int x = centerX - indexRadius; x <= centerX + indexRadius; x++)
            {
                if (x < 0 || y < 0 || x >= width || y >= width)
                {
                    continue;
                }

                if (shape == CustomRangePreviewShape.Circle)
                {
                    float distance = Vector2.Distance(vertexCenter, new Vector2(x, y));
                    if (includeBoundary ? distance > vertexRadius : distance >= vertexRadius)
                    {
                        continue;
                    }
                }

                Vector3 worldPoint = heightmap.transform.TransformPoint(heightmap.CalcVertex(x, y)) + Vector3.up * yOffset;
                min = Vector3.Min(min, worldPoint);
                max = Vector3.Max(max, worldPoint);
                found = true;
            }
        }

        return found;
    }

    private static Color SamplePreviewRingColor(GameObject ghost)
    {
        foreach (Transform ghostOnlyTransform in EnumerateGhostOnlyTransforms(ghost))
        {
            foreach (Renderer renderer in ghostOnlyTransform.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if (renderer != null && TrySampleRendererColor(renderer, out Color color))
                {
                    return NormalizePreviewColor(color);
                }
            }

            foreach (ParticleSystem particleSystem in ghostOnlyTransform.GetComponentsInChildren<ParticleSystem>(includeInactive: true))
            {
                if (particleSystem == null)
                {
                    continue;
                }

                Color color = particleSystem.main.startColor.color;
                if (HasVisibleColor(color))
                {
                    return NormalizePreviewColor(color);
                }
            }
        }

        return FallbackPreviewRingColor;
    }

    private static bool TrySampleRendererColor(Renderer renderer, out Color color)
    {
        MaterialPropertyBlock propertyBlock = new();
        renderer.GetPropertyBlock(propertyBlock);
        foreach (int propertyId in PreviewColorProperties)
        {
            Color blockColor = propertyBlock.GetColor(propertyId);
            if (HasVisibleColor(blockColor))
            {
                color = blockColor;
                return true;
            }
        }

        foreach (Material material in renderer.sharedMaterials)
        {
            if (material == null)
            {
                continue;
            }

            foreach (int propertyId in PreviewColorProperties)
            {
                if (!material.HasProperty(propertyId))
                {
                    continue;
                }

                Color materialColor = material.GetColor(propertyId);
                if (HasVisibleColor(materialColor))
                {
                    color = materialColor;
                    return true;
                }
            }

        }

        color = default;
        return false;
    }

    private static bool HasVisibleColor(Color color)
    {
        return color.a > 0.001f && Mathf.Max(color.r, color.g, color.b) > 0.001f;
    }

    private static Color NormalizePreviewColor(Color color)
    {
        if (!HasVisibleColor(color))
        {
            return FallbackPreviewRingColor;
        }

        color.a = Mathf.Clamp(color.a, 0.2f, 1f);
        return color;
    }

    private static IEnumerable<Transform> EnumerateGhostOnlyTransforms(GameObject ghost)
    {
        if (ghost == null)
        {
            yield break;
        }

        foreach (Transform transform in ghost.GetComponentsInChildren<Transform>(includeInactive: true))
        {
            if (transform != null && transform.name.Equals("_GhostOnly", StringComparison.OrdinalIgnoreCase))
            {
                yield return transform;
            }
        }
    }

    private static void HideVanillaPreviewVisuals(GameObject ghost)
    {
        HiddenPreviewVisuals.Clear();
        foreach (Transform ghostOnlyTransform in EnumerateGhostOnlyTransforms(ghost))
        {
            foreach (Renderer renderer in ghostOnlyTransform.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if (renderer == null)
                {
                    continue;
                }

                HiddenPreviewVisuals.Add(new PreviewVisualVisibilityState(renderer));
                renderer.enabled = false;
            }
        }
    }

    private static void ClearCustomRangePreview()
    {
        RestoreHiddenPreviewVisuals();
        CustomRangePreviewGhost = null;
        InvalidateCustomGridPreview();
        if (CustomRangePreviewObject != null)
        {
            CustomRangePreviewObject.SetActive(false);
        }

        if (CustomGridPreviewObject != null)
        {
            CustomGridPreviewObject.SetActive(false);
        }
    }

    private static void RestoreHiddenPreviewVisuals()
    {
        foreach (PreviewVisualVisibilityState visualState in HiddenPreviewVisuals)
        {
            visualState.Restore();
        }

        HiddenPreviewVisuals.Clear();
    }

    private static void KeepVanillaPreviewVisualsHidden()
    {
        foreach (PreviewVisualVisibilityState visualState in HiddenPreviewVisuals)
        {
            visualState.Hide();
        }
    }

    private static void UpdateRangeLabel(TerrainToolRule rule, GameObject ghost, IReadOnlyList<TerrainOp> terrainOps, float range)
    {
        if (!GroundworkToolsDomain.ToolHudEnabled)
        {
            HideRangeLabel();
            return;
        }

        TextMeshProUGUI? label = EnsureRangeLabel();
        Camera mainCamera = Utils.GetMainCamera();
        if (label == null || mainCamera == null)
        {
            HideRangeLabel();
            return;
        }

        Vector3 worldPoint = ResolveRangeLabelWorldPoint(ghost, terrainOps);
        Vector3 screenPoint = mainCamera.WorldToScreenPointScaled(worldPoint);
        bool visible = screenPoint.z > 0f &&
                       screenPoint.x >= 0f &&
                       screenPoint.x <= Screen.width &&
                       screenPoint.y >= 0f &&
                       screenPoint.y <= Screen.height;
        label.gameObject.SetActive(visible);
        if (!visible)
        {
            return;
        }

        label.text = GroundworkLocalization.Format("groundwork_terrain_range_label", "r: {0}m", FormatRange(range));
        label.rectTransform.position = screenPoint;
    }

    private static Vector3 ResolveRangeLabelWorldPoint(GameObject ghost, IReadOnlyList<TerrainOp> terrainOps)
    {
        if (IsGridRangePreviewEnabled() &&
            TryResolveSnappedRangePreviewWorldPoint(terrainOps, 0.08f, out Vector3 snappedWorldPoint))
        {
            return snappedWorldPoint;
        }

        foreach (TerrainOp terrainOp in terrainOps)
        {
            if (terrainOp != null)
            {
                return terrainOp.transform.position + Vector3.up * 0.08f;
            }
        }

        return ghost != null
            ? ghost.transform.position + Vector3.up * 0.08f
            : Vector3.zero;
    }

    private static TextMeshProUGUI? EnsureRangeLabel()
    {
        if (RangeLabelText != null && RangeLabelObject != null)
        {
            return RangeLabelText;
        }

        if (Hud.instance == null || Hud.instance.m_rootObject == null)
        {
            return null;
        }

        RangeLabelObject = new GameObject("Groundwork_TerrainToolRangeLabel");
        RangeLabelObject.transform.SetParent(Hud.instance.m_rootObject.transform, false);

        RectTransform rectTransform = RangeLabelObject.AddComponent<RectTransform>();
        rectTransform.anchorMin = new Vector2(0f, 0f);
        rectTransform.anchorMax = new Vector2(0f, 0f);
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.sizeDelta = new Vector2(100f, 26f);

        RangeLabelText = RangeLabelObject.AddComponent<TextMeshProUGUI>();
        TMP_Text? sourceText = Hud.instance.m_hoverName != null
            ? Hud.instance.m_hoverName
            : Hud.instance.m_pieceDescription;
        if (sourceText != null)
        {
            RangeLabelText.font = sourceText.font;
            RangeLabelText.fontSharedMaterial = sourceText.fontSharedMaterial;
        }

        RangeLabelText.alignment = TextAlignmentOptions.Center;
        RangeLabelText.color = new Color(1f, 0.95f, 0.78f, 0.96f);
        RangeLabelText.fontSize = 18f;
        RangeLabelText.richText = false;
        RangeLabelText.textWrappingMode = TextWrappingModes.NoWrap;
        RangeLabelText.raycastTarget = false;

        Shadow shadow = RangeLabelObject.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
        shadow.effectDistance = new Vector2(1.25f, -1.25f);

        return RangeLabelText;
    }

    private static void ClearRangePreview()
    {
        ResetScaledPlacementGhost();
        ClearCachedTerrainOps();
        ClearCustomRangePreview();
        HideRangeLabel();
    }

    private static void HideRangeLabel()
    {
        if (RangeLabelObject != null)
        {
            RangeLabelObject.SetActive(false);
        }
    }

    private static bool IsPlacementButtonDown()
    {
        return ZInput.GetButtonDown("Attack") || ZInput.GetButtonDown("JoyPlace");
    }

    private static float GetScaledPlaceDurability(Player player, ItemDrop.ItemData tool, TerrainToolRule rule, float range)
    {
        if (tool?.m_shared == null)
        {
            return 0f;
        }

        float durability = tool.m_shared.m_useDurabilityDrain;
        if (tool.m_shared.m_placementDurabilitySkill != Skills.SkillType.None)
        {
            float skillFactor = player.GetSkillFactor(tool.m_shared.m_placementDurabilitySkill);
            durability -= durability * tool.m_shared.m_placementDurabilityMax * skillFactor;
        }

        return durability * rule.GetDurabilityMultiplier(range);
    }

    private static bool SuppressCameraZoomThisFrame { get; set; }

    private static bool IsRangeModifierHeld()
    {
        return GroundworkToolsDomain.ToolWheelModifierHotkey.IsKeyHeld();
    }

    private static string FormatRangeModifierShortcut()
    {
        return FormatShortcut(GroundworkToolsDomain.ToolWheelModifierHotkey);
    }

    private static string FormatPreviewToggleHint()
    {
        string shortcut = FormatShortcut(GroundworkToolsDomain.TerrainToolPreviewToggleHotkey);
        return shortcut.Length == 0
            ? GroundworkLocalization.Text("groundwork_terrain_preview_toggle_unbound", "Preview toggle hotkey is unbound")
            : GroundworkLocalization.Format("groundwork_terrain_preview_toggle_hint", "{0}: Toggle Preview", shortcut);
    }

    private static string FormatRange(float range)
    {
        return range.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static string FormatShortcut(KeyboardShortcut shortcut)
    {
        if (shortcut.MainKey == KeyCode.None)
        {
            return "";
        }

        List<string> parts = shortcut.Modifiers.Select(modifier => modifier.ToString()).ToList();
        parts.Add(shortcut.MainKey.ToString());
        return string.Join(" + ", parts);
    }

    internal static void ClearCameraZoomSuppression()
    {
        SuppressCameraZoomThisFrame = false;
    }

    private static void CleanupExpiredPendingCosts()
    {
        int currentFrame = Time.frameCount;
        foreach (Player player in PendingPlacementCosts.Keys.ToList())
        {
            if (!PendingPlacementCosts[player].IsValidForFrame(currentFrame))
            {
                PendingPlacementCosts.Remove(player);
            }
        }
    }

    private static void WarnOnce(string key, string message)
    {
        if (ReportedWarnings.Add($"terrain_tool:{key}"))
        {
            GroundworkPlugin.ModLogger.LogWarning(message);
        }
    }

    internal sealed class TerrainOpSettingsState
    {
        private TerrainOpSettingsState(TerrainOp.Settings settings)
        {
            Smooth = settings.m_smooth;
            LevelRadius = settings.m_levelRadius;
            RaiseRadius = settings.m_raiseRadius;
            SmoothRadius = settings.m_smoothRadius;
            PaintRadius = settings.m_paintRadius;
        }

        private bool Smooth { get; }

        private float LevelRadius { get; }

        private float RaiseRadius { get; }

        private float SmoothRadius { get; }

        private float PaintRadius { get; }

        internal static TerrainOpSettingsState Capture(TerrainOp.Settings settings)
        {
            return new TerrainOpSettingsState(settings);
        }

        internal void Restore(TerrainOp.Settings settings)
        {
            settings.m_smooth = Smooth;
            settings.m_levelRadius = LevelRadius;
            settings.m_raiseRadius = RaiseRadius;
            settings.m_smoothRadius = SmoothRadius;
            settings.m_paintRadius = PaintRadius;
        }
    }

    internal sealed class PieceInfoState
    {
        private readonly Piece _piece;
        private readonly string _description;

        internal PieceInfoState(Piece piece)
        {
            _piece = piece;
            _description = piece.m_description;
        }

        internal void Restore()
        {
            if (_piece != null)
            {
                _piece.m_description = _description;
            }
        }
    }

    private sealed class ObjectDbState
    {
        internal Dictionary<Piece, Piece.Requirement[]> OriginalCosts { get; } = new();
    }

    private sealed class PreviewTransformScaleState
    {
        private readonly Transform _transform;
        private readonly Vector3 _baseLocalScale;

        internal PreviewTransformScaleState(Transform transform)
        {
            _transform = transform;
            _baseLocalScale = transform.localScale;
        }

        internal void Apply(float scale)
        {
            if (_transform == null)
            {
                return;
            }

            _transform.localScale = _baseLocalScale * scale;
        }

        internal void Restore()
        {
            if (_transform != null)
            {
                _transform.localScale = _baseLocalScale;
            }
        }
    }

    private sealed class PreviewVisualVisibilityState
    {
        private readonly Renderer _renderer;
        private readonly bool _enabled;

        internal PreviewVisualVisibilityState(Renderer renderer)
        {
            _renderer = renderer;
            _enabled = renderer.enabled;
        }

        internal void Restore()
        {
            if (_renderer != null)
            {
                _renderer.enabled = _enabled;
            }
        }

        internal void Hide()
        {
            if (_renderer != null)
            {
                _renderer.enabled = false;
            }
        }
    }

    private sealed class PendingPlacementCost
    {
        internal PendingPlacementCost(TerrainToolRule rule, float range, int frame)
        {
            Rule = rule;
            Range = range;
            Frame = frame;
        }

        internal TerrainToolRule Rule { get; }

        internal float Range { get; }

        private int Frame { get; }

        internal bool IsCurrentFrame => IsValidForFrame(Time.frameCount);

        internal bool IsValidForFrame(int frame)
        {
            return frame - Frame <= 1;
        }
    }

    private sealed class TerrainToolRuleTemplate
    {
        internal TerrainToolRuleTemplate(NormalizedTerrainToolConfig config)
        {
            ToolPrefabName = config.ToolPrefabName;
            PiecePrefabName = config.PiecePrefabName;
            HasCostOverride = config.HasCostOverride;
            Cost = config.Cost;
            RangeEnabled = config.RangeEnabled;
            MinRange = config.MinRange;
            MaxRange = config.MaxRange;
            DefaultRange = config.DefaultRange;
            MaterialCostFactor = config.MaterialCostFactor;
            StaminaCostFactor = config.StaminaCostFactor;
            DurabilityFactor = config.DurabilityFactor;
            Id = $"{ToolPrefabName}:{PiecePrefabName}";
        }

        internal string Id { get; }

        internal string ToolPrefabName { get; }

        internal string PiecePrefabName { get; }

        internal bool HasCostOverride { get; }

        internal IReadOnlyDictionary<string, int> Cost { get; }

        internal bool RangeEnabled { get; }

        internal float MinRange { get; }

        internal float MaxRange { get; }

        internal float DefaultRange { get; }

        internal float MaterialCostFactor { get; }

        internal float StaminaCostFactor { get; }

        internal float DurabilityFactor { get; }
    }

    private sealed class TerrainToolRule
    {
        private TerrainToolRule(
            TerrainToolRuleTemplate template,
            Piece piece,
            Piece.Requirement[] baseRequirements,
            float baseRange,
            float minRange,
            float maxRange,
            float defaultRange)
        {
            Id = template.Id;
            ToolPrefabName = template.ToolPrefabName;
            PiecePrefabName = template.PiecePrefabName;
            Piece = piece;
            BaseRequirements = baseRequirements;
            RangeEnabled = template.RangeEnabled;
            BaseRange = baseRange;
            MinRange = minRange;
            MaxRange = maxRange;
            DefaultRange = defaultRange;
            MaterialCostFactor = template.MaterialCostFactor;
            StaminaCostFactor = template.StaminaCostFactor;
            DurabilityFactor = template.DurabilityFactor;
        }

        internal string Id { get; }

        internal string ToolPrefabName { get; }

        internal string PiecePrefabName { get; }

        internal Piece Piece { get; }

        internal Piece.Requirement[] BaseRequirements { get; }

        internal bool RangeEnabled { get; }

        private float BaseRange { get; }

        internal float MinRange { get; }

        internal float MaxRange { get; }

        internal float DefaultRange { get; }

        internal float VanillaRange => Mathf.Clamp(BaseRange, MinRange, MaxRange);

        private float MaterialCostFactor { get; }

        private float StaminaCostFactor { get; }

        private float DurabilityFactor { get; }

        internal static TerrainToolRule Create(TerrainToolRuleTemplate template, Piece piece, float detectedBaseRange, Piece.Requirement[] baseRequirements)
        {
            float baseRange = detectedBaseRange > 0.05f
                ? detectedBaseRange
                : template.DefaultRange > 0.05f
                    ? template.DefaultRange
                    : template.MinRange > 0.05f
                        ? template.MinRange
                        : 1f;
            float minRange = template.MinRange > 0.05f ? template.MinRange : baseRange;
            float maxRange = template.MaxRange > 0.05f ? template.MaxRange : Math.Max(minRange, baseRange);
            if (maxRange < minRange)
            {
                maxRange = minRange;
            }

            float defaultRange = template.DefaultRange > 0.05f ? template.DefaultRange : baseRange;
            defaultRange = Mathf.Clamp(defaultRange, minRange, maxRange);
            return new TerrainToolRule(template, piece, baseRequirements, baseRange, minRange, maxRange, defaultRange);
        }

        internal float GetMaterialCostMultiplier(float range)
        {
            return GetCostMultiplier(range, MaterialCostFactor);
        }

        internal float GetStaminaCostMultiplier(float range)
        {
            return GetCostMultiplier(range, StaminaCostFactor);
        }

        internal float GetDurabilityMultiplier(float range)
        {
            return GetCostMultiplier(range, DurabilityFactor);
        }

        internal float GetVisualRangeScale(float range)
        {
            return Mathf.Max(0.01f, Math.Max(0f, range) / Math.Max(0.01f, BaseRange));
        }

        private float GetCostMultiplier(float range, float factor)
        {
            if (!RangeEnabled || factor <= 0f)
            {
                return 1f;
            }

            float rangeRatio = Math.Max(0f, range) / Math.Max(0.01f, BaseRange);
            float areaRatio = rangeRatio * rangeRatio;
            float increase = Math.Max(0f, areaRatio - 1f);
            return 1f + increase * factor;
        }
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.SetPlaceMode))]
internal static class PlayerSetPlaceModeTerrainToolRangePatch
{
    private static void Postfix(Player __instance)
    {
        TerrainToolRangeSystem.ApplyToPlayerBuildPieces(__instance);
    }
}

[HarmonyPatch(typeof(TerrainOp), "Awake")]
internal static class TerrainOpAwakeTerrainToolRangePatch
{
    private static void Prefix(TerrainOp __instance, out TerrainToolRangeSystem.TerrainOpSettingsState? __state)
    {
        __state = TerrainToolRangeSystem.PrepareTerrainOp(__instance);
    }

    private static void Finalizer(TerrainOp __instance, TerrainToolRangeSystem.TerrainOpSettingsState? __state)
    {
        TerrainToolRangeSystem.RestoreTerrainOp(__instance, __state);
    }
}

[HarmonyPatch(typeof(Hud), "SetupPieceInfo")]
internal static class HudSetupPieceInfoTerrainToolRangePatch
{
    private static void Prefix(Piece piece, out PieceInfoPatchState __state)
    {
        __state = new PieceInfoPatchState
        {
            TerrainToolRange = TerrainToolRangeSystem.PreparePieceInfo(piece),
            MassPlanting = MassPlantingSystem.PreparePieceInfo(piece)
        };
    }

    private static void Finalizer(PieceInfoPatchState __state)
    {
        MassPlantingSystem.RestorePieceInfo(__state.MassPlanting);
        TerrainToolRangeSystem.RestorePieceInfo(__state.TerrainToolRange);
    }

    private sealed class PieceInfoPatchState
    {
        internal TerrainToolRangeSystem.PieceInfoState? TerrainToolRange;
        internal MassPlantingSystem.PieceInfoState? MassPlanting;
    }
}

[HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
internal static class GameCameraUpdateCameraTerrainToolRangePatch
{
    private static void Prefix()
    {
        CameraZoomInputSuppressionSystem.BeginGameCameraUpdate();
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMouseScrollWheel = AccessTools.Method(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel));
        var getMouseScrollWheelForCamera = AccessTools.Method(typeof(CameraZoomInputSuppressionSystem), nameof(CameraZoomInputSuppressionSystem.GetMouseScrollWheelForCamera));
        var inputGetAxis = AccessTools.Method(typeof(Input), nameof(Input.GetAxis), [typeof(string)]);
        var inputGetAxisForCamera = AccessTools.Method(typeof(CameraZoomInputSuppressionSystem), nameof(CameraZoomInputSuppressionSystem.GetAxisForCamera));

        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(getMouseScrollWheel))
            {
                instruction.operand = getMouseScrollWheelForCamera;
                yield return instruction;
                continue;
            }

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

    internal static void BeginGameCameraUpdate()
    {
        InsideGameCameraUpdate = true;
    }

    internal static void EndGameCameraUpdate()
    {
        InsideGameCameraUpdate = false;
    }

    internal static float GetMouseScrollWheelForCamera()
    {
        float scroll = ZInput.GetMouseScrollWheel();
        return ShouldSuppressCameraZoomInput() ? 0f : scroll;
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
internal static class ZInputGetMouseScrollWheelCameraZoomSuppressionPatch
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

[HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), typeof(Piece), typeof(Player.RequirementMode))]
internal static class PlayerHaveRequirementsTerrainToolRangePatch
{
    private static void Postfix(Player __instance, Piece piece, Player.RequirementMode mode, ref bool __result)
    {
        TerrainToolRangeSystem.CheckDynamicRequirements(__instance, piece, mode, ref __result);
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.HaveStamina))]
internal static class CharacterHaveStaminaTerrainToolRangePatch
{
    private static void Postfix(Character __instance, ref bool __result)
    {
        TerrainToolRangeSystem.CheckDynamicStamina(__instance, ref __result);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.GetBuildStamina))]
internal static class PlayerGetBuildStaminaTerrainToolRangePatch
{
    private static void Postfix(Player __instance, ref float __result)
    {
        TerrainToolRangeSystem.ApplyDynamicBuildStamina(__instance, ref __result);
    }
}

[HarmonyPatch(typeof(Player), "GetPlaceDurability")]
internal static class PlayerGetPlaceDurabilityTerrainToolRangePatch
{
    private static void Postfix(Player __instance, ItemDrop.ItemData tool, ref float __result)
    {
        TerrainToolRangeSystem.ApplyDynamicPlaceDurability(__instance, tool, ref __result);
    }
}
