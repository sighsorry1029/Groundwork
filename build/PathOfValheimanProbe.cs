using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
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
        VerifyPlantingProgression(mod, null, assert);
        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            assert((float)Call("ValidRate", invalid) == 1f, "Reject invalid PoV rate " + invalid);
        assert((float)Call("ValidRate", 2f) == 2f, "Preserve valid PoV rate");
        foreach (string transpiler in new[] { "KeepHiveTimeUnmodified", "KeepHiveCapacityUnmodified", "UseEffectiveHiveCapacity", "StoreRespawnRateInsteadOfBackdating", "UseRemainingGrowthTime" })
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
            assert(warnings.Count == 1 && warnings[0].Contains("5.0.5") && messages.Any(m => m.Contains("compatibility active")),
                "Unreviewed version is diagnostic, not a rejection");
            bool hasStatsApi = pov.GetType("Valheiman.Api.Stats") != null;
            assert(harmony.GetPatchedMethods().Count() == (hasStatsApi ? 13 : 12), "PoV coordination and optional planting gate install atomically");
            assert((bool)compat.GetProperty("UsesMassPlantingNodes", Static).GetValue(null) == hasStatsApi,
                "Planting takeover requires the public stats API and successful hook installation");
            if (hasStatsApi) VerifyPlantingProgression(mod, pov, assert);

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
                   compat.GetField("_hasPovStat", Static).GetValue(null) == null, "Unload clears planting takeover and API delegate");
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
