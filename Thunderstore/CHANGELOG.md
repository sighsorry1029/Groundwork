# Changelog

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
