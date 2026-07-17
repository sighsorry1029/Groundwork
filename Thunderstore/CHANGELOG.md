# Changelog

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
