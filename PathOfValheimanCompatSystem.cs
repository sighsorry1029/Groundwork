using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace Groundwork;

// PoV owns progression; Groundwork can provide planting controls for its MassPlant ability.
internal static class PathOfValheimanCompatSystem
{
    internal const string PluginGuid = "treextr.pathofvalheiman";
    private static readonly int RespawnRateKey = "Groundwork_PoVRespawnRate".GetStableHashCode();
    internal static bool IsActive { get; private set; }
    internal static Player? ReplantPlayer { get; private set; }
    private static Func<bool>? _massPlantAllowed;
    private static Func<string, bool>? _hasPovStat;
    private static Func<float>? _pickupRadius;
    private static AccessTools.FieldRef<bool>? _massPicking;
    private static Func<Beehive, int>? _hiveCapacity;
    private static Func<Beehive, float>? _meadowBonus;
    private static Func<ZNetView, string, float>? _creditBonus;
    private static Func<Beehive, bool>? _checkBiome;

    internal static void Initialize(Harmony harmony)
    {
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin)) return;
        TryInstall(harmony, plugin.Instance.GetType().Assembly, plugin.Metadata.Version,
            GroundworkPlugin.ModLogger.LogInfo, GroundworkPlugin.ModLogger.LogWarning);
    }

    internal static bool TryInstall(Harmony harmony, Assembly assembly, System.Version version,
        Action<string> logInfo, Action<string> logWarning)
    {
        // Version is diagnostic only. The callback signatures and IL below decide compatibility.
        if (version != new System.Version(5, 0, 4))
        {
            logWarning($"Path of Valheiman {version} differs from the reviewed 5.0.4. Checking callback signatures and IL; matching structure does not establish in-game compatibility for this version.");
        }

        try
        {
            Install(harmony, assembly);
            logInfo($"Path of Valheiman {version} compatibility active after callback checks: shared planting/harvesting, growth and hive calculations coordinated; PoV progression and cultivation recipes preserved.");
            return true;
        }
        catch (Exception ex)
        {
            logWarning($"Path of Valheiman {version} compatibility hook installation failed: {ex.Message}");
            return false;
        }
    }

    // Separate from plugin discovery so the actual supplied DLL can be checked in isolated Mono.
    internal static void Install(Harmony harmony, Assembly assembly)
    {
        var installed = new List<MethodInfo>();
        MethodInfo Find(string type, string name, Type result, params Type[] args)
        {
            MethodInfo? method = AccessTools.DeclaredMethod(assembly.GetType("PassiveTree." + type, true), name, args);
            if (method == null || !method.IsStatic || method.ReturnType != result)
                throw new MissingMethodException(type, name);
            return method;
        }
        void Patch(MethodInfo method, string? prefix = null, string? finalizer = null, string? transpiler = null, string? postfix = null)
        {
            HarmonyMethod? Hook(string? name) => name == null ? null : new HarmonyMethod(typeof(PathOfValheimanCompatSystem), name);
            installed.Add(method);
            try { harmony.Patch(method, Hook(prefix), Hook(postfix), Hook(transpiler), Hook(finalizer), null); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{method.DeclaringType?.FullName}.{method.Name}: {ex.GetBaseException().Message}", ex);
            }
        }

        try
        {
            MethodInfo massPlantAllowed = Find("MassPlant", "get_Allowed", typeof(bool));
            _massPlantAllowed = AccessTools.MethodDelegate<Func<bool>>(massPlantAllowed);
            Type gathering = assembly.GetType("PassiveTree.Gathering", true)!;
            _massPicking = AccessTools.StaticFieldRefAccess<bool>(AccessTools.DeclaredField(gathering, "massPicking"));
            Type stat = assembly.GetType("PassiveTree.Stat", true)!;
            Type stats = assembly.GetType("PassiveTree.StatSet", true)!;
            var value = Expression.Variable(stats, "stats");
            _pickupRadius = Expression.Lambda<Func<float>>(Expression.Block(new[] { value },
                Expression.Assign(value, Expression.Call(Find("Gathering", "get_S", stats))),
                Expression.Condition(Expression.Equal(value, Expression.Constant(null, stats)), Expression.Constant(0f),
                    Expression.Call(value, AccessTools.DeclaredMethod(stats, "Get", new[] { stat }),
                        Expression.Constant(Enum.Parse(stat, "MassPickupRadius"), stat))))).Compile();
            _hiveCapacity = AccessTools.MethodDelegate<Func<Beehive, int>>(Find("CraftBee", "Max", typeof(int), typeof(Beehive)));
            _meadowBonus = AccessTools.MethodDelegate<Func<Beehive, float>>(Find("CraftBee", "MeadowBonus", typeof(float), typeof(Beehive)));
            _creditBonus = AccessTools.MethodDelegate<Func<ZNetView, string, float>>(Find("Credit", "Bonus", typeof(float), typeof(ZNetView), typeof(string)));
            _checkBiome = AccessTools.MethodDelegate<Func<Beehive, bool>>(AccessTools.DeclaredMethod(typeof(Beehive), "CheckBiome", Type.EmptyTypes));

            Patch(Find("Gathering+Pickable_Interact", "Postfix", typeof(void), typeof(Pickable), typeof(Humanoid), typeof(bool)),
                prefix: nameof(BeginGather), finalizer: nameof(EndGather));
            Patch(Find("AirHarvest", "Tick", typeof(void)),
                prefix: nameof(BeginAirHarvest), finalizer: nameof(EndAirHarvest));
            Patch(Find("Farming+Plant_GetGrowTime", "Postfix", typeof(void), typeof(Plant), typeof(float).MakeByRefType()),
                prefix: nameof(ApplyPlantBaseTime));
            Patch(Find("Farming+Pickable_Interact_Wild", "Postfix", typeof(void), typeof(Pickable), typeof(Humanoid), typeof(bool)),
                transpiler: nameof(StoreRespawnRateInsteadOfBackdating));
            Patch(Find("CraftFarmer", "Replant", typeof(void), typeof(Pickable), typeof(Player)),
                prefix: nameof(BeginReplant), finalizer: nameof(EndReplant));
            Patch(Find("CraftFarmer+Plant_Hover", "Postfix", typeof(void), typeof(Plant), typeof(string).MakeByRefType()),
                prefix: nameof(AllowBedWatchTimer), transpiler: nameof(UseRemainingGrowthTime));
            Patch(Find("MassPlant", "Free", typeof(bool), typeof(Plant), typeof(Vector3)), postfix: nameof(CheckPlantBiome));

            Type borrowed = assembly.GetType("PassiveTree.Borrowed", true)!;
            Patch(Find("Conveniences+Beehive_UpdateBees", "Prefix", typeof(void), typeof(Beehive), borrowed.MakeByRefType()),
                transpiler: nameof(KeepHiveTimeUnmodified));
            foreach (string type in new[] { "CraftBee+Hive_Update", "CraftBee+Hive_Hover" })
                Patch(Find(type, "Prefix", typeof(void), typeof(Beehive), typeof(int).MakeByRefType()),
                    transpiler: nameof(KeepHiveCapacityUnmodified));
            Patch(Find("CraftBee", "Swarming", typeof(void), typeof(Beehive)), transpiler: nameof(UseEffectiveHiveCapacity));
            Patch(Find("CraftWorkHooks+Honey", "Postfix", typeof(void), typeof(Beehive), typeof(int)),
                transpiler: nameof(UseEffectiveHiveCapacity));

            // Older PoV releases without the public stats API keep the existing handoff.
            // Bind once, but read the live ability so refunds and character changes need no cache.
            Type? publicStats = assembly.GetType("Valheiman.Api.Stats");
            if (publicStats != null)
            {
                MethodInfo? hasStat = publicStats.GetMethod("Has", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string) }, null);
                if (hasStat == null || hasStat.ReturnType != typeof(bool))
                    throw new MissingMethodException("Valheiman.Api.Stats", "Has(string)");
                _hasPovStat = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), hasStat);
                Patch(massPlantAllowed, postfix: nameof(UseGroundworkPlanting));
            }
            IsActive = true;
        }
        catch (Exception installError)
        {
            // Never leave only the field-neutralizing half of an adapter installed.
            var errors = new List<Exception> { installError };
            foreach (MethodInfo method in installed.AsEnumerable().Reverse())
            {
                try { harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); }
                catch (Exception rollbackError) { errors.Add(rollbackError); }
            }
            Shutdown();
            if (errors.Count > 1) throw new AggregateException("PoV hook installation and rollback failed.", errors);
            throw;
        }
    }

    internal static void Shutdown()
    {
        IsActive = false;
        ReplantPlayer = null;
        _massPlantAllowed = null;
        _hasPovStat = null;
        _pickupRadius = null;
        _massPicking = null;
        _hiveCapacity = null;
        _meadowBonus = null;
        _creditBonus = null;
        _checkBiome = null;
    }

    internal static bool OwnsPlanting(Player player, Piece? piece = null)
    {
        if (!IsActive || UsesMassPlantingNodes || player == null || player != Player.m_localPlayer || !_massPlantAllowed!()) return false;
        GameObject? target = piece != null ? piece.gameObject : GameAccess.PlacementGhost(player);
        return target != null && target.GetComponent<Plant>() != null;
    }

    internal static bool UsesMassPlantingNodes => IsActive && _hasPovStat != null;
    internal static bool MassPlantingUnlocked => UsesMassPlantingNodes && _hasPovStat!("MassPlant");

    private static void UseGroundworkPlanting(ref bool __result)
    {
        // This gate is shared by PoV's additional placements, wheel input and preview.
        // Leave its finalizers and Clear path intact; GW's toggle governs multi-plant placement.
        if (UsesMassPlantingNodes) __result = false;
    }

    internal static bool OwnsRangeHarvest => IsActive && (_massPicking!() || _pickupRadius!() > 0f);

    private static void BeginGather(out bool? __state)
    {
        __state = null;
        if (!FarmingSkillSystem.RangePickupSuppressed) return;
        __state = _massPicking!();
        _massPicking!() = true;
    }

    private static void EndGather(bool? __state)
    {
        if (__state.HasValue) _massPicking!() = __state.Value;
    }

    private static void ApplyPlantBaseTime(Plant __0, ref float __1) => FarmingSkillSystem.ApplyConfiguredPlantGrowTime(__0, ref __1);

    private static void BeginReplant(Player __1, out Player? __state)
    {
        __state = ReplantPlayer;
        ReplantPlayer = __1;
    }

    private static void EndReplant(Player? __state) => ReplantPlayer = __state;

    private static bool AllowBedWatchTimer(Plant __0) => GroundworkToolsDomain.CropHoverHintMode == GroundworkPlugin.HoverHintMode.Off ||
        __0 == null || __0.GetStatus() != Plant.Status.Healthy;

    private static void BeginAirHarvest(out FarmingSkillSystem.RangePickupSuppression __state) => __state = FarmingSkillSystem.SuppressRangePickup();
    private static void EndAirHarvest(FarmingSkillSystem.RangePickupSuppression __state) => __state.Dispose();

    private static double RemainingGrowthTime(Plant plant) => FarmingSkillSystem.TryGetPlantRemainingGrowthSeconds(plant, out float seconds)
        ? seconds : GameAccess.GrowTime(plant) - GameAccess.TimeSincePlanted(plant);

    private static void CheckPlantBiome(Plant __0, Vector3 __1, ref bool __result)
    {
        // Only explicit administrator overrides outrank PoV's own planting policy.
        if (__result && GrowthOverrideSystem.TryGetPlantRule(__0, out var rule) && rule.HasBiomeOverride)
            __result = GrowthOverrideSystem.IsBiomeAllowed(rule.BiomeMask, Heightmap.FindHeightmap(__1), __1);
    }

    internal static int HiveBaseCapacity(Beehive hive) => IsActive ? _hiveCapacity!(hive) : hive.m_maxHoney;
    private static int EffectiveHiveCapacity(Beehive hive) => BeehivePollinationSystem.GetEffectiveMaxHoney(hive);
    internal static bool CheckHiveBiome(Beehive hive) => _checkBiome!(hive);

    internal static float HiveSpeedMultiplier(Beehive hive)
    {
        if (!IsActive) return 1f;
        float bonus = _creditBonus!(GameAccess.BeehiveView(hive), "hive") + _meadowBonus!(hive);
        // Match Borrowed.Faster, including validation of the sum rather than its individual terms.
        return bonus > 0f && !float.IsNaN(bonus) && !float.IsInfinity(bonus) ? 1f + bonus : 1f;
    }

    private static void StoreRespawnRate(ZDO zdo, int unusedKey, long unusedTime, float multiplier)
    {
        // Executed at PoV's original owner-checked write site. Keep vanilla's actual cycle start.
        zdo.Set(RespawnRateKey, ValidRate(multiplier));
    }

    private static float ValidRate(float rate) => rate > 1f && !float.IsNaN(rate) && !float.IsInfinity(rate) ? rate : 1f;

    internal static float RespawnRate(ZDO zdo) => IsActive ? ValidRate(zdo.GetFloat(RespawnRateKey, 1f)) : 1f;

    internal static void ResetRespawnRate(Pickable pickable)
    {
        if (!IsActive || pickable == null) return;
        ZNetView view = GameAccess.PickableView(pickable);
        if (view != null && view.IsValid() && view.IsOwner() && view.GetZDO().GetFloat(RespawnRateKey, 1f) != 1f)
            view.GetZDO().Set(RespawnRateKey, 1f);
    }

    private static int SingleMatch(List<CodeInstruction> code, Predicate<CodeInstruction> match)
    {
        int index = code.FindIndex(match);
        if (index < 0 || code.FindIndex(index + 1, match) >= 0)
            throw new InvalidOperationException("PoV callback IL did not match the reviewed single operation.");
        return index;
    }

    private static MethodInfo Helper(string name) => AccessTools.DeclaredMethod(typeof(PathOfValheimanCompatSystem), name);

    private static IEnumerable<CodeInstruction> KeepHiveTimeUnmodified(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int index = SingleMatch(code, c => c.opcode == OpCodes.Call && c.operand is MethodInfo m && m.IsStatic &&
            m.DeclaringType?.FullName == "PassiveTree.Borrowed" && m.ReturnType == m.DeclaringType &&
            m.Name == "Faster" && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(float).MakeByRefType(), typeof(float) }));
        var pop = new CodeInstruction(OpCodes.Pop).MoveLabelsFrom(code[index]).MoveBlocksFrom(code[index]);
        code.InsertRange(index, new[] { pop, new CodeInstruction(OpCodes.Ldc_R4, 0f) });
        return code;
    }

    private static bool CallsHiveMax(CodeInstruction c) => c.opcode == OpCodes.Call && c.operand is MethodInfo m && m.IsStatic && m.DeclaringType?.FullName == "PassiveTree.CraftBee" &&
        m.Name == "Max" && m.ReturnType == typeof(int) && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Beehive) });

    private static IEnumerable<CodeInstruction> KeepHiveCapacityUnmodified(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int index = SingleMatch(code, CallsHiveMax);
        code[index].opcode = OpCodes.Ldfld;
        code[index].operand = AccessTools.Field(typeof(Beehive), nameof(Beehive.m_maxHoney));
        return code;
    }

    private static IEnumerable<CodeInstruction> UseEffectiveHiveCapacity(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        FieldInfo capacity = AccessTools.Field(typeof(Beehive), nameof(Beehive.m_maxHoney));
        int index = SingleMatch(code, c => CallsHiveMax(c) || c.LoadsField(capacity));
        code[index].opcode = OpCodes.Call;
        code[index].operand = Helper(nameof(EffectiveHiveCapacity));
        return code;
    }

    private static IEnumerable<CodeInstruction> StoreRespawnRateInsteadOfBackdating(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int apply = SingleMatch(code, c => (c.opcode == OpCodes.Call || c.opcode == OpCodes.Callvirt) &&
            c.operand is MethodInfo m && !m.IsStatic && m.DeclaringType?.FullName == "PassiveTree.StatSet" &&
            m.Name == "Apply" && m.ReturnType == typeof(float) && m.GetParameters() is var args && args.Length == 2 &&
            args[0].ParameterType.FullName == "PassiveTree.Stat" && args[0].ParameterType.IsEnum && args[1].ParameterType == typeof(float));
        if (apply + 1 >= code.Count)
            throw new InvalidOperationException("PoV wild growth multiplier local was not found.");
        CodeInstruction store = code[apply + 1];
        CodeInstruction load = store.opcode == OpCodes.Stloc_0 ? new CodeInstruction(OpCodes.Ldloc_0) :
            store.opcode == OpCodes.Stloc_1 ? new CodeInstruction(OpCodes.Ldloc_1) :
            store.opcode == OpCodes.Stloc_2 ? new CodeInstruction(OpCodes.Ldloc_2) :
            store.opcode == OpCodes.Stloc_3 ? new CodeInstruction(OpCodes.Ldloc_3) :
            store.opcode == OpCodes.Stloc || store.opcode == OpCodes.Stloc_S ? new CodeInstruction(OpCodes.Ldloc, store.operand) :
            throw new InvalidOperationException("PoV wild growth multiplier local was not found.");
        SingleMatch(code, c => c.LoadsField(AccessTools.Field(typeof(ZDOVars), nameof(ZDOVars.s_pickedTime))));
        int set = SingleMatch(code, c => c.Calls(AccessTools.Method(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(int), typeof(long) })));
        load.MoveLabelsFrom(code[set]).MoveBlocksFrom(code[set]);
        code[set].opcode = OpCodes.Call;
        code[set].operand = Helper(nameof(StoreRespawnRate));
        code.Insert(set, load);
        return code;
    }

    private static IEnumerable<CodeInstruction> UseRemainingGrowthTime(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int grow = SingleMatch(code, c => c.Calls(AccessTools.DeclaredMethod(typeof(Plant), "GetGrowTime")));
        if (grow + 4 >= code.Count || code[grow + 1].opcode != OpCodes.Conv_R8 || code[grow + 2].opcode != OpCodes.Ldarg_0 ||
            !code[grow + 3].Calls(AccessTools.DeclaredMethod(typeof(Plant), "TimeSincePlanted")) || code[grow + 4].opcode != OpCodes.Sub)
            throw new InvalidOperationException("PoV Bed Watch timer expression changed.");
        code[grow].opcode = OpCodes.Call;
        code[grow].operand = Helper(nameof(RemainingGrowthTime));
        for (int i = grow + 1; i <= grow + 4; i++)
        {
            code[i].opcode = OpCodes.Nop;
            code[i].operand = null;
        }
        return code;
    }
}
