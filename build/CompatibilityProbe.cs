using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;

namespace Groundwork.Tests;

public static class CompatibilityProbe
{
    private static int _checks;
    private static void Assert(bool success, string description)
    {
        if (!success) throw new Exception(description);
        _checks++;
    }

    public static int Run()
    {
        try
        {
            var mod = Assembly.LoadFrom(Environment.GetEnvironmentVariable("GROUNDWORK_MONO_PROBE_DLL"));
            foreach (string name in new[] { "Groundwork.GameAccess", "Groundwork.TerrainOperationSync", "LocalizationManager.Localizer" })
            {
                RuntimeHelpers.RunClassConstructor(mod.GetType(name, true).TypeHandle);
                _checks++;
            }
            Type tooltipText = mod.GetType("Groundwork.FarmingSkillTooltipText", true);
            MethodInfo appendTooltip = tooltipText.GetMethod("Append", BindingFlags.Static | BindingFlags.NonPublic);
            string foragingRange = (string)appendTooltip.Invoke(null, new object[] { "", false, false, true, false, false, false });
            string cropRangeAndRespawn = (string)appendTooltip.Invoke(null, new object[] { "", false, false, true, true, true, false });
            Assert(foragingRange.Contains("$groundwork_skill_farming_foraging_range") &&
                   !foragingRange.Contains("$groundwork_skill_farming_foraging_crop_range"),
                "Foraging-only range tooltip");
            Assert(cropRangeAndRespawn.Contains("$groundwork_skill_farming_foraging_crop_both"),
                "Crop-inclusive range tooltip");
            Type cultivation = mod.GetType("Groundwork.CultivationSystem", true);
            MethodInfo normalizeRemovalPrefabs = cultivation.GetMethod(
                "NormalizeNaturalRemovalPrefabList", BindingFlags.Static | BindingFlags.NonPublic);
            string[] removalPrefabs = (string[])normalizeRemovalPrefabs.Invoke(null, new object[]
            {
                " Pickable_Branch,Pickable_Flint,pickable_branch, , Pickable_Thistle "
            });
            Assert(removalPrefabs.Length == 3 && removalPrefabs[0] == "Pickable_Branch" &&
                   removalPrefabs[1] == "Pickable_Flint" && removalPrefabs[2] == "Pickable_Thistle",
                "Natural removal prefab allowlist normalization");
            Type sync = mod.GetType("Groundwork.TerrainOperationSync", true);
            var write = (Action<TerrainOp.Settings, ZPackage>)Delegate.CreateDelegate(typeof(Action<TerrainOp.Settings, ZPackage>), sync.GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic));
            var read = (Func<TerrainOp.Settings, ZPackage, TerrainOp.Settings>)Delegate.CreateDelegate(typeof(Func<TerrainOp.Settings, ZPackage, TerrainOp.Settings>), sync.GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic));
            var original = new TerrainOp.Settings { m_paintStrength = 0.37f, m_halfOffset = false };
            var modified = new TerrainOp.Settings { m_levelRadius = 5, m_raiseRadius = 6, m_raiseDelta = -1.5f, m_smoothRadius = 7, m_paintRadius = 8, m_smooth = true };
            var pkg = new ZPackage();
            write(modified, pkg);
            Assert(pkg.Size() == 25, "Extension byte count");
            pkg.SetPos(0);
            var restored = read(original, pkg);
            Assert(!ReferenceEquals(original, restored), "Must clone shared prefab");
            foreach (string field in new[] { "m_levelRadius", "m_raiseRadius", "m_raiseDelta", "m_smoothRadius", "m_paintRadius", "m_smooth" })
            {
                var info = typeof(TerrainOp.Settings).GetField(field);
                Assert(Equals(info.GetValue(restored), info.GetValue(modified)), "Round trip: " + field);
            }
            Assert(original.m_levelRadius == 2, "Prefab unmodified");
            Assert(restored.m_paintStrength == original.m_paintStrength && !restored.m_halfOffset, "Preserve new vanilla fields");
            Assert(ReferenceEquals(read(original, new ZPackage()), original), "Vanilla packet");
            var foreign = new ZPackage(); foreign.Write(123); foreign.SetPos(0);
            Assert(ReferenceEquals(read(original, foreign), original) && foreign.GetPos() == 0, "Foreign extension");
            var truncated = new ZPackage(); truncated.Write(0x47575431); truncated.SetPos(0);
            Assert(read(original, truncated) == null, "Truncated extension");
            foreach (float bad in new[] { float.NaN, float.PositiveInfinity, -1f })
            {
                modified.m_levelRadius = bad;
                var invalid = new ZPackage(); write(modified, invalid); invalid.SetPos(0);
                Assert(read(original, invalid) == null, "Reject invalid radius " + bad);
            }
            var same = new ZPackage(); write(original, same); same.SetPos(0);
            Assert(ReferenceEquals(read(original, same), original), "Unchanged values avoid cloning");
            var framed = new ZPackage(); framed.Write(123); write(original, framed); framed.SetPos(4);
            Assert(ReferenceEquals(read(original, framed), original) && framed.GetPos() == framed.Size(), "Extension after vanilla packet prefix");
            VerifyGridPlacementSnapshot(mod);
            HarvestSkillProbe.Run(mod, Assert);
            VerifyPlantEverythingCompatibility(mod);
            System.Console.WriteLine(_checks + " original-DLL / actual Unity Mono managed checks passed.");
            System.Console.WriteLine("No Unity scene, socket, world, or plugin Awake was executed.");
            return 0;
        }
        catch (Exception error)
        {
            System.Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void VerifyPlantEverythingCompatibility(Assembly mod)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type compatibility = mod.GetType("Groundwork.PlantEverythingCompatSystem", true);
        PropertyInfo active = compatibility.GetProperty("IsActive", flags);
        MethodInfo setActive = active.GetSetMethod(true);
        bool originalActive = (bool)active.GetValue(null);
        Type growth = mod.GetType("Groundwork.GrowthOverrideSystem", true);
        MethodInfo parseFiles = growth.GetMethod("TryParseAndNormalizeFiles", flags);
        MethodInfo parseSynced = growth.GetMethod("TryParseAndNormalizeSyncedDocument", flags);
        MethodInfo watchesFile = growth.GetMethod("IsOverrideFilePath", flags);
        const string pickables = "- prefab: BlueberryBush, 17\n";
        const string plants = "- prefab: Carrot, 100~200\n";
        const string cultivation = "- prefab: RaspberryBush\n  resources: ['Raspberry, 1']\n";
        const string invalidCultivation = "broken: [";
        int CountRecipes(object rules) => ((IList)rules.GetType().GetProperty("Cultivation", instanceFlags).GetValue(rules)).Count;
        var metadata = mod.GetType("Groundwork.GroundworkPlugin", true).GetCustomAttributesData();
        Assert(!metadata.Any(x => x.AttributeType.Name == "BepInIncompatibility" &&
                   (string)x.ConstructorArguments[0].Value == "advize.PlantEverything"), "PE is no longer declared incompatible");
        Assert(metadata.Any(x => x.AttributeType.Name == "BepInDependency" &&
                   (string)x.ConstructorArguments[0].Value == "advize.PlantEverything" &&
                   (int)x.ConstructorArguments[1].Value == 2), "PE remains an optional dependency with ordered startup");
        try
        {
            setActive.Invoke(null, new object[] { false });
            object[] normal = { pickables, plants, cultivation, null, null };
            Assert((bool)parseFiles.Invoke(null, normal) && CountRecipes(normal[3]) == 1,
                "Without PE, cultivation recipes still normalize");
            object[] invalid = { pickables, plants, invalidCultivation, null, null };
            Assert(!(bool)parseFiles.Invoke(null, invalid), "Without PE, malformed cultivation is rejected");
            Assert((bool)watchesFile.Invoke(null, new object[] { "cultivation.yml" }), "Without PE, cultivation changes are watched");
            setActive.Invoke(null, new object[] { true });
            object[] ignored = { pickables, plants, invalidCultivation, null, null };
            Assert((bool)parseFiles.Invoke(null, ignored) && CountRecipes(ignored[3]) == 0,
                "With PE, malformed dormant cultivation cannot block growth overrides");
            string yaml = (string)ignored[3].GetType().GetProperty("Yaml", instanceFlags).GetValue(ignored[3]);
            Assert(yaml.Contains("BlueberryBush, 17") && yaml.Contains("Carrot, 100~200"),
                "With PE, explicit Pickable and Plant time overrides are preserved");
            Assert(!(bool)watchesFile.Invoke(null, new object[] { "cultivation.yml" }) &&
                   (bool)watchesFile.Invoke(null, new object[] { "pickables.yml" }) &&
                   (bool)watchesFile.Invoke(null, new object[] { "plants.yml" }), "With PE, only active growth files trigger reloads");
            object[] synced = { "pickables: []\nplants: []\ncultivation:\n- prefab: RaspberryBush\n  plantable: true\n  resources: []\n", null, null };
            Assert((bool)parseSynced.Invoke(null, synced) && CountRecipes(synced[1]) == 0,
                "With PE, synced cultivation is also disabled");
            foreach (string patchName in new[] { "PickableCultivationPersistencePatch", "PickableCultivationFirstCyclePatch",
                         "CultivationRemoveProtectionPatch", "CultivationRemovalRayPatch" })
            {
                Type patch = mod.GetType("Groundwork." + patchName, true);
                var cultivationHarmony = new Harmony("Groundwork.Tests.Cultivation");
                try
                {
                    var skipped = cultivationHarmony.CreateClassProcessor(patch).Patch();
                    Assert(skipped == null || skipped.Count == 0, "With PE, Harmony does not install " + patchName);
                }
                finally
                {
                    cultivationHarmony.UnpatchSelf();
                }
            }

            string pePath = Path.Combine(Path.GetDirectoryName(mod.Location), "Advize_PlantEverything.dll");
            if (!File.Exists(pePath))
            {
                System.Console.WriteLine("PE DLL not supplied; its actual hover detour checks were not run.");
                return;
            }

            Assembly pe = Assembly.LoadFrom(pePath);
            Type hoverPatches = pe.GetType("Advize_PlantEverything.HoverTextPatches", true);
            var harmony = new Harmony("Groundwork.Tests.PlantEverything");
            MethodInfo[] postfixes = new[] { typeof(Pickable), typeof(Plant) }.Select(target =>
                hoverPatches.GetMethod("Postfix", flags, null, new[] { target, typeof(string).MakeByRefType() }, null)).ToArray();
            try
            {
                // Install PE's real postfixes on original game methods, but skip the native
                // vanilla body. No PE Awake, Unity scene, or player/world state is initialized.
                foreach (Type target in new[] { typeof(Pickable), typeof(Plant) })
                {
                    MethodInfo postfix = postfixes.Single(x => x.GetParameters()[0].ParameterType == target);
                    harmony.Patch(target.GetMethod("GetHoverText"),
                        prefix: new HarmonyMethod(typeof(CompatibilityProbe).GetMethod("KeepBaseHoverText", flags)),
                        postfix: new HarmonyMethod(postfix));
                }
                Assert((bool)compatibility.GetMethod("PatchHoverTimers", flags).Invoke(null, new object[] { harmony, pe }),
                    "Both real PE hover postfix signatures are supported");
                foreach (Type target in new[] { typeof(Pickable), typeof(Plant) })
                {
                    object instance = FormatterServices.GetUninitializedObject(target);
                    Assert((string)target.GetMethod("GetHoverText").Invoke(instance, null) == "base hover",
                        "PE " + target.Name + " timer is skipped inside the patched game hover method");
                }
            }
            finally
            {
                harmony.UnpatchSelf();
            }
            Assert(postfixes.All(method => Harmony.GetPatchInfo(method)?.Owners.Contains(harmony.Id) != true),
                "Unloading restores PE hover methods");
        }
        finally
        {
            setActive.Invoke(null, new object[] { originalActive });
        }
    }

    private static bool KeepBaseHoverText(ref string __result)
    {
        __result = "base hover";
        return false;
    }

    private static void VerifyGridPlacementSnapshot(Assembly mod)
    {
        // Exercise managed click state without initializing the outer system's Unity shaders.
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type terrain = mod.GetType("Groundwork.TerrainToolRangeSystem", true);
        Type operation = terrain.GetNestedType("GridPreviewOperationState", BindingFlags.NonPublic);
        Type preview = terrain.GetNestedType("GridPreviewState", BindingFlags.NonPublic);
        Type click = terrain.GetNestedType("ActiveGridPlacementState", BindingFlags.NonPublic);
        object Construct(Type type, params object[] values) => Activator.CreateInstance(type, instance, null, values, null);
        var firstPosition = new UnityEngine.Vector3(1, 2, 3);
        var secondPosition = new UnityEngine.Vector3(4, 5, 6);
        Array operations = Array.CreateInstance(operation, 2);
        operations.SetValue(Construct(operation, "first", firstPosition), 0);
        operations.SetValue(Construct(operation, "second", secondPosition), 1);
        object snapshot = Construct(preview, "Hoe:raise_v2", operations);
        operations.SetValue(Construct(operation, "changed", new UnityEngine.Vector3(99, 99, 99)), 0);
        object firstClick = Construct(click, snapshot);
        object nextClick = Construct(click, snapshot);
        MethodInfo consume = click.GetMethod("TryConsume", instance);
        bool Consume(object state, string path, out UnityEngine.Vector3 position)
        {
            object[] args = { path, null };
            bool result = (bool)consume.Invoke(state, args);
            position = (UnityEngine.Vector3)args[1];
            return result;
        }

        Assert(!Consume(firstClick, "unknown", out _), "Ambiguous grid operations must not use fallback");
        Assert(Consume(firstClick, "first", out var actual) && actual.Equals(firstPosition), "Grid snapshot preserves the preview position");
        Assert(Consume(nextClick, "first", out actual) && actual.Equals(firstPosition), "Each click consumes its own snapshot state");
        Assert(Consume(firstClick, "second", out actual) && actual.Equals(secondPosition), "Grid operation path match");
        Assert(!Consume(firstClick, "second", out _), "Consumed grid operations cannot be reused");
        Assert(Consume(nextClick, "unknown", out actual) && actual.Equals(secondPosition), "A sole remaining grid operation is the fallback");
        Assert(!Consume(nextClick, "unknown", out _), "Grid fallback is consumed only once");
    }
}
