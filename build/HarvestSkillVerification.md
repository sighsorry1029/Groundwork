# Farming harvest ownership fix

## Cause and scope

The old `Beehive.RPC_Extract` postfix read and raised the sender's **remote**
`Player.Skills` on the hive owner. That instance is not the character's saved
skill state. Vanilla can forward its level-up message to the character owner,
explaining a level-1 announcement without resetting the character's level 36.
This is a confirmed invalid code path; the reported session has not been reproduced.
`Pickable.RPC_Pick` also stored remote/observer skill before vanilla rejected
already-picked requests. A missing respawn snapshot fell back to the observer's skill.

Planting already snapshots the planter locally. This change reinforces that
boundary but does not assume plant snapshots suffered the same corruption.
Reduced hive capacity can also reduce surrounding pollination benefits.

## Contracts

- The acting client appends `GWH1`, request ID, target revision, actor ZDOID and
  Farming level to the existing vanilla harvest RPC. Pickable's bonus argument,
  RPC names, vanilla drop generation and the original interaction checks remain.
- The target owner binds the actor ZDOID to the RPC sender's current ownership,
  validates finite levels 0..100 and requires the expected target revision.
  Legitimate level 0 or lower-level players are accepted: this is not a highest-ever
  skill cache. Like vanilla skill-dependent bonuses, the value is client supplied;
  this does not introduce server-side anti-cheat skill verification.
- `Groundwork_HarvestRevisionV1` is one additional persistent ZDO long. Reservation
  precedes the original action, so the same extended packet cannot execute again
  after a partial failure or after regrowth. Nested routed calls have separate
  request context, restored by a finalizer without suppressing exceptions.
- Only a successful original harvest updates skill metadata. Honey acknowledgements
  bind request ID, expected target owner and character. The receiving local player
  consumes an acknowledgement before awarding XP. Vanilla picking XP is untouched.
- Receipts are session-only, bounded to 128 requests and 30 seconds. World shutdown
  clears them; plugin teardown unregisters the receipt RPC. IDs do not reset during
  the process. Disconnects/timeouts can forfeit honey XP; they never replay extraction
  or recreate items. This is not a durable exactly-once reward transaction.
- Old/foreign unextended calls retain vanilla execution but do not guess or overwrite
  a harvesting skill, or award remote honey XP. Successful unextended harvests advance
  the revision. Use the patched DLL on every participating client and server.
- Stale revision/owner or unavailable actor causes rejection before item creation.
  After state synchronizes the player can interact again; there is no automatic retry.
  A fresh request after another mod throws during partial item creation still follows
  vanilla state. This patch does not claim to make arbitrary third-party drops atomic.
- ZenBeehive's separate container path stays local. Its observed decrease only
  counts when the client owned the hive at both samples. It still relies on the
  existing container interaction adapter, not the vanilla extraction receipt.
- Existing config keys/defaults, formulas, skill save data, and plant/foraging/hive
  skill ZDO keys remain. Existing bad hive/foraging snapshots repair on the next
  successful attributable harvest; no world-wide scan or guessed restoration.

## Automated verification

Run `dotnet build Groundwork.sln -c Debug -p:DeployToGame=true`, then
`powershell -NoProfile -ExecutionPolicy Bypass -File build/TestCompatibility.ps1`.
`VerifyCompatibility.ps1` checks direct references/access and Harmony targets;
`TestCompatibility.ps1` runs original game serialization/deserialization, malformed
packets, valid low levels, receipt identity/expiry/duplicate/bounds, nested context
restoration and the actual original-IL transpiler transformations in Unity's Mono.

Reviewed original `Skills`, `Player`, `Pickable`, `Beehive`, `ZNetView`, `ZRoutedRpc`,
`ZRpc` and container handshake code. No publicized game DLLs were used. Validation
targets are client build 25364265 and server build 25364309 (Valheim 1.0.14), using
the existing global snapshots. Relevant Skills/Pickable/Beehive/Plant code was also
compared to the 1.0.12 baseline. Dependencies and support policy were not updated.

Attempting live Harmony detours in the isolated host triggered Player/ZSyncAnimation
initialization, which requires Unity's native Animator bindings. That experiment
failed due to the absent engine; it is not a passed runtime-patching test. The
retained automated suite does not detour or execute a Unity scene, plugin Awake,
network sockets, item creation or ZDO revision writes.

## Remaining in-game checks

Use a backed-up world and matching patched DLLs on a dedicated server and two clients.

1. A has Farming 36, B has a different level (include 0). B owns the hive/pickable;
   A harvests. Verify A's level is used, only A receives honey XP once, no phantom
   level-1 message, expected honey capacity and respawn timing on both clients.
2. Exchange owners, then have B harvest. The most recent actual actor wins, including
   a legitimately lower level. Repeat with same-client owner and a listen host.
3. Delay/repeat an identical extended RPC and its receipt, including after regrowth.
   The repeated revision must create no additional drops or metadata/XP updates.
   Let ownership change or actor disconnect before delivery; rejection must preserve
   target state. Verify the next fresh interaction works after synchronization.
4. Inject an exception/skip-original patch, test nested RPC_SetPicked, unload/reload
   the zone, save/restart/reconnect and change characters. Check revision persistence,
   no receipt replay and no metadata overwrite from a failed harvest.
5. Check normal/manual, range and scythe harvesting, cultivated respawning Pickables,
   plant growth and nearby pollination. Repeat with features disabled and live config
   changes. Compare actual item counts; a tooltip alone is not sufficient.
6. With ZenBeehive installed, test open/take-one/take-all/close and ownership transfer.
   Confirm its own extraction replacement cannot also generate a vanilla XP award.

None of these gameplay, dedicated-server, multiplayer or optional-mod cases were
executed during this patch. Excluded: generated outputs, vendor implementations,
unrelated terrain/UI functionality and broad game-version migration.
