# Farm Queue

Drag a rectangle over your farm and your farmer automatically walks over and completes every job inside it — tilling, watering, planting, fertilizing, harvesting, chopping, mining, placing sprinklers, loading machines, and more.

Inspired by *Don't Starve Together*'s **ActionQueue Reborn**.

---

## Installation

1. Install [SMAPI 4.0.0 or newer](https://smapi.io/).
2. Download and unzip this archive into your `Stardew Valley/Mods` folder.
3. Run the game through SMAPI.

The mod folder should look like this:

```
Mods/
└── FarmQueue/
    ├── FarmQueue.dll
    ├── manifest.json
    ├── README.md
    └── CHANGELOG.md
```

---

## Usage

1. Hold the tool or item you want to use in your inventory bar.
2. Hold **LeftControl** and **drag with the left mouse button** to draw a rectangle over the area.
3. Release. The queue starts automatically and your farmer walks over and does the work.

Tiles are marked as you drag:

- **Green** — this tile has a job the held item can do, and will be processed.
- **Red** — nothing to do here with the held item (already done, wrong tool, blocked, etc.).

### Pausing

Press any **movement key** (WASD / arrow keys) to stop the queue at any time. Switching tools, opening a menu, or alt-tabbing also pauses it.

---

## What each tool does

| Held item | Actions |
|---|---|
| **Hoe** | Till soil, clear weeds, dig artifact spots and seed spots |
| **Watering Can** | Water crops; automatically walks to a water source and refills when empty |
| **Seeds** | Sow on tilled soil (including trellis crops) |
| **Fertilizer** | Apply to tilled soil |
| **Sapling** | Plant fruit trees and saplings |
| **Scythe** | Harvest mature crops and flowers, clear weeds / dead crops / grass (incl. blue grass), collect moss, forage items, and machine products (tappers, kegs, …) — collecting the product only, the tapper itself stays on the tree |
| **Pickaxe** | Break stones, ore nodes, geodes, weeds, fences, boulders; flatten tilled soil; remove idle placed objects |
| **Axe** | Chop trees and fruit trees, clear seeds/saplings, stumps and logs, twigs, weeds, wooden fences and gates; knock the tapper off tapped trees; chop bushes (see below) |
| **Sprinkler** | Place sprinklers on a planned grid |
| **Machine input** | Walk to machines and load them (kegs, furnaces, preserves jars, …) |
| **Other placeable objects** | Place them on the ground |
| **Empty hands / any misc item** | Walk to every farm animal in the rectangle and pet it (nearest first) — holding a Parsnip or a plank works too, as long as the item has no farm-work meaning for the queue |
| **Shears / Milk Pail** | Harvest-if-ready, pet-otherwise: grown sheep/cows with produce ready are sheared/milked; every other animal in the box is petted if not already petted today |

Animal care notes: when shearing or milking, the farmer pets each animal it visits if it hasn't been petted yet, so one trip earns both friendship bonuses. Animals that can't be reached — for example ducks that swam out into open water — are treated as unreachable and skipped, and the end-of-run report says how many. Unreachable animals get one automatic retry pass, since animals move around. Petting and harvesting use the game's own interaction logic, so friendship, happiness, product quality, stamina cost and animations match hand-play.

Bush notes: bushes are only queued when you actually hold the axe — they are not part of the "clear this messy corner" auto tool-switching chain, because destroying a hedge is not something to do by accident. **Clear Bushes** (axe section, on by default) is the switch that turns it off entirely. Whether a bush *can* be chopped is asked from the game (`Bush.isDestroyable()`), so with [Destroyable Bushes](https://www.nexusmods.com/stardewvalley/mods/6304) installed ordinary bushes become choppable in the queue too, and any bush type you disable in that mod's config stops being queued here. Tea bushes are never chopped: they produce tea leaves and vanilla lets an axe destroy them, which makes them the easiest thing to lose by accident.

---

## Configuration

Open **Generic Mod Config Menu** (GMCM) and click the cog next to *Farm Queue*. Every behavior can be toggled per-tool, so you can restrict the queue to exactly the jobs you want.

Sections:

- **General** — stamina floor, walk timeout, charge speed
- **Hoe** — what counts as a job, sprinkler-range-only tilling
- **Watering Can** — water only planted crops, auto-refill
- **Seeds** — sprinkler-range-only sowing
- **Scythe / Pickaxe / Axe** — per-target toggles
- **Machine loading** — idle machines only
- **Sprinklers** — grid layout
- **Layout** — sprinkler spacing for trellis-crop layouts

The in-game config menu, on-screen status text and tool names follow your **game language** automatically — English and Chinese are included. Other languages fall back to English.

---

## Known limitations

- The farmer walks using simulated directional input, so very tight corridors or crowded farms may occasionally cause a tile to be skipped. Skipped tiles are retried automatically.
- Charged tool use is matched to the original game's charge-level shapes; the mod will never till outside the rectangle you drew.

---

## Requirements

- Stardew Valley 1.6+
- SMAPI 4.0.0+
- Generic Mod Config Menu (optional, recommended)

---

## Credits

- **Author:** MLD
- **Inspiration:** *ActionQueue Reborn* for Don't Starve Together

---

## Source / License

Feel free to report bugs or suggest features in the comments.
