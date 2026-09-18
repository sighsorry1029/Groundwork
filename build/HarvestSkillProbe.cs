using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;

namespace Groundwork.Tests;

internal static class HarvestSkillProbe
{
    private const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags HiddenInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Assembly mod, Action<bool, string> check)
    {
        Type sync = mod.GetType("Groundwork.HarvestSkillSync", true);
        MethodInfo read = sync.GetMethod("ReadSnapshot", HiddenStatic);
        int marker = (int)sync.GetField("Marker", HiddenStatic).GetRawConstantValue();
        ZDOID actor = new ZDOID(123L, 45u);
        Func<int, float, ZPackage> packet = (prefix, level) =>
        {
            var result = new ZPackage();
            object[] parameters = prefix == 4
                ? new object[] { 2, marker, 19L, 7L, actor, level }
                : new object[] { marker, 19L, 7L, actor, level };
            ZRpc.Serialize(parameters, ref result);
            result.SetPos(0);
            return result;
        };
        foreach (int prefix in new[] { 0, 4 })
        {
            var pkg = packet(prefix, 36f);
            object[] args = { pkg, prefix, null, false };
            check((bool)read.Invoke(null, args) && (bool)args[3], "Recognize harvest snapshot");
            object snapshot = args[2];
            check((float)snapshot.GetType().GetField("Level", HiddenInstance).GetValue(snapshot) == 36f,
                "Harvest actor level 36 survives wire round trip");
            check((ZDOID)snapshot.GetType().GetField("Actor", HiddenInstance).GetValue(snapshot) == actor,
                "Harvest actor identity survives wire round trip");
            check(pkg.GetPos() == 0, "Inspection preserves vanilla RPC cursor");
            MethodInfo original = prefix == 4
                ? AccessTools.DeclaredMethod(typeof(Pickable), "RPC_Pick")
                : AccessTools.DeclaredMethod(typeof(Beehive), "RPC_Extract");
            object[] decoded = ZNetView.Deserialize(123L, original.GetParameters(), pkg);
            check((long)decoded[0] == 123L && (prefix == 0 ? decoded.Length == 1 : (int)decoded[1] == 2),
                "Original game RPC deserializer retains sender and vanilla arguments with appended snapshot");
        }

        foreach (float level in new[] { 0f, 1f, 100f })
        {
            object[] args = { packet(4, level), 4, null, false };
            check((bool)read.Invoke(null, args), "Legitimate low/high skill is not treated as missing: " + level);
        }
        foreach (float level in new[] { -1f, 101f, float.NaN, float.PositiveInfinity })
        {
            object[] args = { packet(4, level), 4, null, false };
            check(!(bool)read.Invoke(null, args) && (bool)args[3], "Reject invalid harvest skill: " + level);
        }
        var truncated = new ZPackage(); truncated.Write(2); truncated.Write(marker); truncated.SetPos(0);
        object[] shortArgs = { truncated, 4, null, false };
        check(!(bool)read.Invoke(null, shortArgs) && (bool)shortArgs[3] && truncated.GetPos() == 0,
            "Recognized truncated snapshot fails closed without consuming vanilla bonus");
        var vanilla = new ZPackage(); vanilla.Write(2); vanilla.SetPos(0);
        object[] vanillaArgs = { vanilla, 4, null, false };
        check(!(bool)read.Invoke(null, vanillaArgs) && !(bool)vanillaArgs[3], "Unextended vanilla request is not a zero-skill snapshot");
        var foreign = new ZPackage(); foreign.Write(2); foreign.Write(456); foreign.SetPos(0);
        object[] foreignArgs = { foreign, 4, null, false };
        check(!(bool)read.Invoke(null, foreignArgs) && !(bool)foreignArgs[3] && foreign.GetPos() == 0,
            "Foreign RPC extension is preserved");

        foreach (var invalid in new[]
        {
            new { Id = 0L, Revision = 7L, Actor = actor },
            new { Id = 19L, Revision = -1L, Actor = actor },
            new { Id = 19L, Revision = long.MaxValue, Actor = actor },
            new { Id = 19L, Revision = 7L, Actor = ZDOID.None }
        })
        {
            var pkg = new ZPackage();
            ZRpc.Serialize(new object[] { marker, invalid.Id, invalid.Revision, invalid.Actor, 36f }, ref pkg);
            pkg.SetPos(0);
            object[] args = { pkg, 0, null, false };
            check(!(bool)read.Invoke(null, args) && (bool)args[3], "Reject invalid request identity/revision");
        }
        var tailed = packet(4, 36f);
        tailed.SetPos(tailed.Size()); tailed.Write(12345); tailed.SetPos(4);
        object[] tailedArgs = { tailed, 4, null, false };
        check((bool)read.Invoke(null, tailedArgs) && tailed.GetPos() == 4,
            "Additional trailing metadata and a nonzero original cursor are preserved");

        Type ledgerType = sync.GetNestedType("ReceiptLedger", BindingFlags.NonPublic);
        object ledger = Activator.CreateInstance(ledgerType, true);
        MethodInfo add = ledgerType.GetMethod("Add", HiddenInstance);
        MethodInfo take = ledgerType.GetMethod("Take", HiddenInstance);
        Action<long, float> send = (id, time) => add.Invoke(ledger, new object[] { id, 900L, actor, time });
        Func<long, long, ZDOID, float, bool> receive = (id, owner, player, time) =>
            (bool)take.Invoke(ledger, new object[] { id, owner, player, time });
        send(1, 10);
        check(!receive(1, 901, actor, 11), "Unexpected peer cannot award honey XP");
        check(!receive(1, 900, new ZDOID(123L, 46u), 11), "Receipt cannot award another character");
        check(receive(1, 900, actor, 11), "Expected owner awards the requesting character");
        check(!receive(1, 900, actor, 12), "Duplicate receipt cannot award XP twice");
        send(2, 10);
        check(!receive(2, 900, actor, 41), "Expired receipt cannot award XP");
        send(3, 50);
        ledgerType.GetMethod("Clear", HiddenInstance).Invoke(ledger, null);
        check(!receive(3, 900, actor, 51), "Disconnect clears outstanding awards");
        for (int i = 10; i < 139; i++) send(i, 60);
        check(!receive(10, 900, actor, 61) && receive(138, 900, actor, 61), "Receipt storage remains bounded");

        // Nested broadcasts (e.g. RPC_SetPicked) must not inherit the outer harvest identity,
        // and must restore that identity when they return. No Unity object is needed here.
        Type requestType = sync.GetNestedType("Request", BindingFlags.NonPublic);
        object outer = FormatterServices.GetUninitializedObject(requestType);
        MethodInfo end = sync.GetMethod("End", HiddenStatic);
        end.Invoke(null, new[] { outer });
        object[] nested = { null, new ZRoutedRpc.RoutedRPCData { m_methodHash = 6789 }, null };
        check((bool)sync.GetMethod("Begin", HiddenStatic).Invoke(null, nested) && ReferenceEquals(nested[2], outer),
            "Nested unrelated RPC preserves outer request for finalizer");
        check(sync.GetField("_current", HiddenStatic).GetValue(null) == null, "Nested RPC cannot consume outer snapshot");
        end.Invoke(null, new[] { nested[2] });
        check(ReferenceEquals(sync.GetField("_current", HiddenStatic).GetValue(null), outer), "Finalizer restores outer request");
        end.Invoke(null, new object[] { null });

        foreach (bool pick in new[] { true, false })
        {
            MethodInfo original = pick
                ? AccessTools.DeclaredMethod(typeof(Pickable), "Interact")
                : AccessTools.DeclaredMethod(typeof(Beehive), "Extract");
            var instructions = PatchProcessor.GetOriginalInstructions(original).ToList();
            var patched = ((IEnumerable<CodeInstruction>)sync.GetMethod("ReplaceSend", HiddenStatic)
                .Invoke(null, new object[] { instructions, pick })).ToList();
            string replacement = pick ? "SendPick" : "Send";
            int index = patched.FindIndex(i => i.operand is MethodInfo m && m.DeclaringType == sync && m.Name == replacement);
            check(index >= 0 && patched.Count(i => i.operand is MethodInfo m && m.DeclaringType == sync && m.Name == replacement) == 1,
                "Original game IL has exactly one replaceable harvest send: " + original.Name);
            check(!pick || patched[index - 1].opcode == OpCodes.Ldarg_1, "Pick request passes the actual interacting character");
        }

    }
}
