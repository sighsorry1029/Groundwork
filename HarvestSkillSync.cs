using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Groundwork;

// Skills belong to the acting client, while harvest state belongs to the target's owner.
// Keep vanilla RPC names/arguments and append one versioned, action-scoped snapshot.
internal static class HarvestSkillSync
{
    internal const int Marker = 0x47574831; // GWH1
    private const int ExtensionBytes = 36;
    private const string RevisionKey = "Groundwork_HarvestRevisionV1";
    private const string ReceiptRpc = "Groundwork_HoneyReceiptV1";
    private static readonly int PickRpcHash = "RPC_Pick".GetStableHashCode();
    private static readonly int ExtractRpcHash = "RPC_Extract".GetStableHashCode();
    internal static readonly MethodInfo VanillaSend = AccessTools.DeclaredMethod(
        typeof(ZNetView), nameof(ZNetView.InvokeRPC), [typeof(string), typeof(object[])]);
    private static long _nextRequest;
    private static readonly ReceiptLedger Receipts = new();
    [ThreadStatic] private static Request? _current;

    internal sealed class Request
    {
        internal readonly ZNetView View;
        internal readonly ZDO Zdo;
        internal readonly Player Actor;
        internal readonly long Sender;
        internal readonly Snapshot Data;
        internal Request(ZNetView view, Player actor, long sender, Snapshot data)
        {
            View = view;
            Zdo = view.GetZDO();
            Actor = actor;
            Sender = sender;
            Data = data;
        }
    }

    internal readonly struct Snapshot(long id, long revision, ZDOID actor, float level)
    {
        internal readonly long Id = id;
        internal readonly long Revision = revision;
        internal readonly ZDOID Actor = actor;
        internal readonly float Level = level;
    }

    // Missing/foreign extensions retain vanilla behavior. Recognized malformed packets fail closed.
    // Restore the cursor: vanilla/other patches must still deserialize the original arguments.
    internal static bool ReadSnapshot(ZPackage package, int vanillaBytes, out Snapshot data, out bool recognized)
    {
        data = default;
        recognized = false;
        int position = package.GetPos();
        try
        {
            if (package.Size() < vanillaBytes + 4) return false;
            package.SetPos(vanillaBytes);
            if (package.ReadInt() != Marker) return false;
            recognized = true;
            if (package.Size() < vanillaBytes + ExtensionBytes) return false;
            data = new Snapshot(package.ReadLong(), package.ReadLong(), package.ReadZDOID(), package.ReadSingle());
            return data.Id > 0 && data.Revision >= 0 && data.Revision < long.MaxValue &&
                   data.Actor != ZDOID.None && ValidLevel(data.Level);
        }
        finally
        {
            package.SetPos(position);
        }
    }

    internal static bool ValidLevel(float level) => !float.IsNaN(level) && !float.IsInfinity(level) && level >= 0f && level <= 100f;

    internal static void SendPick(ZNetView view, string method, object[] arguments, Humanoid actor)
    {
        if (actor != null && actor == Player.m_localPlayer)
            Send(view, method, arguments);
        else
            view.InvokeRPC(method, arguments);
    }

    internal static void Send(ZNetView view, string method, object[] arguments)
    {
        Player? player = Player.m_localPlayer;
        ZNetView? playerView = player != null ? GameAccess.CharacterView(player) : null;
        if (view == null || !view.IsValid() || playerView == null || !playerView.IsValid() || !playerView.IsOwner())
        {
            // Calls without a local actor cannot supply an authoritative skill snapshot.
            if (view != null && view.IsValid()) view.InvokeRPC(method, arguments);
            return;
        }

        float level = Mathf.Clamp(player!.GetSkillLevel(Skills.SkillType.Farming), 0f, 100f);
        if (!ValidLevel(level)) return;
        long owner = view.GetZDO().GetOwner();
        if (owner == 0L) return;
        long id = ++_nextRequest;
        long revision = view.GetZDO().GetLong(RevisionKey, 0L);
        object[] extended = new object[arguments.Length + 5];
        Array.Copy(arguments, extended, arguments.Length);
        extended[arguments.Length] = Marker;
        extended[arguments.Length + 1] = id;
        extended[arguments.Length + 2] = revision;
        extended[arguments.Length + 3] = player.GetZDOID();
        extended[arguments.Length + 4] = level;
        if (method == "RPC_Extract")
        {
            // Register before sending: a locally owned hive responds synchronously.
            Receipts.Add(id, owner, player.GetZDOID(), Time.realtimeSinceStartup);
        }
        view.InvokeRPC(owner, method, extended);
    }

    internal static bool Begin(ZNetView view, ZRoutedRpc.RoutedRPCData rpc, out Request? previous)
    {
        previous = _current;
        _current = null; // Nested RPCs must never inherit a different action's skill.
        bool pick = rpc.m_methodHash == PickRpcHash;
        if (!pick && rpc.m_methodHash != ExtractRpcHash) return true;
        if (!ReadSnapshot(rpc.m_parameters, pick ? 4 : 0, out Snapshot data, out bool recognized))
            return !recognized;
        if (view == null || !view.IsValid() || !view.IsOwner() ||
            rpc.m_targetZDO != view.GetZDO().m_uid ||
            view.GetZDO().GetLong(RevisionKey, 0L) != data.Revision)
            return false;

        Player? actor = FindActor(data.Actor, rpc.m_senderPeerID);
        if (actor == null) return false;
        // Ward/distance/interaction checks stay in the original interaction paths. Do not
        // run PrivateArea.CheckAccess here: it checks this peer's local player, not the sender.
        if (pick)
        {
            Pickable? target = view.GetComponent<Pickable>();
            if (target == null || target.GetPicked()) return false;
        }
        else
        {
            Beehive? target = view.GetComponent<Beehive>();
            if (target == null || view.GetZDO().GetInt(ZDOVars.s_level) <= 0) return false;
        }

        _current = new Request(view, actor, rpc.m_senderPeerID, data);
        // Reserve before invoking vanilla. Duplicate/reentrant/late requests must not repeat
        // drops, even if vanilla or another patch throws after partially processing an action.
        view.GetZDO().Set(RevisionKey, data.Revision + 1);
        return true;
    }

    internal static void End(Request? previous) => _current = previous;

    private static Player? FindActor(ZDOID id, long sender)
    {
        foreach (Player player in Player.GetAllPlayers())
        {
            ZNetView? view = player != null ? GameAccess.CharacterView(player) : null;
            if (view != null && view.IsValid() && view.GetZDO().m_uid == id && view.GetZDO().GetOwner() == sender)
                return player;
        }
        return null;
    }

    private static Request? Match(ZNetView? view, long sender)
    {
        Request? request = _current;
        return request != null && view != null && view.IsValid() && view.IsOwner() &&
               request.View == view && ReferenceEquals(request.Zdo, view.GetZDO()) && request.Sender == sender
            ? request : null;
    }

    internal static void PickSucceeded(Pickable pickable, long sender)
    {
        ZNetView? view = GameAccess.PickableView(pickable);
        Request? request = Match(view, sender);
        if (request != null)
            FarmingSkillSystem.RememberForagingPickerSkill(pickable, request.Data.Level / 100f);
        else
            AdvanceUntrackedHarvest(view);
    }

    internal static void HoneySucceeded(Beehive hive, long sender, int honey)
    {
        ZNetView? view = GameAccess.BeehiveView(hive);
        Request? request = Match(view, sender);
        if (request == null)
        {
            AdvanceUntrackedHarvest(view);
            return; // Unknown actor/legacy RPC: never read or reward a remote Skills object.
        }
        BeehivePollinationSystem.StoreTendedFarmingLevel(hive, request.Data.Level);
        ZNetView? actorView = request.Actor != null ? GameAccess.CharacterView(request.Actor) : null;
        if (actorView != null && actorView.IsValid() && actorView.GetZDO().GetOwner() == sender)
            actorView.InvokeRPC(sender, ReceiptRpc, request.Data.Id, honey);
    }

    private static void AdvanceUntrackedHarvest(ZNetView? view)
    {
        if (view == null || !view.IsValid() || !view.IsOwner()) return;
        long revision = view.GetZDO().GetLong(RevisionKey, 0L);
        if (revision < long.MaxValue) view.GetZDO().Set(RevisionKey, revision + 1);
    }

    internal static void Register(Player player)
    {
        ZNetView? view = GameAccess.CharacterView(player);
        if (view == null || !view.IsValid()) return;
        view.Register<long, int>(ReceiptRpc, (sender, id, honey) =>
        {
            if (player != Player.m_localPlayer || !view.IsValid() || !view.IsOwner() || honey <= 0) return;
            if (Receipts.Take(id, sender, player.GetZDOID(), Time.realtimeSinceStartup))
                BeehivePollinationSystem.RaiseFarmingSkillForHarvest(player, honey);
        });
    }

    internal static void ClearSession()
    {
        Receipts.Clear();
        _current = null;
        // Do not reset IDs: a late receipt from a previous character/session cannot match a new request.
    }

    internal static void Shutdown()
    {
        ClearSession();
        foreach (Player player in Player.GetAllPlayers())
        {
            ZNetView? view = player != null ? GameAccess.CharacterView(player) : null;
            if (view != null) view.Unregister(ReceiptRpc);
        }
    }

    // Bounded session-only receipts. Disconnected/expired awards are not replayed on reconnect;
    // an acknowledgement can never trigger a second extraction or recreate any items.
    internal sealed class ReceiptLedger
    {
        private readonly Dictionary<long, (long Owner, ZDOID Actor, float Sent)> _pending = new();
        private readonly List<long> _order = new();
        internal void Add(long id, long owner, ZDOID actor, float now)
        {
            while (_order.Count > 0 && (_order.Count >= 128 ||
                   !_pending.TryGetValue(_order[0], out var old) || now - old.Sent > 30f))
            {
                _pending.Remove(_order[0]);
                _order.RemoveAt(0);
            }
            _pending.Add(id, (owner, actor, now));
            _order.Add(id);
        }
        internal bool Take(long id, long sender, ZDOID actor, float now)
        {
            if (!_pending.TryGetValue(id, out var pending) || pending.Owner != sender || pending.Actor != actor)
                return false;
            _pending.Remove(id); // Consume before invoking player code, including exceptional callbacks.
            return now >= pending.Sent && now - pending.Sent <= 30f;
        }
        internal void Clear() { _pending.Clear(); _order.Clear(); }
    }

    internal static IEnumerable<CodeInstruction> ReplaceSend(IEnumerable<CodeInstruction> instructions, bool pick)
    {
        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(VanillaSend))
            {
                if (pick)
                    yield return new CodeInstruction(OpCodes.Ldarg_1).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.DeclaredMethod(typeof(HarvestSkillSync), pick ? nameof(SendPick) : nameof(Send));
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException($"Expected one harvest RPC send, found {replaced}.");
    }
}

[HarmonyPatch(typeof(Pickable), nameof(Pickable.Interact))]
internal static class PickableInteractHarvestSnapshotPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => HarvestSkillSync.ReplaceSend(instructions, pick: true);
}

[HarmonyPatch(typeof(Beehive), "Extract")]
internal static class BeehiveExtractHarvestSnapshotPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => HarvestSkillSync.ReplaceSend(instructions, pick: false);
}

[HarmonyPatch(typeof(ZNetView), nameof(ZNetView.HandleRoutedRPC))]
internal static class ZNetViewHarvestSnapshotPatch
{
    private static bool Prefix(ZNetView __instance, ZRoutedRpc.RoutedRPCData rpcData, out HarvestSkillSync.Request? __state) =>
        HarvestSkillSync.Begin(__instance, rpcData, out __state);
    private static void Finalizer(HarvestSkillSync.Request? __state) => HarvestSkillSync.End(__state);
}

[HarmonyPatch(typeof(Player), "Awake")]
internal static class PlayerAwakeHarvestReceiptPatch
{
    private static void Postfix(Player __instance) => HarvestSkillSync.Register(__instance);
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Shutdown))]
internal static class ZNetSceneShutdownHarvestSnapshotPatch
{
    private static void Prefix() => HarvestSkillSync.ClearSession();
}
