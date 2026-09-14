using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

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
}
