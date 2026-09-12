using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Groundwork;

// Cultivator placement only. Respawn and Farming remain in pickables.yml.
internal static class CultivationSystem
{
    private const string PieceDescription = "$groundwork_cultivation_description";
    private static readonly int PlantedKey = "Groundwork.Cultivation.Planted".GetStableHashCode();
    private static readonly MethodInfo? UpdateKnownRecipes = AccessTools.Method(typeof(Player), "UpdateKnownRecipesList");
    private static readonly MethodInfo? UpdateAvailablePieces = AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList");
    private static readonly FieldInfo? PickedTimeField = AccessTools.Field(typeof(Pickable), "m_pickedTime");
    private static readonly Dictionary<string, Entry> Rules = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Registration> Registrations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ReportedWarnings = new(StringComparer.Ordinal);
    private static ZNetScene? _scene;
    private static ObjectDB? _objectDb;
    private static PieceTable? _cultivatorTable;
    private static bool _originalCanRemove;

    public sealed class Entry
    {
        public string Prefab { get; set; } = "";
        public bool? Plantable { get; set; }
        public string[] Resources { get; set; } = Array.Empty<string>();
        public bool CultivatedGroundOnly { get; set; } = true;
        public float Spacing { get; set; } = 1f;
        public PlantBiomeList? Biomes { get; set; }
    }

    internal static List<Entry> NormalizeEntries(IEnumerable<Entry> entries)
    {
        List<Entry> result = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (Entry entry in entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Prefab) || entry.Prefab.Contains(","))
            {
                throw new InvalidDataException("Each cultivation entry requires one prefab name without a time tuple.");
            }

            string prefab = entry.Prefab.Trim();
            if (!names.Add(prefab))
            {
                throw new InvalidDataException($"Duplicate cultivation prefab '{prefab}'.");
            }

            Entry normalized = new()
            {
                Prefab = prefab,
                CultivatedGroundOnly = entry.CultivatedGroundOnly,
                Spacing = entry.Spacing,
                Biomes = GrowthOverrideSystem.NormalizeBiomeList(entry.Biomes, prefab)
            };
            if (!IsFinitePositive(entry.Spacing) || entry.Spacing > 100f)
            {
                throw new InvalidDataException($"Cultivation '{prefab}': spacing must be greater than 0 and at most 100 metres.");
            }

            // Keep and validate recipe settings even while planting is disabled, so re-enabling
            // a rule requires only changing plantable.
            HashSet<string> resources = new(StringComparer.OrdinalIgnoreCase);
            List<string> tuples = [];
            foreach (string tuple in entry.Resources ?? Array.Empty<string>())
            {
                ParseResource(tuple, prefab, out string item, out int amount);
                if (!resources.Add(item))
                {
                    throw new InvalidDataException($"Cultivation '{prefab}': duplicate resource '{item}'.");
                }

                tuples.Add(item + ", " + amount.ToString(CultureInfo.InvariantCulture));
            }

            normalized.Resources = tuples.ToArray();
            normalized.Plantable = entry.Plantable ?? tuples.Count > 0;
            if (normalized.Plantable == true && tuples.Count == 0)
            {
                throw new InvalidDataException($"Cultivation '{prefab}': plantable: true requires at least one resource tuple 'Item, amount'.");
            }

            result.Add(normalized);
        }

        return result;
    }

    // Called only after the complete growth document has passed normalization.
    internal static void ApplyNormalizedRules(IReadOnlyList<Entry> entries)
    {
        Rules.Clear();
        foreach (Entry entry in entries)
        {
            Rules.Add(entry.Prefab, entry);
        }

        ReportedWarnings.Clear();
        SynchronizeRegistrations();
    }

    internal static void OnZNetSceneReady(ZNetScene scene)
    {
        if (_scene != scene)
        {
            RestoreRegistrations(preservePlanted: true);
            _scene = scene;
        }

        SynchronizeRegistrations();
    }

    internal static void ApplyToObjectDb(ObjectDB objectDb)
    {
        _objectDb = objectDb;
        SynchronizeRegistrations();
    }

    internal static bool TryGetPlanting(Piece piece, out float spacing)
    {
        spacing = 0f;
        if (piece == null || !Registrations.ContainsKey(PrefabName(piece)) ||
            !Rules.TryGetValue(PrefabName(piece), out Entry? rule) || rule.Plantable != true)
        {
            return false;
        }

        spacing = rule.Spacing;
        return true;
    }

    internal static bool IsPlantedPickable(Pickable? pickable)
    {
        ZNetView? view = pickable != null ? pickable.GetComponent<ZNetView>() : null;
        return view != null && view.IsValid() && view.GetZDO().GetBool(PlantedKey);
    }

    internal static bool HasPlacementBiomeOverride(Piece piece)
    {
        return TryGetPlanting(piece, out _) && Rules[PrefabName(piece)].Biomes != null;
    }

    internal static bool IsPlacementBiomeAllowed(Piece piece, Heightmap? heightmap, Vector3 position)
    {
        Heightmap.Biome mask = piece.m_onlyInBiome;
        if (HasPlacementBiomeOverride(piece))
        {
            Entry rule = Rules[PrefabName(piece)];
            if (!GrowthOverrideSystem.TryResolveBiomeMask(rule.Prefab, rule.Biomes, out mask))
            {
                // An explicit restriction must never become unrestricted while EWD loads,
                // or when a configured biome name is misspelled.
                return false;
            }
        }

        return GrowthOverrideSystem.IsBiomeAllowed(mask, heightmap, position);
    }

    internal static void InitializePlacedPickable(Piece piece, Player player)
    {
        if (player == null || !TryGetPlanting(piece, out _) || piece.GetCreator() != player.GetPlayerID())
        {
            return;
        }

        Pickable? pickable = piece.GetComponent<Pickable>();
        ZNetView? view = piece.GetComponent<ZNetView>();
        if (pickable == null || view == null || !view.IsValid() || !view.IsOwner() ||
            view.GetZDO().GetBool(PlantedKey))
        {
            return;
        }

        view.GetZDO().Set(PlantedKey, true);
        FarmingSkillSystem.RememberCultivationPlanter(pickable, player);
        // Ordinary state transition starts the first cycle now, without dropping any items.
        view.InvokeRPC(ZNetView.Everybody, "RPC_SetPicked", true);
        RestorePickedTimeCache(pickable);
    }

    internal static void RestorePickedTimeCache(Pickable pickable)
    {
        if (PickedTimeField == null || !IsPlantedPickable(pickable) ||
            (long)PickedTimeField.GetValue(pickable) != 0L)
        {
            return;
        }

        // A peer can receive the empty state before its first ZDO timestamp, then become owner.
        // Use the persisted cycle instead of vanilla's random initial elapsed-time fallback.
        long ticks = pickable.GetComponent<ZNetView>().GetZDO().GetLong(ZDOVars.s_pickedTime, 0L);
        if (ticks > 0L)
        {
            PickedTimeField.SetValue(pickable, ticks);
        }
    }

    internal static void EnsurePersistedPlantedPiece(Pickable pickable)
    {
        if (!IsPlantedPickable(pickable))
        {
            return;
        }

        if (_cultivatorTable != null)
        {
            _cultivatorTable.m_canRemovePieces = true;
        }

        Piece? piece = pickable.GetComponent<Piece>();
        if (piece == null)
        {
            piece = pickable.gameObject.AddComponent<Piece>();
            ConfigurePiece(piece, pickable, Array.Empty<Piece.Requirement>(), true, null);
        }
    }

    internal static void Shutdown()
    {
        RestoreRegistrations(preservePlanted: false);
        Rules.Clear();
        ReportedWarnings.Clear();
        _scene = null;
        _objectDb = null;
    }

    private static void SynchronizeRegistrations()
    {
        if (_scene == null || _objectDb == null)
        {
            return;
        }

        ItemDrop? cultivator = FindItem(_objectDb, "Cultivator");
        PieceTable? table = cultivator?.m_itemData?.m_shared?.m_buildPieces;
        if (table == null)
        {
            return;
        }

        if (_cultivatorTable != table)
        {
            RestoreRegistrations(preservePlanted: true);
            _cultivatorTable = table;
            _originalCanRemove = table.m_canRemovePieces;
        }

        foreach (Registration registration in Registrations.Values.ToArray())
        {
            if (!Rules.TryGetValue(registration.Name, out Entry? rule) || rule.Plantable != true)
            {
                RestoreRegistration(registration, preservePlanted: true);
                Registrations.Remove(registration.Name);
            }
        }

        foreach (Entry rule in Rules.Values)
        {
            if (rule.Plantable != true)
            {
                continue;
            }

            try
            {
                if (!ApplyPlantingRule(rule, table))
                {
                    RemoveRegistration(rule.Prefab);
                }
            }
            catch (Exception ex)
            {
                RemoveRegistration(rule.Prefab);
                Warn(rule.Prefab, ex.GetBaseException().Message);
            }
        }

        // Retain removal for saved planted objects even when their recipe is no longer configured.
        Pickable[] loadedPickables = Object.FindObjectsByType<Pickable>(FindObjectsSortMode.None);
        table.m_canRemovePieces = Rules.Values.Any(rule => rule.Plantable == true) ||
                                  loadedPickables.Any(IsPlantedPickable) || _originalCanRemove;
        RefreshLoadedPieces(loadedPickables);
        Player? player = Player.m_localPlayer;
        if (player != null && Game.instance != null)
        {
            UpdateKnownRecipes?.Invoke(player, null);
            UpdateAvailablePieces?.Invoke(player, null);
        }
    }

    private static bool ApplyPlantingRule(Entry rule, PieceTable table)
    {
        GameObject? prefab = FindPrefab(rule.Prefab);
        Pickable? pickable = prefab != null ? prefab.GetComponent<Pickable>() : null;
        if (prefab == null || pickable == null || prefab.GetComponent<Plant>() != null ||
            prefab.GetComponent<ZNetView>() == null || prefab.GetComponent<WearNTear>() != null ||
            prefab.GetComponent<Character>() != null)
        {
            Warn(rule.Prefab, "planting requires a networked Pickable without Plant, WearNTear, or Character components");
            return false;
        }

        if (!FarmingSkillSystem.TryGetPickableRespawnTiming(pickable, out _) || pickable.m_respawnTimeMinutes <= 0f)
        {
            Warn(rule.Prefab, "planting requires a positive effective respawn time and a repeating native respawn timer");
            return false;
        }

        if (!TryResolveRequirements(rule, out Piece.Requirement[] requirements))
        {
            return false;
        }

        if (!Registrations.TryGetValue(rule.Prefab, out Registration? registration))
        {
            if (table.m_pieces.Any(candidate => candidate != null &&
                string.Equals(Utils.GetPrefabName(candidate), rule.Prefab, StringComparison.OrdinalIgnoreCase)))
            {
                Warn(rule.Prefab, "already registered with the cultivator; its existing recipe is preserved");
                return false;
            }

            Piece? existing = prefab.GetComponent<Piece>();
            Piece piece = existing != null ? existing : prefab.AddComponent<Piece>();
            registration = new Registration(rule.Prefab, prefab, piece, existing == null);
            Registrations.Add(rule.Prefab, registration);
            table.m_pieces.Add(prefab);
        }

        ConfigurePiece(registration.Piece, pickable, requirements,
            rule.CultivatedGroundOnly, FindPlacementEffects(table));
        return true;
    }

    private static void RemoveRegistration(string name)
    {
        if (Registrations.TryGetValue(name, out Registration? registration))
        {
            RestoreRegistration(registration, preservePlanted: true);
            Registrations.Remove(name);
        }
    }

    private static void RefreshLoadedPieces(IEnumerable<Pickable> loadedPickables)
    {
        foreach (Pickable pickable in loadedPickables)
        {
            if (!IsPlantedPickable(pickable))
            {
                continue;
            }

            EnsurePersistedPlantedPiece(pickable);
            if (Registrations.TryGetValue(PrefabName(pickable), out Registration? registration))
            {
                Piece piece = pickable.GetComponent<Piece>();
                ConfigurePiece(piece, pickable, registration.Piece.m_resources,
                    registration.Piece.m_cultivatedGroundOnly, registration.Piece.m_placeEffect);
            }
        }
    }

    private static void ConfigurePiece(Piece piece, Pickable pickable, Piece.Requirement[] requirements,
        bool cultivatedGroundOnly, EffectList? placeEffects)
    {
        ItemDrop? drop = pickable.m_itemPrefab != null ? pickable.m_itemPrefab.GetComponent<ItemDrop>() : null;
        piece.m_name = PickableRespawnHoverSystem.GetSafeHoverName(pickable);
        piece.m_description = PieceDescription;
        piece.m_icon = drop?.m_itemData?.GetIcon();
        piece.m_category = Piece.PieceCategory.Misc;
        piece.m_enabled = true;
        piece.m_groundPiece = true;
        piece.m_groundOnly = true;
        piece.m_cultivatedGroundOnly = cultivatedGroundOnly;
        piece.m_noInWater = true;
        piece.m_canBeRemoved = true;
        piece.m_targetNonPlayerBuilt = false;
        piece.m_randomTarget = false;
        piece.m_resources = requirements;
        piece.m_craftingStation = null;
        if (placeEffects != null)
        {
            piece.m_placeEffect = placeEffects;
        }
    }

    private static bool TryResolveRequirements(Entry rule, out Piece.Requirement[] requirements)
    {
        List<Piece.Requirement> resolved = [];
        foreach (string tuple in rule.Resources)
        {
            ParseResource(tuple, rule.Prefab, out string name, out int amount);
            ItemDrop? item = FindItem(_objectDb!, name);
            if (item == null)
            {
                Warn(rule.Prefab, $"resource item '{name}' was not found");
                requirements = Array.Empty<Piece.Requirement>();
                return false;
            }

            resolved.Add(new Piece.Requirement { m_resItem = item, m_amount = amount, m_amountPerLevel = 0, m_recover = false });
        }

        requirements = resolved.ToArray();
        return true;
    }

    private static GameObject? FindPrefab(string name)
    {
        GameObject? prefab = _scene!.GetPrefab(name);
        if (prefab != null)
        {
            return prefab;
        }

        return GameAccess.NamedPrefabs(_scene).Values.FirstOrDefault(candidate => candidate != null &&
            string.Equals(Utils.GetPrefabName(candidate), name, StringComparison.OrdinalIgnoreCase));
    }

    private static ItemDrop? FindItem(ObjectDB objectDb, string name)
    {
        GameObject? prefab = objectDb.GetItemPrefab(name);
        prefab ??= objectDb.m_items.FirstOrDefault(candidate => candidate != null &&
            string.Equals(candidate.name, name, StringComparison.OrdinalIgnoreCase));
        return prefab != null ? prefab.GetComponent<ItemDrop>() : null;
    }

    private static EffectList? FindPlacementEffects(PieceTable table)
    {
        foreach (GameObject prefab in table.m_pieces)
        {
            if (prefab != null && prefab.GetComponent<Plant>() != null)
            {
                return prefab.GetComponent<Piece>()?.m_placeEffect;
            }
        }

        return null;
    }

    private static void RestoreRegistrations(bool preservePlanted)
    {
        foreach (Registration registration in Registrations.Values)
        {
            RestoreRegistration(registration, preservePlanted);
        }

        Registrations.Clear();
        if (_cultivatorTable != null)
        {
            _cultivatorTable.m_canRemovePieces = _originalCanRemove;
        }

        _cultivatorTable = null;
    }

    private static void RestoreRegistration(Registration registration, bool preservePlanted)
    {
        if (_cultivatorTable != null)
        {
            _cultivatorTable.m_pieces.Remove(registration.Prefab);
        }

        foreach (Piece piece in Object.FindObjectsByType<Piece>(FindObjectsSortMode.None))
        {
            if (piece == null || piece == registration.Piece || piece.m_description != PieceDescription ||
                !string.Equals(PrefabName(piece), registration.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Pickable? pickable = piece.GetComponent<Pickable>();
            if (preservePlanted && pickable != null && IsPlantedPickable(pickable))
            {
                piece.m_resources = Array.Empty<Piece.Requirement>();
                continue;
            }

            registration.Restore(piece);
        }

        if (registration.Piece != null)
        {
            registration.Restore(registration.Piece);
        }
    }

    private static string PrefabName(Component component) => Utils.GetPrefabName(component.gameObject);

    private static bool IsFinitePositive(float value) => value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);

    private static void ParseResource(string tuple, string prefab, out string item, out int amount)
    {
        string[] parts = (tuple ?? "").Split(',');
        item = parts.Length > 0 ? parts[0].Trim() : "";
        amount = 0;
        if (parts.Length != 2 || item.Length == 0 ||
            !int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out amount) || amount <= 0)
        {
            throw new InvalidDataException($"Cultivation '{prefab}': resource '{tuple}' must be 'Item, positive integer'.");
        }
    }

    private static void Warn(string prefab, string reason)
    {
        string message = $"Skipping cultivation planting for '{prefab}': {reason}.";
        if (ReportedWarnings.Add(message))
        {
            GroundworkPlugin.ModLogger.LogWarning(message);
        }
    }

    internal static bool IsManagedPiece(Piece piece) => piece != null &&
        (Registrations.ContainsKey(PrefabName(piece)) || IsPlantedPickable(piece.GetComponent<Pickable>()));

    internal static bool RestrictsCultivatorRemoval(Player player) =>
        !_originalCanRemove && _cultivatorTable != null &&
        GameAccess.RightItem(player)?.m_shared?.m_buildPieces == _cultivatorTable;

    private sealed class Registration
    {
        internal readonly string Name;
        internal readonly GameObject Prefab;
        internal readonly Piece Piece;
        private readonly bool _added;
        private readonly string _name;
        private readonly string _description;
        private readonly Sprite _icon;
        private readonly Piece.PieceCategory _category;
        private readonly bool _enabled, _groundPiece, _groundOnly, _cultivatedGroundOnly, _noInWater,
            _canBeRemoved, _targetNonPlayerBuilt, _randomTarget;
        private readonly Piece.Requirement[] _resources;
        private readonly CraftingStation _craftingStation;
        private readonly EffectList _placeEffect;

        internal Registration(string name, GameObject prefab, Piece piece, bool added)
        {
            Name = name;
            Prefab = prefab;
            Piece = piece;
            _added = added;
            _name = piece.m_name;
            _description = piece.m_description;
            _icon = piece.m_icon;
            _category = piece.m_category;
            _enabled = piece.m_enabled;
            _groundPiece = piece.m_groundPiece;
            _groundOnly = piece.m_groundOnly;
            _cultivatedGroundOnly = piece.m_cultivatedGroundOnly;
            _noInWater = piece.m_noInWater;
            _canBeRemoved = piece.m_canBeRemoved;
            _targetNonPlayerBuilt = piece.m_targetNonPlayerBuilt;
            _randomTarget = piece.m_randomTarget;
            _resources = piece.m_resources;
            _craftingStation = piece.m_craftingStation;
            _placeEffect = piece.m_placeEffect;
        }

        internal void Restore(Piece piece)
        {
            if (_added)
            {
                Object.DestroyImmediate(piece);
                return;
            }

            piece.m_name = _name;
            piece.m_description = _description;
            piece.m_icon = _icon;
            piece.m_category = _category;
            piece.m_enabled = _enabled;
            piece.m_groundPiece = _groundPiece;
            piece.m_groundOnly = _groundOnly;
            piece.m_cultivatedGroundOnly = _cultivatedGroundOnly;
            piece.m_noInWater = _noInWater;
            piece.m_canBeRemoved = _canBeRemoved;
            piece.m_targetNonPlayerBuilt = _targetNonPlayerBuilt;
            piece.m_randomTarget = _randomTarget;
            piece.m_resources = _resources;
            piece.m_craftingStation = _craftingStation;
            piece.m_placeEffect = _placeEffect;
        }
    }

    internal static string DefaultTemplate() => @"# Edit on the server (single-player: edit locally).
# Costs are per planted instance; new plantings start empty and must regenerate.
# Uprooting gives no materials or harvest.
# Respawn times and Farming settings: pickables.yml.
# Use [] to disable all recipes; existing plantings and fixed visuals remain.

- prefab: RaspberryBush
  plantable: true # Optional when resources exist. Set false to hide only this planting recipe.
  resources:
    - Raspberry, 30 # Item prefab, consumed amount.
    - Resin, 15
  cultivatedGroundOnly: true # Default true when omitted; require cultivated terrain at placement time.
  spacing: 2 # Default 1 when omitted; centre-to-centre metres, also used by grid and mass planting.
  # biomes: [Meadows, BlackForest, Plains] # Optional allowed planting locations; existing defaults stay unrestricted.
  # biomes: [Mistlands] # EWD nature group: allows vanilla Mistlands and custom biomes with nature: Mistlands.
  #                    # List the vanilla nature name, not a custom alias, when selecting that whole nature group.
  # biomes: [MyIndependentBiome] # Use a custom name only for an independent EWD biome without a nature/terrain alias.

- prefab: BlueberryBush
  plantable: true
  resources:
    - Blueberries, 30
    - Pukeberries, 5
  cultivatedGroundOnly: true
  spacing: 2

- prefab: CloudberryBush
  plantable: true
  resources:
    - Cloudberry, 30
    - RottenMeat, 15
  cultivatedGroundOnly: true
  spacing: 2

- prefab: Pickable_Dandelion
  plantable: true
  resources:
    - Dandelion, 30
    - Resin, 15
  cultivatedGroundOnly: true
  spacing: 1

- prefab: Pickable_Mushroom
  plantable: true
  resources:
    - Mushroom, 30
    - Resin, 15
  cultivatedGroundOnly: true
  spacing: 1

- prefab: Pickable_Mushroom_yellow
  plantable: true
  resources:
    - MushroomYellow, 20
    - Pukeberries, 5
  cultivatedGroundOnly: true
  spacing: 1

- prefab: Pickable_Thistle
  plantable: true
  resources:
    - Thistle, 20
    - Pukeberries, 5
  cultivatedGroundOnly: true
  spacing: 1

- prefab: Pickable_SmokePuff
  plantable: true
  resources:
    - MushroomSmokePuff, 20
    - RottenMeat, 10
  cultivatedGroundOnly: true
  spacing: 1

- prefab: Pickable_Fiddlehead
  plantable: true
  resources:
    - Fiddleheadfern, 20
    - RottenMeat, 10
  cultivatedGroundOnly: true
  spacing: 1

# Optional blue mushroom (not normally available as a vanilla resource; no generated cut remnant):
# - prefab: Pickable_Mushroom_blue
#   plantable: true
#   resources: [""MushroomBlue, 5""]
#   cultivatedGroundOnly: true
#   spacing: 1
";
}

[HarmonyPatch(typeof(Pickable), "Awake")]
internal static class PickableCultivationPersistencePatch
{
    private static void Postfix(Pickable __instance) => CultivationSystem.EnsurePersistedPlantedPiece(__instance);
}

[HarmonyPatch(typeof(Pickable), "UpdateRespawn")]
internal static class PickableCultivationFirstCyclePatch
{
    private static void Prefix(Pickable __instance)
    {
        CultivationSystem.RestorePickedTimeCache(__instance);
        if (string.Equals(Utils.GetPrefabName(__instance.gameObject), PickedVisualSystem.FiddleheadPrefabName, StringComparison.OrdinalIgnoreCase))
        {
            // A peer can receive RPC_SetPicked before the planted marker's ZDO update.
            // Reuse vanilla's periodic call (including non-owners) to restore its foliage.
            PickableRespawnHoverSystem.RefreshHoverProxy(__instance);
        }
    }
}

[HarmonyPatch(typeof(Player), "CheckCanRemovePiece")]
internal static class CultivationRemoveProtectionPatch
{
    private static bool Prefix(Player __instance, Piece piece, ref bool __result)
    {
        bool managed = CultivationSystem.IsManagedPiece(piece);
        if (!managed && !CultivationSystem.RestrictsCultivatorRemoval(__instance))
        {
            return true;
        }

        if (managed && CultivationSystem.IsPlantedPickable(piece.GetComponent<Pickable>()))
        {
            return true;
        }

        __result = false;
        return false;
    }
}

// Include pickable/proxy colliders only for this action; keep vanilla distance, ward and removal checks.
[HarmonyPatch(typeof(Player), "RemovePiece")]
internal static class CultivationRemovalRayPatch
{
    private static readonly FieldInfo? RemoveMask = AccessTools.Field(typeof(Player), "m_removeRayMask");

    private static void Prefix(Player __instance, out int? __state)
    {
        __state = null;
        if (RemoveMask == null || GameAccess.RightItem(__instance)?.m_shared?.m_name != "$item_cultivator")
        {
            return;
        }

        int mask = (int)RemoveMask.GetValue(__instance);
        __state = mask;
        RemoveMask.SetValue(__instance, mask | LayerMask.GetMask("item"));
    }

    private static void Finalizer(Player __instance, int? __state)
    {
        if (__state.HasValue)
        {
            RemoveMask?.SetValue(__instance, __state.Value);
        }
    }
}
