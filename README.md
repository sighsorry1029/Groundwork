# Groundwork

Pollination boosts honey production and plant growth, rain speeds plants, hoe/cultivator ranges scale with grid views, pickaxes dig scaling, scythes harvest all crops, and Farming skill scales mass/grid planting, plant growth, and beehive capacity.

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
Rain and pollination plant growth info. Hover text shows active Farming, pollination, and rain multipliers, remaining growth time, and total growth speed.

![](https://i.ibb.co/ycJdxWfq/Screenshot-2026-06-16-020330.png) <br>
Farming-scaled mass/grid planting and foraging pollination. Plant in clean grids, scale planting count by Farming level, and let beehives boost nearby foraging respawn when conditions are right.

## Features

### Farming and Planting

- Mass planting scales with Farming level:
  - 0-19: Off
  - 20-39: 5 plants
  - 40-59: 10 plants
  - 60-79: 15 plants
  - 80-99: 20 plants
  - 100: 25 plants
- Hold the tool wheel modifier hotkey and use the mouse wheel to change mass-plant count.
- Toggle grid planting while placing crops.
- Planted crops can grow faster based on the planter's Farming skill.
- Mass planting can grant extra Farming skill.

### Foraging

- Edible respawning pickables, such as berry bushes, can be affected by Farming skill.
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

### Terrain Tools

- `Groundwork.yml` controls Hoe and Cultivator piece costs and adjustable terrain ranges.
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

## Config

Main config:

```text
BepInEx/config/sighsorry.Groundwork.cfg
```

Terrain tool config:

```text
BepInEx/config/Groundwork.yml
```

Synced entries are controlled by the server in multiplayer. Local entries only affect the client.

### 1 - General

- `Lock Configuration` = `On` [Synced]  
  Locks synced config to server admins.

### 2 - Terrain Tools

- `Terrain Tool Range Step` = `0.5` [Local]  
  Range adjustment step. Hoe/Cultivator use meters; Pickaxe `terrainDig` uses scale units.
- `Terrain Tool Default Preview Mode` = `Vanilla` [Local]
- `Terrain Tool Preview Toggle Hotkey` = `G` [Local]
- `Tool HUD` = `On` [Local]  
  Shows terrain range and pickaxe dig scale HUD.
- `Paved Road Smooth Height` = `On` [Local]
- `Tool Wheel Modifier Hotkey` = `LeftAlt` [Local]  
  Hold while using the mouse wheel for Groundwork tool controls.

Terrain tool range costs only increase above the vanilla/base range:

```text
rangeRatio = selectedRange / baseRange
areaRatio = rangeRatio^2
multiplier = 1 + max(0, areaRatio - 1) * factor
```

Pickaxe terrain digging uses radius and depth:

```text
radiusScale = min(selectedScale, radiusMax)
depthScale = min(selectedScale, depthMax)
rawMultiplier = radiusScale * radiusScale * depthScale
costMultiplier = 1 + (rawMultiplier - 1) * factor
```

Pickaxe `terrainDig` enable state, scale, and cost factors are configured in `Groundwork.yml`.

### 3 - Mass Planting

- `Mass Planting Enabled` = `On` [Synced]
- `Toggle Grid Planting Hotkey` = `G` [Local]
- `Mass Plant Spacing Factor` = `1.0` [Synced]
- `Mass Plant Skill Gain Factor` = `0.5` [Synced]

Spacing uses the selected plant prefab's `growRadius * 2`, then applies `Mass Plant Spacing Factor`.

### 4 - Plants and Foraging

- `Plant Grow Speed Factor` = `2.5` [Synced]  
  Grow speed at Farming 100 for placed plants. `0` disables.
- `Foraging Pickup Max Range` = `5` [Synced]  
  Pickup range at Farming 100. `0` disables.
- `Foraging Respawn Speed Factor` = `5` [Synced]  
  Respawn speed at Farming 100. `0` disables.
- `Rain Plant Grow Speed Factor` = `2` [Synced]  
  Plant grow speed while wet. `1` disables.
- `Rain Foraging Respawn Speed Factor` = `2` [Synced]  
  Foraging respawn speed while wet. `1` disables.

### 5 - Beehives

- `Beehive Capacity Farming Levels Per Bonus Honey` = `20` [Synced]  
  Farming levels needed for each `+1` honey capacity. `0` disables.
- `Beehive Farming Skill Gain Per Honey` = `0.25` [Synced]
- `Beehive Cover Max Speed Multiplier` = `2` [Synced]
- `Beehive Night Honey Rate` = `0.5` [Synced]  
  Honey production rate at night. `1` is the vanilla value, `0.5` is half speed, and `0` pauses night production. Unloaded catch-up uses an average day/night rate.
- `Beehive Rain Honey Rate` = `0.5` [Synced]  
  Honey production rate while wet. `1` is the vanilla value, `0.5` is half speed, and `0` pauses rain production. Rain is not accumulated during unloaded catch-up.

### 6 - Pollination

- `Beehive Pollination Radius` = `3` [Synced]
- `Beehive Pollination Max Plants` = `24` [Synced]
- `Beehive Pollination Plant Grow Speed Factor` = `2` [Synced]
- `Beehive Pollination Foraging Respawn Speed Factor` = `4` [Synced]
- `Beehive Pollination Honey Speed Bonus Percent Per Target` = `10` [Synced]

Honey pollination rate:

```text
1 + targetCount * percentPerTarget / 100
```

With defaults, `24` targets at `10%` gives:

```text
1 + 24 * 10 / 100 = x3.4
```

When an area catches up after being unloaded, cover and pollination bonuses use half effectiveness:

```text
unloaded = 1 + (currentMultiplier - 1) * 0.5
```

Eligibility is checked first, so full hives, finished plants, blocked hives, wrong biomes, and rainy loaded areas do not get invalid pollination bonuses.

## Groundwork.yml

`Groundwork.yml` defines terrain tool piece costs and adjustable ranges.

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

On multiplayer, the server's `Groundwork.yml` is synced to clients.

## Notes

- Rain effects use the currently detected wet environment and are not accumulated while an area is unloaded.
- `Beehive Rain Honey Rate` affects loaded beehives only. Unloaded honey catch-up is processed without rain history; current rain applies after the beehive is loaded.
- Rain disables beehive pollination while the area is loaded.
- Dedicated servers may not have precise per-position weather history.
- Groundwork does not write transient pollination multipliers to ZDOs.
- `Groundwork.yml` is created automatically if missing.
