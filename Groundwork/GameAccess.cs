using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Groundwork;

// Explicit, cached access to non-public members in the original Valheim 1.0.7 assemblies.
// Keep shared bindings here so compilation never depends on publicized game DLLs.
internal static class GameAccess
{
    internal static readonly AccessTools.FieldRef<Attack, Humanoid> Attacker = AccessTools.FieldRefAccess<Attack, Humanoid>("m_character");
    internal static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> RightItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_rightItem");
    internal static readonly AccessTools.FieldRef<Humanoid, Attack> CurrentAttack = AccessTools.FieldRefAccess<Humanoid, Attack>("m_currentAttack");
    internal static readonly AccessTools.FieldRef<Humanoid, bool> SecondaryAttack = AccessTools.FieldRefAccess<Humanoid, bool>("m_currentAttackIsSecondary");
    internal static readonly AccessTools.FieldRef<Character, ZNetView> CharacterView = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");
    internal static readonly AccessTools.FieldRef<Pickable, ZNetView> PickableView = AccessTools.FieldRefAccess<Pickable, ZNetView>("m_nview");
    internal static readonly AccessTools.FieldRef<Plant, ZNetView> PlantView = AccessTools.FieldRefAccess<Plant, ZNetView>("m_nview");
    internal static readonly AccessTools.FieldRef<Beehive, ZNetView> BeehiveView = AccessTools.FieldRefAccess<Beehive, ZNetView>("m_nview");
    internal static readonly AccessTools.FieldRef<Player, Player.PlacementStatus> PlacementStatus = AccessTools.FieldRefAccess<Player, Player.PlacementStatus>("m_placementStatus");
    internal static readonly AccessTools.FieldRef<Player, GameObject> PlacementGhost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
    internal static readonly AccessTools.FieldRef<Player, int> InteractMask = AccessTools.FieldRefAccess<Player, int>("m_interactMask");
    internal static readonly AccessTools.FieldRef<Plant, int> PlantSeed = AccessTools.FieldRefAccess<Plant, int>("m_seed");
    internal static readonly AccessTools.FieldRef<Heightmap, List<float>> Heights = AccessTools.FieldRefAccess<Heightmap, List<float>>("m_heights");
    internal static readonly AccessTools.FieldRef<ZNetScene, Dictionary<int, GameObject>> NamedPrefabs = AccessTools.FieldRefAccess<ZNetScene, Dictionary<int, GameObject>>("m_namedPrefabs");
    internal static readonly AccessTools.FieldRef<InventoryGrid, List<InventoryElement>> InventoryElements = AccessTools.FieldRefAccess<InventoryGrid, List<InventoryElement>>("m_elements");
    internal static readonly AccessTools.FieldRef<SkillsDialog, List<GameObject>> SkillElements = AccessTools.FieldRefAccess<SkillsDialog, List<GameObject>>("m_elements");
    internal static readonly AccessTools.FieldRef<UITooltip, RectTransform?> TooltipAnchor = AccessTools.FieldRefAccess<UITooltip, RectTransform?>("m_anchor");
    internal static readonly AccessTools.FieldRef<UITooltip, Vector2> TooltipPosition = AccessTools.FieldRefAccess<UITooltip, Vector2>("m_fixedPosition");
    internal static readonly AccessTools.FieldRef<UITooltip> CurrentTooltip = AccessTools.StaticFieldRefAccess<UITooltip>(AccessTools.Field(typeof(UITooltip), "m_current"));
    internal static readonly AccessTools.FieldRef<GameObject> TooltipRoot = AccessTools.StaticFieldRefAccess<GameObject>(AccessTools.Field(typeof(UITooltip), "m_tooltip"));

    // Valheim 1.0.12 changed this public member from a field to a getter-only property.
    // Keeping the access here makes the compile-time game contract explicit.
    internal static bool BypassCheatChecks => PlayerProfile.s_bypassCheatChecks;

    internal static readonly Func<Attack, float> AttackStamina = AccessTools.MethodDelegate<Func<Attack, float>>(AccessTools.DeclaredMethod(typeof(Attack), "GetAttackStamina", Type.EmptyTypes));
    internal static readonly Func<Player, float> BuildStamina = AccessTools.MethodDelegate<Func<Player, float>>(AccessTools.DeclaredMethod(typeof(Player), "GetBuildStamina", Type.EmptyTypes));
    internal static readonly Func<Player, ItemDrop.ItemData, float> PlaceDurability = AccessTools.MethodDelegate<Func<Player, ItemDrop.ItemData, float>>(AccessTools.DeclaredMethod(typeof(Player), "GetPlaceDurability", new[] { typeof(ItemDrop.ItemData) }));
    internal static readonly Func<Plant, float> GrowTime = AccessTools.MethodDelegate<Func<Plant, float>>(AccessTools.DeclaredMethod(typeof(Plant), "GetGrowTime", Type.EmptyTypes));
    internal static readonly Func<Plant, double> TimeSincePlanted = AccessTools.MethodDelegate<Func<Plant, double>>(AccessTools.DeclaredMethod(typeof(Plant), "TimeSincePlanted", Type.EmptyTypes));
    internal static readonly Func<Heightmap, int, int, Vector3> CalcVertex = AccessTools.MethodDelegate<Func<Heightmap, int, int, Vector3>>(AccessTools.DeclaredMethod(typeof(Heightmap), "CalcVertex", new[] { typeof(int), typeof(int) }));
    internal static readonly Func<Player, Recipe, bool, int, int, bool> HaveRequirementItems = AccessTools.MethodDelegate<Func<Player, Recipe, bool, int, int, bool>>(AccessTools.DeclaredMethod(typeof(Player), "HaveRequirementItems", new[] { typeof(Recipe), typeof(bool), typeof(int), typeof(int) }));
}
