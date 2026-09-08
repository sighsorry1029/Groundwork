# Groundwork

Pollination boosts honey and plant growth, rain speeds plant growth, hoe/cultivator ranges scale with grid views, pickaxes get scalable digging, scythes harvest all crops, and Farming skill scales mass planting, plant growth, and beehive capacity.

![](https://i.ibb.co/whdVzJdg/Screenshot-2026-06-16-020402.png) <br>
Configurable terrain tool scaling with radius and cost. Larger terrain ranges can require proportionally more materials, stamina, and durability, making expanded tools powerful but balanced.

![](https://i.ibb.co/CKg7BVVh/Video-Project-4.gif) <br>
![](https://i.ibb.co/RT1YXGnY/Video-Project-3-1.gif) <br>
Hoe and Cultivator terrain range scaling with precision grid preview. Adjust tool radius with the wheel modifier, preview affected terrain cells, and see range/cost feedback before placing.

![](https://i.ibb.co/W42y4ZqG/Video-Project-2.gif) <br>
Scalable pickaxe terrain digging. Increase radius and depth per pickaxe, with stamina and durability costs scaling from the selected dig size.

![](https://i.ibb.co/NdWsq8D6/Video-Project-3.gif) <br>
Scythe harvesting for all supported crops. Sweep through planted fields and harvest mature crops in a wide arc.

![](https://i.ibb.co/1tsPyLXh/Screenshot-2026-06-16-020255.png) <br>
Beehive upgrades and hover details. Farming-scaled honey capacity, cover, pollination, night/rain modifiers, total honey rate, and next honey timing are shown at a glance.

![](https://i.ibb.co/zHfv96hg/Screenshot-2026-06-16-020315.png) <br>
Rain and pollination plant growth info. Hover text shows active Farming, pollination, and rain multipliers alongside the remaining growth time.

![](https://i.ibb.co/ycJdxWfq/Screenshot-2026-06-16-020330.png) <br>
Farming-scaled mass planting, grid planting, and foraging pollination. Plant in clean grids, scale planting count by Farming level, and let beehives boost nearby foraging respawn when conditions are right.

## Features

### Farming and Planting

- Grid planting is always available while placing crops.
- Mass planting scales with Farming level:
  - 0-19: Off
  - 20-39: 5 plants
  - 40-59: 10 plants
  - 60-79: 15 plants
  - 80-99: 20 plants
  - 100: 25 plants
- Hold the tool wheel modifier hotkey and use the mouse wheel to change mass-plant count.
- Turn off `Mass Planting Enabled` to disable only multi-plant placement; grid planting stays available.
- Mass planting processes slots nearest to the player first (horizontal distance), using the same order for preview and placement. Material, stamina, and durability limits select the nearest slots; invalid selected slots are skipped without extending the batch to farther slots.
- Planted crops can grow faster based on the planter's Farming skill.
- Mass planting can grant extra Farming skill.
- Hovering Farming in the Skills tab shows its original description and enabled Groundwork Farming effects to the left of the Skills panel, aligned with the Farming row and kept within the screen bounds.

### Foraging

- Edible respawning pickables, plus prefabs registered in `BepInEx/config/Groundwork/pickables.yml`, can be affected by Farming skill.
- Higher Farming skill can increase nearby pickup range.
- Higher Farming skill can speed up foraging respawn.
- Rain can speed up foraging respawn while the current environment is wet.

### Beehives

- Beehive capacity can increase with Farming level.
- Newly placed hives store the builder's Farming level.
- Harvesting honey updates the hive's stored Farming level from the harvester.
- Harvesting honey can grant Farming skill.
- Lower cover increases honey production speed.
- Night honey production can be slowed or paused.
- Rain can slow or pause loaded honey production.
- Beehives can pollinate nearby growing plants and foraging targets, except while it is raining.
- Empty hives give stronger pollination bonuses; the bonus fades as the hive fills with honey.
- Growing nearby pollination targets can speed up honey production.
- Beehive hover text shows honey capacity in the title, plus cover, pollination, night/rain rates, and next honey time with total rate.
- A client-only setting can show short explanations below the next-honey line for the configured cover and pollination effects.
- One client setting controls both the centered terrain range guide and marker highlights around Plant/Pickable targets assigned to a hovered beehive. Raising a hive shrinks the guide, while slopes change it by direction. The range turns gray when unavailable or paused; assigned target markers turn gray while rain or night pauses pollination.

### Terrain Tools

- `BepInEx/config/Groundwork/Groundwork.yml` controls Hoe and Cultivator piece costs and adjustable terrain ranges.
- Hold the tool wheel modifier hotkey and use the mouse wheel to adjust terrain range.
- Preview mode can be vanilla ghost scaling or an exact grid preview.
- Paved Road can optionally skip vanilla smooth-height behavior.
- Pickaxe primary terrain digging can scale radius and depth.
- Pickaxe scaled digging has configurable stamina and durability cost factors.

### Hover Info

Groundwork adds compact hover information to:

- Beehives: honey capacity in the title, cover, pollination, night/rain rates, next honey with total rate.
- Plants: remaining growth time, rain growth, pollination growth.
- Foraging pickables: remaining respawn time, rain respawn, pollination respawn.

## Groundwork.yml

`BepInEx/config/Groundwork/Groundwork.yml` defines terrain tool piece costs and adjustable ranges.

Example:

```yaml
Pickaxe:
  terrainDig:
    range:
      enabled: true
      radiusMax: 1.5
      depthMax: 1.5
      staminaCostFactor: 1
      durabilityFactor: 1

# Exact override for a specific pickaxe prefab.
# Unspecified values fall back to Pickaxe: terrainDig.
PickaxeBlackMetal:
  terrainDig:
    range:
      # Optional: overrides the generic Pickaxe enabled state.
      # enabled: true
      radiusMax: 2
      depthMax: 2

Hoe:
  raise_v2:
    cost:
      Stone: 2
    range:
      enabled: true
      min: 1
      max: 5
      materialCostFactor: 1
      staminaCostFactor: 1
      durabilityFactor: 1
```

Fields:

- `cost`: replaces the build menu material cost. Use `{}` for no material cost.
- `range.enabled`: enables mouse-wheel range adjustment.
- `range.min`: minimum range.
- `range.max`: maximum range.
- `range.default`: optional default range.
- `range.radiusMax`: maximum radius scale for `Pickaxe: terrainDig`.
- `range.depthMax`: maximum depth scale for `Pickaxe: terrainDig`.
- `range.materialCostFactor`: material scaling above base range. `0` keeps base cost.
- `range.staminaCostFactor`: stamina scaling above base range. `0` keeps base cost.
- `range.durabilityFactor`: durability scaling above base range. `0` keeps base cost.

For `Pickaxe: terrainDig`, min and default scale are fixed at `1`. Use `range.enabled` to turn the feature on or off, and use `radiusMax` and `depthMax` to set separate caps. `staminaCostFactor` and `durabilityFactor` apply to extra costs above `x1`; `cost` and `materialCostFactor` are ignored.

Specific pickaxe prefab blocks, such as `PickaxeBlackMetal: terrainDig`, override only the values they specify. Missing values fall back to the generic `Pickaxe: terrainDig` block, and an exact block can set `range.enabled: true` or `false` independently.

On multiplayer, the server's `BepInEx/config/Groundwork/Groundwork.yml` is synced to clients.

## Pickable and Plant overrides

Groundwork keeps each growth domain in a separate file under `BepInEx/config/Groundwork/`:

- `pickables.reference.yml`: generated Pickable values, grouped by the prefab's original provider.
- `pickables.yml`: editable Pickable and Farming overrides.
- `plants.reference.yml`: generated Plant values, grouped by the prefab's original provider.
- `plants.yml`: editable Plant grow-time and allowed-biome overrides.
- `cultivation.yml`: editable Cultivator recipes for respawning Pickables. Harvested visuals use fixed built-in defaults.

Each override file is a root YAML sequence, not a `pickables:` or `plants:` mapping. Groundwork creates `pickables.yml` with `Pickable_Dandelion` and `Pickable_Thistle` registered as Farming targets:

```yaml
- prefab: Pickable_Dandelion
  farming: [true, true, 0.25, 1]
- prefab: Pickable_Thistle
  farming: [true, true, 0.25, 1]
```

`plants.yml` starts as an empty root sequence:

```yaml
[]
```

Copy only the entries you want to change from the corresponding generated reference. For example, a Plant override can be written as:

```yaml
- prefab: Beech_Sapling, 3000~5000
- prefab: sapling_jotunpuffs
  biomes: [Mistlands, Meadows]
```

Tuple schema:

- Pickable: `prefab: <prefab>[, <respawnMinutes>]`. The optional positive second value replaces a Pickable's base respawn time. Omitting it keeps the live value; a live value of `0` cannot be made respawning by this overlay.
- Farming: `farming: [foragingTarget, bonusYield, maxChanceAtLevel100, bonusAmount]`. When present, the tuple has exactly four positions; use `null` to preserve automatic/live behavior in a position.
- Plant: `prefab: <prefab>[, <growSecondsMin>~<growSecondsMax>]`. The optional values are positive seconds and the maximum must be at least the minimum. Omitting the range keeps the live grow time.
- Plant biomes: `biomes: [<biome>, ...]` applies one non-empty list of biome names to both cultivator placement and the planted crop's health check. Omitting it keeps the live restrictions. `None`, `All`, and numeric masks are rejected.
- `plants.reference.yml` reports the live Plant growth mask. An inline note marks entries whose live cultivator placement mask differs, because copying `biomes` also replaces that placement mask.
- `foragingTarget`: `true` opts in to range/scythe harvesting, Farming respawn scaling, pollination, rain effects, hover info, and missing bonus-effect fallback; `false` opts out; `null` keeps automatic edible-pickable detection.
- `bonusYield`: `true` opts in to Valheim's Farming skill gain and bonus-yield roll. `false` or `null` does not add behavior and never disables a prefab's native Farming bonus.
- `maxChanceAtLevel100`: bonus probability at Farming 100, from `0` to `1`.
- `bonusAmount`: additional items on a successful roll.

On a successful configured or native Farming bonus roll, Groundwork temporarily supplies fallback VFX/SFX when the Pickable's `m_bonusEffect` is empty. This includes vanilla Farming pickables such as `RaspberryBush` and configured targets such as Dandelion.

When a respawning foraging target hides all of its natural hover colliders after harvesting, Groundwork keeps an invisible hover proxy at the original Pickable position. Aiming at that position shows the target name, active Farming, pollination, and rain factors, and the estimated respawn time. The same proxy keeps the hidden target discoverable by beehive pollination. Cultivator-planted Pickables also keep this proxy for spacing and removal when Farming is opted out. Built-in harvested remnants reuse this collider; existing natural post-harvest models are preserved.

Omitted Pickable/Plant times, omitted Plant biomes, and `null` Farming positions use live prefab values. Explicit times form the base before Farming, pollination, and rain multipliers.

Expand World Data custom biome names are resolved after EWD's synchronized biome map is available. A custom biome configured with `nature: Mistlands` participates in Plant checks as Mistlands, so it belongs to the same effective group as vanilla Mistlands and other custom biomes with that nature; members of one nature group cannot be selected separately. An independent custom biome can be selected by its EWD `biome` name. Groundwork synchronizes names rather than EWD's order-dependent numeric bits; until every configured name resolves, the complete biome override stays inactive and live restrictions remain in effect.

Biome overrides do not alter independent cultivated-ground, heat/cold tolerance, roof, or spacing checks.

On each reload, Groundwork reads and validates `pickables.yml`, `plants.yml`, and `cultivation.yml` before replacing the in-memory rules. If any file is invalid, the current in-memory rules remain unchanged. Unexpected failures during live application are logged separately and can leave partially updated state; the next configuration application retries even when the YAML is unchanged. On multiplayer, the server synchronizes these rules together after successful local application; the generated owner-grouped reference files remain local to the server or single-player source of truth.

The root-sequence compact tuple schema is the only accepted growth schema. Groundwork does not read, parse, or migrate previous root-level YAML files or the unreleased combined/expanded Growth layouts.

## Pickable cultivation and harvested visuals

`cultivation.yml` uses a root sequence with flat recipe settings. Harvested visuals are built in and are not configured in this file:

```yaml
- prefab: Pickable_Mushroom
  plantable: false # disable planting only; keep the recipe settings and harvested visual
  resources:
    - Mushroom, 30 # item prefab, positive amount
    - Resin, 15
  cultivatedGroundOnly: true # default true when omitted
  spacing: 1 # default 1; base center-to-center planting distance in meters
  biomes: [Meadows, BlackForest, Plains] # optional allowed planting locations
```

- Defaults enable recipes for Raspberry, Blueberry, and Cloudberry bushes; red/yellow mushrooms; Dandelion; Thistle; SmokePuff; and Fiddlehead. Each planting costs 20 or 30 of its harvested item plus Resin, Pukeberries, or RottenMeat, as listed in `cultivation.yml`. These are one-time costs per planted instance, not per harvest. Bush spacing defaults to 2 meters; other recipes use 1 meter, before the mass/grid spacing factor. RottenMeat costs on Cloudberry, SmokePuff, and Fiddlehead require access to that material; other mods may change when it becomes available.
- The generated file keeps five short header comments, with field defaults and EWD biome guidance beside the first recipe. New defaults are only written when `cultivation.yml` is missing; existing files are not overwritten or migrated.
- Fixed harvested remnants retain the bottom 30% for `Pickable_Mushroom`, `Pickable_Mushroom_yellow`, `Pickable_Dandelion`, `Pickable_Thistle`, `Pickable_SmokePuff`, and wild `Pickable_Fiddlehead`; `Pickable_RoyalJelly` retains the top 30%, lowered to its original base. These visuals require a positive effective respawn time but no cultivation entry or Farming opt-in. Berry bushes keep their native harvested appearance. Blue mushroom cultivation remains an optional commented recipe; it and unknown/custom prefabs receive no generated cut remnant, leaving existing hover handling unchanged.
- Omitting `plantable` (or setting it to `null`) enables planting if `resources` contains at least one valid tuple; absent/null/empty resources leave planting disabled. `plantable: true` requires resources. `plantable: false` disables only planting, preserving the resource cost and ground/spacing/biome settings for later re-enabling. Supplied settings are validated even for disabled recipes.
- Omitting `cultivatedGroundOnly` defaults to `true`; omitting `spacing` defaults to 1 meter. Spacing must be finite, greater than 0 and at most 100; the existing grid/mass spacing factor also affects preview spacing, and placement uses a 0.25-meter floor.
- Fixed harvested visuals apply to both wild and previously planted instances, except where planted Fiddlehead foliage replaces them. Disabling a recipe, removing its entry, or using `[]` disables planting only, not these visuals. Cut direction/fraction and per-prefab visual on/off are not configurable. Existing YAML files are never replaced by generated defaults.
- The removed `pickedVisual` field is not accepted, ignored, or migrated. Delete every `pickedVisual:` field from existing YAML files; leaving one present (even `null`) rejects the configuration. Visual-only RoyalJelly entries may also be removed because they are no longer needed.
- The old nested `planting:` schema is not supported or migrated. For an existing file, remove each `planting:` wrapper and move its `resources`, `cultivatedGroundOnly`, and `spacing` fields to the same level as `prefab`, preserving your values. Invalid files are rejected and leave the last-known-good rules unchanged for the running session.
- Newly planted Pickables start empty and use their normal respawn cycle as the first growth wait. Their planter's Farming level is recorded for that first cycle; later cycles use the picker. Respawn time and Farming opt-in still belong in `pickables.yml` and are not duplicated here.
- These are directly planted Pickables, not new saplings. Cultivated-ground and spacing are placement checks; Plant health and growth-biome checks do not apply. Existing vanilla crop and tree saplings are unchanged.
- `biomes: [Meadows, BlackForest, Plains]` belongs at the same level as `prefab`. It restricts single, grid, and mass placement (each final slot is checked) only when planting is enabled, not regeneration of existing plantings or wild Pickables. Omit it to preserve live placement restrictions; no biome restriction is added if the live prefab has none. A non-empty flow list of explicit names is required; `None`, `All`, numbers, and duplicate names are rejected.
- Cultivation biome names use the same EWD nature groups as `plants.yml`: `[Mistlands]` allows vanilla Mistlands and custom biomes with `nature: Mistlands`. Prefer the vanilla nature name over a custom alias when selecting that group. An independent custom biome without a nature/terrain alias can use its own name. If any name is unknown or its EWD mapping is not ready, the complete cultivation restriction is retried and new planting is blocked until it resolves; no partially resolved list is applied.
- Grid and mass planting support configured Pickables. Planted Pickables can be uprooted with the Cultivator without returning materials or harvesting their contents; unplanted wild Pickables are not made removable by this feature. Disabling a recipe does not delete existing plantings.
- Static cut meshes are cached once per source prefab; instances share meshes and vanilla materials. Recipe or growth YAML changes refresh visibility without rebuilding fixed visual meshes. No lights, particle effects, extra mesh colliders, or asset bundles are copied.
- Groundwork-planted `Pickable_Fiddlehead` automatically retains the vanilla `FernAshlands` foliage before and after harvesting, replacing its cut remnant. Wild Fiddlehead keeps its fixed bottom-30% remnant. The foliage shares vanilla meshes/materials and LODs without copying colliders, destruction scripts, or a network object; hover, harvesting, growth, and removal still belong to the Pickable. It is restored from the saved planting marker on reload, persists when the recipe is disabled/removed, and disappears with the parent when uprooted. If the source visual is unavailable, the fixed cut remnant remains the fallback. Leaf overlap at close spacing and appearance on slopes should be checked in game.
- Unsupported or unreadable meshes fall back to hover-only behavior. Actual silhouette, cut surfaces, slopes, and modded materials should be checked in game.

Groundwork declares a BepInEx incompatibility with **PlantEverything** (`advize.PlantEverything`). If both are installed, BepInEx skips loading Groundwork and reports the incompatibility. Use one of the two mods; no PlantEverything installation is required for Groundwork's recipes or visuals.

## Notes

- Rain effects use the currently detected wet environment and are not accumulated while an area is unloaded.
- `Beehive Rain Honey Rate` affects loaded beehives only. Unloaded honey catch-up is processed without rain history; current rain applies after the beehive is loaded.
- Rain disables beehive pollination while the area is loaded.
- Dedicated servers may not have precise per-position weather history.
- Plant and foraging targets store owner-authoritative dynamic bonus progress and the last loaded/unloaded rates in ZDOs so later honey, weather, or pollination changes affect only future time; transient target-assignment caches remain local.
- ZenBeehive beehive containers are supported: honey removed from the container counts as beehive harvest for Farming skill gain and capacity ownership.
- `BepInEx/config/Groundwork/Groundwork.yml`, `pickables.yml`, `plants.yml`, and `cultivation.yml` are created automatically if missing.
- `pickables.reference.yml` and `plants.reference.yml` are checked after each world prefab load and rewritten only when their settled content differs.

## Building

Configure the local Valheim/BepInEx paths in `environment.props`. For development and testing:

```text
dotnet build Groundwork.sln -c Debug -p:DeployToGame=true
```

This builds the final merged DLL in `bin/Debug/` and copies it to the configured game plugin directory after successful compilation and merging. Use `-p:DeployToGame=false` to skip copying. The legacy `DeployLocal` option remains a fallback when `DeployToGame` is omitted. Debug builds never package releases or update the distribution manifest.

For an explicitly requested release, run `dotnet build Groundwork.sln -c Release`. Ordinary Release builds update `Thunderstore/manifest.json` from the assembly version and generate the Thunderstore and Nexus ZIPs. Registered Mod Release Manager watch folders can automatically publish newly generated ZIPs to the project's selected sites. For packaging without publishing, disable that project's automatic upload before building or use an unwatched output location. Use `-p:BuildPackages=false` for a DLL-only Release build. Game DLL deployment remains opt-in.
