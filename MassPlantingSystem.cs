using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Groundwork;

internal static class MassPlantingSystem
{
    private const float GroupRotationStepDegrees = 15f;
    private static readonly int[] MassPlantCountOptions = [5, 10, 15, 20, 25];
    private static readonly int OriginalGhostColorProperty = Shader.PropertyToID("_Color");
    private static readonly int OriginalGhostEmissionColorProperty = Shader.PropertyToID("_EmissionColor");
    private static readonly Collider[] SpaceHits = new Collider[128];
    private static readonly List<PlantSlot> PlantSlots = [];
    private static readonly List<PlantPreviewGhost> PreviewGhosts = [];
    private static readonly MethodInfo? UpdatePlacementGhostMethod = AccessTools.Method(typeof(Player), "UpdatePlacementGhost", [typeof(bool)]);
    private static readonly MethodInfo? GetBuildStaminaMethod = AccessTools.Method(typeof(Player), "GetBuildStamina");
    private static readonly MethodInfo? GetPlaceDurabilityMethod = AccessTools.Method(typeof(Player), "GetPlaceDurability", [typeof(ItemDrop.ItemData)]);
    private static readonly FieldInfo? PlacementGhostField = AccessTools.Field(typeof(Player), "m_placementGhost");

    private static bool _gridPlantingMode;
    private static bool _placingBatch;
    private static bool _showingMassBuildHints;
    private static int _selectedPlantCount;
    private static string? _activePlantSelectionKey;
    private static Vector3 _activePlantGroupRight;
    private static Vector3 _activePlantGroupForward;
    private static bool _gridPlantGroupRotated;
    private static bool _suppressPlayerUpdateMouseWheel;
    private static bool _hasCapturedPlayerUpdateScroll;
    private static float _capturedPlayerUpdateScroll;
    private static int _spaceMask;
    private static int _defaultLayer = -1;
    private static int _staticSolidLayer = -1;
    private static int _defaultSmallLayer = -1;
    private static int _pieceLayer = -1;
    private static int _pieceNonSolidLayer = -1;
    private static KeyHintCell? _fallbackBuildHint;
    private static TextMeshProUGUI? _gridHint;
    private static TextMeshProUGUI? _copyHint;
    private static KeyHints? _activeKeyHints;
    private static KeyHintCell? _gridHintSlot;
    private static KeyHintCell? _cycleHintSlot;
    private static KeyHintCell? _massHintSlot;
    private static GameObject? _limitLabelObject;
    private static TextMeshProUGUI? _limitLabelText;
    private static string? _previewSourceName;
    private static GameObject? _hiddenOriginalGhost;
    private static GameObject? _invalidOriginalGhost;
    private static readonly Dictionary<TextMeshProUGUI, string> OriginalHintTexts = [];
    private static readonly Dictionary<Renderer, bool> OriginalGhostRendererStates = [];
    private static readonly Dictionary<Renderer, MaterialPropertyBlock> OriginalGhostPropertyBlocks = [];
    private static readonly MaterialPropertyBlock InvalidOriginalGhostPropertyBlock = new();

    private enum PlacementFailure
    {
        None,
        NoBuildZone,
        PrivateZone,
        NeedCultivated,
        NeedDirt,
        WrongBiome,
        NoGround,
        MoreSpace,
        Invalid
    }

    private readonly struct PlantSlot(Vector3 offset, int row, int column, int rows, int columns)
    {
        internal readonly Vector3 Offset = offset;
        internal readonly int Row = row;
        internal readonly int Column = column;
        internal readonly int Rows = rows;
        internal readonly int Columns = columns;
        internal float Distance => Offset.sqrMagnitude;
    }

    private readonly struct PlacementCapacity(int count, int resources, int stamina, int durability)
    {
        internal readonly int Count = count;
        internal readonly int Resources = resources;
        internal readonly int Stamina = stamina;
        internal readonly int Durability = durability;
    }

    private sealed class PlantPreviewGhost
    {
        private static readonly int ColorProperty = Shader.PropertyToID("_Color");
        private static readonly int EmissionColorProperty = Shader.PropertyToID("_EmissionColor");
        private readonly List<Renderer> _renderers = [];
        private readonly MaterialPropertyBlock _propertyBlock = new();

        internal PlantPreviewGhost(GameObject sourceGhost, int index)
        {
            Root = new GameObject($"Groundwork_MassPlantPreview_{index}");
            Root.layer = sourceGhost.layer;
            Root.transform.SetPositionAndRotation(sourceGhost.transform.position, sourceGhost.transform.rotation);
            Root.transform.localScale = sourceGhost.transform.lossyScale;
            CopyMeshRenderers(sourceGhost);
            Root.SetActive(false);
        }

        internal GameObject Root { get; }
        internal bool HasRenderers => _renderers.Count > 0;

        internal void SetActive(bool active)
        {
            if (Root != null)
            {
                Root.SetActive(active);
            }
        }

        internal void SetPositionAndRotation(Vector3 position, Quaternion rotation)
        {
            if (Root != null)
            {
                Root.transform.SetPositionAndRotation(position, rotation);
            }
        }

        internal void SetInvalid(bool invalid)
        {
            foreach (Renderer renderer in _renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                if (!invalid)
                {
                    renderer.SetPropertyBlock(null);
                    continue;
                }

                _propertyBlock.Clear();
                _propertyBlock.SetColor(ColorProperty, new Color(1f, 0.15f, 0.1f, 0.55f));
                _propertyBlock.SetColor(EmissionColorProperty, new Color(1f, 0.05f, 0.02f, 0.4f));
                renderer.SetPropertyBlock(_propertyBlock);
            }
        }

        internal void Destroy()
        {
            if (Root != null)
            {
                Object.Destroy(Root);
            }
        }

        private void CopyMeshRenderers(GameObject sourceGhost)
        {
            MeshRenderer[] sourceRenderers = sourceGhost.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
            foreach (MeshRenderer sourceRenderer in sourceRenderers)
            {
                MeshFilter sourceFilter = sourceRenderer.GetComponent<MeshFilter>();
                if (sourceFilter == null || sourceFilter.sharedMesh == null)
                {
                    continue;
                }

                GameObject child = new(sourceRenderer.gameObject.name);
                child.layer = sourceRenderer.gameObject.layer;
                child.transform.SetPositionAndRotation(sourceRenderer.transform.position, sourceRenderer.transform.rotation);
                child.transform.localScale = sourceRenderer.transform.lossyScale;
                child.transform.SetParent(Root.transform, worldPositionStays: true);

                MeshFilter filter = child.AddComponent<MeshFilter>();
                filter.sharedMesh = sourceFilter.sharedMesh;

                MeshRenderer renderer = child.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = sourceRenderer.sharedMaterials;
                renderer.shadowCastingMode = sourceRenderer.shadowCastingMode;
                renderer.receiveShadows = sourceRenderer.receiveShadows;
                renderer.lightProbeUsage = sourceRenderer.lightProbeUsage;
                renderer.reflectionProbeUsage = sourceRenderer.reflectionProbeUsage;
                _renderers.Add(renderer);
            }
        }
    }

    // Placement interception and preview.
    internal static bool TryInterceptPlace(Player player, Piece piece, ref bool result)
    {
        if (_placingBatch ||
            !TryGetPlant(piece, out Plant? plantCandidate) ||
            UpdatePlacementGhostMethod == null ||
            PlacementGhostField == null)
        {
            return true;
        }

        Plant plant = plantCandidate!;

        int currentPlantCount = GetCurrentPlantCount(player, piece);
        bool wantsMassPlant = currentPlantCount > 1;
        bool wantsGridSnap = _gridPlantingMode;
        if (!wantsMassPlant && !wantsGridSnap)
        {
            return true;
        }

        UpdatePlacementGhostMethod.Invoke(player, [true]);
        if (player.GetPlacementStatus() != Player.PlacementStatus.Valid)
        {
            return true;
        }

        GameObject? ghost = PlacementGhostField.GetValue(player) as GameObject;
        if (ghost == null)
        {
            return true;
        }

        Vector3 basePosition = ghost.transform.position;
        Quaternion rotation = ghost.transform.rotation;
        float spacing = ResolveSpacing(plant);
        if (wantsGridSnap)
        {
            basePosition = SnapToGrid(basePosition, spacing);
            ghost.transform.position = basePosition;
        }

        int wantedCount = wantsMassPlant ? currentPlantCount : 1;
        PlacementCapacity capacity = ResolvePlacementCapacity(player, piece, wantedCount);
        int placeLimit = capacity.Count;
        if (placeLimit <= 0)
        {
            result = false;
            return false;
        }

        BuildPlantSlots(wantedCount, spacing, wantsGridSnap);
        GetPlantingGroupAxes(player, ghost, wantsGridSnap, out Vector3 right, out Vector3 forward);
        PlacementFailure firstFailure = PlacementFailure.None;
        int placed = 0;

        _placingBatch = true;
        try
        {
            for (int i = 0; i < PlantSlots.Count; i++)
            {
                if (i >= placeLimit)
                {
                    break;
                }

                PlantSlot slot = PlantSlots[i];
                Vector3 position = basePosition + right * slot.Offset.x + forward * slot.Offset.z;
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(position, out float groundHeight))
                {
                    position.y = groundHeight;
                }

                PlacementFailure failure = ValidatePlantPosition(player, piece, plant, position);
                if (failure != PlacementFailure.None)
                {
                    if (firstFailure == PlacementFailure.None)
                    {
                        firstFailure = failure;
                    }

                    continue;
                }

                Quaternion plantRotation = ResolvePlantRotation(piece, rotation, basePosition, i, randomize: wantsMassPlant);
                ZLog.Log("Placed " + piece.gameObject.name);
                Game.instance?.IncrementPlayerStat(PlayerStatType.Builds);
                player.PlacePiece(piece, position, plantRotation, doAttack: placed == 0);
                placed++;
            }
        }
        finally
        {
            _placingBatch = false;
            PlantSlots.Clear();
        }

        if (placed <= 0)
        {
            ShowPlacementFailure(player, firstFailure);
            result = false;
            return false;
        }

        PayExtraPlacementCosts(player, piece, placed - 1);
        RaiseExtraMassPlantSkill(player, placed - 1);
        result = true;
        return false;
    }

    internal static void TrySnapPlacementGhost(Player player)
    {
        if (_placingBatch ||
            !_gridPlantingMode ||
            PlacementGhostField == null ||
            player.GetPlacementStatus() != Player.PlacementStatus.Valid ||
            !TryGetSelectedPlant(player, out _, out Plant? plantCandidate))
        {
            return;
        }

        Plant plant = plantCandidate!;

        GameObject? ghost = PlacementGhostField.GetValue(player) as GameObject;
        if (ghost == null)
        {
            return;
        }

        ghost.transform.position = SnapToGrid(ghost.transform.position, ResolveSpacing(plant));
    }

    internal static void UpdatePlacementPreview(Player player)
    {
        if (_placingBatch ||
            PlacementGhostField == null ||
            !TryGetSelectedPlant(player, out Piece? pieceCandidate, out Plant? plantCandidate))
        {
            ClearPlacementPreview();
            return;
        }

        Piece piece = pieceCandidate!;
        Plant plant = plantCandidate!;
        int currentPlantCount = GetCurrentPlantCount(player, piece);
        GameObject? ghost = PlacementGhostField.GetValue(player) as GameObject;
        if (ghost == null || !ghost.activeInHierarchy)
        {
            ClearPlacementPreview();
            return;
        }

        if (currentPlantCount <= 1)
        {
            UpdateSinglePlantPreview(player, piece, plant, ghost);
            return;
        }

        Vector3 basePosition = ghost.transform.position;
        Quaternion rotation = ghost.transform.rotation;
        float spacing = ResolveSpacing(plant);
        if (_gridPlantingMode)
        {
            basePosition = SnapToGrid(basePosition, spacing);
        }

        int wantedCount = currentPlantCount;
        PlacementCapacity capacity = ResolvePlacementCapacity(player, piece, wantedCount);
        BuildPlantSlots(wantedCount, spacing, _gridPlantingMode);

        GetPlantingGroupAxes(player, ghost, _gridPlantingMode, out Vector3 right, out Vector3 forward);

        EnsurePreviewGhosts(ghost, wantedCount);
        if (PreviewGhosts.Count == 0)
        {
            SetOriginalGhostVisualHidden(null, false);
            HideLimitLabel();
            return;
        }

        SetOriginalGhostInvalid(null, false);
        SetOriginalGhostVisualHidden(ghost, true);
        UpdateLimitLabel(basePosition, capacity, wantedCount);

        for (int i = 0; i < PreviewGhosts.Count; i++)
        {
            PlantPreviewGhost preview = PreviewGhosts[i];
            bool active = i < PlantSlots.Count;
            preview.SetActive(active);
            if (!active)
            {
                continue;
            }

            PlantSlot slot = PlantSlots[i];
            Vector3 position = basePosition + right * slot.Offset.x + forward * slot.Offset.z;
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(position, out float groundHeight))
            {
                position.y = groundHeight;
            }

            Quaternion plantRotation = ResolvePlantRotation(piece, rotation, basePosition, i, randomize: false);
            preview.SetPositionAndRotation(position, plantRotation);
            bool invalid = i >= capacity.Count || ValidatePlantPosition(player, piece, plant, position) != PlacementFailure.None;
            preview.SetInvalid(invalid);
        }
    }

    private static void UpdateSinglePlantPreview(Player player, Piece piece, Plant plant, GameObject ghost)
    {
        foreach (PlantPreviewGhost preview in PreviewGhosts)
        {
            preview.SetActive(false);
        }

        SetOriginalGhostVisualHidden(null, false);
        HideLimitLabel();

        Vector3 position = ghost.transform.position;
        if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(position, out float groundHeight))
        {
            position.y = groundHeight;
        }

        bool invalid = player.GetPlacementStatus() != Player.PlacementStatus.Valid ||
                       ValidatePlantPosition(player, piece, plant, position) != PlacementFailure.None;
        SetOriginalGhostInvalid(ghost, invalid);
    }

    // Input capture and build hint UI.
    internal static void UpdateInput(Player player)
    {
        if (player != Player.m_localPlayer)
        {
            ClearActivePlantSelection();
            return;
        }

        if (!player.InPlaceMode() ||
            Hud.IsPieceSelectionVisible() ||
            Hud.InRadial() ||
            InventoryGui.IsVisible() ||
            Menu.IsVisible() ||
            Console.IsVisible() ||
            (Chat.instance != null && Chat.instance.HasFocus()))
        {
            return;
        }

        if (!TryGetSelectedPlant(player, out Piece? pieceCandidate, out _))
        {
            if (pieceCandidate != null)
            {
                ClearActivePlantSelection();
            }

            return;
        }

        EnsureActivePlantSelection(player, pieceCandidate!);
        if (!TryHandleMassPlantWheel(player))
        {
            TryHandlePlantGroupRotationWheel(player);
        }

        if (GroundworkToolsDomain.ToggleGridPlantingHotkey.IsKeyDown())
        {
            _gridPlantingMode = !_gridPlantingMode;
        }
    }

    internal static bool ShouldSuppressCameraZoomInput()
    {
        Player? player = Player.m_localPlayer;
        if (player == null ||
            !player.InPlaceMode() ||
            !TryGetSelectedPlant(player, out Piece? piece, out _) ||
            piece == null)
        {
            return false;
        }

        return IsMassPlantingEnabled() && IsMassPlantWheelModifierHeld() ||
               GetCurrentPlantCount(player, piece) > 1;
    }

    internal static void BeginPlayerUpdateInput(Player player)
    {
        ClearPlayerUpdateInput();
        if (player != Player.m_localPlayer ||
            !player.InPlaceMode() ||
            Hud.IsPieceSelectionVisible() ||
            Hud.InRadial() ||
            InventoryGui.IsVisible() ||
            Menu.IsVisible() ||
            Console.IsVisible() ||
            (Chat.instance != null && Chat.instance.HasFocus()) ||
            !TryGetSelectedPlant(player, out Piece? piece, out _) ||
            piece == null)
        {
            return;
        }

        bool modifierHeld = IsMassPlantingEnabled() && IsMassPlantWheelModifierHeld();
        bool rotatesGroup = !modifierHeld && GetCurrentPlantCount(player, piece) > 1;
        if (!modifierHeld && !rotatesGroup)
        {
            return;
        }

        _capturedPlayerUpdateScroll = ZInput.GetMouseScrollWheel();
        _hasCapturedPlayerUpdateScroll = true;
        _suppressPlayerUpdateMouseWheel = Mathf.Abs(_capturedPlayerUpdateScroll) >= 0.01f;
    }

    internal static void EndPlayerUpdateInputSuppression()
    {
        _suppressPlayerUpdateMouseWheel = false;
    }

    internal static void ClearPlayerUpdateInput()
    {
        _suppressPlayerUpdateMouseWheel = false;
        _hasCapturedPlayerUpdateScroll = false;
        _capturedPlayerUpdateScroll = 0f;
    }

    internal static bool ShouldBlockPlayerUpdateMouseScrollWheel()
    {
        return _suppressPlayerUpdateMouseWheel;
    }

    internal static PieceInfoState? PreparePieceInfo(Piece piece)
    {
        if (piece == null || !TryGetPlant(piece, out _))
        {
            return null;
        }

        PieceInfoState state = new(piece);
        List<string> hints = [];
        if (IsMassPlantingEnabled())
        {
            hints.Add($"{FormatMassPlantWheelShortcut()}: {GroundworkLocalization.Text("groundwork_mass_plant", "Mass Plant")}");
        }

        hints.Add($"{FormatShortcut(GroundworkToolsDomain.ToggleGridPlantingHotkey)}: {GroundworkLocalization.Text("groundwork_grid_plant", "Grid Plant")}");
        string hint = string.Join("\n", hints);
        piece.m_description = string.IsNullOrWhiteSpace(piece.m_description)
            ? hint
            : $"{piece.m_description}\n{hint}";
        return state;
    }

    internal static void RestorePieceInfo(PieceInfoState? state)
    {
        state?.Restore();
    }

    internal static void InitializeBuildHints(KeyHints hints)
    {
        _activeKeyHints = hints;
        _gridHintSlot = null;
        _cycleHintSlot = null;
        _massHintSlot = null;
        _fallbackBuildHint = null;
        _gridHint = null;
        _copyHint = null;
        _showingMassBuildHints = false;
        OriginalHintTexts.Clear();
        EnsureBuildHintSlots(hints);
        UpdateBuildHint(hints);
    }

    internal static void RefreshBuildHintUi()
    {
        if (_activeKeyHints != null)
        {
            UpdateBuildHint(_activeKeyHints);
        }
    }

    internal static void UpdateBuildHint(KeyHints hints)
    {
        if (hints == null)
        {
            return;
        }

        _activeKeyHints = hints;
        EnsureBuildHintSlots(hints);

        Player? player = Player.m_localPlayer;
        bool show = player != null &&
                    player.InPlaceMode() &&
                    !Hud.IsPieceSelectionVisible() &&
                    !Hud.InRadial() &&
                    !InventoryGui.IsVisible() &&
                    !Menu.IsVisible() &&
                    !Console.IsVisible() &&
                    (Chat.instance == null || !Chat.instance.HasFocus()) &&
                    TryGetSelectedPlant(player, out _, out _);
        if (!show)
        {
            if (_showingMassBuildHints)
            {
                RestoreBuildHints(hints);
                RestoreBuildHintSlots();
                _fallbackBuildHint?.Restore();

                _showingMassBuildHints = false;
            }
        }

        if (!show || player == null)
        {
            return;
        }

        int count = GetCurrentPlantCount(player);
        bool massPlantingEnabled = IsMassPlantingEnabled();
        string massState = FormatMassPlantState(count);
        string gridState = _gridPlantingMode
            ? GroundworkLocalization.Text("groundwork_state_on", "On")
            : GroundworkLocalization.Text("groundwork_state_off", "Off");
        string massPlantText = GroundworkLocalization.Text("groundwork_mass_plant", "Mass Plant");
        string gridPlantText = GroundworkLocalization.Text("groundwork_grid_plant", "Grid Plant");
        _showingMassBuildHints = true;

        string gridKey = FormatShortcut(GroundworkToolsDomain.ToggleGridPlantingHotkey);
        string[] massKeys = FormatMassPlantWheelKeys();

        ArrangeBuildHintSlots(massPlantingEnabled);
        HideBuildHintSlot(_cycleHintSlot);
        if (massPlantingEnabled)
        {
            _massHintSlot?.Set($"{massPlantText}<br>{massState}", massKeys, preferredTextWidth: 120f);
        }
        else
        {
            HideBuildHintSlot(_massHintSlot);
        }

        _gridHintSlot?.Set($"{gridPlantText}<br>{gridState}", new[] { gridKey }, preferredTextWidth: 100f);
        (_gridHintSlot ?? _massHintSlot)?.RebuildParentLayout();

        TextMeshProUGUI? gridHint = ResolveGridHint(hints);
        TextMeshProUGUI? copyHint = ResolveCopyHint(hints);

        if (_gridHintSlot == null)
        {
            SetHintText(gridHint, $"{gridPlantText} <mspace=0.6em>{gridKey}</mspace> {gridState}");
        }

        bool needsFallback = (_gridHintSlot == null && gridHint == null) ||
                             (massPlantingEnabled && _massHintSlot == null && copyHint == null);

        if (massPlantingEnabled && _massHintSlot == null && copyHint != null)
        {
            SetHintText(
                copyHint,
                $"{massPlantText} <mspace=0.6em>{FormatMassPlantWheelShortcut()}</mspace> {massState}");
        }

        if (needsFallback)
        {
            EnsureFallbackBuildHint(hints);
            if (_fallbackBuildHint != null)
            {
                List<string> fallbackParts = [];
                if (massPlantingEnabled)
                {
                    fallbackParts.Add($"{GroundworkLocalization.Text("groundwork_mass", "Mass")} <mspace=0.6em>{FormatMassPlantWheelShortcut()}</mspace> {massState}");
                }

                fallbackParts.Add($"{GroundworkLocalization.Text("groundwork_grid", "Grid")} <mspace=0.6em>{gridKey}</mspace> {gridState}");
                _fallbackBuildHint.SetText(string.Join("  ", fallbackParts));
                _fallbackBuildHint.RebuildParentLayout();
            }
        }
        else if (_fallbackBuildHint != null)
        {
            _fallbackBuildHint.Restore();
        }
    }

    private static void EnsureBuildHintSlots(KeyHints hints)
    {
        _gridHintSlot ??= KeyHintCell.Resolve(hints.transform, "BuildHints/Keyboard/AltPlace");
        _cycleHintSlot ??= KeyHintCell.Resolve(hints.transform, "BuildHints/Keyboard/Snap");
        _massHintSlot ??= KeyHintCell.Resolve(hints.transform, "BuildHints/Keyboard/Copy");
        _massHintSlot ??= KeyHintCell.CloneFrom(_gridHintSlot ?? _cycleHintSlot, "Groundwork_MassPlantHint", hideOnRestore: true);
    }

    private static void ArrangeBuildHintSlots(bool massPlantingEnabled)
    {
        if (massPlantingEnabled && _massHintSlot != null)
        {
            _massHintSlot.MoveToStart();
            if (_gridHintSlot != null)
            {
                _gridHintSlot.MoveAfter(_massHintSlot.Root);
                return;
            }

            _cycleHintSlot?.MoveAfter(_massHintSlot.Root);
            return;
        }

        if (_gridHintSlot != null)
        {
            _gridHintSlot.MoveToStart();
            return;
        }
    }

    private static void HideBuildHintSlot(KeyHintCell? slot)
    {
        if (slot == null)
        {
            return;
        }

        slot.Set("", System.Array.Empty<string>(), hideExtraTexts: true);
        slot.SetActive(false);
    }

    private static void RestoreBuildHintSlots()
    {
        _gridHintSlot?.Restore();
        _cycleHintSlot?.Restore();
        _massHintSlot?.Restore();
    }

    private static bool IsMassPlantingEnabled()
    {
        return GroundworkToolsDomain.MassPlantingEnabled;
    }

    private static bool TryGetSelectedPlant(Player player, out Piece? piece, out Plant? plant)
    {
        piece = null;
        plant = null;
        if (!player.InPlaceMode())
        {
            return false;
        }

        player.GetBuildSelection(out Piece selectedPiece, out _, out _, out _, out _);
        piece = selectedPiece;
        return TryGetPlant(piece, out plant);
    }

    private static bool TryGetPlant(Piece? piece, out Plant? plant)
    {
        plant = piece != null ? piece.GetComponentInChildren<Plant>(includeInactive: true) : null;
        return plant != null;
    }

    private static int GetCurrentPlantCount(Player player)
    {
        if (!TryGetSelectedPlant(player, out Piece? piece, out _) || piece == null)
        {
            if (piece != null)
            {
                ClearActivePlantSelection();
            }

            return 0;
        }

        return GetCurrentPlantCount(player, piece);
    }

    private static int GetCurrentPlantCount(Player player, Piece piece)
    {
        EnsureActivePlantSelection(player, piece);
        ClampSelectedPlantCount(player);
        return _selectedPlantCount;
    }

    private static void EnsureActivePlantSelection(Player player, Piece piece)
    {
        string key = piece != null ? piece.gameObject.name : "";
        if (_activePlantSelectionKey != null &&
            _activePlantSelectionKey.Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _activePlantSelectionKey = key;
        _selectedPlantCount = GetDefaultPlantCount();
        _activePlantGroupRight = Vector3.zero;
        _activePlantGroupForward = Vector3.zero;
        _gridPlantGroupRotated = false;
    }

    private static void ClearActivePlantSelection()
    {
        _activePlantSelectionKey = null;
        _selectedPlantCount = 0;
        _activePlantGroupRight = Vector3.zero;
        _activePlantGroupForward = Vector3.zero;
        _gridPlantGroupRotated = false;
    }

    private static void ClampSelectedPlantCount(Player player)
    {
        int maxUnlocked = GetMaxUnlockedPlantCount(player);
        if (maxUnlocked <= 0)
        {
            _selectedPlantCount = 0;
            return;
        }

        if (_selectedPlantCount > 0)
        {
            _selectedPlantCount = ClampPlantCountToUnlockedOptions(_selectedPlantCount, maxUnlocked);
        }
    }

    private static int GetDefaultPlantCount()
    {
        return 0;
    }

    private static int GetMaxUnlockedPlantCount(Player player)
    {
        if (!GroundworkToolsDomain.MassPlantingEnabled)
        {
            return 0;
        }

        float farmingLevel = Mathf.Clamp(player.GetSkillLevel(Skills.SkillType.Farming), 0f, 100f);
        if (farmingLevel < 20f)
        {
            return 0;
        }

        if (farmingLevel < 40f)
        {
            return 5;
        }

        if (farmingLevel < 60f)
        {
            return 10;
        }

        if (farmingLevel < 80f)
        {
            return 15;
        }

        if (farmingLevel < 100f)
        {
            return 20;
        }

        return 25;
    }

    private static bool TryHandleMassPlantWheel(Player player)
    {
        if (!IsMassPlantingEnabled() || !IsMassPlantWheelModifierHeld())
        {
            return false;
        }

        float scroll = GetMassPlantScrollWheel();
        if (Mathf.Abs(scroll) < 0.01f)
        {
            return false;
        }

        int maxUnlocked = GetMaxUnlockedPlantCount(player);
        int current = GetCurrentPlantCount(player);
        int next = GetNextPlantCount(current, maxUnlocked, scroll > 0f);

        if (next == current)
        {
            return false;
        }

        _selectedPlantCount = next;
        RefreshBuildHintUi();
        return true;
    }

    private static int GetNextPlantCount(int current, int maxUnlocked, bool increase)
    {
        if (maxUnlocked <= 0)
        {
            return 0;
        }

        if (increase)
        {
            foreach (int option in MassPlantCountOptions)
            {
                if (option > current && option <= maxUnlocked)
                {
                    return option;
                }
            }

            return current;
        }

        if (current <= 0)
        {
            return 0;
        }

        int previous = 0;
        foreach (int option in MassPlantCountOptions)
        {
            if (option >= current)
            {
                return previous;
            }

            if (option <= maxUnlocked)
            {
                previous = option;
            }
        }

        return previous;
    }

    private static int ClampPlantCountToUnlockedOptions(int count, int maxUnlocked)
    {
        if (count <= 0 || maxUnlocked <= 0)
        {
            return 0;
        }

        int clamped = 0;
        foreach (int option in MassPlantCountOptions)
        {
            if (option > count || option > maxUnlocked)
            {
                break;
            }

            clamped = option;
        }

        return clamped;
    }

    private static bool TryHandlePlantGroupRotationWheel(Player player)
    {
        if (IsMassPlantWheelModifierHeld() || GetCurrentPlantCount(player) <= 1)
        {
            return false;
        }

        float scroll = GetMassPlantScrollWheel();
        if (Mathf.Abs(scroll) < 0.01f)
        {
            return false;
        }

        if (_gridPlantingMode)
        {
            _gridPlantGroupRotated = !_gridPlantGroupRotated;
            return true;
        }

        if (PlacementGhostField?.GetValue(player) is GameObject ghost)
        {
            EnsureActivePlantGroupAxes(player, ghost);
        }

        float delta = scroll > 0f ? GroupRotationStepDegrees : -GroupRotationStepDegrees;
        RotateActivePlantGroupAxes(delta);
        return true;
    }

    private static float GetMassPlantScrollWheel()
    {
        return _hasCapturedPlayerUpdateScroll ? _capturedPlayerUpdateScroll : ZInput.GetMouseScrollWheel();
    }

    private static void GetGrid(int count, out int rows, out int columns)
    {
        rows = 1;
        columns = Math.Max(1, count);
        for (int candidate = 1; candidate * candidate <= count; candidate++)
        {
            if (count % candidate != 0)
            {
                continue;
            }

            rows = candidate;
            columns = count / candidate;
        }
    }

    private static void BuildPlantSlots(int count, float spacing, bool anchorToCorner)
    {
        PlantSlots.Clear();
        GetGrid(count, out int rows, out int columns);
        float rowAnchor = anchorToCorner ? 0f : (rows - 1) * 0.5f;
        float columnAnchor = anchorToCorner ? 0f : (columns - 1) * 0.5f;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                float x = (column - columnAnchor) * spacing;
                float z = (row - rowAnchor) * spacing;
                PlantSlots.Add(new PlantSlot(new Vector3(x, 0f, z), row, column, rows, columns));
            }
        }

        PlantSlots.Sort(ComparePlantSlotPlacementOrder);
    }

    private static string FormatMassPlantState(int count)
    {
        return count > 0
            ? count.ToString()
            : GroundworkLocalization.Text("groundwork_state_off", "Off");
    }

    private static void GetPlantingGroupAxes(
        Player player,
        GameObject ghost,
        bool gridSnap,
        out Vector3 right,
        out Vector3 forward)
    {
        if (gridSnap)
        {
            if (_gridPlantGroupRotated)
            {
                right = Vector3.forward;
                forward = -Vector3.right;
            }
            else
            {
                right = Vector3.right;
                forward = Vector3.forward;
            }

            return;
        }

        EnsureActivePlantGroupAxes(player, ghost);
        right = _activePlantGroupRight;
        forward = _activePlantGroupForward;
    }

    private static void EnsureActivePlantGroupAxes(Player player, GameObject ghost)
    {
        if (_activePlantGroupRight.sqrMagnitude > 0.001f &&
            _activePlantGroupForward.sqrMagnitude > 0.001f)
        {
            return;
        }

        Vector3 forward = Flatten(ghost != null ? ghost.transform.forward : Vector3.zero, player.transform.forward);
        Vector3 right = Flatten(ghost != null ? ghost.transform.right : Vector3.zero, player.transform.right);
        if (Mathf.Abs(Vector3.Dot(right, forward)) > 0.95f)
        {
            right = Vector3.Cross(Vector3.up, forward);
            if (right.sqrMagnitude < 0.001f)
            {
                right = Flatten(player.transform.right, Vector3.right);
            }
            else
            {
                right.Normalize();
            }
        }

        _activePlantGroupForward = forward;
        _activePlantGroupRight = right;
    }

    private static void RotateActivePlantGroupAxes(float degrees)
    {
        if (_activePlantGroupRight.sqrMagnitude < 0.001f ||
            _activePlantGroupForward.sqrMagnitude < 0.001f)
        {
            _activePlantGroupRight = Vector3.right;
            _activePlantGroupForward = Vector3.forward;
        }

        Quaternion rotation = Quaternion.AngleAxis(degrees, Vector3.up);
        _activePlantGroupRight = Flatten(rotation * _activePlantGroupRight, Vector3.right);
        _activePlantGroupForward = Flatten(rotation * _activePlantGroupForward, Vector3.forward);
    }

    private static int ComparePlantSlotPlacementOrder(PlantSlot a, PlantSlot b)
    {
        bool horizontal = a.Columns >= a.Rows;
        int primary = horizontal ? b.Column.CompareTo(a.Column) : b.Row.CompareTo(a.Row);
        if (primary != 0)
        {
            return primary;
        }

        int secondary = horizontal ? b.Row.CompareTo(a.Row) : b.Column.CompareTo(a.Column);
        if (secondary != 0)
        {
            return secondary;
        }

        return a.Distance.CompareTo(b.Distance);
    }

    private static float ResolveSpacing(Plant plant)
    {
        float baseSpacing = Mathf.Max(0.5f, plant.m_growRadius * 2f);
        if (plant.m_growRadiusVines > 0f)
        {
            baseSpacing = Mathf.Max(baseSpacing, plant.m_growRadiusVines * 2f);
        }

        return Mathf.Max(0.25f, baseSpacing * GroundworkToolsDomain.MassPlantSpacingFactor);
    }

    private static Vector3 SnapToGrid(Vector3 position, float spacing)
    {
        if (spacing <= 0.001f)
        {
            return position;
        }

        position.x = Mathf.Round(position.x / spacing) * spacing;
        position.z = Mathf.Round(position.z / spacing) * spacing;
        if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(position, out float groundHeight))
        {
            position.y = groundHeight;
        }

        return position;
    }

    private static Vector3 Flatten(Vector3 vector, Vector3 fallback)
    {
        vector.y = 0f;
        if (vector.sqrMagnitude < 0.001f)
        {
            vector = fallback;
            vector.y = 0f;
        }

        return vector.sqrMagnitude > 0.001f ? vector.normalized : Vector3.forward;
    }

    private static Quaternion ResolvePlantRotation(
        Piece piece,
        Quaternion baseRotation,
        Vector3 basePosition,
        int slotIndex,
        bool randomize)
    {
        if (!randomize)
        {
            return baseRotation;
        }

        return baseRotation * Quaternion.Euler(0f, ResolvePlantRandomYaw(piece, basePosition, slotIndex), 0f);
    }

    private static float ResolvePlantRandomYaw(Piece piece, Vector3 basePosition, int slotIndex)
    {
        unchecked
        {
            uint hash = 2166136261u;
            hash = MixHash(hash, StableHash(piece != null ? piece.gameObject.name : string.Empty));
            hash = MixHash(hash, Mathf.RoundToInt(basePosition.x * 10f));
            hash = MixHash(hash, Mathf.RoundToInt(basePosition.z * 10f));
            hash = MixHash(hash, slotIndex);
            hash = AvalancheHash(hash);
            return (hash / (float)uint.MaxValue) * 360f;
        }
    }

    private static uint MixHash(uint hash, int value)
    {
        unchecked
        {
            hash ^= (uint)value;
            hash *= 16777619u;
            return hash;
        }
    }

    private static uint AvalancheHash(uint hash)
    {
        unchecked
        {
            hash ^= hash >> 16;
            hash *= 0x7feb352du;
            hash ^= hash >> 15;
            hash *= 0x846ca68bu;
            hash ^= hash >> 16;
            return hash;
        }
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            int hash = 5381;
            foreach (char character in value)
            {
                hash = ((hash << 5) + hash) ^ character;
            }

            return hash;
        }
    }

    // Placement limits, resource payment, and skill rewards.
    private static int ResolveAffordableCount(Player player, Piece piece, int wantedCount)
    {
        if (player.NoCostCheat() || ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()))
        {
            return wantedCount;
        }

        int affordable = wantedCount;
        foreach (Piece.Requirement requirement in piece.m_resources)
        {
            if (requirement.m_resItem == null)
            {
                continue;
            }

            int amount = requirement.GetAmount(0);
            if (amount <= 0)
            {
                continue;
            }

            string itemName = requirement.m_resItem.m_itemData.m_shared.m_name;
            affordable = Math.Min(affordable, player.GetInventory().CountItems(itemName) / amount);
        }

        return Mathf.Clamp(affordable, 0, wantedCount);
    }

    private static PlacementCapacity ResolvePlacementCapacity(Player player, Piece piece, int wantedCount)
    {
        int resources = ResolveAffordableCount(player, piece, wantedCount);
        int stamina = ResolveStaminaCount(player, wantedCount);
        int durability = ResolveDurabilityCount(player, wantedCount);
        int count = Math.Min(wantedCount, Math.Min(resources, Math.Min(stamina, durability)));
        return new PlacementCapacity(count, resources, stamina, durability);
    }

    private static int ResolveStaminaCount(Player player, int wantedCount)
    {
        float staminaCost = GetBuildStamina(player);
        if (staminaCost <= 0.001f)
        {
            return wantedCount;
        }

        return Mathf.Clamp(Mathf.FloorToInt(player.GetStamina() / staminaCost), 0, wantedCount);
    }

    private static int ResolveDurabilityCount(Player player, int wantedCount)
    {
        ItemDrop.ItemData? rightItem = player.GetRightItem();
        if (rightItem?.m_shared.m_useDurability != true)
        {
            return wantedCount;
        }

        float durabilityCost = GetPlaceDurability(player, rightItem);
        if (durabilityCost <= 0.001f)
        {
            return wantedCount;
        }

        return Mathf.Clamp(Mathf.FloorToInt(rightItem.m_durability / durabilityCost), 0, wantedCount);
    }

    private static void UpdateLimitLabel(Vector3 worldPosition, PlacementCapacity capacity, int wantedCount)
    {
        if (!TryBuildLimitLabelText(capacity, wantedCount, out string text))
        {
            HideLimitLabel();
            return;
        }

        TextMeshProUGUI? label = EnsureLimitLabel();
        Camera mainCamera = Utils.GetMainCamera();
        if (label == null || mainCamera == null)
        {
            HideLimitLabel();
            return;
        }

        Vector3 screenPoint = mainCamera.WorldToScreenPointScaled(worldPosition + Vector3.up * 0.45f);
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

        label.text = text;
        label.rectTransform.position = screenPoint;
    }

    private static bool TryBuildLimitLabelText(PlacementCapacity capacity, int wantedCount, out string text)
    {
        text = string.Empty;
        if (wantedCount <= 1 || capacity.Count >= wantedCount)
        {
            return false;
        }

        List<string> reasons = [];
        if (capacity.Stamina == capacity.Count)
        {
            reasons.Add(GroundworkLocalization.Text("groundwork_resource_stamina", "stamina"));
        }

        if (capacity.Durability == capacity.Count)
        {
            reasons.Add(GroundworkLocalization.Text("groundwork_resource_durability", "durability"));
        }

        if (reasons.Count == 0)
        {
            return false;
        }

        text = $"{capacity.Count}/{wantedCount} - {string.Join(", ", reasons)}";
        return true;
    }

    private static TextMeshProUGUI? EnsureLimitLabel()
    {
        if (_limitLabelText != null && _limitLabelObject != null)
        {
            return _limitLabelText;
        }

        if (Hud.instance == null || Hud.instance.m_rootObject == null)
        {
            return null;
        }

        _limitLabelObject = new GameObject("Groundwork_MassPlantLimitLabel");
        _limitLabelObject.transform.SetParent(Hud.instance.m_rootObject.transform, false);

        RectTransform rectTransform = _limitLabelObject.AddComponent<RectTransform>();
        rectTransform.anchorMin = new Vector2(0f, 0f);
        rectTransform.anchorMax = new Vector2(0f, 0f);
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.sizeDelta = new Vector2(240f, 28f);

        _limitLabelText = _limitLabelObject.AddComponent<TextMeshProUGUI>();
        TMP_Text? sourceText = Hud.instance.m_hoverName != null
            ? Hud.instance.m_hoverName
            : Hud.instance.m_pieceDescription;
        if (sourceText != null)
        {
            _limitLabelText.font = sourceText.font;
            _limitLabelText.fontSharedMaterial = sourceText.fontSharedMaterial;
        }

        _limitLabelText.alignment = TextAlignmentOptions.Center;
        _limitLabelText.color = new Color(1f, 0.78f, 0.55f, 0.96f);
        _limitLabelText.fontSize = 18f;
        _limitLabelText.richText = false;
        _limitLabelText.textWrappingMode = TextWrappingModes.NoWrap;
        _limitLabelText.raycastTarget = false;

        Shadow shadow = _limitLabelObject.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
        shadow.effectDistance = new Vector2(1.25f, -1.25f);

        return _limitLabelText;
    }

    private static void HideLimitLabel()
    {
        if (_limitLabelObject != null)
        {
            _limitLabelObject.SetActive(false);
        }
    }

    private static void PayExtraPlacementCosts(Player player, Piece piece, int extraPlacements)
    {
        if (extraPlacements <= 0)
        {
            return;
        }

        if (!player.NoCostCheat() && !ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()))
        {
            player.ConsumeResources(piece.m_resources, 0, multiplier: extraPlacements);
        }

        float staminaCost = GetBuildStamina(player);
        if (staminaCost > 0f)
        {
            player.UseStamina(staminaCost * extraPlacements);
        }

        ItemDrop.ItemData? rightItem = player.GetRightItem();
        if (rightItem?.m_shared.m_useDurability == true)
        {
            rightItem.m_durability -= GetPlaceDurability(player, rightItem) * extraPlacements;
        }
    }

    private static void RaiseExtraMassPlantSkill(Player player, int extraPlacements)
    {
        if (extraPlacements <= 0)
        {
            return;
        }

        float factor = GroundworkToolsDomain.MassPlantSkillGainFactor;
        if (factor <= 0f)
        {
            return;
        }

        player.RaiseSkill(Skills.SkillType.Farming, extraPlacements * factor);
    }

    private static float GetBuildStamina(Player player)
    {
        if (GetBuildStaminaMethod?.Invoke(player, []) is float stamina)
        {
            return stamina;
        }

        return player.GetRightItem()?.m_shared.m_attack.m_attackStamina ?? 0f;
    }

    private static float GetPlaceDurability(Player player, ItemDrop.ItemData item)
    {
        if (GetPlaceDurabilityMethod?.Invoke(player, [item]) is float durability)
        {
            return durability;
        }

        return item.m_shared.m_useDurabilityDrain;
    }

    private static PlacementFailure ValidatePlantPosition(Player player, Piece piece, Plant plant, Vector3 position)
    {
        if (Location.IsInsideNoBuildLocation(position))
        {
            return PlacementFailure.NoBuildZone;
        }

        PrivateArea? privateArea = piece.GetComponent<PrivateArea>();
        if (!PrivateArea.CheckAccess(position, privateArea != null ? privateArea.m_radius : 0f, flash: false, wardCheck: privateArea != null))
        {
            return PlacementFailure.PrivateZone;
        }

        Heightmap? heightmap = Heightmap.FindHeightmap(position);
        if ((piece.m_groundOnly || piece.m_groundPiece || piece.m_cultivatedGroundOnly || plant.m_needCultivatedGround) && heightmap == null)
        {
            return PlacementFailure.NoGround;
        }

        if ((piece.m_cultivatedGroundOnly || plant.m_needCultivatedGround) && (heightmap == null || !heightmap.IsCultivated(position)))
        {
            return PlacementFailure.NeedCultivated;
        }

        if (piece.m_vegetationGroundOnly && IsInvalidVegetationGround(heightmap, position))
        {
            return PlacementFailure.NeedDirt;
        }

        if (piece.m_onlyInBiome != Heightmap.Biome.None && (Heightmap.FindBiome(position) & piece.m_onlyInBiome) == 0)
        {
            return PlacementFailure.WrongBiome;
        }

        if (Vector3.Distance(player.transform.position, position) > Mathf.Max(player.m_maxPlaceDistance + 3f, player.m_maxPlaceDistance * 2f))
        {
            return PlacementFailure.Invalid;
        }

        return HasPlantSpace(player, plant, position) ? PlacementFailure.None : PlacementFailure.MoreSpace;
    }

    private static bool IsInvalidVegetationGround(Heightmap? heightmap, Vector3 position)
    {
        if (heightmap == null)
        {
            return true;
        }

        Heightmap.Biome biome = heightmap.GetBiome(position);
        float vegetationMask = heightmap.GetVegetationMask(position);
        return biome == Heightmap.Biome.AshLands ? vegetationMask > 0.1f : vegetationMask < 0.25f;
    }

    private static bool HasPlantSpace(Player player, Plant plant, Vector3 position)
    {
        GameObject? placementGhost = PlacementGhostField?.GetValue(player) as GameObject;
        int count = Physics.OverlapSphereNonAlloc(
            position,
            plant.m_growRadius,
            SpaceHits,
            GetSpaceMask(),
            QueryTriggerInteraction.UseGlobal);

        for (int i = 0; i < count; i++)
        {
            Collider hit = SpaceHits[i];
            if (hit == null || IsPlacementGhostCollider(placementGhost, hit))
            {
                continue;
            }

            Plant? otherPlant = hit.GetComponentInParent<Plant>();
            if (otherPlant != null)
            {
                if (otherPlant.GetStatus() == Plant.Status.Healthy)
                {
                    return false;
                }

                continue;
            }

            if (IsBlockingPlantSpaceCollider(hit))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPlacementGhostCollider(GameObject? placementGhost, Collider hit)
    {
        return placementGhost != null &&
               hit.transform != null &&
               hit.transform.IsChildOf(placementGhost.transform);
    }

    private static bool IsBlockingPlantSpaceCollider(Collider hit)
    {
        if (hit.isTrigger)
        {
            return false;
        }

        if (hit.GetComponentInParent<Piece>() != null ||
            hit.GetComponentInParent<WearNTear>() != null)
        {
            return true;
        }

        int layer = hit.gameObject.layer;
        EnsureSpaceLayers();
        return layer == _defaultLayer ||
               layer == _staticSolidLayer ||
               layer == _defaultSmallLayer ||
               layer == _pieceLayer ||
               layer == _pieceNonSolidLayer;
    }

    private static void EnsureSpaceLayers()
    {
        if (_defaultLayer >= 0)
        {
            return;
        }

        _defaultLayer = LayerMask.NameToLayer("Default");
        _staticSolidLayer = LayerMask.NameToLayer("static_solid");
        _defaultSmallLayer = LayerMask.NameToLayer("Default_small");
        _pieceLayer = LayerMask.NameToLayer("piece");
        _pieceNonSolidLayer = LayerMask.NameToLayer("piece_nonsolid");
    }

    private static int GetSpaceMask()
    {
        if (_spaceMask == 0)
        {
            _spaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid");
        }

        return _spaceMask;
    }

    private static void ShowPlacementFailure(Player player, PlacementFailure failure)
    {
        string message = failure switch
        {
            PlacementFailure.NoBuildZone => "$msg_nobuildzone",
            PlacementFailure.PrivateZone => "$msg_privatezone",
            PlacementFailure.NeedCultivated => "$msg_needcultivated",
            PlacementFailure.NeedDirt => "$msg_needdirt",
            PlacementFailure.WrongBiome => "$msg_wrongbiome",
            PlacementFailure.MoreSpace => "$msg_needspace",
            _ => "$msg_invalidplacement"
        };
        player.Message(MessageHud.MessageType.Center, message);
    }

    private static void EnsureFallbackBuildHint(KeyHints hints)
    {
        if (_fallbackBuildHint != null)
        {
            return;
        }

        GameObject? source = _cycleHintSlot?.Root ??
                             _gridHintSlot?.Root ??
                             _massHintSlot?.Root ??
                             hints.m_cycleSnapKey?.gameObject ??
                             hints.m_buildMenuKey?.gameObject;
        _fallbackBuildHint = KeyHintCell.CloneFrom(source, "Groundwork_MassPlantingHint", hideOnRestore: true);
        _fallbackBuildHint?.MoveToEnd();
    }

    private static TextMeshProUGUI? ResolveGridHint(KeyHints hints)
    {
        if (_gridHint != null)
        {
            return _gridHint;
        }

        _gridHint = ResolveHint(
            hints,
            hints.m_buildAlternativePlacingKey,
            ["toggle snapping", "options", "$hud_altplacement", "$hud_toggle"],
            ["alternative", "alt", "snap", "option"]);
        return _gridHint;
    }

    private static TextMeshProUGUI? ResolveCopyHint(KeyHints hints)
    {
        if (_copyHint != null)
        {
            return _copyHint;
        }

        if (hints.m_buildHints == null)
        {
            return null;
        }

        _copyHint = ResolveHint(
            hints,
            null,
            ["$hud_copy", "copy", "shift + mouse-3", "shift+mouse-3"],
            ["copy"]);
        return _copyHint;
    }

    private static TextMeshProUGUI? ResolveHint(
        KeyHints hints,
        TextMeshProUGUI? preferred,
        string[] textTokens,
        string[] nameTokens)
    {
        if (preferred != null)
        {
            return preferred;
        }

        if (hints.m_buildHints == null)
        {
            return null;
        }

        TextMeshProUGUI[] texts = hints.m_buildHints.GetComponentsInChildren<TextMeshProUGUI>(includeInactive: true);
        foreach (TextMeshProUGUI text in texts)
        {
            if (text == null || text == hints.m_buildMenuKey ||
                text == hints.m_buildRotateKey ||
                _fallbackBuildHint?.Contains(text) == true)
            {
                continue;
            }

            string value = (text.text ?? string.Empty).ToLowerInvariant();
            string objectName = text.name.ToLowerInvariant();
            if (textTokens.Any(token => value.Contains(token)) ||
                nameTokens.Any(token => objectName.Contains(token)))
            {
                return text;
            }
        }

        return null;
    }

    private static void SetHintText(TextMeshProUGUI? text, string value)
    {
        if (text == null)
        {
            return;
        }

        if (!OriginalHintTexts.ContainsKey(text))
        {
            OriginalHintTexts[text] = text.text;
        }

        Localization.instance?.RemoveTextFromCache(text);
        text.text = value;
    }

    private static void RestoreBuildHints(KeyHints hints)
    {
        RestoreHint(_gridHint);
        if (_copyHint != null)
        {
            RestoreHint(_copyHint);
        }
    }

    private static void RestoreHint(TextMeshProUGUI? text)
    {
        if (text == null || !OriginalHintTexts.TryGetValue(text, out string original))
        {
            return;
        }

        Localization.instance?.RemoveTextFromCache(text);
        text.text = original;
    }

    private static string FormatShortcut(KeyboardShortcut shortcut)
    {
        if (shortcut.MainKey == KeyCode.None)
        {
            return GroundworkLocalization.Text("groundwork_state_none", "None");
        }

        IEnumerable<KeyCode>? modifiers = shortcut.Modifiers;
        if (modifiers == null || !modifiers.Any())
        {
            return shortcut.MainKey.ToString();
        }

        List<string> parts = [];
        foreach (KeyCode modifier in modifiers)
        {
            parts.Add(modifier.ToString());
        }

        parts.Add(shortcut.MainKey.ToString());
        return string.Join(" + ", parts);
    }

    private static string[] FormatMassPlantWheelKeys()
    {
        KeyboardShortcut shortcut = GroundworkToolsDomain.ToolWheelModifierHotkey;
        if (shortcut.MainKey == KeyCode.None)
        {
            return ["Wheel"];
        }

        List<string> parts = shortcut.Modifiers.Select(modifier => modifier.ToString()).ToList();
        parts.Add(shortcut.MainKey.ToString());
        parts.Add("Wheel");
        return parts.ToArray();
    }

    private static string FormatMassPlantWheelShortcut()
    {
        return string.Join(" + ", FormatMassPlantWheelKeys());
    }

    private static bool IsMassPlantWheelModifierHeld()
    {
        return GroundworkToolsDomain.ToolWheelModifierHotkey.IsKeyHeld();
    }

    private static void EnsurePreviewGhosts(GameObject sourceGhost, int count)
    {
        string sourceName = sourceGhost.name;
        if (_previewSourceName != sourceName)
        {
            DestroyPreviewGhosts();
            _previewSourceName = sourceName;
        }

        while (PreviewGhosts.Count < count)
        {
            PlantPreviewGhost preview = new(sourceGhost, PreviewGhosts.Count);
            if (!preview.HasRenderers)
            {
                preview.Destroy();
                break;
            }

            PreviewGhosts.Add(preview);
        }

        for (int i = count; i < PreviewGhosts.Count; i++)
        {
            PreviewGhosts[i].SetActive(false);
        }
    }

    private static void ClearPlacementPreview()
    {
        SetOriginalGhostInvalid(null, false);
        SetOriginalGhostVisualHidden(null, false);
        HideLimitLabel();
        foreach (PlantPreviewGhost preview in PreviewGhosts)
        {
            preview.SetActive(false);
        }
    }

    private static void DestroyPreviewGhosts()
    {
        foreach (PlantPreviewGhost preview in PreviewGhosts)
        {
            preview.Destroy();
        }

        PreviewGhosts.Clear();
    }

    private static void SetOriginalGhostVisualHidden(GameObject? ghost, bool hidden)
    {
        if (!hidden)
        {
            SetOriginalGhostInvalid(null, false);
            foreach (KeyValuePair<Renderer, bool> state in OriginalGhostRendererStates)
            {
                if (state.Key != null)
                {
                    state.Key.enabled = state.Value;
                }
            }

            OriginalGhostRendererStates.Clear();
            _hiddenOriginalGhost = null;
            return;
        }

        if (ghost == null)
        {
            return;
        }

        if (_hiddenOriginalGhost != ghost)
        {
            SetOriginalGhostVisualHidden(null, false);
            _hiddenOriginalGhost = ghost;
        }

        foreach (Renderer renderer in ghost.GetComponentsInChildren<Renderer>(includeInactive: true))
        {
            if (!OriginalGhostRendererStates.ContainsKey(renderer))
            {
                OriginalGhostRendererStates[renderer] = renderer.enabled;
            }

            renderer.enabled = false;
        }
    }

    private static void SetOriginalGhostInvalid(GameObject? ghost, bool invalid)
    {
        if (!invalid)
        {
            RestoreOriginalGhostPropertyBlocks();
            return;
        }

        if (ghost == null)
        {
            return;
        }

        if (_invalidOriginalGhost != ghost)
        {
            RestoreOriginalGhostPropertyBlocks();
            _invalidOriginalGhost = ghost;
        }

        InvalidOriginalGhostPropertyBlock.Clear();
        InvalidOriginalGhostPropertyBlock.SetColor(OriginalGhostColorProperty, new Color(1f, 0.15f, 0.1f, 0.55f));
        InvalidOriginalGhostPropertyBlock.SetColor(OriginalGhostEmissionColorProperty, new Color(1f, 0.05f, 0.02f, 0.4f));

        foreach (Renderer renderer in ghost.GetComponentsInChildren<Renderer>(includeInactive: true))
        {
            if (renderer == null)
            {
                continue;
            }

            if (!OriginalGhostPropertyBlocks.ContainsKey(renderer))
            {
                MaterialPropertyBlock original = new();
                renderer.GetPropertyBlock(original);
                OriginalGhostPropertyBlocks[renderer] = original;
            }

            renderer.SetPropertyBlock(InvalidOriginalGhostPropertyBlock);
        }
    }

    private static void RestoreOriginalGhostPropertyBlocks()
    {
        foreach (KeyValuePair<Renderer, MaterialPropertyBlock> state in OriginalGhostPropertyBlocks)
        {
            if (state.Key != null)
            {
                state.Key.SetPropertyBlock(state.Value);
            }
        }

        OriginalGhostPropertyBlocks.Clear();
        _invalidOriginalGhost = null;
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
}

// Harmony patches.
[HarmonyPatch(typeof(KeyHints), "Awake")]
internal static class KeyHintsAwakeMassPlantingPatch
{
    private static void Postfix(KeyHints __instance)
    {
        MassPlantingSystem.InitializeBuildHints(__instance);
    }
}

[HarmonyPatch(typeof(KeyHints), "UpdateHints")]
internal static class KeyHintsUpdateMassPlantingPatch
{
    private static void Postfix(KeyHints __instance)
    {
        MassPlantingSystem.UpdateBuildHint(__instance);
    }
}
