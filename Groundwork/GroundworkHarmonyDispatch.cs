using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

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
        GrowthOverrideSystem.CheckCultivationPlacementBiome(__instance);
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
        PickedVisualSystem.InvalidateCache();
        GrowthOverrideSystem.OnZNetSceneReady(__instance);
        CultivationSystem.OnZNetSceneReady(__instance);
        ScytheHarvestSystem.RefreshCultivatedPickables(__instance);
        FarmingSkillSystem.RefreshForagingBonusEffectFallback(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
internal static class PlayerPlacePieceGroundworkPatch
{
    private static bool _recordPlantPlanter;
    private static bool _recordBeehiveBuilder;
    internal static Player? PlacingPlayer { get; private set; }
    internal static Player? PlantPlanter => _recordPlantPlanter ? PlacingPlayer : null;
    internal static Player? BeehiveBuilder => _recordBeehiveBuilder ? PlacingPlayer : null;

    private static void Prefix(Player __instance, Piece piece)
    {
        PlacingPlayer = __instance;
        // Keep each feature's eligibility at the same point before PlacePiece runs.
        _recordPlantPlanter = GroundworkToolsDomain.PlantGrowFeatureEnabled;
        _recordBeehiveBuilder = __instance != null &&
                                piece != null &&
                                GroundworkToolsDomain.BeehiveCapacityFarmingLevelsPerBonusHoney > 0 &&
                                piece.GetComponentInChildren<Beehive>(includeInactive: true) != null;
    }

    private static void Finalizer()
    {
        ClearContext();
    }

    internal static void ClearContext()
    {
        PlacingPlayer = null;
        _recordPlantPlanter = false;
        _recordBeehiveBuilder = false;
    }
}

[HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
internal static class PieceSetCreatorCultivationPatch
{
    private static void Postfix(Piece __instance)
    {
        Player? player = PlayerPlacePieceGroundworkPatch.PlacingPlayer;
        if (player == null)
        {
            return;
        }

        Pickable? pickable = __instance.GetComponent<Pickable>();
        if (pickable != null)
        {
            MassPlantingSystem.TrySynchronizePendingPickable(pickable);
            CultivationSystem.InitializePlacedPickable(__instance, player);
        }
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

[HarmonyPatch(typeof(Hud), "Update")]
internal static class HudUpdateGroundworkPatch
{
    private static void Postfix(Hud __instance)
    {
        TerrainToolRangeSystem.UpdateAimHeightHud(__instance);
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

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix()
    {
        // Vanilla reuses rows, so the previous Farming row may represent another skill after Setup.
        FarmingSkillTooltipPosition.Clear();
    }

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

            FarmingSkillTooltipPosition.Bind(__instance, tooltip);
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

[HarmonyPatch(typeof(SkillsDialog), nameof(SkillsDialog.OnClose))]
internal static class FarmingSkillTooltipClosePatch
{
    private static void Postfix(SkillsDialog __instance)
    {
        FarmingSkillTooltipPosition.ClearForDialog(__instance);
    }
}

[HarmonyPatch(typeof(UITooltip), "LateUpdate")]
internal static class FarmingSkillTooltipPositionPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(UITooltip __instance)
    {
        FarmingSkillTooltipPosition.UpdateVisibleTooltip(__instance);
    }
}

internal static class FarmingSkillTooltipPosition
{
    // Match the vanilla SkillListTooltip body width; padding belongs to this runtime instance only.
    internal const float BodyWidth = 250f;
    internal const float Padding = 16f;
    internal const float Width = BodyWidth + Padding * 2f;
    private const float Gap = 8f;
    private const float ScreenMargin = 12f;
    private static readonly Vector3[] Corners = new Vector3[4];
    private static Binding? _binding;
    private static bool _failureLogged;

    internal static void Bind(SkillsDialog dialog, UITooltip tooltip)
    {
        if (dialog == null || tooltip == null || dialog.m_listRoot == null)
        {
            return;
        }

        RectTransform? row = null;
        for (Transform current = tooltip.transform; current != null && current != dialog.m_listRoot; current = current.parent)
        {
            if (current.parent == dialog.m_listRoot)
            {
                row = current as RectTransform;
                break;
            }
        }

        // The description fallback searches the inventory, but only an actual Skills row is repositioned.
        if (row == null)
        {
            return;
        }

        RectTransform? panel = null;
        for (Transform current = dialog.m_listRoot.parent; current != null && current != dialog.transform; current = current.parent)
        {
            if (current.parent == dialog.transform && current is RectTransform frame)
            {
                // SkillsDialog spans the screen; the visible frame's backdrop defines its left edge.
                panel = frame.Find("bkg") as RectTransform ?? frame;
                break;
            }
        }

        Canvas? canvas = tooltip.GetComponentInParent<Canvas>();
        if (panel == null || canvas == null || canvas.transform is not RectTransform canvasRect)
        {
            return;
        }

        Clear();
        _binding = new Binding(dialog, tooltip, panel, row, canvas, canvasRect);
        // Gamepad tooltips otherwise inherit the scroll viewport's mask through their vanilla anchor.
        tooltip.m_anchor = canvasRect;
        tooltip.m_fixedPosition = Vector2.zero;
    }

    internal static void Clear()
    {
        Binding? binding = _binding;
        _binding = null;
        if (binding?.Tooltip == null)
        {
            return;
        }

        binding.Tooltip.m_anchor = binding.OriginalAnchor;
        binding.Tooltip.m_fixedPosition = binding.OriginalFixedPosition;
        // UITooltip owns a shared root. Never hide the tooltip currently owned by another skill or item.
        if (UITooltip.m_current == binding.Tooltip)
        {
            UITooltip.HideTooltip();
        }
    }

    internal static void ClearForDialog(SkillsDialog dialog)
    {
        if (_binding?.Dialog == dialog)
        {
            Clear();
        }
    }

    internal static void UpdateVisibleTooltip(UITooltip tooltip)
    {
        Binding? binding = _binding;
        if (binding == null || binding.Tooltip != tooltip || UITooltip.m_current != tooltip || UITooltip.m_tooltip == null)
        {
            return;
        }

        try
        {
            if (binding.Dialog == null || !binding.Dialog.isActiveAndEnabled || binding.Panel == null ||
                binding.Row == null || !binding.Row.gameObject.activeInHierarchy ||
                binding.Canvas == null || binding.CanvasRect == null)
            {
                Clear();
                return;
            }

            GameObject root = UITooltip.m_tooltip;
            if (!root.activeSelf)
            {
                return; // Preserve vanilla's hover delay and pointer-exit handling.
            }

            if (binding.View == null || binding.View.Root != root)
            {
                binding.View = TooltipView.Create(root, binding.CanvasRect);
                if (binding.View == null)
                {
                    Clear();
                    return;
                }
            }

            Camera? camera = binding.Canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : binding.Canvas.worldCamera;
            Rect safeArea = Screen.safeArea;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(binding.CanvasRect, safeArea.min, camera, out Vector2 screenMin) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(binding.CanvasRect, safeArea.max, camera, out Vector2 screenMax))
            {
                Clear();
                return;
            }

            Rect viewport = Rect.MinMaxRect(screenMin.x, screenMin.y, screenMax.x, screenMax.y);
            if (viewport.width <= 0f || viewport.height <= 0f)
            {
                Clear();
                return;
            }

            Rect panelBounds = GetCanvasBounds(binding.Panel, binding.CanvasRect);
            Rect rowBounds = GetCanvasBounds(binding.Row, binding.CanvasRect);
            binding.View.UpdateLayout();
            float scale = GetScale(viewport, binding.View.Size);
            Vector2 topLeft = GetTopLeft(viewport, panelBounds, rowBounds.yMax, binding.View.Size * scale);
            binding.View.Panel.localScale = new Vector3(scale, scale, 1f);
            // Vanilla clamps the first child; absolute placement prevents its prior offset accumulating.
            binding.View.Panel.position = binding.CanvasRect.TransformPoint(new Vector3(topLeft.x, topLeft.y, 0f));
        }
        catch (Exception exception)
        {
            Clear();
            if (!_failureLogged)
            {
                _failureLogged = true;
                GroundworkPlugin.ModLogger.LogWarning(
                    "Could not position the Farming skill tooltip: " + exception.GetBaseException().Message);
            }
        }
    }

    internal static float GetScale(Rect viewport, Vector2 size)
    {
        float widthScale = Mathf.Max(1f, viewport.width - ScreenMargin * 2f) / Mathf.Max(1f, size.x);
        float heightScale = Mathf.Max(1f, viewport.height - ScreenMargin * 2f) / Mathf.Max(1f, size.y);
        return Mathf.Min(1f, Mathf.Min(widthScale, heightScale));
    }

    internal static Vector2 GetTopLeft(Rect viewport, Rect skillPanel, float rowTop, Vector2 size)
    {
        float minX = viewport.xMin + ScreenMargin;
        float maxY = viewport.yMax - ScreenMargin;
        float x = Mathf.Clamp(skillPanel.xMin - Gap - size.x, minX,
            Mathf.Max(minX, viewport.xMax - ScreenMargin - size.x));
        float y = Mathf.Clamp(rowTop, Mathf.Min(maxY, viewport.yMin + ScreenMargin + size.y), maxY);
        return new Vector2(x, y);
    }

    private static Rect GetCanvasBounds(RectTransform rect, RectTransform canvas)
    {
        rect.GetWorldCorners(Corners);
        Vector2 min = canvas.InverseTransformPoint(Corners[0]);
        Vector2 max = min;
        for (int index = 1; index < Corners.Length; index++)
        {
            Vector2 point = canvas.InverseTransformPoint(Corners[index]);
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }

        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private sealed class Binding
    {
        internal readonly SkillsDialog Dialog;
        internal readonly UITooltip Tooltip;
        internal readonly RectTransform Panel;
        internal readonly RectTransform Row;
        internal readonly Canvas Canvas;
        internal readonly RectTransform CanvasRect;
        internal readonly RectTransform? OriginalAnchor;
        internal readonly Vector2 OriginalFixedPosition;
        internal TooltipView? View;

        internal Binding(SkillsDialog dialog, UITooltip tooltip, RectTransform panel, RectTransform row,
            Canvas canvas, RectTransform canvasRect)
        {
            Dialog = dialog;
            Tooltip = tooltip;
            Panel = panel;
            Row = row;
            Canvas = canvas;
            CanvasRect = canvasRect;
            OriginalAnchor = tooltip.m_anchor;
            OriginalFixedPosition = tooltip.m_fixedPosition;
        }
    }

    private sealed class TooltipView
    {
        private const float TopicGap = 8f;
        internal readonly GameObject Root;
        internal readonly RectTransform Panel;
        private readonly TMP_Text _body;
        private readonly TMP_Text? _topic;
        private string? _bodyText;
        private string? _topicText;
        internal Vector2 Size => Panel.sizeDelta;

        private TooltipView(GameObject root, RectTransform panel, TMP_Text body, TMP_Text? topic)
        {
            Root = root;
            Panel = panel;
            _body = body;
            _topic = topic;
        }

        internal static TooltipView? Create(GameObject root, RectTransform canvas)
        {
            TMP_Text? body = Utils.FindChild(root.transform, "Text")?.GetComponent<TMP_Text>();
            TMP_Text? topic = Utils.FindChild(root.transform, "Topic")?.GetComponent<TMP_Text>();
            if (body == null || body.font == null || body.transform == root.transform)
            {
                return null;
            }

            root.transform.SetParent(canvas, false);
            root.transform.localScale = Vector3.one;
            root.transform.localRotation = Quaternion.identity;
            DisableAutomaticLayout(root);
            CanvasGroup group = root.GetComponent<CanvasGroup>() ?? root.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            GameObject panelObject = new("Groundwork_FarmingSkillTooltip", typeof(RectTransform), typeof(Image));
            panelObject.layer = root.layer;
            RectTransform panel = (RectTransform)panelObject.transform;
            panel.SetParent(root.transform, false);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0f, 1f);
            Image background = panelObject.GetComponent<Image>();
            background.color = new Color(0.055f, 0.045f, 0.04f, 0.94f);
            background.raycastTarget = false;

            PrepareText(body, panel, TextAlignmentOptions.TopLeft);
            if (topic != null && topic != body)
            {
                PrepareText(topic, panel, TextAlignmentOptions.Top);
            }

            // Preserve the cloned text/font/material while replacing only this instance's layout and backdrop.
            for (int index = 0; index < root.transform.childCount; index++)
            {
                Transform child = root.transform.GetChild(index);
                if (child != panel)
                {
                    child.gameObject.SetActive(false);
                }
            }

            panel.SetAsFirstSibling();
            return new TooltipView(root, panel, body, topic == body ? null : topic);
        }

        private static void PrepareText(TMP_Text text, RectTransform panel, TextAlignmentOptions alignment)
        {
            DisableAutomaticLayout(text.gameObject);
            RectTransform rect = text.rectTransform;
            rect.SetParent(panel, false);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.enableAutoSizing = false;
            text.margin = Vector4.zero;
            text.raycastTarget = false;
            text.gameObject.SetActive(true);
        }

        private static void DisableAutomaticLayout(GameObject target)
        {
            LayoutGroup? layout = target.GetComponent<LayoutGroup>();
            ContentSizeFitter? fitter = target.GetComponent<ContentSizeFitter>();
            AspectRatioFitter? aspect = target.GetComponent<AspectRatioFitter>();
            if (layout != null) layout.enabled = false;
            if (fitter != null) fitter.enabled = false;
            if (aspect != null) aspect.enabled = false;
        }

        internal void UpdateLayout()
        {
            if (_bodyText == _body.text && _topicText == _topic?.text)
            {
                return;
            }

            _bodyText = _body.text;
            _topicText = _topic?.text;
            float y = Padding;
            if (_topic != null)
            {
                bool hasTopic = !string.IsNullOrWhiteSpace(_topicText);
                _topic.gameObject.SetActive(hasTopic);
                if (hasTopic)
                {
                    y += SetTextRect(_topic, y) + TopicGap;
                }
            }

            y += SetTextRect(_body, y);
            Panel.sizeDelta = new Vector2(Width, y + Padding);
        }

        private static float SetTextRect(TMP_Text text, float y)
        {
            float height = Mathf.Max(1f, Mathf.Ceil(text.GetPreferredValues(text.text, BodyWidth, float.PositiveInfinity).y));
            text.rectTransform.anchoredPosition = new Vector2(Padding, -y);
            text.rectTransform.sizeDelta = new Vector2(BodyWidth, height);
            return height;
        }
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
        GroundworkInputIcons.Initialize(__instance);
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
