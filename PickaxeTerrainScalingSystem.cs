using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Groundwork;

internal static class PickaxeTerrainScalingSystem
{
    private const string GenericPickaxeToolPrefabName = "Pickaxe";
    private const string TerrainDigConfigName = "terrainDig";
    private static readonly Dictionary<string, float> CurrentScalesByPrefab = new(System.StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<Attack> VanillaFallbackAttacks = [];
    private static readonly Dictionary<Attack, PendingTerrainDigText> PendingFallbackTexts = [];
    private static Attack? _activeMeleeAttack;
    private static KeyHints? _activeKeyHints;
    private static KeyHintCell? _digHint;
    private static bool _showingDigHint;
    private static string? _lastDigHintLabel;
    private static string? _lastDigHintKeyText;

    internal static Attack? ActiveMeleeAttack => _activeMeleeAttack;

    internal static void BeginMeleeAttack(Attack attack)
    {
        _activeMeleeAttack = attack;
    }

    internal static void EndMeleeAttack(Attack attack)
    {
        if (_activeMeleeAttack == attack)
        {
            _activeMeleeAttack = null;
        }

        VanillaFallbackAttacks.Remove(attack);
        PendingFallbackTexts.Remove(attack);
    }

    internal static TerrainScalingScope? Begin(GameObject? prefab, Character? character, ItemDrop.ItemData? weapon)
    {
        if (prefab == null || !TryResolvePrimaryTerrainDig(character, weapon, _activeMeleeAttack))
        {
            return null;
        }

        if (_activeMeleeAttack != null && VanillaFallbackAttacks.Remove(_activeMeleeAttack))
        {
            return null;
        }

        if (!TryResolveTerrainDigScales(weapon!, out float radiusScale, out float depthScale, out TerrainDigRule rule))
        {
            return null;
        }

        if (radiusScale <= 1.001f && depthScale <= 1.001f)
        {
            return null;
        }

        TerrainScalingScope scope = new(character!, weapon!, _activeMeleeAttack, radiusScale, depthScale, rule.StaminaCostFactor, rule.DurabilityFactor);
        foreach (TerrainOp terrainOp in prefab.GetComponentsInChildren<TerrainOp>(includeInactive: true))
        {
            if (terrainOp?.m_settings == null)
            {
                continue;
            }

            scope.TerrainOps.Add(new TerrainOpState(terrainOp.m_settings));
            Apply(terrainOp.m_settings, radiusScale, depthScale);
        }

        foreach (TerrainModifier modifier in prefab.GetComponentsInChildren<TerrainModifier>(includeInactive: true))
        {
            if (modifier == null)
            {
                continue;
            }

            scope.TerrainModifiers.Add(new TerrainModifierState(modifier));
            Apply(modifier, radiusScale, depthScale);
        }

        return scope;
    }

    internal static void End(TerrainScalingScope? scope, GameObject? spawnedTerrainObject)
    {
        if (scope != null)
        {
            scope.ApplyTerrainHitResult(spawnedTerrainObject);
        }
        else
        {
            ShowPendingFallbackText(spawnedTerrainObject);
        }

        scope?.Restore();
    }

    internal static bool CanSpawnTerrainModifier(Character character, ItemDrop.ItemData weapon, Attack? attack)
    {
        if (!TryResolveCostProfile(character, weapon, attack, out TerrainCostProfile profile))
        {
            return true;
        }

        float extraStaminaCost = profile.BaseStaminaCost * profile.ExtraStaminaMultiplier;
        bool hasScaledDurability = HasEnoughDurability(character, weapon, profile.TotalDurabilityMultiplier);
        bool hasBaseDurability = HasEnoughDurability(character, weapon, 1f);
        if (!hasScaledDurability && !hasBaseDurability)
        {
            return false;
        }

        if (extraStaminaCost > 0f && !character.HaveStamina(extraStaminaCost))
        {
            if (character.IsPlayer())
            {
                Hud.instance?.StaminaBarEmptyFlash();
            }

            UseVanillaFallback(attack, profile, "stamina");
            return true;
        }

        if (!hasScaledDurability)
        {
            UseVanillaFallback(attack, profile, "durability");
            return true;
        }

        return true;
    }

    internal static void UpdateInput(Player player)
    {
        if (player == null || player != Player.m_localPlayer || !TryResolveEquippedPickaxeTerrainDig(player, out ItemDrop.ItemData? weapon))
        {
            return;
        }

        float scroll = ZInput.GetMouseScrollWheel();
        if (Mathf.Abs(scroll) < 0.01f || !IsScaleModifierHeld())
        {
            return;
        }

        if (!TryGetTerrainDigRule(weapon!, out TerrainDigRule rule))
        {
            return;
        }

        float currentScale = GetCurrentScale(weapon!, rule);
        const float minScale = TerrainDigRule.FixedMinScale;
        float maxScale = rule.SelectedMaxScale;
        float step = GroundworkToolsDomain.TerrainToolRangeStep;
        float nextScale = Mathf.Clamp(currentScale + System.Math.Sign(scroll) * step, minScale, maxScale);
        nextScale = Mathf.Round(nextScale / step) * step;
        nextScale = Mathf.Clamp(nextScale, minScale, maxScale);
        if (Mathf.Abs(nextScale - currentScale) < 0.001f)
        {
            return;
        }

        string prefabName = GetWeaponPrefabName(weapon);
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return;
        }

        CurrentScalesByPrefab[prefabName] = nextScale;
        SuppressCameraZoomThisFrame = true;
        RefreshKeyHintUi();
    }

    internal static bool ShouldSuppressCameraZoom()
    {
        if (!ShouldSuppressCameraZoomInput() || Mathf.Abs(ZInput.GetMouseScrollWheel()) < 0.01f)
        {
            return SuppressCameraZoomThisFrame;
        }

        return true;
    }

    internal static bool ShouldSuppressCameraZoomInput()
    {
        if (Player.m_localPlayer == null ||
            !IsScaleModifierHeld() ||
            !TryResolveEquippedPickaxeTerrainDig(Player.m_localPlayer, out _))
        {
            return SuppressCameraZoomThisFrame;
        }

        return true;
    }

    internal static void ClearCameraZoomSuppression()
    {
        SuppressCameraZoomThisFrame = false;
    }

    internal static void InitializeKeyHints(KeyHints hints)
    {
        _activeKeyHints = hints;
        _digHint = null;
        _showingDigHint = false;
        ClearDigHintCache();
        UpdateKeyHint(hints);
    }

    internal static void RefreshKeyHintUi()
    {
        if (_activeKeyHints != null)
        {
            UpdateKeyHint(_activeKeyHints);
        }
    }

    internal static void UpdateKeyHint(KeyHints hints)
    {
        if (hints == null)
        {
            return;
        }

        _activeKeyHints = hints;
        if (!GroundworkToolsDomain.ToolHudEnabled)
        {
            HideDigHint();
            return;
        }

        Player? player = Player.m_localPlayer;
        if (player == null || !TryResolveEquippedPickaxeTerrainDig(player, out ItemDrop.ItemData? weapon))
        {
            HideDigHint();
            return;
        }

        EnsureDigHint(hints);
        if (_digHint?.IsValid != true)
        {
            return;
        }

        float scale = GetCurrentScale(weapon!);
        string shortcut = FormatShortcut(GroundworkToolsDomain.ToolWheelModifierHotkey);
        string keyText = shortcut.Length == 0
            ? GroundworkLocalization.Text("groundwork_state_unbound", "Unbound")
            : $"{shortcut}+Wheel";
        string label = GroundworkLocalization.Format(
            "groundwork_pickaxe_dig_scale",
            "Dig Scale {0}x",
            FormatScale(scale));
        if (_showingDigHint &&
            string.Equals(_lastDigHintLabel, label, System.StringComparison.Ordinal) &&
            string.Equals(_lastDigHintKeyText, keyText, System.StringComparison.Ordinal))
        {
            return;
        }

        _digHint.Set(label, new[] { keyText }, hideExtraTexts: true);
        _digHint.RebuildParentLayout();
        _showingDigHint = true;
        _lastDigHintLabel = label;
        _lastDigHintKeyText = keyText;
    }

    private static bool TryResolvePrimaryTerrainDig(Character? character, ItemDrop.ItemData? weapon, Attack? attack)
    {
        return character != null &&
               weapon?.m_shared != null &&
               weapon.m_shared.m_skillType == Skills.SkillType.Pickaxes &&
               weapon.m_shared.m_spawnOnHitTerrain != null &&
               TryGetTerrainDigRule(weapon, out _) &&
               attack != null &&
               !IsAlternateAttack(character, attack);
    }

    private static bool TryResolveCostProfile(Character character, ItemDrop.ItemData weapon, Attack? attack, out TerrainCostProfile profile)
    {
        profile = default;
        if (!TryResolvePrimaryTerrainDig(character, weapon, attack))
        {
            return false;
        }

        if (!TryResolveTerrainDigScales(weapon, out float radiusScale, out float depthScale, out TerrainDigRule rule))
        {
            return false;
        }

        float rawMultiplier = Mathf.Max(1f, radiusScale * radiusScale * depthScale);
        float totalStaminaMultiplier = 1f + (rawMultiplier - 1f) * rule.StaminaCostFactor;
        float totalDurabilityMultiplier = 1f + (rawMultiplier - 1f) * rule.DurabilityFactor;
        float selectedScale = Mathf.Max(radiusScale, depthScale);
        if (totalStaminaMultiplier <= 1.001f && totalDurabilityMultiplier <= 1.001f)
        {
            return false;
        }

        profile = new TerrainCostProfile(attack?.GetAttackStamina() ?? 0f, totalStaminaMultiplier, totalDurabilityMultiplier, selectedScale);
        return true;
    }

    private static bool TryResolveTerrainDigScales(ItemDrop.ItemData weapon, out float radiusScale, out float depthScale, out TerrainDigRule rule)
    {
        radiusScale = 1f;
        depthScale = 1f;
        if (!TryGetTerrainDigRule(weapon, out rule))
        {
            return false;
        }

        float scale = GetCurrentScale(weapon, rule);
        radiusScale = Mathf.Min(scale, rule.RadiusMaxScale);
        depthScale = Mathf.Min(scale, rule.DepthMaxScale);
        return true;
    }

    private static bool TryGetTerrainDigRule(ItemDrop.ItemData? weapon, out TerrainDigRule rule)
    {
        rule = default;
        if (weapon?.m_shared == null ||
            weapon.m_shared.m_skillType != Skills.SkillType.Pickaxes ||
            weapon.m_shared.m_spawnOnHitTerrain == null)
        {
            return false;
        }

        string prefabName = GetWeaponPrefabName(weapon);
        NormalizedTerrainToolConfig? fallbackConfig = FindTerrainDigConfig(GenericPickaxeToolPrefabName);
        NormalizedTerrainToolConfig? exactConfig = prefabName.Equals(GenericPickaxeToolPrefabName, System.StringComparison.OrdinalIgnoreCase)
            ? null
            : FindTerrainDigConfig(prefabName);
        NormalizedTerrainToolConfig? config = exactConfig ?? fallbackConfig;
        if (config == null)
        {
            return false;
        }

        rule = TerrainDigRule.FromConfig(config, exactConfig != null ? fallbackConfig : null);
        return rule.SelectedMaxScale > 1.001f;
    }

    private static NormalizedTerrainToolConfig? FindTerrainDigConfig(string toolPrefabName)
    {
        if (string.IsNullOrWhiteSpace(toolPrefabName))
        {
            return null;
        }

        foreach (NormalizedTerrainToolConfig config in GroundworkPlugin.TerrainTools)
        {
            if (config.ToolPrefabName.Equals(toolPrefabName, System.StringComparison.OrdinalIgnoreCase) &&
                config.PiecePrefabName.Equals(TerrainDigConfigName, System.StringComparison.OrdinalIgnoreCase))
            {
                return config;
            }
        }

        return null;
    }

    private static float GetCurrentScale(ItemDrop.ItemData weapon)
    {
        return TryGetTerrainDigRule(weapon, out TerrainDigRule rule)
            ? GetCurrentScale(weapon, rule)
            : 1f;
    }

    private static float GetCurrentScale(ItemDrop.ItemData weapon, TerrainDigRule rule)
    {
        string prefabName = GetWeaponPrefabName(weapon);
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return TerrainDigRule.FixedDefaultScale;
        }

        if (!CurrentScalesByPrefab.TryGetValue(prefabName, out float scale))
        {
            scale = TerrainDigRule.FixedDefaultScale;
            CurrentScalesByPrefab[prefabName] = scale;
        }

        float clamped = Mathf.Clamp(scale, TerrainDigRule.FixedMinScale, rule.SelectedMaxScale);
        if (Mathf.Abs(clamped - scale) > 0.001f)
        {
            CurrentScalesByPrefab[prefabName] = clamped;
        }

        return clamped;
    }

    private static string GetWeaponPrefabName(ItemDrop.ItemData? weapon)
    {
        return weapon?.m_dropPrefab != null ? weapon.m_dropPrefab.name : "";
    }

    private static bool TryResolveEquippedPickaxeTerrainDig(Player player, out ItemDrop.ItemData? weapon)
    {
        weapon = player.GetRightItem();
        return TryGetTerrainDigRule(weapon, out _);
    }

    private static bool IsAlternateAttack(Character character, Attack attack)
    {
        return character is Humanoid humanoid &&
               humanoid.m_currentAttack == attack &&
               humanoid.m_currentAttackIsSecondary;
    }

    private static bool IsScaleModifierHeld()
    {
        return GroundworkToolsDomain.ToolWheelModifierHotkey.IsKeyHeld();
    }

    private static bool SuppressCameraZoomThisFrame { get; set; }

    private static void EnsureDigHint(KeyHints hints)
    {
        GameObject? template = ResolveVanillaCombatHintTemplate(hints);
        if (template == null || template.transform.parent == null)
        {
            return;
        }

        if (_digHint != null && _digHint.Root.transform.parent == template.transform.parent)
        {
            _digHint.MoveBefore(template);
            return;
        }

        if (_digHint != null)
        {
            UnityEngine.Object.Destroy(_digHint.Root);
            _digHint = null;
            _showingDigHint = false;
            ClearDigHintCache();
        }

        _digHint = KeyHintCell.CloneFrom(template, "Groundwork_PickaxeTerrainDigHint", hideOnRestore: true);
        _digHint?.MoveBefore(template);
        ClearDigHintCache();
    }

    private static GameObject? ResolveVanillaCombatHintTemplate(KeyHints hints)
    {
        GameObject? preferredPrimary = ZInput.IsGamepadActive() ? hints.m_primaryAttackGP : hints.m_primaryAttackKB;
        if (KeyHintCell.IsUsableTemplate(preferredPrimary))
        {
            return preferredPrimary;
        }

        GameObject? alternatePrimary = ZInput.IsGamepadActive() ? hints.m_primaryAttackKB : hints.m_primaryAttackGP;
        if (KeyHintCell.IsUsableTemplate(alternatePrimary))
        {
            return alternatePrimary;
        }

        GameObject? preferredSecondary = ZInput.IsGamepadActive() ? hints.m_secondaryAttackGP : hints.m_secondaryAttackKB;
        if (KeyHintCell.IsUsableTemplate(preferredSecondary))
        {
            return preferredSecondary;
        }

        GameObject? alternateSecondary = ZInput.IsGamepadActive() ? hints.m_secondaryAttackKB : hints.m_secondaryAttackGP;
        if (KeyHintCell.IsUsableTemplate(alternateSecondary))
        {
            return alternateSecondary;
        }

        if (hints.m_combatHints == null)
        {
            return null;
        }

        Transform? parent = KeyHintCell.FindParentWithTemplates(hints.m_combatHints, ZInput.IsGamepadActive() ? "Gamepad" : "Keyboard")
                            ?? KeyHintCell.FindParentWithTemplates(hints.m_combatHints, "Keyboard")
                            ?? KeyHintCell.FindParentWithTemplates(hints.m_combatHints, "Gamepad")
                            ?? hints.m_combatHints.transform;
        return parent
            .Cast<Transform>()
            .Select(static transform => transform.gameObject)
            .FirstOrDefault(KeyHintCell.IsUsableTemplate);
    }

    private static void SetDigHintActive(bool active)
    {
        if (_digHint?.Root == null || _digHint.Root.activeSelf == active)
        {
            return;
        }

        _digHint.SetActive(active);
        _digHint.RebuildParentLayout();
    }

    private static void HideDigHint()
    {
        if (!_showingDigHint)
        {
            return;
        }

        SetDigHintActive(false);
        _showingDigHint = false;
        ClearDigHintCache();
    }

    private static void ClearDigHintCache()
    {
        _lastDigHintLabel = null;
        _lastDigHintKeyText = null;
    }

    private static string FormatShortcut(KeyboardShortcut shortcut)
    {
        if (shortcut.MainKey == KeyCode.None)
        {
            return "";
        }

        List<string> parts = shortcut.Modifiers.Select(modifier => modifier.ToString()).ToList();
        parts.Add(shortcut.MainKey.ToString());
        return string.Join("+", parts);
    }

    private static bool HasEnoughDurability(Character character, ItemDrop.ItemData weapon, float totalDurabilityMultiplier)
    {
        if (!weapon.m_shared.m_useDurability || !character.IsPlayer())
        {
            return true;
        }

        float totalDurabilityCost = GetItemDurabilityDrain(weapon) * totalDurabilityMultiplier;
        return totalDurabilityCost <= 0f || weapon.m_durability + 0.001f >= totalDurabilityCost;
    }

    private static void UseVanillaFallback(Attack? attack, TerrainCostProfile profile, string missingResource)
    {
        if (attack != null)
        {
            VanillaFallbackAttacks.Add(attack);
            PendingFallbackTexts[attack] = new PendingTerrainDigText(
                GroundworkLocalization.Format(
                    "groundwork_pickaxe_not_enough_resource",
                    "Not enough {0} for x{1} terrain dig",
                    FormatResourceName(missingResource),
                    FormatScale(profile.SelectedScale)),
                new Color(1f, 0.63f, 0.2f, 1f));
        }
    }

    private static void ShowPendingFallbackText(GameObject? spawnedTerrainObject)
    {
        if (spawnedTerrainObject == null ||
            _activeMeleeAttack == null ||
            !PendingFallbackTexts.TryGetValue(_activeMeleeAttack, out PendingTerrainDigText pendingText))
        {
            return;
        }

        PendingFallbackTexts.Remove(_activeMeleeAttack);
        TerrainDigFloatingTextSystem.Show(spawnedTerrainObject.transform.position, pendingText.Text, pendingText.Color);
    }

    private static string FormatScale(float scale)
    {
        return scale.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static float GetItemDurabilityDrain(ItemDrop.ItemData? weapon)
    {
        float drain = weapon?.m_shared?.m_useDurabilityDrain ?? 0f;
        return drain > 0f ? drain : 1f;
    }

    private static void Apply(TerrainOp.Settings settings, float radiusScale, float depthScale)
    {
        if (settings.m_level)
        {
            settings.m_levelRadius *= radiusScale;
        }

        if (settings.m_raise)
        {
            settings.m_raiseRadius *= radiusScale;
            if (settings.m_raiseDelta < 0f)
            {
                settings.m_raiseDelta *= depthScale;
            }
        }

        if (settings.m_smooth)
        {
            settings.m_smoothRadius *= radiusScale;
        }
    }

    private static void Apply(TerrainModifier modifier, float radiusScale, float depthScale)
    {
        if (modifier.m_level)
        {
            modifier.m_levelRadius *= radiusScale;
            if (modifier.m_levelOffset < 0f)
            {
                modifier.m_levelOffset *= depthScale;
            }
        }

        if (modifier.m_smooth)
        {
            modifier.m_smoothRadius *= radiusScale;
        }
    }

    internal sealed class TerrainScalingScope
    {
        internal readonly List<TerrainOpState> TerrainOps = [];
        internal readonly List<TerrainModifierState> TerrainModifiers = [];
        private readonly Character _character;
        private readonly ItemDrop.ItemData _weapon;
        private readonly Attack? _attack;
        private readonly float _radiusScale;
        private readonly float _depthScale;
        private readonly float _staminaCostFactor;
        private readonly float _durabilityCostFactor;

        internal TerrainScalingScope(
            Character character,
            ItemDrop.ItemData weapon,
            Attack? attack,
            float radiusScale,
            float depthScale,
            float staminaCostFactor,
            float durabilityCostFactor)
        {
            _character = character;
            _weapon = weapon;
            _attack = attack;
            _radiusScale = radiusScale;
            _depthScale = depthScale;
            _staminaCostFactor = staminaCostFactor;
            _durabilityCostFactor = durabilityCostFactor;
        }

        internal void ApplyTerrainHitResult(GameObject? spawnedTerrainObject)
        {
            if (spawnedTerrainObject == null)
            {
                return;
            }

            ApplyExtraCosts();
            ShowScaleText(spawnedTerrainObject.transform.position);
        }

        internal void Restore()
        {
            foreach (TerrainOpState state in TerrainOps)
            {
                state.Restore();
            }

            foreach (TerrainModifierState state in TerrainModifiers)
            {
                state.Restore();
            }
        }

        private void ApplyExtraCosts()
        {
            float rawMultiplier = Mathf.Max(1f, _radiusScale * _radiusScale * _depthScale);
            float extraStaminaMultiplier = (rawMultiplier - 1f) * _staminaCostFactor;
            float extraDurabilityMultiplier = (rawMultiplier - 1f) * _durabilityCostFactor;
            if (extraStaminaMultiplier <= 0.001f && extraDurabilityMultiplier <= 0.001f)
            {
                return;
            }

            if (_attack != null)
            {
                float stamina = _attack.GetAttackStamina() * extraStaminaMultiplier;
                if (stamina > 0f)
                {
                    _character.UseStamina(stamina);
                }
            }

            if (_weapon.m_shared.m_useDurability && _character.IsPlayer())
            {
                float drain = GetItemDurabilityDrain(_weapon) * extraDurabilityMultiplier;
                if (drain > 0f)
                {
                    _weapon.m_durability = Mathf.Max(0f, _weapon.m_durability - drain);
                }
            }
        }

        private void ShowScaleText(Vector3 position)
        {
            float scale = Mathf.Max(_radiusScale, _depthScale);
            TerrainDigFloatingTextSystem.Show(
                position,
                GroundworkLocalization.Format(
                    "groundwork_pickaxe_floating_scale",
                    "scale: {0}",
                    FormatScale(scale)),
                new Color(1f, 0.92f, 0.72f, 1f));
        }
    }

    private static string FormatResourceName(string resource)
    {
        return resource switch
        {
            "stamina" => GroundworkLocalization.Text("groundwork_resource_stamina", "stamina"),
            "durability" => GroundworkLocalization.Text("groundwork_resource_durability", "durability"),
            _ => resource
        };
    }

    private readonly struct PendingTerrainDigText
    {
        internal PendingTerrainDigText(string text, Color color)
        {
            Text = text;
            Color = color;
        }

        internal string Text { get; }

        internal Color Color { get; }
    }

    private readonly struct TerrainCostProfile(float baseStaminaCost, float totalStaminaMultiplier, float totalDurabilityMultiplier, float selectedScale)
    {
        internal readonly float BaseStaminaCost = baseStaminaCost;
        internal readonly float TotalStaminaMultiplier = totalStaminaMultiplier;
        internal readonly float TotalDurabilityMultiplier = totalDurabilityMultiplier;
        internal readonly float SelectedScale = selectedScale;
        internal float ExtraStaminaMultiplier => Mathf.Max(0f, TotalStaminaMultiplier - 1f);
    }

    private readonly struct TerrainDigRule(
        float radiusMaxScale,
        float depthMaxScale,
        float staminaCostFactor,
        float durabilityFactor)
    {
        internal const float FixedMinScale = 1f;
        internal const float FixedDefaultScale = 1f;

        internal readonly float RadiusMaxScale = radiusMaxScale;
        internal readonly float DepthMaxScale = depthMaxScale;
        internal readonly float StaminaCostFactor = staminaCostFactor;
        internal readonly float DurabilityFactor = durabilityFactor;
        internal float SelectedMaxScale => Mathf.Max(RadiusMaxScale, DepthMaxScale);

        internal static TerrainDigRule FromConfig(NormalizedTerrainToolConfig config, NormalizedTerrainToolConfig? fallback)
        {
            if (!ResolveEnabled(config, fallback))
            {
                return new TerrainDigRule(FixedMinScale, FixedMinScale, 0f, 0f);
            }

            float radiusMax = ResolveScaleCap(config, fallback, useRadius: true);
            float depthMax = ResolveScaleCap(config, fallback, useRadius: false);
            return new TerrainDigRule(
                Mathf.Max(FixedMinScale, radiusMax),
                Mathf.Max(FixedMinScale, depthMax),
                Mathf.Max(0f, ResolveStaminaCostFactor(config, fallback)),
                Mathf.Max(0f, ResolveDurabilityFactor(config, fallback)));
        }

        private static bool ResolveEnabled(NormalizedTerrainToolConfig config, NormalizedTerrainToolConfig? fallback)
        {
            if (config.HasRangeEnabled)
            {
                return config.RangeEnabled;
            }

            return fallback?.RangeEnabled == true;
        }

        private static float ResolveScaleCap(NormalizedTerrainToolConfig config, NormalizedTerrainToolConfig? fallback, bool useRadius)
        {
            if (useRadius && config.HasRadiusMax)
            {
                return config.RadiusMax;
            }

            if (!useRadius && config.HasDepthMax)
            {
                return config.DepthMax;
            }

            if (config.HasMaxRange)
            {
                return config.MaxRange;
            }

            if (fallback == null)
            {
                return FixedMinScale;
            }

            if (useRadius && fallback.HasRadiusMax)
            {
                return fallback.RadiusMax;
            }

            if (!useRadius && fallback.HasDepthMax)
            {
                return fallback.DepthMax;
            }

            return fallback.HasMaxRange ? fallback.MaxRange : FixedMinScale;
        }

        private static float ResolveStaminaCostFactor(NormalizedTerrainToolConfig config, NormalizedTerrainToolConfig? fallback)
        {
            return config.HasStaminaCostFactor
                ? config.StaminaCostFactor
                : fallback?.StaminaCostFactor ?? 1f;
        }

        private static float ResolveDurabilityFactor(NormalizedTerrainToolConfig config, NormalizedTerrainToolConfig? fallback)
        {
            return config.HasDurabilityFactor
                ? config.DurabilityFactor
                : fallback?.DurabilityFactor ?? 1f;
        }
    }

    internal sealed class TerrainOpState
    {
        private readonly TerrainOp.Settings _settings;
        private readonly float _levelRadius;
        private readonly float _raiseRadius;
        private readonly float _raiseDelta;
        private readonly float _smoothRadius;

        internal TerrainOpState(TerrainOp.Settings settings)
        {
            _settings = settings;
            _levelRadius = settings.m_levelRadius;
            _raiseRadius = settings.m_raiseRadius;
            _raiseDelta = settings.m_raiseDelta;
            _smoothRadius = settings.m_smoothRadius;
        }

        internal void Restore()
        {
            _settings.m_levelRadius = _levelRadius;
            _settings.m_raiseRadius = _raiseRadius;
            _settings.m_raiseDelta = _raiseDelta;
            _settings.m_smoothRadius = _smoothRadius;
        }
    }

    internal sealed class TerrainModifierState
    {
        private readonly TerrainModifier _modifier;
        private readonly float _levelOffset;
        private readonly float _levelRadius;
        private readonly float _smoothRadius;

        internal TerrainModifierState(TerrainModifier modifier)
        {
            _modifier = modifier;
            _levelOffset = modifier.m_levelOffset;
            _levelRadius = modifier.m_levelRadius;
            _smoothRadius = modifier.m_smoothRadius;
        }

        internal void Restore()
        {
            _modifier.m_levelOffset = _levelOffset;
            _modifier.m_levelRadius = _levelRadius;
            _modifier.m_smoothRadius = _smoothRadius;
        }
    }
}

[HarmonyPatch(typeof(Attack), nameof(Attack.DoMeleeAttack))]
internal static class AttackDoMeleeAttackPickaxeTerrainScalingPatch
{
    private static void Prefix(Attack __instance)
    {
        PickaxeTerrainScalingSystem.BeginMeleeAttack(__instance);
    }

    private static void Postfix(Attack __instance)
    {
        PickaxeTerrainScalingSystem.EndMeleeAttack(__instance);
    }
}

[HarmonyPatch(typeof(Attack), nameof(Attack.SpawnOnHitTerrain))]
internal static class AttackSpawnOnHitTerrainPickaxeScalingPatch
{
    private static bool Prefix(
        GameObject prefab,
        Character character,
        ItemDrop.ItemData weapon,
        ref GameObject? __result,
        out PickaxeTerrainScalingSystem.TerrainScalingScope? __state)
    {
        if (!PickaxeTerrainScalingSystem.CanSpawnTerrainModifier(character, weapon, PickaxeTerrainScalingSystem.ActiveMeleeAttack))
        {
            __state = null;
            __result = null;
            return false;
        }

        __state = PickaxeTerrainScalingSystem.Begin(prefab, character, weapon);
        return true;
    }

    private static void Postfix(GameObject? __result, PickaxeTerrainScalingSystem.TerrainScalingScope? __state)
    {
        PickaxeTerrainScalingSystem.End(__state, __result);
    }
}

[HarmonyPatch(typeof(KeyHints), "Awake")]
internal static class KeyHintsAwakePickaxeTerrainScalingPatch
{
    private static void Postfix(KeyHints __instance)
    {
        PickaxeTerrainScalingSystem.InitializeKeyHints(__instance);
    }
}

[HarmonyPatch(typeof(KeyHints), "UpdateHints")]
internal static class KeyHintsUpdatePickaxeTerrainScalingPatch
{
    private static void Postfix(KeyHints __instance)
    {
        PickaxeTerrainScalingSystem.UpdateKeyHint(__instance);
    }
}
