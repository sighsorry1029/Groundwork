# Path of Valheiman compatibility

[Back to Groundwork](../README.md)

Groundwork detects Path of Valheiman (PoV) as an optional dependency. It checks the required callback signatures and IL patterns before installing its compatibility hooks. A version number alone neither rejects the adapter nor produces a warning. A failed check still logs the reason, including the callback when applicable, and cancels the installation, removing hooks installed during that attempt.

The cultivation integration and seed list below were checked against the supplied PoV **5.1.0** DLL. Matching code structure does not establish that every future version behaves identically. No PoV DLL is bundled with Groundwork. Use the same mod combination and versions on the server and clients.

## Wild sowing and YAML planting

When the compatibility adapter is active and PlantEverything is absent, PoV's **`[1. Balance] Wild sowing`** setting controls Groundwork's additional Pickable recipes:

| Effective PoV setting | New planting with the Cultivator |
| --- | --- |
| `true` | Groundwork's valid, `plantable: true` entries in `cultivation.yml` are available. YAML defines the ingredients, amounts, cultivated-ground requirement, biomes and spacing. |
| `false` | Those additional recipes are hidden and new placement is blocked. Ordinary vanilla crops, saplings and terrain tools keep their existing behavior. |

Wild sowing defaults to `true` in PoV 5.1.0. Groundwork reads PoV's effective setting without rewriting PoV's configuration. PoV 5.1.0 synchronizes this balance setting from the host; dedicated-server operators should set it on the server. Live changes refresh the recipe menu on the next Groundwork update.

PoV's separate `PT_wild_*` planting recipes are suppressed while Groundwork supplies cultivation. They cannot provide a second, cheaper route around YAML costs or restrictions. An empty YAML file or `plantable: false` does not reopen the PoV route. PoV's prefab registration is preserved so this change does not intentionally delete existing PoV plantings. Existing Groundwork plantings also retain their growth, harvested visuals and removal behavior when Wild sowing is switched off. Neither type is converted to the other.

**Wild sowing is a setting, not the Cuttings node.** Groundwork does not require a personal Cuttings unlock to use a recipe. Cuttings controls PoV's seed drops. Adding a seed cost makes seed possession a requirement, and those seeds can also be obtained from other players. Turning off Cuttings does not delete seeds players already own.

If **PlantEverything** is installed, Groundwork's existing PlantEverything handoff takes precedence: Groundwork does not load or generate `cultivation.yml`, and this YAML gate does not take over PoV's recipes. This does not add new PlantEverything–PoV coordination.

## Optional PoV seed ingredients

**Groundwork never adds, replaces or removes seed ingredients automatically.** Edit `resources` explicitly if you want a seed cost. Existing YAML is not rewritten when PoV is installed, removed or updated.

These are PoV 5.1.0's seed mappings. The first column is the harvested **item prefab**, not the `prefab` to plant in `cultivation.yml`.

| Harvested item prefab | PoV ingredient prefab |
| --- | --- |
| `Raspberry` | `PT_SeedRaspberry` |
| `Blueberries` | `PT_SeedBlueberry` |
| `Cloudberry` | `PT_SeedCloudberry` |
| `Mushroom` | `PT_SporeMushroom` |
| `MushroomYellow` | `PT_SporeYellowMushroom` |
| `MushroomBlue` | `PT_SporeBlueMushroom` |
| `MushroomJotunPuffs` | `PT_SporeJotunPuff` |
| `MushroomMagecap` | `PT_SporeMagecap` |
| `MushroomSmokePuff` | `PT_SporeSmokePuff` |
| `Thistle` | `PT_SeedThistle` |
| `Dandelion` | `PT_SeedDandelion` |
| `Vineberry` | `PT_SeedVineberry` |

This ingredient list does not make every corresponding plant eligible for Groundwork cultivation. Its normal requirement still applies: a supported networked Pickable with a repeating native respawn timer. Ordinary `Plant` crops and saplings are not registered through `cultivation.yml`.

PoV 5.1.0 has **no seed mapping for Lingonberry or Fiddlehead fern**. Do not invent a seed ID for them. Keep or customize their YAML costs directly. Their new planting uses Groundwork's rules rather than PoV's fallback recipe costing one harvested item.

### Example: add a seed to a blueberry recipe

Replace the existing `BlueberryBush` entry in `cultivation.yml`; do not append a duplicate. This example charges all three listed ingredients for each bush:

```yaml
- prefab: BlueberryBush
  plantable: true
  resources:
    - Blueberries, 30
    - Pukeberries, 5
    - PT_SeedBlueberry, 1
  cultivatedGroundOnly: true
  spacing: 2
```

You may choose different amounts or remove a produce requirement yourself. Groundwork does not add a hidden seed charge beyond this list.

### Example: keep Lingonberry's explicit costs and biome

Replace the existing `LingonberryBush` entry with:

```yaml
- prefab: LingonberryBush
  plantable: true
  resources:
    - Lingonberry, 30
    - RottenMeat, 15
  cultivatedGroundOnly: false
  biomes: [DeepNorth]
  spacing: 2
```

There is no PoV seed requirement in this example. Wild sowing must still be enabled for new planting, and Groundwork's Deep North restriction and material costs remain in effect.

Use Groundwork's existing YAML authority and synchronization settings. A missing ingredient prefab produces a warning and prevents that recipe from being registered; the missing cost is never silently dropped. If you remove PoV later, update any `PT_Seed*` or `PT_Spore*` requirements yourself.

## Other compatibility behavior

- **Progression:** PoV's skill progression policy remains authoritative. Its default `Disable vanilla progression = true` and `Frozen skill level = 0` can reduce Groundwork's other Farming-based bonuses. Groundwork does not substitute saved skill levels or tree levels.
- **Mass/grid planting:** With PoV's public stats API, Broad Sowing (`MassPlant`) unlocks up to 25 plants regardless of Farming level. Refunding it returns selection to single planting. Alt + wheel, previews, G grid toggling and Groundwork's key hints remain available; G grid planting itself does not require the ability. PoV's separate mass-plant input, preview and extra placements are suppressed, including while its own key is held. Groundwork's `Mass Planting Enabled` still controls multi-plant placement. Without the stats API, the previous PoV planting handoff is retained. No PoV width/length settings are imported. Without PoV, Groundwork keeps its Farming-level conditions.
- **Harvesting:** PoV's area-pickup ability handles automatic range harvesting when available. Additional calls from Groundwork's scythe/range harvest or PoV's airborne harvest do not start another area search.
- **Tools:** PoV's scythe radius and pickaxe terrain-prefab selection run before Groundwork's corresponding changes. Node bonuses remain PoV's calculations.
- **Growth:** Explicit `plants.yml` grow times are applied before PoV's growth bonus and Groundwork's effects. PoV wild-regrowth bonuses use a separate per-cycle rate combined with Groundwork's required time, rather than backdating the harvest timestamp. PoV's existing owner eligibility remains in place; this does not add remote-owner bonus delivery. Previously backdated timestamps are not migrated.
- **Hives:** Node bonuses and Groundwork effects share production-time and capacity calculations. Winter Hive's biome permission is respected. Builder credit, extra rewards, Hive Chain, Swarm Split and other PoV node behavior remain enabled. Stored honey is preserved when effective capacity decreases.
- **Replanting and hints:** Quick Replant supplies its actual player to Groundwork's planting-skill snapshot. Bed Watch uses Groundwork's remaining-time calculation; only its duplicate timer is suppressed when Groundwork displays one. Other PoV credit/planter text is retained.
- **Biome rules:** Explicit administrator restrictions take precedence over PoV's planting unlock for matching `plants.yml` entries. That unlock is not extended to Groundwork's `cultivation.yml` Pickable recipes.

## Validation scope

The adapter does not introduce new RPC authorization or anti-cheat enforcement. Its placement gate covers the normal player planting path; it does not redefine other mods' direct placement APIs or PoV's reward/RPC acceptance policy.

Automated checks use original game/PoV assemblies in an isolated Mono process and cover configuration transitions, duplicate recipe suppression, existing registration retention, PlantEverything handoff and rollback on an incompatible callback. They do not run a Unity scene or a multiplayer server.

Before deploying to an existing multiplayer world, verify:

1. Wild sowing on/off on the host, including a client joining after a change and a previously selected recipe.
2. Exact material consumption for single and mass planting with and without sufficient PoV seeds; no alternative PoV recipe appears.
3. Existing Groundwork and PoV plantings after save/reload, harvest, removal and network-owner changes.
4. Broad Sowing unlock/refund, simultaneous harvests, and the server's YAML synchronization mode.
