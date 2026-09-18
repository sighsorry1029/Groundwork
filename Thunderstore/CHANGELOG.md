# Changelog

## 1.1.13

- Fixed dedicated-server harvest bookkeeping using an uninitialized remote `Player.Skills` instance, which could store Farming level 0 and emit a misleading Farming level 1 message even though the harvesting character retained their real level. Pickable and beehive requests now carry the acting character's Farming level and bind it to that character's current network owner.
- Added owner-authoritative request revisions and bounded honey-harvest acknowledgements so duplicate, delayed, stale, wrong-character, and wrong-owner requests cannot overwrite Farming snapshots, award honey experience twice, or repeat item creation. Existing low snapshots are replaced by the next successful attributable harvest; character skill saves and existing configuration remain unchanged.
- Prevented ZenBeehive compatibility bookkeeping from attributing a honey decrease across a hive ownership transition to the observing local player.

## 1.1.12

- Added the server-synced `Farming Range Harvest Targets` setting. The default `ForagingOnly` behavior is unchanged, while `ForagingAndCrops` extends Farming-scaled nearby harvesting to mature crops produced by Plant prefabs.
- Added opt-in Cultivator removal of natural Pickables through a server-synced prefab allowlist. The default list covers branches, flint, dandelions, mushrooms, and thistles; removal keeps Valheim's normal placement, ward, ownership, and distance checks and grants no items.
- Added the server-synced `Scythe Handle Required Global Key` setting. Groundwork now defaults the Bog Witch's `ScytheHandle` sale requirement to `defeated_bonemass` instead of Vanilla's `defeated_dragon`, while retaining Valheim's `ZoneSystem.GetGlobalKey` query so YouAreNotWorthy can apply per-player progression and provide discovery of additional global keys.

## 1.1.11

- Prevented the Plant progress checkpoint patch from calling Unity component APIs on an already destroyed `ZNetView` during zone unloading, avoiding the resulting Groundwork `NullReferenceException` while preserving checkpoints for live Plants.

## 1.1.10

- Added independent client-only `Off`, `Compact`, and `Detailed` hover-hint modes for growing crops, picked respawning forage targets, and beehives. These settings affect display only; growth, respawn, pollination, honey production, Farming effects, and forage proxy target discovery remain active.
- Replaced `Beehive Hover Explanation` with `Beehive Hover Hint`; existing values are not migrated.
- Fixed `LiberationSans SDF` missing-font warnings from pickaxe terrain-change text, the mass-planting limit label, and mouse-wheel key-hint separators by assigning the Valheim HUD font before activation.
- Fixed interrupted mass-planting batches leaving confirmed placements unpaid when another callback throws; completed placements now consume their corresponding resources, stamina, and tool durability without hiding the original error.
- Limited pickaxe terrain input updates to the local player and reduced repeated placement-ghost reflection in frequently executed placement paths.
- Restored BepInEx's original `SaveOnConfigSet` value when Groundwork initialization fails, while preserving the existing startup order and error propagation.
- Added repeatable compatibility checks against the original Valheim client and dedicated-server assemblies and the game's Unity Mono runtime.

## 1.1.9

- Updated Groundwork for Valheim 1.0.12's `PlayerProfile.s_bypassCheatChecks` field-to-property change, fixing repeated `MissingFieldException` failures when using the Hoe or Cultivator with Groundwork placement features.
- Initialized the terrain range and aimed-height TMP labels only after assigning Valheim's HUD font, preventing missing `LiberationSans SDF` warnings.

## 1.1.8

- Updated Groundwork for Valheim 1.0.7, including original game assembly references, current method signatures, hover offsets, input paths, and cached access to required non-public members.
- Updated the embedded ServerSync implementation for Valheim 1.0.7, preserving synchronized configuration initialization, administrator checks, version checks, and connection message ordering.
- Preserved adjusted hoe and pickaxe terrain operations across network ownership boundaries using a validated, versioned settings extension without mutating shared vanilla prefab settings.
- Updated mass planting for the new placement API, build statistics, Deep North snow restrictions, cheated-placement attribution, and global durability scaling.
- Prevented the Farming tooltip's custom layout from leaking into other reused Valheim 1.0.7 tooltip views.
- Updated the required BepInExPack Valheim dependency to 5.4.2350.

## 1.1.7

- readme change

## 1.1.6

- Standardized local Debug deployment on `DeployToGame=true`, copying only the final merged plugin DLL after successful compilation and merging. An unconfigured deployment destination now fails explicitly.
- Restored automatic manifest version updates and Thunderstore/Nexus ZIP packaging for ordinary Release builds, matching Mod Release Manager's ZIP-watching workflow. Debug builds do not create release packages or update the manifest.
- Updated build instructions to distinguish local game testing from Release packaging and automatic site publishing. No gameplay changes in this release.

## 1.1.5

- Added server-synced `cultivation.yml` recipes for planting respawning berry bushes, mushrooms, Dandelion, Thistle, SmokePuff, and Fiddlehead with configurable costs, spacing, cultivated-ground requirements, and EWD-aware placement biomes.
- Extended single, grid, and mass planting to configured Pickables. New plantings start empty, remember the planter's Farming level for their first respawn cycle, and can be uprooted with the Cultivator without returning materials or harvesting their contents.
- Added fixed harvested remnants for supported respawning Pickables and persistent vanilla fern foliage for Groundwork-planted Fiddlehead. Disabling a recipe preserves existing plantings and these visuals.
- Declared PlantEverything incompatible: BepInEx now skips loading Groundwork when both mods are installed.
- Changed mass planting to process slots nearest to the player first, with matching preview and placement order. Material, stamina, and durability limits select the nearest slots; invalid selected slots are skipped without extending the batch to farther slots.
- Positioned the Farming skill tooltip beside the Skills panel, aligned with its row and kept within screen bounds, while preserving existing tooltip text.
- Added native mouse-wheel icons to tool hints and descriptions, plus a default-on client setting, `Terrain Height Hint`, for standing-height and Shift+Click guidance above the build panel. This setting is independent of Tool HUD; Paved Road guidance requires Paved Road Smooth Height.
- Reduced repeated key-hint layout work, placement-preview allocations, and duplicate terrain-preview searches; simplified placement context and growth-rule normalization.
- Fixed failed growth-rule applications causing later attempts with the same YAML, or a return to the last successfully applied YAML, to be skipped. Runtime application failures are logged separately from validation failures and may leave partially updated live state until a subsequent application succeeds.
- Prevented skipped or unchanged beehive extractions from granting Farming harvest rewards; partial extractions grant rewards only for the honey removed.
- Made Release packaging opt-in with `/p:BuildPackages=true`. Normal builds produce the merged DLL without deploying it or rewriting the distribution manifest; local deployment still requires `/p:DeployLocal=true`.
- Existing `cultivation.yml` files are preserved. For configurations from unreleased builds, flatten the old `planting:` wrapper and remove all `pickedVisual:` fields; neither legacy form is migrated or accepted.

## 1.1.4

- Added a Groundwork section to the Farming skill tooltip that preserves existing and third-party text while describing enabled mass-planting, crop-growth, foraging, and beehive-capacity effects alongside bonus-yield behavior for eligible Pickables.
- Added a default-on client setting that shows short, configuration-aware explanations below a beehive's next-honey line for cover, nearby growing targets, and stored-honey growth bonuses.

## 1.1.3

- Removed the world-space post-harvest dot for respawning foraging Pickables while retaining a 0.32 m invisible hover and pollination proxy at the original position; targets without a respawn timer no longer receive a proxy, and natural or PlantEverything visuals remain untouched.

## 1.1.2

- Added a client-side beehive pollination preview with a terrain-following sphere footprint and highlights for assigned Plant and foraging Pickable targets, including active and paused coloring.
- Added small faint-gray post-harvest surface markers and respawn-factor hover details for foraging Pickables whose natural hover target disappears, while preserving natural and PlantEverything visuals and keeping hidden targets discoverable by pollination.
- Made custom circular and square terrain-tool range outlines follow slopes and heightmap seams, with a safe flat fallback.
- Changed the beehive cover honey bonus from a quadratic to a linear curve and raised the new-config default maximum multiplier from x2 to x3 at 0% cover; existing saved values remain unchanged.
- Fixed cover, pollination, night/rain, and loaded/unloaded rate changes retroactively speeding up or slowing down accumulated honey progress; the next-honey hover estimate now uses the same checkpointed progress fraction. Hives without an existing progress checkpoint adopt their current partial timer without legacy conversion, so the first honey time may shift once after upgrading.

## 1.1.1

- Reduced startup log noise by replacing per-prefab warnings for unnamed live Plant biome bits with one Debug summary, while preserving actionable warnings for other reference conflicts.

## 1.1.0

- Added optional Plant biome overrides to `plants.yml`, applying the same allowed-biome list to cultivator placement, mass planting, and planted-crop health checks while leaving other growth requirements unchanged.
- Added Expand World Data compatibility for biome names and effective nature groups, with unresolved custom names safely preserving live restrictions until EWD's synchronized biome map is available.
- Added live Plant growth biome masks to `plants.reference.yml`, including an inline warning when a cultivator placement mask differs from the reported Plant mask.
- Fixed beehive honey, pollination, and rain multiplier changes retroactively rewriting earlier Plant growth or foraging respawn progress; owner-authoritative progress now records loaded and unloaded rate segments and preserves them across zone reloads.
- Updated Plant and Pickable hover countdowns to use the segmented progress calculation.

## 1.0.9

- Added owner-grouped `pickables.reference.yml` and `plants.reference.yml`, with root-sequence `pickables.yml` and `plants.yml` overrides for Pickable respawn/Farming behavior and Plant grow-time ranges under `BepInEx/config/Groundwork/`.
- Registered `Pickable_Dandelion` and `Pickable_Thistle` as default Farming targets with range/scythe harvesting, Farming skill gain, and vanilla bonus-yield rolls.
- Applied growth overrides at runtime without permanently replacing prefab values, preserving live values from mods such as PlantEverything when override fields are omitted.
- Read and validated both Pickable and Plant override files before atomically replacing either in-memory rule set, then server-synced the normalized pair.
- Moved the generated `Groundwork.yml` terrain-tool configuration into `BepInEx/config/Groundwork/`; previous root-level YAML files and the unreleased combined/expanded Growth schemas are not migrated or parsed.
- Preserved native and configured Farming bonus VFX/SFX by supplying an interaction-scoped fallback when a Pickable has an empty `m_bonusEffect`.

## 1.0.8

- Fixed scaled terrain-tool placements so extra stamina and durability costs apply exactly once to the tool that performed the placement.
- Fixed terrain tools resetting to the vanilla radius instead of the configured `range.default`, prevented range-wheel input from also zooming the camera, and bounded oversized grid preview searches with a safe fallback.
- Hardened scalable pickaxe digs so temporary radius and depth changes stay bound to the exact terrain hit and are restored even when spawning fails.
- Fixed pollination catch-up for honey, plant growth, and forage respawn so unloaded time keeps its reduced bonus while loaded bonuses still obey night and rain restrictions.
- Fixed ranged foraging being limited by a fixed collider buffer in dense areas and removed duplicate bonus pickup effects.
- Made plant and forage hover countdowns follow the actual timing calculations and removed the potentially misleading combined speed multiplier.
- Improved mass-planting previews by rejecting overlaps with earlier preview slots, restricting rare placement fallback searches, and avoiding duplicate Grid or Mass instructions in partial key-hint layouts.
- Prevented scythe sweeps from processing the same unhealthy multi-collider crop more than once, improved Jewelcrafting recalculation, and made temporary scythe item-type changes reversible.
- Fixed stale ZenBeehive state after containers close automatically, preventing later inventory actions from being mistaken for a local honey harvest.
- Added comprehensive unload cleanup for configuration, localization, generated UI, previews, Farming state, and compatibility changes.
- Kept normal builds isolated from the live Valheim plugin directory; local deployment now requires explicitly setting `DeployLocal=true`.

## 1.0.7

- Clarified the Farming level 20 mass planting requirement in piece tooltips and build key hints, including live Farming progress, while listing always-available grid planting first.

## 1.0.6

- Fixed first-use mass planting previews stopping at five visible crops when cycling to 10, 15, 20, or 25 before the first placement.
- Reused the original renderer state of Groundwork-hidden placement ghosts when expanding a batch preview, preventing partial `3+2` layouts and repeated empty preview object creation.

## 1.0.5

- Expanded scythe harvesting to modded wild and cultivated pickables by recognizing additional collider layers and Plant-grown prefab relationships, with shared targeting for HarvestSweep compatibility.
- Added AzuCraftyBoxes-compatible resource checks for mass planting while preserving an exact resource recheck before placement.
- Fixed mass planting previews to copy only active plant renderers, preventing hidden mature growth stages from appearing in placement ghosts.
- Added client-controlled pickaxe terrain-dig tooltips showing the configured `x1~xMax` range and directing players to the live key hint.

## 1.0.4

- Improved mass planting persistence and performance by synchronizing the exact planted crop instance, with a compatibility fallback for unusual prefabs.
- Expanded pollination search buffers on demand in unusually dense fields instead of truncating targets at the initial capacity.
- Debounced `Groundwork.yml` hot reloads and simplified internal terrain tool, configuration, and Harmony state handling.

## 1.0.3

- Fixed terrain tool grid preview placement consistency so the terrain operation uses the last visible grid preview position at placement time.
- Removed orange coloring from terrain tool piece tooltip hints for a cleaner white/default tooltip style.

## 1.0.2

- Fixed grid/mass planting persistence by syncing planted crop ZDO positions and reserving batch plant spaces to prevent crops from stacking after reload.
- Disabled beehive pollination at night for both honey rate bonuses and plant/foraging growth bonuses.
- Clarified beehive capacity hover text as Max +N and kept Honey rate wording on the final next-honey line.

## 1.0.1

- Made grid planting always available independently from mass planting, while mass planting still scales by Farming level.
- Added ZenBeehive container compatibility so honey removed from beehive containers counts as Groundwork harvest.
- Minor refactoring and optimizations.

## 1.0.0

- Initial release.
