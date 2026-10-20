using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using HarmonyLib;

namespace Groundwork.Tests;

internal static class PathOfValheimanProbe
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run(Assembly mod, Action<bool, string> assert)
    {
        Type compat = mod.GetType("Groundwork.PathOfValheimanCompatSystem", true);
        object Call(string method, params object[] args) => compat.GetMethod(method, Static).Invoke(null, args);
        bool Active() => (bool)compat.GetProperty("IsActive", Static).GetValue(null);
        assert(!Active(), "PoV absent: adapter is inactive");
        assert(!mod.GetReferencedAssemblies().Any(a => a.Name == "PathOfValheiman"), "No hard PoV assembly dependency");
        assert(mod.GetType("Groundwork.GroundworkPlugin", true).GetCustomAttributesData().Any(a =>
            a.AttributeType.Name == "BepInDependency" && (string)a.ConstructorArguments[0].Value == "treextr.pathofvalheiman" &&
            (int)a.ConstructorArguments[1].Value == 2), "PoV is an ordered soft dependency");
        assert(!(bool)compat.GetProperty("OwnsRangeHarvest", Static).GetValue(null), "PoV absent: no range takeover");
        assert(!(bool)compat.GetProperty("UsesMassPlantingNodes", Static).GetValue(null), "PoV absent: Farming planting progression");
        assert((bool)compat.GetProperty("CultivationEnabled", Static).GetValue(null), "PoV absent: YAML planting stays enabled");
        VerifyPlantingProgression(mod, null, assert);
        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            assert((float)Call("ValidRate", invalid) == 1f, "Reject invalid PoV rate " + invalid);
        assert((float)Call("ValidRate", 2f) == 2f, "Preserve valid PoV rate");
        foreach (string transpiler in new[] { "KeepHiveTimeUnmodified", "KeepHiveCapacityUnmodified", "UseEffectiveHiveCapacity", "StoreRespawnRateInsteadOfBackdating", "UseRemainingGrowthTime", "UseCultivationRecipes" })
        {
            bool rejected = false;
            try { Call(transpiler, new List<CodeInstruction>()); }
            catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidOperationException; }
            assert(rejected, "Reject unsupported callback shape: " + transpiler);
        }
        foreach (string patch in new[] { "AttackDoMeleeAttackScytheHarvestPatch", "AttackSpawnOnHitTerrainPickaxeScalingPatch", "BeehiveUpdateBeesPollinationPatch", "PlayerUpdatePlacementGhostGroundworkPatch" })
        {
            var after = mod.GetType("Groundwork." + patch, true).GetMethod("Prefix", Static).GetCustomAttributesData()
                .FirstOrDefault(a => a.AttributeType == typeof(HarmonyAfter));
            assert(after != null && ((IEnumerable<CustomAttributeTypedArgument>)after.ConstructorArguments[0].Value)
                .Any(a => (string)a.Value == "treextr.pathofvalheiman"), patch + " explicitly follows PoV");
        }
        string path = Path.Combine(Path.GetDirectoryName(mod.Location), "PathOfValheiman.dll");
        if (!File.Exists(path))
        {
            System.Console.WriteLine("PoV DLL not supplied; its actual callback detours were not tested.");
            return;
        }

        Assembly pov = Assembly.LoadFrom(path);
        // No plugin Awake is run. Supply only its already-bound effective configuration.
        AccessTools.Method(typeof(BepInEx.Paths), "SetExecutablePath").Invoke(null,
            new object[] { Path.Combine(Path.GetDirectoryName(path), "probe.exe"), null, null, null });
        var config = new ConfigFile(Path.Combine(Path.GetDirectoryName(path), "pov-probe.cfg"), false) { SaveOnConfigSet = false };
        ConfigEntry<bool> wildSowing = config.Bind("1. Balance", "Wild sowing", true);
        FieldInfo wildSowingField = pov.GetType("PassiveTree.Plugin", true).GetField("WildSowingEnabled", Static);
        object originalWildSowing = wildSowingField.GetValue(null);
        wildSowingField.SetValue(null, wildSowing);
        var harmony = new Harmony("Groundwork.Tests.PathOfValheiman");
        var nativeStubs = new Harmony("Groundwork.Tests.PoVNativeStubs");
        var povPatches = new Harmony("treextr.pathofvalheiman");
        var gwPatches = new Harmony("Groundwork.Tests.PoVGameHooks");
        var messages = new List<string>();
        var warnings = new List<string>();
        MethodInfo Pov(string type, string name) => pov.GetType("PassiveTree." + type, true).GetMethod(name, Static);
        try
        {
            // Player's static initializer uses this Unity native utility. No scene is loaded.
            nativeStubs.Patch(AccessTools.Method(Type.GetType("UnityEngine.Animator, UnityEngine.AnimationModule", true), "StringToHash"),
                prefix: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(AnimationHash)));
            // Retain actual ZDO storage, but this isolated process has no ZNet/ZDOMan transport.
            nativeStubs.Patch(AccessTools.Method(typeof(ZDO), "IncreaseDataRevision"),
                prefix: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(NoNetworkRevision)));
            // Reproduce startup order: PoV already installed its callback before GW patches that callback.
            povPatches.CreateClassProcessor(pov.GetType("PassiveTree.Farming+Plant_GetGrowTime", true)).Patch();
            assert((bool)Call("TryInstall", harmony, pov, new System.Version(5, 0, 5),
                (Action<string>)messages.Add, (Action<string>)warnings.Add),
                "Matching callbacks install through startup path with different version metadata");
            assert(Active(), "Actual PoV DLL: complete adapter installs");
            assert(warnings.Count == 0 && messages.Any(m => m.Contains("compatibility active")),
                "Matching callbacks do not warn just because version metadata differs");
            bool hasStatsApi = pov.GetType("Valheiman.Api.Stats") != null;
            assert(harmony.GetPatchedMethods().Count() == (hasStatsApi ? 14 : 13), "PoV coordination, cultivation and optional planting gate install atomically");
            assert((bool)compat.GetProperty("UsesMassPlantingNodes", Static).GetValue(null) == hasStatsApi,
                "Planting takeover requires the public stats API and successful hook installation");
            if (hasStatsApi) VerifyPlantingProgression(mod, pov, assert);
            VerifyCultivation(mod, pov, wildSowing, assert);

            // This is the supplied PoV Borrowed struct and real patched callback, not a replica.
            var hive = (Beehive)FormatterServices.GetUninitializedObject(typeof(Beehive));
            hive.m_secPerUnit = 1200f;
            hive.m_maxHoney = 4;
            MethodInfo credit = Pov("Credit", "Bonus");
            MethodInfo meadow = Pov("CraftBee", "MeadowBonus");
            var fixtures = new Harmony("Groundwork.Tests.PoVFixtures");
            try
            {
                fixtures.Patch(credit, prefix: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(Bonus)));
                fixtures.Patch(meadow, prefix: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(NoMeadow)));
                var plant = (Plant)FormatterServices.GetUninitializedObject(typeof(Plant));
                AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(plant, new IntPtr(1));
                fixtures.Patch(AccessTools.Method(typeof(Plant), "GetGrowTime"),
                    prefix: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(BaseGrowTime)));
                fixtures.Patch(mod.GetType("Groundwork.FarmingSkillSystem", true).GetMethod("ApplyConfiguredPlantGrowTime", Static),
                    prefix: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(ConfiguredGrowTime)));
                fixtures.Patch(mod.GetType("Groundwork.GroundworkToolsDomain", true).GetProperty("PlantGrowSpeedFactor", Static).GetGetMethod(true),
                    prefix: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(NoMeadow)));
                gwPatches.CreateClassProcessor(mod.GetType("Groundwork.PlantGetGrowTimeGroundworkPatch", true)).Patch();
                float growth = (float)AccessTools.Method(typeof(Plant), "GetGrowTime").Invoke(plant, null);
                assert(growth == 300f, "Actual game wrapper: YAML base 600 / PoV bonus 2, without YAML overwrite or double bonus");
                object[] speedArgs = { hive, null };
                Pov("Conveniences+Beehive_UpdateBees", "Prefix").Invoke(null, speedArgs);
                assert(hive.m_secPerUnit == 1200f, "PoV time callback preserves base despite positive node bonus");
                assert((float)Call("HiveSpeedMultiplier", hive) == 2f, "GW reads PoV node bonus once");
                hive.m_secPerUnit = 300f;
                Pov("Conveniences+Beehive_UpdateBees", "Finalizer").Invoke(null, new[] { hive, speedArgs[1] });
                assert(hive.m_secPerUnit == 300f, "PoV finalizer no longer interferes with GW's temporary threshold");
                hive.m_secPerUnit = 1200f;

                foreach (string type in new[] { "CraftBee+Hive_Update", "CraftBee+Hive_Hover" })
                {
                    object[] capacityArgs = { hive, 0 };
                    Pov(type, "Prefix").Invoke(null, capacityArgs);
                    assert(hive.m_maxHoney == 4 && (int)capacityArgs[1] == 4, type + " preserves base and finalizer state");
                }
            }
            finally { fixtures.UnpatchSelf(); }

            Type farming = mod.GetType("Groundwork.FarmingSkillSystem", true);
            FieldInfo massPicking = pov.GetType("PassiveTree.Gathering", true).GetField("massPicking", Static);
            object suppression = farming.GetMethod("SuppressRangePickup", Static).Invoke(null, null);
            try
            {
                massPicking.SetValue(null, false);
                object[] scope = { null };
                Call("BeginGather", scope);
                assert((bool)massPicking.GetValue(null), "GW batch suppresses PoV area expansion without skipping rewards");
                object[] nested = { null };
                Call("BeginGather", nested);
                Call("EndGather", nested[0]);
                assert((bool)massPicking.GetValue(null), "Nested gathering retains outer suppression");
                Call("EndGather", scope[0]);
                assert(!(bool)massPicking.GetValue(null), "Gather finalizer restores prior PoV flag");
            }
            finally { ((IDisposable)suppression).Dispose(); }
            assert(!(bool)farming.GetProperty("RangePickupSuppressed", Static).GetValue(null), "GW suppression scope is balanced");
            object[] air = { null };
            Call("BeginAirHarvest", air);
            assert((bool)farming.GetProperty("RangePickupSuppressed", Static).GetValue(null), "PoV airborne harvest suppresses expansion");
            Call("EndAirHarvest", air[0]);
            assert(!(bool)farming.GetProperty("RangePickupSuppressed", Static).GetValue(null), "Airborne finalizer restores suppression");
            FieldInfo radius = compat.GetField("_pickupRadius", Static);
            object originalRadius = radius.GetValue(null);
            try
            {
                radius.SetValue(null, (Func<float>)(() => 2f));
                assert((bool)compat.GetProperty("OwnsRangeHarvest", Static).GetValue(null), "PoV pickup node takes precedence");
                radius.SetValue(null, (Func<float>)(() => 0f));
                assert(!(bool)compat.GetProperty("OwnsRangeHarvest", Static).GetValue(null), "Without PoV pickup node, GW range remains available");
            }
            finally { radius.SetValue(null, originalRadius); }

            // Round-trip the additional rate through a real ZDO; do not touch vanilla cycle ticks.
            var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
            long cycle = 123456789L;
            zdo.Set(ZDOVars.s_pickedTime, cycle);
            Call("StoreRespawnRate", zdo, ZDOVars.s_pickedTime, cycle - 100, 2f);
            assert(zdo.GetLong(ZDOVars.s_pickedTime) == cycle, "PoV rate leaves actual harvest timestamp unchanged");
            assert((float)Call("RespawnRate", zdo) == 2f, "PoV respawn multiplier persists in ZDO");
            Call("StoreRespawnRate", zdo, ZDOVars.s_pickedTime, cycle - 100, 2f);
            assert((float)Call("RespawnRate", zdo) == 2f, "Repeated rate receipt does not accumulate");

            // Replant scope must restore both nested and exceptional exits via its finalizer.
            var player = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
            object[] outer = { player, null };
            Call("BeginReplant", outer);
            assert(ReferenceEquals(compat.GetProperty("ReplantPlayer", Static).GetValue(null), player), "Replant uses explicit actor");
            object[] inner = { null, null };
            Call("BeginReplant", inner);
            Call("EndReplant", inner[1]);
            assert(ReferenceEquals(compat.GetProperty("ReplantPlayer", Static).GetValue(null), player), "Nested replant restores outer actor");
            Call("EndReplant", outer[1]);
            assert(compat.GetProperty("ReplantPlayer", Static).GetValue(null) == null, "Replant finalizer clears actor");

            harmony.UnpatchSelf();
            Call("Shutdown");
            assert(!Active() && (float)Call("RespawnRate", zdo) == 1f, "Unload ignores retained PoV cycle data");
            assert(!(bool)compat.GetProperty("UsesMassPlantingNodes", Static).GetValue(null) &&
                   compat.GetField("_hasPovStat", Static).GetValue(null) == null &&
                   compat.GetField("_wildSowingEnabled", Static).GetValue(null) == null, "Unload clears planting takeover and configuration references");
            assert(!harmony.GetPatchedMethods().Any(), "PoV callback hooks are removed on unload");

            bool rejectedOperand = false;
            try { Call("KeepHiveCapacityUnmodified", new List<CodeInstruction> { new CodeInstruction(OpCodes.Ldtoken, Pov("CraftBee", "Max")) }); }
            catch (TargetInvocationException ex) { rejectedOperand = ex.InnerException is InvalidOperationException; }
            assert(rejectedOperand, "A matching member token is not accepted as a method call");

            // Change one late callback's IL in this disposable process. Earlier hooks must roll back.
            var changedCallback = new Harmony("Groundwork.Tests.PoVChangedCallback");
            try
            {
                MethodInfo honey = Pov("CraftWorkHooks+Honey", "Postfix");
                changedCallback.Patch(honey, transpiler: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(RemoveCapacityRead)) { priority = Priority.First });
                warnings.Clear();
                assert(!(bool)Call("TryInstall", harmony, pov, new System.Version(5, 0, 5),
                    (Action<string>)messages.Add, (Action<string>)warnings.Add), "Changed callback shape rejects adapter");
                assert(!Active() && !harmony.GetPatchedMethods().Any(), "Late failure removes all earlier adapter hooks");
                assert(warnings.Any(w => w.Contains("5.0.5") && w.Contains("CraftWorkHooks+Honey.Postfix")), "Failure identifies version and callback");
                assert(changedCallback.GetPatchedMethods().Contains(honey), "Rollback preserves other owners' patches");
            }
            finally { changedCallback.UnpatchSelf(); }

            // Reject a changed WildSowing getter before leaving any of the earlier hooks installed.
            var changedSowing = new Harmony("Groundwork.Tests.PoVChangedSowing");
            try
            {
                MethodInfo sync = Pov("WildSowing", "Sync");
                changedSowing.Patch(sync, transpiler: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(RemoveWildSowingRead)) { priority = Priority.First });
                warnings.Clear();
                assert(!(bool)Call("TryInstall", harmony, pov, new System.Version(99, 0, 0),
                    (Action<string>)messages.Add, (Action<string>)warnings.Add), "Changed Wild sowing read rejects adapter");
                assert(!Active() && !harmony.GetPatchedMethods().Any() &&
                       compat.GetField("_wildSowingEnabled", Static).GetValue(null) == null, "Cultivation failure rolls back all hooks and captured config");
                assert(warnings.Count == 1 && warnings[0].Contains("WildSowing.Sync"), "Actual cultivation mismatch warns with the failing callback");
            }
            finally { changedSowing.UnpatchSelf(); }

            warnings.Clear();
            assert((bool)Call("TryInstall", harmony, pov, new System.Version(5, 0, 4),
                (Action<string>)messages.Add, (Action<string>)warnings.Add) && warnings.Count == 0,
                "Reviewed version reinstalls after rollback without an unreviewed-version warning");
        }
        finally
        {
            gwPatches.UnpatchSelf();
            povPatches.UnpatchSelf();
            harmony.UnpatchSelf();
            nativeStubs.UnpatchSelf();
            Call("Shutdown");
            wildSowingField.SetValue(null, originalWildSowing);
        }
    }

    private static PieceTable _wildTable;
    private static Piece _povPiece;
    private static int _menuRefreshes;

    private static T ManagedUnityObject<T>() where T : UnityEngine.Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, new IntPtr(1));
        return value;
    }

    private static void VerifyCultivation(Assembly mod, Assembly pov, ConfigEntry<bool> setting, Action<bool, string> assert)
    {
        Type compat = mod.GetType("Groundwork.PathOfValheimanCompatSystem", true);
        Type cultivation = mod.GetType("Groundwork.CultivationSystem", true);
        Type pe = mod.GetType("Groundwork.PlantEverythingCompatSystem", true);
        Type sowing = pov.GetType("PassiveTree.WildSowing", true);
        object Call(string name, params object[] args) => cultivation.GetMethod(name, Static).Invoke(null, args);
        bool Enabled() => (bool)compat.GetProperty("CultivationEnabled", Static).GetValue(null);
        var fixtures = new Harmony("Groundwork.Tests.CultivationNativeFixtures");
        var registrations = (IDictionary)cultivation.GetField("Registrations", Static).GetValue(null);
        var rules = (IDictionary)cultivation.GetField("Rules", Static).GetValue(null);
        var saplings = (IList)sowing.GetField("saplings", Static).GetValue(null);
        FieldInfo inTable = sowing.GetField("inTable", Static);
        MethodInfo sync = sowing.GetMethod("Sync", Static);
        var table = ManagedUnityObject<PieceTable>();
        table.m_pieces = new List<UnityEngine.GameObject>();
        table.m_canRemovePieces = true;
        var prefab = ManagedUnityObject<UnityEngine.GameObject>();
        var piece = ManagedUnityObject<Piece>();
        piece.m_resources = new[] { new Piece.Requirement { m_amount = 30 } };
        piece.m_description = "$groundwork_cultivation_description";
        _povPiece = ManagedUnityObject<Piece>();
        _wildTable = ManagedUnityObject<PieceTable>();
        _wildTable.m_pieces = new List<UnityEngine.GameObject> { prefab };
        saplings.Add(prefab);
        inTable.SetValue(null, true);
        _menuRefreshes = 0;
        try
        {
            fixtures.Patch(sowing.GetMethod("Table", Static), prefix: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(WildTable)));
            fixtures.Patch(cultivation.GetMethod("RefreshAvailablePieces", Static), prefix: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(CountMenuRefresh)));
            fixtures.Patch(cultivation.GetMethod("PrefabName", Static), prefix: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(CultivationPrefabName)));
            cultivation.GetField("_cultivatorTable", Static).SetValue(null, table);
            Type entry = cultivation.GetNestedType("Entry", BindingFlags.Public);
            object rule = Activator.CreateInstance(entry);
            entry.GetProperty("Prefab").SetValue(rule, "RaspberryBush");
            entry.GetProperty("Plantable").SetValue(rule, true);
            entry.GetProperty("Resources").SetValue(rule, new[] { "Raspberry, 30", "PT_SeedRaspberry, 2" });
            rules.Add("RaspberryBush", rule);
            Type registration = cultivation.GetNestedType("Registration", BindingFlags.NonPublic);
            registrations.Add("RaspberryBush", Activator.CreateInstance(registration, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { "RaspberryBush", prefab, piece, false, true }, null));
            table.m_pieces.Add(prefab);

            setting.Value = true;
            Call("RefreshPlantingAvailability");
            assert(Enabled() && piece.m_enabled && table.m_pieces.Count == 1, "Wild sowing on enables the YAML recipe");
            assert(!(bool)Call("BlocksPlacement", piece), "Enabled YAML placement is allowed");
            assert((bool)Call("BlocksPlacement", _povPiece), "Stale PoV sapling selection cannot bypass YAML");
            sync.Invoke(null, null);
            assert(_wildTable.m_pieces.Count == 0 && !(bool)inTable.GetValue(null) && saplings.Count == 1,
                "Actual PoV Sync removes its menu but preserves registered saplings");
            assert(setting.Value, "PoV effective setting is never overwritten");
            int refreshed = _menuRefreshes;
            Call("RefreshPlantingAvailability");
            assert(_menuRefreshes == refreshed, "Unchanged setting does not rebuild UI");

            setting.Value = false;
            assert(!Enabled() && (bool)Call("BlocksPlacement", piece), "Placement closes immediately before the next UI refresh");
            Call("RefreshPlantingAvailability");
            assert(!piece.m_enabled && table.m_pieces.Count == 0 && table.m_canRemovePieces &&
                   registrations.Count == 1 && rules.Count == 1, "Turning off removes only the menu; registration, removal and saved-object rule remain");
            object[] planting = { piece, null };
            assert((bool)Call("TryGetPlanting", planting), "Disabled new planting still recognizes placement initialization/lifecycle");
            // Simulate recipe reapplication: it must not reopen a disabled menu.
            piece.m_enabled = true;
            table.m_pieces.Add(prefab);
            cultivation.GetField("_appliedPlantingEnabled", Static).SetValue(null, null);
            Call("RefreshPlantingAvailability");
            assert(!piece.m_enabled && table.m_pieces.Count == 0, "YAML reapplication honors the off switch");

            setting.Value = true;
            Call("RefreshPlantingAvailability");
            Call("RefreshPlantingAvailability");
            assert(table.m_pieces.Count == 1 && piece.m_resources[0].m_amount == 30 &&
                   ((string[])entry.GetProperty("Resources").GetValue(rule)).SequenceEqual(new[] { "Raspberry, 30", "PT_SeedRaspberry, 2" }),
                "Re-enable restores one menu entry without changing or injecting recipe costs");
            entry.GetProperty("Plantable").SetValue(rule, false);
            assert((bool)Call("BlocksPlacement", piece), "Disabled YAML rule blocks a stale selection");
            rules.Clear();
            assert((bool)Call("BlocksPlacement", piece) && (bool)Call("BlocksPlacement", _povPiece),
                "Empty YAML does not reopen either planting route");

            pe.GetProperty("IsActive", Static).GetSetMethod(true).Invoke(null, new object[] { true });
            sync.Invoke(null, null);
            assert(_wildTable.m_pieces.Count == 1 && !(bool)Call("BlocksPlacement", _povPiece),
                "PlantEverything mode keeps PoV's own recipe provider");
            setting.Value = false;
            sync.Invoke(null, null);
            assert(_wildTable.m_pieces.Count == 0 && Enabled(), "PE mode leaves GW dormant and respects PoV off");
        }
        finally
        {
            fixtures.UnpatchSelf();
            pe.GetProperty("IsActive", Static).GetSetMethod(true).Invoke(null, new object[] { false });
            setting.Value = true;
            registrations.Clear();
            rules.Clear();
            saplings.Clear();
            inTable.SetValue(null, false);
            cultivation.GetField("_cultivatorTable", Static).SetValue(null, null);
            cultivation.GetField("_appliedPlantingEnabled", Static).SetValue(null, null);
            _wildTable = null;
            _povPiece = null;
        }
    }

    private static bool WildTable(ref PieceTable __result) { __result = _wildTable; return false; }
    private static bool CountMenuRefresh() { _menuRefreshes++; return false; }
    private static bool CultivationPrefabName(UnityEngine.Component __0, ref string __result)
    {
        __result = ReferenceEquals(__0, _povPiece) ? "PT_wild_RaspberryBush" : "RaspberryBush";
        return false;
    }
    private static IEnumerable<CodeInstruction> RemoveWildSowingRead(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (!instruction.Calls(AccessTools.PropertyGetter(typeof(ConfigEntry<bool>), "Value")))
            {
                yield return instruction;
                continue;
            }
            yield return new CodeInstruction(OpCodes.Pop).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
            yield return new CodeInstruction(OpCodes.Ldc_I4_0);
        }
    }

    private static float _farmingLevel;
    private static bool _massPlantEnabled;
    private static bool _broadSowing;
    private static string _queriedStat;

    private static void VerifyPlantingProgression(Assembly mod, Assembly pov, Action<bool, string> assert)
    {
        Type progression = mod.GetType("Groundwork.MassPlantingSystem+Progression", true);
        Type compat = mod.GetType("Groundwork.PathOfValheimanCompatSystem", true);
        MethodInfo maximum = progression.GetMethod("GetMaxUnlockedPlantCount", Static);
        MethodInfo hint = progression.GetMethod("GetLockedHint", Static);
        int Max() => (int)maximum.Invoke(null, new object[] { null });
        string Hint(bool compact) => (string)hint.Invoke(null, new object[] { null, compact });
        var fixtures = new Harmony("Groundwork.Tests.PlantingProgression");
        try
        {
            // Control only the external skill/config/localization inputs. The actual shared
            // progression and hint paths run without initializing renderer/preview objects.
            fixtures.Patch(progression.GetMethod("GetFarmingLevel", Static),
                prefix: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(FarmingLevel)));
            fixtures.Patch(mod.GetType("Groundwork.GroundworkToolsDomain", true).GetProperty("MassPlantingEnabled", Static).GetGetMethod(true),
                prefix: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(MassPlantEnabled)));
            fixtures.Patch(mod.GetType("Groundwork.GroundworkLocalization", true).GetMethod("Text", Static),
                prefix: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(FallbackText)));
            _massPlantEnabled = true;
            if (pov == null)
            {
                float[] levels = { 0f, 19.99f, 20f, 39.99f, 40f, 59.99f, 60f, 79.99f, 80f, 99.99f, 100f };
                int[] counts = { 0, 0, 5, 5, 10, 10, 15, 15, 20, 20, 25 };
                for (int i = 0; i < levels.Length; i++)
                {
                    _farmingLevel = levels[i];
                    assert(Max() == counts[i], "Without PoV, Farming " + levels[i] + " unlocks " + counts[i]);
                }
                assert(Hint(false) == null && Hint(true) == null, "Unlocked Farming has no lock hint");
                _farmingLevel = 0;
                assert(Hint(false).Contains("Farming skill 0/20") && Hint(true) == "Locked 0/20", "Both Farming lock hints retain level requirement");
                _farmingLevel = 100;
            }
            else
            {
                MethodInfo has = pov.GetType("Valheiman.Api.Stats", true).GetMethod("Has", Static);
                MethodInfo allowed = pov.GetType("PassiveTree.MassPlant", true).GetMethod("get_Allowed", Static);
                fixtures.Patch(has, prefix: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(HasBroadSowing)));
                // Simulate PoV's own ability/key gate being true. GW's installed postfix
                // must still stop the extra bed, input and preview, including through its delegate.
                fixtures.Patch(allowed, prefix: new HarmonyMethod(typeof(PathOfValheimanProbe), nameof(AllowPoVPlanting)));
                _farmingLevel = 100;
                _broadSowing = false;
                assert(Max() == 0, "PoV without Broad Sowing stays locked even at Farming 100");
                assert(Hint(false).Contains("Broad Sowing") && Hint(true).Contains("Broad Sowing") &&
                       !Hint(false).Contains("Farming"), "Both PoV lock hints name Broad Sowing instead of Farming");
                _farmingLevel = 0;
                _broadSowing = true;
                assert(Max() == 25 && _queriedStat == "MassPlant", "Public API Broad Sowing unlocks 25 at Farming zero without a held key");
                assert(Hint(false) == null && Hint(true) == null, "Broad Sowing removes both lock hints");
                assert(!(bool)allowed.Invoke(null, null), "PoV's real allowed getter is suppressed even when its own gate is true");
                var cachedAllowed = (Func<bool>)compat.GetField("_massPlantAllowed", Static).GetValue(null);
                assert(!cachedAllowed(), "Cached PoV getter also observes Groundwork takeover");
                _broadSowing = false;
                assert(Max() == 0 && Hint(false).Contains("Broad Sowing"), "Refund is read immediately without stale unlock cache");
                _broadSowing = true;
                assert(Max() == 25, "Reacquiring the ability restores the maximum");
                compat.GetProperty("IsActive", Static).GetSetMethod(true).Invoke(null, new object[] { false });
                try { assert((bool)allowed.Invoke(null, null), "Inactive adapter leaves PoV's own allowed result intact"); }
                finally { compat.GetProperty("IsActive", Static).GetSetMethod(true).Invoke(null, new object[] { true }); }
            }
            _massPlantEnabled = false;
            assert(Max() == 0, "Mass Planting Enabled off still disables Groundwork batches");
        }
        finally { fixtures.UnpatchSelf(); }
    }

    private static bool FarmingLevel(ref float __result) { __result = _farmingLevel; return false; }
    private static bool MassPlantEnabled(ref bool __result) { __result = _massPlantEnabled; return false; }
    private static bool FallbackText(string __1, ref string __result) { __result = __1; return false; }
    private static bool HasBroadSowing(string __0, ref bool __result) { _queriedStat = __0; __result = _broadSowing; return false; }
    private static bool AllowPoVPlanting(ref bool __result) { __result = true; return false; }

    private static bool Bonus(ref float __result) { __result = 1f; return false; }
    private static bool NoMeadow(ref float __result) { __result = 0f; return false; }
    private static bool AnimationHash(string name, ref int __result) { __result = name.GetStableHashCode(); return false; }
    private static bool NoNetworkRevision() => false;
    private static bool BaseGrowTime(ref float __result) { __result = 1200f; return false; }
    private static bool ConfiguredGrowTime(ref float __1) { __1 = 600f; return false; }
    private static IEnumerable<CodeInstruction> RemoveCapacityRead(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (!instruction.LoadsField(AccessTools.Field(typeof(Beehive), nameof(Beehive.m_maxHoney))))
            {
                yield return instruction;
                continue;
            }
            yield return new CodeInstruction(OpCodes.Pop).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
            yield return new CodeInstruction(OpCodes.Ldc_I4_4);
        }
    }
}
