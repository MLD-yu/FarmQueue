# Changelog

All notable changes to **Farm Queue** are documented here.
Versions follow the mod's `manifest.json`.

## [0.5.2] — 2026-11-10

### Clear bushes (with Destroyable Bushes)
Requested as *"can you support this mod? if it's installed, let me box the bushes too"*.

- **Before.** Bushes live in `GameLocation.largeTerrainFeatures`, not in `terrainFeatures` or
  `objects`, so the tile reader never saw them: boxing a hedge with the axe in hand painted it red
  and the queue walked straight past. Only shaking them for berries was implemented.
- **Now.** A new axe option, **Clear Bushes** (on by default), queues a bush tile and swings the axe
  at it until it is gone. The wood that falls out is picked up like any other debris.
- **Whether a bush *can* be cleared is asked from the game, not guessed.** The check is
  `Bush.isDestroyable()`: vanilla answers `true` only for tea bushes and for the few bushes along
  the edges of some farm maps, while Destroyable Bushes (`Esca.DestroyableBushes`) postfixes the
  same call so ordinary bushes answer `true` too. That keeps the two mods in sync for free — turn a
  bush type off in that mod's config and this option stops queueing it.
- **Multi-tile bushes count once.** A bush is 1, 2 or 3 tiles wide, but the queue only queues its
  anchor tile, so one hedge is one job instead of two or three.
- **Tea bushes are never cleared.** They are a crop (tea leaves), the mod does not touch them, and
  vanilla lets an axe destroy them — exactly the kind of thing you do not want an automatic queue
  deciding for you. Do those by hand.
- Not part of the "clear this messy corner" auto tool-switching chain: bushes are only cleared when
  you actually hold the axe. Same reason as above.
- A level-0 axe still queues nothing without the mod (vanilla refuses to damage bushes below axe
  level 1); with the mod installed its `AxeUpgradesRequired` setting decides, and it defaults to 0.

### The config menu is no longer a wall of text
Nearly every switch carried a paragraph explaining how it works, which made the page hard to scan.

- Tooltips removed entirely for: skip charge animation, charge animation speed, open backpack on
  finish, and every per-tool switch in the hoe / watering can / seeds / scythe / pickaxe / axe /
  load sections. A switch that needs that much explaining can be inferred from its name.
- Tooltips cut back to one line where the extra detail was an argument rather than information:
  the modifier key (no more aside about Shift being vanilla's run key), the stamina threshold (only
  "stops the queue when stamina drops below this value"), and auto tool switching.
- **All switches and sliders are still there, with the same defaults** — only the prose is gone.
  That includes the sapling gap sliders, which keep their place in the Layout section.
- The Layout section keeps its two short notes, because "Default" vs "Trellis" is not
  self-explanatory.

### Mod name is plain FarmQueue
`manifest.json`'s `Name` is now **FarmQueue** instead of `MLD·FarmQueue`, so the SMAPI console load
line and the GMCM page title match the mod's page name. Only the display name changed — `UniqueID`
stays `MLD.FarmQueue`, since that is what config files, GMCM settings and any third-party
integration key off.

### Routine queue notices no longer spam the log
Reported as *"delete the warning-type messages, like these"* with a console window full of
`WARN` lines: *paused*, *out of stamina*, *no work in this area*, *out of seeds*,
*no animals here*, plus a one-line run summary after every box.

- **Before.** Every one of those went through the same helper that wrote a `WARN` line to the
  SMAPI log *and* a warning-coloured HUD bubble. Dragging a box that turned out to be empty, or
  tapping the hotkey a few times, produced dozens of `WARN` lines a minute — the log became
  unreadable and looked like the mod was constantly failing.
- **Now.** These notices are what they actually are — normal end-of-run information — so they no
  longer touch the log at all: the in-game bubble is still shown (that is how you know *why* the
  queue stopped), but nothing is written to the console. Turn on **Debug log** in the config to get
  them back as `TRACE` lines while troubleshooting.
- Real problems are untouched: failed machine loads, refill failures, gates that will not open and
  thrown exceptions still log at `WARN`/`ERROR`.

### Update checks
`manifest.json` now carries `UpdateKeys: ["Nexus:54145"]`, so SMAPI tells players when a newer
version is on the Nexus page instead of them having to re-download by hand.

## [0.5.1] — 2026-11-04

### Sprinklers are actually placed again
Reported as *"after I drag a box with a sprinkler in hand, it only walks around and never puts one
down."*

- **Before.** The whole sprinkler action was an empty shell: the farmer walked to the tile, cleared
  its click target, "verified" the result and moved on — the actual placement call had been lost in
  an earlier refactor. Planning, queueing and walking were all fine, so the only thing visible on
  screen was a farmer pacing the field. Because the placement work type also sits in the retry band,
  every tile got one extra walk-back before being counted as failed.
- **Now.** Placement is back and is verified **by fact**, like every other job: the tile counts as
  done only when a sprinkler is really sitting on it. A tile that could not be placed is recorded as
  a failure instead of being counted as "done" (the old code incremented the done counter
  unconditionally).
- Note for the same report: the summary was already recomputed from the real world state, so a run
  that placed nothing ended with *0 done / N failed* — the numbers were honest, the action was not.

### Auto-switch item quality (new option, on by default)
Reported as *"different qualities of crops / produce don't switch automatically when they run out
(brewing, processing) — you have to switch by hand."*

- **Before.** The farmer loads the stack in hand into every machine it reaches. When that stack runs
  out the queue stops with *"out of items"*. A silver- or gold-quality stack of the same crop
  sitting in the backpack was never picked up — the player had to select it by hand and start
  another run.
- **Now.** A new option, **Switch item quality automatically** (on by default), makes the farmer
  pick up the same item at another quality when the held stack runs out: the normal corn goes into
  the kegs, then the silver stack, then the gold one, without a single manual switch. Turn the
  option off to get the old behaviour back.
- **Same item only.** The match is on the item id, and preserves / wines / juices are additionally
  matched on their source fruit — a pear wine never tops up a blueberry wine. Seeds, fertilizer and
  flooring have no quality and never trigger this.
- **Backpack stacks are moved onto the toolbar** the way the game's own shift-scroll does (toolbar
  slot and backpack slot are swapped), so nothing is lost.

### Tapped trees (axe section: a switch for the tapper itself)
*"A tree with a tapper is only dealt with when the tapper option is on"* — and, pointing at the scythe
section, *"the machine-output option uses the scythe, it only collects the syrup."* Both were right:
the two jobs belong to two different tools and two different switches, and nothing said so.

- **Before.** `GetAxeWork` returned `None` for every tree with `tapped == true`, unconditionally: no
  combination of options could queue it. Vanilla is the reason (`Tree.performToolAction` bails out on
  `tapped` before the axe does any damage), but the mod never offered a way through.
- **Now.** A tapped mature tree is only touched when **Remove tree tappers** is on:
  - with **Remove trees (mature)** on as well, the farmer knocks the tapper off and then chops the
    tree; the stump follows as usual;
  - with it off, the tapper comes off and the tree stays standing.
  - with **Remove tree tappers** off, the tree is left completely alone — the mod never dismantles a
    player's machine on its own.
- **It is the vanilla action, not a scripted removal.** The farmer swings the axe at the tree and
  vanilla does the rest: `Axe.DoFunction` hits the terrain feature first (a tapped tree refuses the
  hit — no damage, no felling), then the object at that tile, so the tapper is knocked loose together
  with the syrup it collected. Both pop out as debris at the farmer's feet and get picked up by
  walking over them. An earlier build removed the tapper through the API instead, which looked like
  it falling off by itself — that is gone.
- **The scythe's "Collect machine output" is untouched by this**: it still just collects the syrup
  (right-click on the machine), never takes a tapper off, never chops a tree.
- **Mixed-tool run fixed.** With a scythe in hand the scythe used to claim those tiles for "collect"
  and the axe never got them, so "Remove tree tappers" would have silently done nothing there. When
  the axe can handle the tile, it is now handed to the axe (which collects the syrup on the way).
- Syrup that hasn't finished collecting is lost, exactly like prying a tapper off by hand.

## [0.5.0] — 2026-11-02

### Cleanup (release build)
- **Debug logging removed.** Every per-tile trace line is gone — the walk
  narration, the per-object "placed X" lines, the swing/verify dumps, the
  stall and give-up notes, and the `walktrace.log` file dump. What is left is
  the startup banner, the HUD messages you actually see in game, and real
  errors. A full field session now adds a handful of lines to the SMAPI log
  instead of a thousand.
- **The performance probe is no longer shipped** — no more `perf.on` / `perf.log`
  timing hooks patched into the hot paths.
- **Dead code pruned.** Decompiler leftovers, helpers and constants that only
  existed to build those log lines are gone (~18% fewer source lines). No
  gameplay, routing or tool behaviour was changed by this release.

## [0.5.98] — 2026-11-02

### Reporting
- **The "no work here" notices no longer editorialise about the box.** 0.5.96
  phrased the reasons as *"only sprinkler-covered soil is set to be tilled, and
  there is none here (144 tiles)"* — the tile count belonged to the box, not to
  the reason, and the trailing clause just restated the obvious. The notices now
  name the cause and stop: *"只处理洒水器覆盖到的土地"*, *"没有可以播种的耕地"*,
  *"没有空置的机器"*.

## [0.5.99] — 2026-11-02

### Placement (the gate is a stand, not an order swap)
Follow-up to 0.5.97, with a screenshot of the farmer sealing himself out:
*"when it reaches the last tile of the first row it places the last tile of the
second row, so it has to walk around again."*

- **What went wrong.** Swapping the turn tile with the next row's first tile made
  the farmer fill that next-row tile while he was still on the outside of the
  wall. The gap was used, but the row was completed from the wrong side, so the
  rest of it was still cut off.
- **Fix.** The order is back to plain S; instead the *stand* moves. When the
  current tile is a row turn (`GateStandFor` — next tile one row over and within
  one column), the approach tiles are re-ranked so the stand on the next row's
  side comes first. The farmer walks through the gap, turns round and places the
  turn tile from the inside, then carries on along the next row. Solid placement
  only (`SealPlacedTiles`); seeds, tilling and watering are untouched.

## [0.5.97] — 2026-11-02

### Placement (a gate at the end of every row)
Reported with a screenshot of a 35-wide keg wall: *"the last tile could be left
empty — walk through it into the next row, place it afterwards, and you don't
have to take the long way round."*

- **What it did.** After filling a row, the farmer stands on the outside of that
  row while the next row sits on the other side of the wall he just built, so the
  only way across is around the block. A live run logged
  `(34, 16) → (34, 18)，寻路 57 格` — for a tile that is two steps away.
- **Fix.** At every row turn the sweep now swaps the last tile of the finished
  row with the first tile of the next row (`OpenRowEndGates`). That gap is the
  only hole in the wall, so the walk into the next row goes straight through it
  (one or two tiles), and the tile itself is placed from the far side. Only
  turns that are exactly one row apart and aligned within a column are swapped;
  otherwise the hole would open somewhere the farmer cannot use.
- **Diagnostic.** The start-up note now ends with `；行尾留门 N 处`.
- **Superseded in 0.5.99.** Swapping the two tiles put the farmer on the wrong
  side: he placed the next row's last tile while still standing outside the wall,
  which sealed him out of the rest of that row — the same long walk, one tile
  later. The gate has to be a *stand*, not an order change.

## [0.5.96] — 2026-11-02

### Reporting
- **"Nothing to do in this area" now says what is actually blocking it.** The
  generic line appeared for every empty selection — a shed of ripe kegs with no
  bag room, a sprinkler-only setting with no sprinkler coverage, a box over
  grass. The start-up scan already counted a blocking reason per tile
  (`TillBlock`); it just never left the debug log. The notice now reports the
  most common reason with its tile count: *"背包放不下机器产物（3 台）"*,
  *"只处理洒水器覆盖到的土地，框里没有这样的格子（25 格）"*, and so on.
- **Machine tiles blocked by a full bag are no longer misfiled as "wrong
  tool".** They fell through to the generic wrong-tool bucket, which would have
  outvoted the real reason and hidden it.
- **When there is no per-tile reason** — the box simply holds nothing the held
  item can do — the message names what that item was looking for: nothing to
  water, nothing to sow, nothing to harvest, nothing to load.
- **The notices state the cause and stop there.** Instructions such as
  "collect the output or free a slot, then load again" are gone.

## [0.5.95] — 2026-11-02

### Fixed: coffee beans couldn't "collect + refill", and every successful load was
booked as a failure
Reported as *"coffee beans can't collect-and-fill — with a filler in hand,
selecting a machine that has output should collect and refill"*. Two separate
things were wrong.

- **`placed = false` is not a failure.** The load call passes
  `returnFalseIfItemConsumed: true`, which means *"I already took the input
  myself, don't take another one"*. Any recipe that eats more than one input
  (5 coffee beans into a keg) comes back `false` — while the keg is in fact
  filled and running. The old code read that return value as the verdict, so
  every such load was logged as `没干成` and pushed into the retry path.
  Success is now read from the result instead: the machine holds something new,
  or the stack in hand actually shrank. Live evidence (2026-11-02, 137 kegs):
  `probe=True placed=False`, yet `结束时 heldObject=Starfruit Wine` — the coffee
  was collected and the wine was loaded.
- **A ripe machine is a collect + refill, not a skip.** When the held item is a
  seed that doubles as a machine input (coffee bean), the ripe-machine branch
  bailed out, to avoid "walking past a ripe machine with seeds and sweeping its
  output". That made a shed full of ripe kegs read as 238 red tiles. The branch
  now asks the machine directly — *would you take this item if you were empty?*
  — and only then plans collect + refill. The answer is cached per machine type
  + input, so a 238-keg shed is probed once.

## [0.5.94] — 2026-11-02

### Reverted: 0.5.92's routing change
0.5.92 sent pot planting, machine loading, machine harvesting and pot-crop
harvesting through the row-run sweep (`BuildSegmentedPlacementOrder`) instead of
the nearest-first walk, to stop the left/right edge tiles being left until last.
In play that turned out worse than the behaviour it replaced, so **it is undone**:
those four jobs are back on the 0.5.91 route (nearest-first walk for pots and
machines, 3-row band-S for pot crops).

Untouched by this revert: 0.5.93's solid-placeable change (nearest-corner entry
with the escape check) still applies to actual object placement.

## [0.5.93] — 2026-11-02

### Placement (solid objects start from the nearest corner again)
Reported as *"shouldn't solid placement start from the nearest corner and sweep
in an S? what is it doing now?"*

- **What it was doing.** Since 0.4.33 the row order for solid placeables was
  hard-coded to start at the topmost planned row; only the left/right entry
  point of that first row followed the farmer. Standing below the field meant
  walking its full height before placing anything — a 34×21 keg run walked 19
  tiles from the farmer at `(14,18)` to `(4,8)` while a planned tile sat 2 tiles
  away, and the mod itself logged `⚠ 身边有活却要走远`.
- **Why it was that way.** A solid object becomes a wall the moment it is
  placed, so a bottom-up sweep leaves the farmer boxed in at the top edge: one
  live run retried the top-left tiles 466 times, every stand answering
  "no path", 225 tiles left. 0.4.33 pinned the direction to top-down as a blunt
  fix for that.
- **Fix.** The entry side is decided by the farmer's position again — whichever
  of the top/bottom rows is nearer — and the sweep runs away from it, so the
  order is "nearest corner, then S". A new escape check (`HasEscapeAtEnd`)
  guards it: if the row just past the last planned row has no standable tile
  (and neither end of the row does either), the sweep would finish against a
  wall and the whole block flips. Pot/machine work keeps the same nearest-side
  entry but skips the check — those jobs are done from the aisles and never
  seal anyone in.
- **Diagnostic.** Debug logging prints the chosen side, e.g.
  `摆放起手：从最下面那排往上扫（就近角）` or
  `…（就近那侧退不出去，已翻面）`.

## [0.5.92] — 2026-11-02

### Routing (edge tiles no longer deferred to the end)
Reported against three different jobs at once: *"when planting into garden pots or
filling machines, the ones on the left/right get ignored at the start, it plants
straight down, and those pots/machines are left until the very end — wasting a
whole lap."*

- **Root cause.** All three of those jobs went through `BuildPathWorkOrder`, a
  pure nearest-first walk with momentum: each step picks whatever is closest to
  where you are and prefers staying next to the last target. In a pot/keg grid you
  can only stand in the aisles, so the sweep locks onto a direction on the first
  pick and keeps going. Anything it walked away from is no longer "nearest" and is
  only picked up when the field runs dry — i.e. last. In the 2026-11-02 log a
  137-tile pot-planting run finished `(1,15)` and `(2,13)` dead last, and even
  logged `⚠ 身边有活却要走远：玩家 (2,11)｜身边 (2,13)｜要去 (8,9)` in between.
- **Fix.** Jobs whose *target tile you cannot stand on* (garden pots, kegs, casks,
  preserve jars, crab pots) now use the row-run sweep
  (`BuildSegmentedPlacementOrder`) instead of the nearest-first walk: pick the
  nearest run, enter at whichever end is closer, and sweep it to the far end
  before choosing the next run. A run is never abandoned half-done, so there is
  nothing left to come back for. Machines that aren't ready yet don't fragment the
  runs either — the run builder tolerates gaps of up to 3 tiles.
  Applies to sowing, machine loading, scythe-harvesting machine output and
  scythe-harvesting pot crops. Tilling, watering, ground sowing and ground
  harvesting are untouched (you stand on those tiles, so the old behaviour is
  correct there).
- **Pot crops now harvest nearest-first.** Scythe work on crops growing in garden
  pots was routed through the 3-row band-S sweep meant for open fields. Pots are
  spaced with aisles, so band-S kept weaving between neighbouring pot rows. Pot
  crops are now split out of the crop batch and swept start-from-nearest like the
  machines; only crops in real tilled soil keep band-S.
- **Requeue paths updated to match.** The two "tile was missed, put it back"
  paths both force band-S whenever the run contains crop work; they now exclude
  pot crops so a missed pot crop doesn't get shoved back into the S sweep.

## [0.5.91] — 2026-11-02

### Reporting
- **"Skipped N tiles" no longer counts work that was finished later.** The
  "done" figure in the end-of-run report was already recomputed from the world
  state, but the skip figure came from a counter that only ever went up. A tile
  that was given up on (couldn't walk there, or three failed checks) and then
  finished by a later pass — the retry round, the pre-finish rescan, the scythe
  re-check — was counted as done *and* as skipped, so "40 done / 3 skipped"
  could add up to more than the planned total. `FinishReport` now counts what is
  actually left after the final sweep and splits it into three non-overlapping
  buckets (unreachable / missed swing / plain skip); a tile that ends up
  finished is removed from the unreachable and failed sets on the way through.
- **The SMAPI summary line also reports unreachable tiles.** They only used to
  appear in the in-game bubble, which made the log's numbers impossible to
  balance against the plan.
- **Animal queue: the same fix for "skipped N animals".** A failed pet or
  harvest incremented the skip count and re-queued the animal; when the retry
  succeeded only the "unreachable" tally was cleared, so the bubble could say
  "12 animals done, 2 skipped" for a run where all 14 were handled. A target is
  now counted at most once (`SkippedOnce`) and the count is taken back when it
  is finally done.

## [0.5.90] — 2026-11-02

### Harvesting (audit against vanilla)
A pass over every "collect / pick / harvest" action in the mod, compared line by
line with the vanilla code path it imitates. Four things did not match.

- **Fruit trees are harvestable now.** A `FruitTree` is a `terrainFeature`, but
  the only fruit-tree handling was *chopping* it — its fruit was never picked,
  so dragging a box over an orchard did nothing until you cut the trees down.
  `HarvestFruit` work now plans any full-grown tree with `fruit.Count > 0` and
  plays vanilla's own action (`performUseAction` → `shake()`), which knocks the
  fruit off as debris aimed at the farmer, quality taken from the tree, and lets
  the built-in debris magnet collect it. (Same option as other foraging:
  "collect forage".)
- **Crops in garden pots can finally be hand-picked.** Detection was fixed in
  0.5.83, but the *picking* step still looked the soil up in `terrainFeatures` —
  a pot's soil lives on `IndoorPot.hoeDirt`, so a potted crop that has to be
  picked by hand (anything the scythe can't take) reported "nothing to pick" and
  was retried three times and dropped. It now asks
  `GameLocation.GetHoeDirtAtTile`, which knows both kinds of soil.
- **Potted tea bushes count as bushes.** `IndoorPot.bush` isn't a
  `LargeTerrainFeature`, so a greenhouse row of tea in pots was invisible to the
  bush work check. `BushAt` now covers both, and shaking goes through the same
  vanilla `Bush.performUseAction`.
- **Truffles (and any ground produce) no longer skip the forage settlement.**
  The animal queue picked ground items up with just the animation and the coin
  sound: no `OnHarvestedForage` (that is where the 7 foraging XP comes from), no
  `ItemsForaged` credit, and no 20% gatherer double. Vanilla's
  `GameLocation.checkAction` block is now mirrored, including the 5 farming XP
  you get for picking inside a farm building interior.
- **Ground forage pickup: two smaller mismatches.** The queue would pick up a
  quest item whose quest the player hasn't accepted (vanilla refuses and leaves
  it), and the 20% gatherer re-roll used `Game1.random` instead of the per-tile
  day-save RNG, so re-running the same tile could double where vanilla never
  would. Both now follow `GameLocation.checkAction`.

### Checked and already correct
Machine and crab-pot output go through `Object.checkForAction`; fish ponds use
vanilla's XP formula (10 + 4% of sell price); crops, grass, moss and weeds are
harvested by real tool swings; animal produce uses the real
`BeginUsingTool` path; ground-produce quality follows
`GetHarvestSpawnedObjectQuality` with the botanist override.

## [0.5.89] — 2026-11-02

### Foraging
- **Shaking a bush looks and behaves like vanilla again.** 0.5.85 fired the
  berries straight into the inventory, which meant no berries flying out and the
  farmer playing the ground-pickup animation. The queue now just plays vanilla's
  own action — `Bush.performUseAction` → `shake()` — so the bush shakes, the
  berries are knocked off as debris, and `Debris.chunksMoveTowardPlayer` pulls
  them in on the player's magnetic radius about 0.6 s later, exactly like
  right-clicking a bush by hand. Berry count, botanist iridium quality and
  foraging XP all come from vanilla again.
- **A bush that is still shaking no longer wastes the visit.** Vanilla's
  `shake()` bails out when the bush is already mid-shake, so two bushes shaken
  back to back could leave the second one with its berries on the branch. The
  fallback forces the shake in that case.

## [0.5.88] — 2026-11-02

### Scythe harvest
- **"Can I reach it from here?" now uses the farmer's real bounding box.** The
  reach test used the *stand* model, which assumes the farmer's collision box
  sits at the tile's left edge (+8 px). After the walk driver stops, the farmer
  usually stands centred (+24 px) — a 16 px difference, which is exactly the
  width of the tile at the edge of a field. In the 13:15 run the farmer stood at
  `(47,44)`, the model said `(45,44)` was reachable, so it swung instead of
  walking; the real box could not touch it in any facing, so the tile was retried
  twice in place, given up, and its bubble disappeared. The test now asks
  `ScytheCanHit(scythe, farmer, tile)` with the live box, so the farmer walks to
  a stand that really works.
- **Stand estimates cover both pixel poses.** For tiles the farmer has not walked
  to yet, `ScytheCanHit(stand)` now accepts a hit from either pose (+8 edge and
  +24 centred). Judging only the +8 pose kept stands that cannot actually work
  (the retry above), and judging only +24 would filter out stands that do work.

## [0.5.87] — 2026-11-02

### Queue start
- **A full bag stops the run at the start line, not at the first target.** The
  inventory check only ran once the farmer had walked all the way to a hand-pick
  target (forage / berry bush / fish pond / machine output) and was about to
  grab it, so a run started with a full bag began with a long pointless walk
  before reporting "inventory full". `Start` now pre-checks every planned
  hand-pick tile: if not a single drop fits the bag, the queue stops on the spot
  with the same message; if even one fits, the run starts normally and stops at
  the first blocked target like before (stacking still works).
- **Forage now reports "inventory full" too.** `IsHandPickInventoryFull` only
  knew about bushes, ponds and machines — a full-bag forage tile was silently
  retried three times, given up, and the farmer moved to the next one, never
  stopping at all. Forage items now use the same capacity check, so both the
  start-line pre-check and the on-arrival check cover them.

## [0.5.86] — 2026-11-02

### Scythe harvest
- **A scythe stand has to actually reach the target tile.** Approach tiles were
  ranked purely by "how many tiles one swing covers", never by "can this stand
  reach the tile at all". In the 12:39 run the queue picked `(47,44)` for
  `(45,45)` because that stand covered 14 tiles — but from there no facing touches
  `(45,45)`, so the tile was dropped as failed and its bubble disappeared while
  the crop stayed in the ground. Candidates are now filtered through
  `ScytheCanHit` before the coverage ranking.
- **An unreachable tile gets a different stand instead of failing immediately.**
  When all four facings from the current stand miss, the tile goes back to the
  front of the queue and that stand is remembered (`ScytheBadStands`), so the next
  attempt walks somewhere else and `FindApproachTiles` won't offer the dead stand
  again. Two stand changes later the tile is given up for real, so an impossible
  corner still ends the job instead of looping.

## [0.5.85] — 2026-11-02

### Foraging
- **Berry bushes are queued now (salmonberry / blackberry / tea).** A bush isn't an
  object and isn't a `terrainFeature` — it's a `LargeTerrainFeature`, so none of the
  work checks could see it: a box dragged over a row of spring salmonberry bushes
  reported "nothing to do" and the farmer walked straight past them. A new
  `ShakeBush` work item is planned for any bush whose berries are actually
  harvestable (vanilla's own `readyForHarvest()` **and** `inBloom()`, so spring
  days 15–18 / fall days 8–11 / tea's own window), gated by the "collect forage"
  option like any other forage.
- **Shaking delivers the berries straight to the bag.** Vanilla shakes the bush and
  flings the berries out as ground debris for the player to walk over; the queue
  stands *next to* the bush, so the chunks that land on the bush tile are
  unreachable and the yield would silently sit on the ground. The drop is now
  replicated with vanilla's own numbers (1–2 berries + foraging level / 4, fixed
  seed so nothing is duplicated by re-running, botanist profession forces iridium,
  foraging XP per berry) and added to the inventory directly, keeping the shake
  animation and the leaf/coin sounds. Bushes that are only decorative (town
  bushes) and the golden walnut bush are left alone.

## [0.5.84] — 2026-11-02

### Quality audit
- **Non-forage ground produce keeps its own quality.** The animal-queue ground pickup path was recalculating quality for every spawned object, which matched vanilla forage pickup but incorrectly reset non-forage animal drops to normal. Quality recalculation now only applies to `isForage()` items, matching `GameLocation.checkAction`; other ground objects keep the quality they were spawned with. Tool-harvested animal produce, crop harvests, machine output and fish-pond output were already using their original vanilla quality sources.

## [0.5.83] — 2026-11-02

### Scythe harvest
- **Crops in pots are queued now.** Crop detection only read `terrainFeatures`, but
  a garden pot's soil lives on `IndoorPot.hoeDirt`, so any crop in a pot
  (greenhouse, shed, indoors) was invisible to the queue — the scythe could cut
  it, yet it was never planned, never highlighted, and stayed behind. Both the
  crop work check and the "is this a scythe-harvest crop" check now go through
  vanilla's own `GameLocation.GetHoeDirtAtTile`, which knows both kinds of soil.
- **Missed-tile checks now wait for the swing to actually land.** The
  post-swing recheck ran on the same tick as `beginUsing`, but the scythe's
  harvest lands a few frames later (vanilla resolves it per animation frame, and
  scythe-harvest mods patch on top of that). A tile that had just been cut was
  therefore recorded as "still has work", re-queued, and counted as a failed
  attempt — three of those and the tile was dropped: the bubble disappeared while
  the crop was still standing. The recheck now runs 8 ticks after the swing.
- **A swing that cuts nothing no longer burns the tile's retries.** Each swing
  snapshots which covered tiles still had work. If a swing clears *none* of them
  it is treated as a mis-aimed swing (standing a half tile off, facing the wrong
  way, nudged by the walk driver) rather than a failed tile: the tiles go back to
  the front of the queue without spending one of their three attempts. Only two
  consecutive empty swings fall back to normal retry accounting, so a genuinely
  impossible tile still gets dropped instead of looping forever.
- **Scythe requeue keeps the band-S order.** Missed tiles used to be sorted by
  "closest to where this swing landed", which laid a second, opposite route on
  top of the band sweep — the farmer finished a row right-to-left and then walked
  back across the field. Crop tiles now go back through the same
  `BuildBandSWorkOrder` the main route uses.

### Route (scythe, gapped fields)
- **Bands now break at empty rows.** Bands were cut by a fixed
  `(y - minY) / 3`, so a field with a blank row between crop rows put two
  non-adjacent rows into the same band; walking that band column-by-column means
  jumping between rows that aren't neighbours, which reads as "finishes a row
  right-to-left and then walks back to the right" (seen in the 703-tile log: one
  band held both the whole y=9 row and a single leftover on y=11). Bands are now
  cut wherever a row is missing, and `bandHeight` only groups rows that really are
  adjacent. Contiguous (dense) fields produce exactly the same bands as before,
  so the large-field route is unchanged.
- **Mixed runs no longer flatten the crop route.** When the selection also
  contained debris (so the queue switched tools per tile), every scythe tile took
  the "nearest tile first" path, which is not S-shaped at all. Crop batches now
  keep their band-S order in mixed runs too; the nearest-first rule is limited to
  the debris segment.

## [0.5.82] — 2026-11-02

### Animal queue
- **Picking up ground produce now shows the vanilla pickup action.** The animal queue's `ActGroundItem` path played only the pickup sound and immediately inserted the item, so collecting truffles or other ground forage looked like a silent inventory transfer. It now plays the same `279 + facing` pickup animation and delayed coin sound as normal forage pickup. The queue already waits for `PauseForSingleAnimation` before moving, so the action is no longer cut off.

## [0.5.81] — 2026-11-02

### Route (hoe / watering / scythe requeue)
- **Row-S routes no longer break into blocks at small gaps.** When a plot had a
  path or untilled strip in the middle, `SplitConnectedBlocks` (4-connectivity)
  split it into left and right blocks, and each block got its own independent
  S-sweep. The farmer finished row 1 right-to-left on the right half, then went
  *back to the right* for row 2 of the same block instead of crossing the gap to
  continue row 1 (`收完会回到右边 而不是往下`). Same-row tiles within 3 tiles
  of each other are now treated as connected, so a 1–2 tile gap no longer
  splits the route; genuinely separate fields (3+ tiles apart) still get their
  own S-sweeps in nearest order. Applies to the hoe / watering-can initial
  route and the scythe crop requeue path.

## [0.5.80] — 2026-11-02

### Animal queue
- **Ground animal produce now uses the same pickup quality as normal forage.** The animal queue's `ActGroundItem` path added spawned ground items directly to the inventory without rolling quality, so a pig's truffle stayed normal while the same truffle picked through the scythe/hand-pick path became iridium with Botanist. Ground pickup now applies the vanilla harvest quality first (profession 16 forces iridium), then checks inventory capacity using the real item quality.

## [0.5.79] — 2026-11-02

### Walk
- **Walking through PassableCrops trellis crops no longer flickers back and forth.**
  With NCarigon.PassableCrops installed, crops on trellises (hops, grapes) are
  walkable but apply a heavy speed debuff (-3 or -1 temporary speed). The walk
  driver's stuck detection counted frames-per-tile, so the slow crawl through a
  single crop tile hit the 28-frame "stuck" threshold and triggered a snap-to-
  tile-center, producing a visible backward flash then resume — like network
  lag. Manual walking was unaffected because it bypasses the simulated-input
  pipeline. Stuck detection now tracks pixel-level displacement (a frame only
  counts as stuck when the player's pixel position is unchanged) and leg
  progress now measures pixel distance to the leg endpoint instead of tile
  Manhattan distance. Slow-but-steady movement through crops no longer
  misfires the fallback; genuinely stuck situations (against walls, rocks) are
  still detected within the same frame budget.

## [0.5.78] — 2026-11-02

### Hoe / watering
- **Small disconnected tiles are now watered or tilled while the farmer is passing nearby.** Hoe and watering-can runs used to finish the main connected block first, then travel back to the scattered edge tiles. Small blocks (up to six tiles) now insert into the row-S route wherever they add at most five steps of detour, so perimeter one-tile jobs are finished in passing instead of becoming a late backtracking trip.

## [0.5.77] — 2026-11-02

### Scythe
- **Debris cleanup no longer repositions for a wider swing.** In a scythe-only
  debris run, a weed that was already reachable could still trigger the
  "seek a stand tile that covers 2+ more tiles" step, and the arrival /
  reachability checks could then send the farmer back the other way - the
  live log showed him pacing between two stand tiles around one target for
  minutes without swinging (`走到 (54, 22) 旁边` repeated while the remaining
  count stayed 83). That step now only runs for the crop batch
  (crop / flower / dead-crop tiles); weeds, grass, moss and the other debris
  work swing as soon as the current position can reach the target. Mixed
  runs (`Plan != null`) were already excluded and are unchanged, and the
  crop-batch S-route / coverage behavior itself is untouched.

## [0.5.76] — 2026-11-02

### Forage
- **Botanist forage is explicitly locked to iridium quality.** Farm Queue's hand-pick path now applies profession 16 before either the normal item or the Gatherer bonus item enters the inventory. Players without Botanist keep the vanilla level-based quality roll.

## [0.5.57] — 2026-11-01

### Queue
- **Near-work swapping is stricter, so irregular selections stop zig-zagging.**
  The near-work rule fired whenever the head was 8+ steps away and another tile
  saved 3+ steps, which on irregular fields swapped rows after nearly every
  swing (`NEAR d=5/11`, `NEAR d=4/15`). It now only swaps when the head is at
  least 12 steps away and the closer tile saves at least 4 steps
  (`headWalk >= 12f`, `dist + 4f <= headWalk`). The big cross-field returns
  (26/25 steps) are still taken nearby first.

## [0.5.75] — 2026-11-01

### Queue
- **The nearest-tile ranking no longer treats chickens, cows, the pet or
  villagers as walls.** The field that decides "which pending tile is nearest"
  used the standability mask that counts every character as a blocker, and that
  map is cached for a second: an animal grazing in front of the farmer froze the
  weeds at his feet into "unreachable" for up to 60 ticks at a time, so the pick
  jumped to whatever was reachable somewhere else and he walked off past work he
  could touch (`明明眼前就有杂草 他非要绕一大圈去别的地方`). Ranking now floods the
  critter-free mask (249: terrain, objects, trees and buildings still block,
  characters are air) - ranking only sorts, the walk driver still routes around
  real bodies on the spot. Applied to the mixed debris run, the axe/pickaxe
  re-pick, the scythe nearest-pick and the hoe/can "work at my feet" rule.
- **A weed the scythe can already reach is never deferred for lack of a stand
  tile.** Reachability in the ranking field means "can I stand next to it", which
  is right for the axe and the pickaxe but too strict for a scythe: a weed one
  layer inside a patch has no standable neighbour, read as unreachable, and
  waited while the farmer went around. Tiles the scythe in hand can hit from
  where he stands now rank at 0 steps, both in the mixed-run scan and in the
  scythe nearest-pick.
- The box-close line and the `LONG-WALK` trace (0.5.73) use the same critter-free
  field, so "其中 N 格此刻走不过去" no longer counts tiles that are only blocked by
  a passing chicken.

## [0.5.74] — 2026-11-01

> Ships in the build labelled **0.5.75**: the version moved on while the offline
> verification below was running.

### Performance
Third batch of the big-map frame-drop work (0.5.66 fixed the walk start, 0.5.71
the render path, the per-tile distance field and the after-swing rescan). These
are the last whole-map costs that ran repeatedly. No route, order or on-screen
change intended, and the ordering claims below are backed by offline replays
rather than assumed.

- **The animal queue no longer floods the whole map for every stand tile it
  tries.** `FindApproachTiles` ranks the 8 spots around an animal by real walking
  distance, so it built a walk field over the entire map - and one
  `TryStartWalking` can call it up to 17 times in a single tick (8 `TryStart`
  candidates, then 8 `TryStartThroughCritters`, and the chase replan re-arms
  every 15 ticks). On a 200x150 farm each flood is ~30,000 BFS cells plus a
  120 KB `int[]`, over the 85,000-byte Large Object Heap threshold, so the animal
  queue was the biggest source of the hitches that no stage timer can attribute.
  The field is now cropped to farmer + animal (+2 for the stand spots) with the
  shared margin rule below. Because "unreachable" here means "fenced off - give
  up on this animal", a spot that reads unreachable in *both* the walk field and
  the terrain-only field drops the crop and refloods the whole map once, so a
  detour that leaves the box can never be mistaken for a fence. Maps over 65,536
  tiles get no field at all, as before, and keep the straight-line fallback.
- **Chopping / mining / scythe runs no longer reflood the whole map per
  re-queued tile.** `BuildPathWorkOrder` - which runs at the start of every axe,
  pickaxe, debris and machine run, after every swing in `RequeueMissedCovers`,
  and per segment in `BuildSegmentedPlacementOrder` - floods the entire map once
  per work tile to pick the nearest one: O(N x map area). The flood is now
  confined to the bounding box of every candidate stand position plus the
  farmer. Cost model (`.research/sim_workorder_cost.py`, cells flooded per call):
  on a 200x150 map a 20x15 box goes 4,500,000 -> 257,400 cells (**17.5x**), a
  40x26 box 18,720,000 -> 3,294,720 (**5.7x**), an 80x60 box **2.2x**, and a box
  that already covers the map is unchanged - the worst case is parity, never
  worse. On a 256x256 map the same boxes are 38x / 12x / 4.7x.
  *Order equivalence was measured, not assumed.* Two offline ports of the greedy
  pick replay bounded vs unbounded: `.research/sim_workorder_bounds.py` (24
  terrain/size/density combinations) and the more faithful
  `.research/sim_workorder.py` (88 scenarios x 8 margins = 704 runs; it also
  models the two cursors - the BFS origin moves to the chosen **service
  position** while the tie-break cursor moves to the chosen **work tile** - the
  "within 2 of the player" tie-break chain, the first-minimum-wins scan order
  that decides *which* service position becomes the next origin, and
  `sealPlacedTiles`). Result (`workorder-out.txt`): order differences 9/72 at
  margin 0, 5/72 at 2, 2/72 at 4, **0/72 at margins 6, 8, 12 and 16**, plus 0/8
  in the seal subset. A percentage-only margin (25% of the box) diverges on a
  thin five-row box, which is why the shipped rule keeps a fixed floor. Every
  observed divergence was a *distance inflated because the shortest path left the
  region* (all of them on fence-gate maps) or, subtler, the same work tile winning
  with a different argmin service position, which silently moves the next BFS
  origin. None was caused by plain unreachability. The shipped margin (12-24)
  sits above the clean threshold with room to spare.
  Fallback, and why it is not simply "some tile came back unreachable":
  unreachable work tiles are *normal* here - the comment above the function
  documents inner targets that cannot be reached until the outer ring is cleared
  - and a fallback that fired on those would reflood the whole map every round,
  which the replay measured at 0.6-1.0x, i.e. slower than not cropping at all. So
  the crop is paid for once up front: one unbounded flood records which service
  positions are reachable at all (a connected-component pass - 1 extra flood
  against N), and a tile triggers the unbounded redo only when it is unreachable
  in the crop *and* reachable in that pass. The same idea guards the per-tile
  distance field: a tile read as unreachable only forces the wider rebuild when
  its straight-line distance is already better than the winner's walking
  distance, which it cannot beat (walking distance >= Manhattan), so genuinely
  pocketed tiles no longer cost a double flood on every pick.
- **All three crops now share one margin rule: `max(12, min(24, span / 2))`.**
  The per-tile distance field (`LiveSlotBounds`) was the tightest at 6-ish tiles,
  and since 0.5.73 that field is no longer only used to pick the nearest tile -
  the scythe's nearest-swing pick and the "take the work at my feet" rule read it
  too, so an overestimated distance there changes decisions, not just
  diagnostics. 12 is the floor the replays came back clean at (they diverge at 4
  on fence-gate maps); 24 is the cap so a large selection still gets a real
  saving instead of cropping to the whole map.
- **The scythe's "is the destination a better stand than where I am" comparison
  is recomputed only when the farmer changes tile.** It ran every tick while
  walking, and each side samples 4 facings x 6 animation frames with a linear
  scan of the queue per sample. Both inputs (the tile underfoot, the walk
  destination) are constant between steps, so the verdict is cached per tile and
  dropped in `ResetWalkClocks`: it can only be stale for the rest of one walk,
  and it only decides whether the farmer stops one tile early to swing.

## [0.5.73] — 2026-11-01

### Queue
- **"Nearest" now means walking distance everywhere a nearest tile is picked,
  not straight-line, Chebyshev or Manhattan.** A tile two steps away across a
  grove can be forty steps away on foot; counting it as "near" is what sent the
  farmer on the long loop around the trees (`不是按照距离最近来了？` /
  `绕超级远的路`). The scythe's nearest-pick (`PickNextScytheTile`) measured
  Manhattan steps and the hoe/watering-can "take the work at my feet instead of
  walking back" rule measured Chebyshev tiles; both now read the same cropped
  walking-distance field the axe, pickaxe and mixed debris runs already use.
  A tile the farmer cannot walk to at all no longer counts as near for any of
  them - it waits until whatever blocks it is cleared, or, when the blocker is
  not in the queue at all (a hedge of trees outside the box, water, buildings),
  until the run goes around. The scythe pick's orphan discount, heading inertia
  and coverage tie-breaks keep working on top of the walking distance, and a
  missing/invalid field still falls back to Manhattan rather than guessing
  nothing.
- **Long walks are explained instead of silent.** When the next tile costs 20+
  walking steps the walk trace gets a `LONG-WALK` line
  (`next=(53,25) walk=41 straight=4 unreachableNow=12`) and the debug log says
  how many pending tiles are currently unwalkable; closing a box logs the same
  count up front (`⚠ 其中 N 格此刻走不过去，要等外圈清完或绕外圈`). A detour around a
  hedge that is *not* part of the selection no longer looks like the
  nearest-pick misfiring - the log says it was the only way through.

## [0.5.72] — 2026-11-01

### Auto-pause
- **Running out of stamina now auto-pauses like a normal finish: the backpack
  opens (pausing time in single player) instead of only flashing the HUD line.**
  `OpenBackpackAfterTask` deliberately fired only on "finished everything on its
  own" - its doc comment listed 体力不支 among the interruptions that must not
  pop the menu. But unlike walking away or changing maps, an out-of-stamina stop
  is a *planned* end of the run, and the whole point of the open-backpack pause
  is "the queue is done, the game should not keep running while I am not
  looking". All three stamina stops now open the menu after stopping: the
  `Stamina <= StopBelowStamina` threshold in `TillQueue.OutOfStamina`, the
  `Stamina <= 1` tool hard floor, and the animal queue's milking/shearing gate.
  Debris mixed-tool runs still tear the chain down as interrupted (tools return
  to the original slot either way); a new `LastStopWasStamina` flag, reset by
  every `Stop`/`Start`, lets the chain finalizer open the menu for them too.
  Still gated by the `OpenBackpackOnFinish` config and the usual guards (no
  menu/event already up, game window focused, no queue still running).

## [0.5.71] — 2026-11-01

### Performance
Second half of the big-map frame-drop work started in 0.5.66 (that entry has the
diagnosis and the measurements). None of these change a route, a queue order or
anything drawn on screen inside the viewport.

- **The distance field is flooded over the job's own box, not the whole map.**
  Picking "the nearest tile that still has work" builds a walking-distance field
  every time a tile is taken, and `FloodDistances` allocated `new int[map tiles]`
  to do it: on a 200x150 farm that is a 120 KB allocation per call - over the
  85,000-byte Large Object Heap threshold, so every tile taken pushed the LOH and
  the gen2 collections that come with it (the "stutters for no reason" that no
  stage timer can attribute), plus a 30,000-cell BFS. The flood is now confined
  to the bounding box of the farmer and the pending tiles, with a margin of at
  least 6 tiles (a 40x26 box floods ~2,350 cells into a 9 KB gen0 array), and
  `BuildDistanceField` returns a cropped field that knows its own origin, so
  callers read it through `field.Serve(tile)` instead of indexing a map-sized
  array. Cropping can cut a route that only connects by going around the box (a
  lake, a fence line), which would read as "unreachable" - so if any workable
  tile comes back unreachable the field is rebuilt unbounded once and the pick
  redone. Being genuinely unreachable still costs that second pass; being cut off
  by the crop no longer silently drops the tile.
- **The after-swing rescan is O(selection) instead of O(selection x queue).**
  `RequeueMissedCovers` ran `Tiles.Contains(key)` - a linear scan of a `List` -
  for every tile in the selection, after every swing: an 800-tile box against an
  800-tile queue is ~640,000 comparisons per swing. It now takes one
  `HashSet<Vector2>` snapshot of the queue next to the loop. Deliberately *not* a
  persistent mirror of `Tiles`: the list has 15 mutation sites and legitimately
  holds duplicates (a passed tile is re-inserted for a retry), so a mirror that
  missed one update would silently lose or double-queue tiles - the exact class
  of bug this mod spent 50 versions fighting. A snapshot local to one method
  cannot desync.
- **The HUD's "remaining" counter is computed at most once every 4 ticks.**
  `Remaining` is O(tiles left) with three `HashSet` and one `Dictionary` lookup
  per tile, and `OnRenderedHud` read it every frame: ~3,200 lookups per frame for
  an 800-tile queue, forever. 0.3.1 kept this counter O(1). It is now cached for
  4 ticks (67 ms - invisible on a counter, and the value is only ever displayed
  or logged, never used to decide anything), invalidated on the click-to-edit
  paths so toggling a tile still updates the number immediately.
- **Grid markers and task bubbles outside the viewport are no longer drawn.**
  Both loops drew every queued tile whether it was on screen or not, which is
  what made a big selection box cost per frame even while looking at one corner
  of it (measured worst frame: 1.20 ms for the grid, 1.21 ms for the bubbles at
  ~800 tiles; a 20,000-tile box would be ~1-2 ms per frame of pure off-screen
  draws). Both now cull against the screen rect with a 128 px margin. The night
  glow capture is left alone on purpose: trees and bushes draw well above their
  own tile, so culling them by tile position would clip glows at the screen edge.
- **A throwing `draw` can no longer leave the tint stuck on.** The 11 entity glow
  patches set `WorkHighlight.Tinting` in a prefix and cleared it in a postfix; if
  the patched `draw` threw, the postfix never ran and the flag stayed set, so
  every subsequent `SpriteBatch.Draw` in the game took the tint branch
  (`isDarkOut` + `Color.Lerp`) and tinted sprites that were not queued at all.
  All 11 are finalizers now, which Harmony runs whether the original returned or
  threw, and they return `__exception` unchanged so a draw exception still
  propagates exactly as before. (`docs/未解决问题总结-2026-10-26.md:378` had this
  on the list since 0.4.9.)
- **Diagnostics: the probe's two `UpdateFrom` overloads get separate buckets.**
  `PerfProbe` keyed its timing buckets by method name only, so
  `WorkHighlight.UpdateFrom(TillQueue)` (throttled to every 10 ticks) and
  `UpdateFrom(AnimalQueue)` (every tick, no throttle) were summed into one number
  and neither could be judged. Overloaded names are now bucketed as
  `UpdateFrom(TillQueue)` / `UpdateFrom(AnimalQueue)`; every other bucket keeps
  its old name so existing `perf.log` files stay comparable. Five buckets were
  added in 0.5.66 for the walk path (`GetStandableMap`, `GetTerrainMap`,
  `GetWalkableMap`, `BuildBlockedGrid`, `Simplify`).

## [0.5.70] — 2026-11-01

### Compatibility
- **Standability now asks the game whether the farmer can walk through a tile's
  terrain feature, so "pass through crops" mods work.** The planning side
  (`IsStandable`, `IsStandableIgnoringCritters`, `IsWalkableThroughAnimals`)
  judged tiles via `IsTileBlockedBy`, which calls the parameterless
  `isPassable()` — effectively `c = null`. Passable Crops
  (`NCarigon.PassableCrops`) grants crop passability in a postfix on
  `isPassable(Character)` that only fires for a farmer, so the planning side
  never saw it and kept treating every trellis-crop tile as a wall: in a solid
  block of adjacent hops the interior tiles had no standable neighbor,
  `FindApproachTiles` came back empty, and the middle of the field was silently
  skipped (`中间的识别不到浇水`). The walk side already passed the real farmer
  into `isCollidingPosition`, so the two layers disagreed. The three checks now
  probe `isPassable(Game1.player)` — the same call real movement makes — and
  drop the TerrainFeatures bit from the occupancy mask when it says passable,
  keeping everything else on the tile (machines, characters, furniture,
  buildings) blocking. Without such a mod no vanilla `isPassable` overload
  reads the character argument, so behavior is unchanged.

## [0.5.69] — 2026-11-01

### Scythe
- **Leftover crops are mopped up right after the band sweep, not at the end of
  the run.** The scythe arc is irregular - a swing clears 3-4 columns x 5 rows
  but routinely misses exactly the top row of the band it is sweeping - so each
  band left one tile per swing step behind. Those scraps sat in the queue at
  their original band position, i.e. already passed, and only resurfaced in the
  end-of-run retry as a sparse row of bubbles across the top of the field
  (`每次收完上面都会漏一个 这样不就又要白跑一趟`). The moment the queue head
  leaves the crop segment, every crop tile that is still alive is collected and
  re-inserted at the cursor in nearest-first order, so the scraps are taken on
  the spot while the farmer is still in the field. Done tiles are not
  re-queued, so the remaining-count stays honest.

## [0.5.68] — 2026-11-01

### Scythe
- **The crop S sweep is ordered in 3-row bands, not in single rows.** 0.5.67's
  row-by-row S still jumped: one iridium-scythe swing clears 3-5 rows x 3-4
  columns (measured in the 0.5.67 log), so sweeping row by row queues the same
  ground three times - the second and third passes find only scraps, and a
  scrap sits a whole row away from where the swing that should have taken it
  just was. In game that read as "row 2 finished at the right edge, then the
  farmer flew back to the left for row 3" (`第二行到第三行怎么又从右回到左边去了`);
  the log shows it as swing (34,12) followed by swing (7,13). Crops are now
  grouped into bands 3 rows tall, each band swept as one serpentine pass in
  column order (its swings eat the band's own top and bottom rows), the next
  band entering where the previous one ended; scraps therefore stay inside the
  band that is being swept and get taken in place. Band height 3 matches the
  measured swing footprint.
- **Row re-sorting no longer touches a scythe crop run.** `OrderHeadRow`
  re-orders the head tile's row by the farmer's current X; it is meant for the
  hoe/can charge bands, and on a scythe band sweep it flipped the column
  direction every time the head crossed into a new row, tearing the serpentine
  apart. It is now skipped for the crop segment (mixed debris runs already
  skipped it).

## [0.5.67] — 2026-11-01

### Scythe
- **Crops sweep row by row in one S pass again; everything else keeps
  nearest-pick.** 0.5.65's orphan/inertia nearest-pick measured better offline
  but still looked like random walking in game on a crop field
  (`还是在乱走 不如S型了`), and what the player watches is the order, not the
  step count - so the 0.5.58 walking-distance verdict is set aside for crops.
  Dead crops, crops and flowers (whether this scythe can swing them or has to
  hand-pick them) now form the **first segment** of the queue, ordered by the
  watering-can row sweep: nearest selection edge first, one direction per row,
  entering each next row where the previous one ended. The nearest re-pick is
  **disabled for that segment** - it is what scattered the S - while weeds,
  grass, moss, forage, machine produce and fish ponds keep the 0.5.59 + 0.5.65
  nearest-pick as the second segment. Three guards keep the segments honest:
  missed-cover requeues and per-tile retries are re-sorted/inserted inside
  their own segment only, coverage counting ignores weeds while the crop
  segment runs (otherwise stand-seeking gets pulled off the S toward a weed
  clump), and the farmer's own tile is no longer pulled to the front of a
  crop run. Mixed debris runs (auto tool switching, 0.5.63) are untouched.

## [0.5.66] — 2026-11-01

> These changes ship in the build labelled **0.5.68**: the version number moved
> on (two scythe releases) while this was being measured and re-measured, so the
> dll that carries them is 0.5.68.

### Performance
- **Big-map farms no longer stutter while the queue walks.** A player on a large
  farm map reported frame drops that 0.3.1 does not have, worse where obstacles
  are (`用大地图农场的话，新版本会掉帧，0.3.1版本不卡，寻路方式变成每走一步都整图扫描吗，有障碍物时会掉帧的多些`).
  The guess was right: since 0.4.38-0.4.53 a walk start can rescan the **whole
  map** up to three times, and each rescan costs map area x the number of
  buildings / objects / clumps / animals on it, so both a bigger map and more
  obstacles make every step more expensive. Measured on this machine's standard
  farm (141x65 = 9,165 tiles) with the in-mod probe: `BuildWalkField` 5.77 ms
  per call, `FindPathAvoiding` 9.14 ms per call, walk starts 4.7-8.2 ms in the
  slowest frames; a 200x150 farm pays ~3.3x that per call. Three fixes, none of
  which change a route:
  - *The terrain passability grid is cached* (60 ticks, plus explicit
    invalidation). That grid is the expensive half of `BuildWalkField` and
    `GetWalkableMap`: one `IsTileBlockedBy` per tile, each of which walks
    buildings, resource clumps, large terrain features and furniture and reads
    map properties. Moving characters are why it was never cached, so they no
    longer go into it - solid characters are rasterised once per call
    (O(characters)) and overlaid on the cached terrain, which is exactly what
    the per-tile `IsWalkableThroughAnimals` computed before. An animal-queue
    tick that fans out into 17 walk starts used to scan the whole map 17 times;
    it now scans it at most once per 60 ticks.
  - *Route compression reads an array instead of asking the game per tile - but
    only once asking has proved expensive.* `Simplify`/`ClearStraight` probe
    O(route²) tiles - many more once obstacles make the inner loop walk back from
    the far end - and since 0.4.41 every probe was
    `isCollidingPosition(pathfinding: false)`, the variant that also loops every
    NPC on top of animals, buildings, clumps, furniture and large terrain
    features. Offline replay of both compressors over four terrains
    (`.research/sim_walkcost.py`): on a 200x150 cluttered map one walk start made
    43,197 such calls where 0.3.1 made 292 cheaper ones. The same verdicts can
    come from a `bool[]`, and the rule is identical: a tile is blocked iff a
    character occupies it or the terrain is not standable, because
    `IsWalkableThroughAnimals` is "terrain standable and no solid character" and
    every solid character tile is already in the character table. With `DebugLog`
    on, every walk start re-asks the game for the route's own tiles and warns
    (`⚠ 阻挡图与逐格判定不一致 N 格…`) if the two ever disagree, dropping the grid
    for that walk.
    Two measurements shaped *when* the grid gets built. The first cut built it
    over the whole map on every walk start: a probe-measured session (288 walk
    starts, median route 2 tiles, longest 35, 482 arrivals for 487 walks, no
    stalls and no failed walks - so the verdicts were right) showed `TryStart`
    going **1.26 -> 5.07 ms per call**, because a whole-map grid *is* a
    whole-map scan and short walks are the common case. Bounding the grid to the
    route's box fixed that, but then a long open-field transfer (route 323 tiles,
    box = the whole 200x150 map) still paid a ~19 ms grid to save 0.8 ms of
    queries. So the grid is now built lazily, behind a probe budget proportional
    to what the grid itself costs (one per-tile query is worth about four grid
    cells, floor 256 probes): short routes and open ground never build one,
    obstacle-heavy routes switch to array reads once the queries have paid for
    it, and a warm terrain cache drops the budget to the floor. Same replay, per
    walk start, costed with the measured per-call numbers:
      - 200x150 open, route 323 tiles: 21.1 ms -> **1.6 ms**
      - 200x150 cluttered, route 321: 127.5 ms -> **20.6 ms** (1.7 ms warm)
      - 141x65 fenced, route 201: 10.9 ms -> **4.9 ms**
      - route 4 tiles, any terrain (the common case): 0.01 ms -> **0.01 ms**
    A box over 4,096 tiles uses the cached whole-map terrain grid instead of a
    fresh local one. Maps over 65,536 tiles get a bounded local grid too - there
    `TileUtil` returns no whole-map grid at all, so before this they had no grid
    *and* silently lost the animal push-through and avoiding routes.
  - *The "push through the flock" detour only builds its field when an animal is
    actually near the route.* Vanilla pathfinding ignores NPCs
    (`pathfinding: true`), so the only thing its route can be detouring around
    is a farm animal; with none within 3 tiles of the route the extra
    whole-map field can only return the same answer, yet on an obstacle-heavy
    map the `route length > Chebyshev + 2` trigger made it run on almost every
    walk start.
- **Clearing an obstacle no longer throws the whole standable cache away.**
  Axe/pickaxe runs invalidate it after every verified clear so the next target
  is picked against the new terrain - which made the next distance field pay a
  full whole-map rescan *per obstacle cleared*, with the most expensive of the
  three predicates (mask 251 also loops every animal and character per tile).
  The invalidation is now local: the ±4 tiles around the cleared tile are
  recomputed in both cached grids. Reachability is unaffected, because the BFS
  field is rebuilt from the grid on every call anyway.
- **`BuildPathWorkOrder` uses the cached standable map** instead of its own
  private whole-map scan (it clones the cached array, since it marks the
  farmer's own tile standable). That runs after every swing in scythe, axe and
  pickaxe runs.
- **Placing something invalidates the terrain cache.** A sprinkler, sapling or
  pot that just went down changes what is walkable; without this the cached grid
  would keep treating it as air for up to a second and route through it.

## [0.5.65] — 2026-11-01

### Scythe
- **The nearest-swing picker now eats scraps on the way and keeps its heading.**
  Two corrections on top of 0.5.59's Manhattan nearest-pick
  (`那就继续优化就近挑`), both measured offline before touching the game. The
  measuring harness is new: `.research/sim_scythe_current.py`, whose V0
  reproduces `PickNextScytheTile` line for line. The two older sims
  (`sim_scythe_pick.py`, `sim_scythe_band.py`) still picked by **Chebyshev**,
  i.e. they modelled the pre-0.5.59 algorithm - every "current" number they
  printed was a comparison against code that no longer exists, so they were
  left alone and superseded.
  - *Orphan discount*: a tile within 4 steps that one swing can cover at most
    3 of counts one step nearer. Such scraps lose every equal-distance tie to
    a bigger swing today and get postponed into an end-of-run crossing (the
    same pocket-postponing 0.5.58's log showed); the discount makes passing
    them pick them up.
  - *Heading inertia*: effective distance also drops by half the alignment
    between the candidate direction and the farmer's movement since the last
    swing, which stops the stand saw-toothing between two equally-far crop
    rows.
  Over five layouts (two solid fields, a checkerboard field, a walled animal
  pen, a walled greenhouse - the walled ones are new, the old sims were all
  open ground and could not see the through-wall distance problem) total
  walking 853 -> 803 (-5.9%), crossings of 8+ tiles 19 -> 12, end-of-run
  walking 168 -> 136, no per-layout regression (pen 119 -> 94 with its
  end-of-run crossing gone entirely).
- **The S-shaped crop row sweep was re-considered and rejected again.** It is
  what 0.5.53/0.5.54 shipped and 0.5.58 reverted with measured data; a fresh
  calibrated replay of band/row sweeping against the current picker still puts
  it 10-25% above nearest-pick on walking (`E h4k5` 1680 vs `A0` 1528 over nine
  layouts), buying only fewer big jumps. Kept as-is per that evidence
  (`肯定要S型啊` was the request; the numbers above are why it did not ship).

## [0.5.64] — 2026-11-01

### Loading machines
- **Finished machines are no longer queued as load targets when your bag can't
  take their output.** Loading a finished machine means "collect the product,
  then drop the new input"; with a full inventory neither half can happen, yet
  the old planner still queued those machines, so the farmer walked the whole
  ready column (typically the oldest kegs - one entire side of the field),
  bounced off every one (`why=machine`), shoved them to the back of the queue
  and never came back before the run ended: "he passed the left side and did
  nothing" (`他过来的时候左边一点都没收`). Ready machines whose output has no
  bag room are now skipped at planning time with a chat message saying how
  many and why (`背包放不下机器产物…`), and if a machine finishes mid-run into
  a full bag the tile is skipped cleanly instead of failing twice. Walktrace
  now also records the machine's state (ready/held/minutes) on the first
  failed load, so the next report of this kind is self-explanatory.

## [0.5.63] — 2026-11-01

### Queue
- **A debris run works one route and switches tools per tile, instead of one
  tool per pass.** 0.5.61 cleared the box tool by tool: every weed in the box
  first, then every tree, then every stone - so the farmer swept the same ground
  three times and the tool only changed between sweeps
  (`工作时工具不是自动切换的 而是先镰刀 镰刀结束然后是斧头 斧头结束然后是稿子`). The box is now
  split per tile - weeds to the scythe, trees/twigs/stumps to the axe,
  stones/boulders/clumps to the pickaxe - and the queue always takes the tile
  nearest by real walking steps whatever tool it belongs to, switching on the
  way to it: weeds in front → scythe, the nearest twig → axe, the next stone →
  pickaxe (`根据斧头和镐头的路径 进行工作`). One route, one pass, no crossings.
  The tool you held when the box was closed still does all of its own work (a
  scythe also harvests crops, forage and machine produce); the other two only
  clear debris. Weed priority is unchanged - scythe > axe > pickaxe, the first
  of the three whose "clear weeds" toggle is on *and* that you actually carry -
  so with all three toggles on the scythe still sweeps the weeds and the
  axe/pickaxe passes never touch them. Being interrupted (you walk, switch tools
  yourself, leave the map, run out of stamina) still ends the whole run and puts
  your tool back. A box one tool can finish alone is not taken over at all, so
  single-tool runs behave exactly as before.
- **"Does this tile still have work" is now asked of the tile's own tool.** The
  queue asked the tool in hand, so while the scythe was up, a queued stone read
  as "no work here" and was silently booked as finished - the stale-tile sweep,
  the splash bookkeeping and the green markers all shared that mistake.
- **Box-selecting paints a tile green when any tool you actually carry can
  handle it** (`框选时也只有手里真正拿着的工具能处理的才显示绿色`). The marker only ever
  asked the held tool, so with a scythe in hand every tree and stone in the box
  was red even though the run was about to switch to the axe and pickaxe and
  clear them - the markers and the run disagreed. Green now means "this tile
  will be worked", decided by the same per-tile assignment the run uses; a tool
  you do not carry counts as absent, so tiles only it could handle stay red.
- **A box whose work all belongs to another tool you carry is worked instead of
  being reported as empty.** Holding an axe with "axe clears weeds" off over a
  box of weeds and stones used to stop with `该区域无法工作` while the markers said
  green; it now switches to the pickaxe and clears them.
- The auto-switch HUD message showed the literal `{tools}` instead of the tool
  names (the translation token was written with single braces; SMAPI wants
  `{{tools}}`).
- **The scythe keeps its "widest swing" pick inside a mixed run.** One route
  ordered purely by distance would have cost the scythe the one thing that makes
  it cheap - a swing clears 3x3, so among equally near weeds it should take the
  one that drops the most (`镰刀清理杂物时的路径和稿子斧头是一样的吗`). Tiles up to one
  walking step farther than the nearest are now compared by coverage and the
  widest wins; farther tiles never compete, so a weed across the field still
  cannot pull the farmer off the twig or stone at his feet. Coverage is measured
  after the tool switch, since the swing sample and the hand-pick rule both read
  the tool in hand. Single-tool runs keep `PickNextScytheTile` exactly as before.
- Diagnostics: one log line per switch
  (`自动换工具 → 斧头（槽位 3），为了 (41,18)；本趟已换 4 次`), `MIX-NEAR` in the walk
  trace when the nearest tile is pulled to the front, `镰刀覆盖优先：(x,y) 换成
  (x,y)（同样近，走 N 步，一刀覆盖 M 格）` when the widest swing wins, and the
  end-of-run line says `杂物混合跑：沿途换工具 N 次`.

## [0.5.62] — 2026-11-01

### Loading machines
- **The 0.5.60 nearest-first loading fix now actually takes effect.** That fix
  added the dedicated machine-loading order branch, but the earlier
  hoe/watering-can branch (row-segment S sweep) still matched machine-load
  items first, so the new branch was dead code and loading kept sweeping the
  machine field row by row (`机器装填还是一行一行`). Machine-load items - and
  seed runs where every target is a machine - no longer match the row-sweep
  branch; loading is once again planned by real walking distance, nearest
  first, continuing next to the tile just filled.

## [0.5.61] — 2026-11-01

### Queue
- **Debris runs switch tools on their own.** Box-selecting while holding a
  scythe, axe or pickaxe no longer limits the run to what that one tool can
  clear: debris the held tool cannot handle (trees, weeds, stones, twigs) is
  cleared too, one tool per pass, switching back to the tool you held when the
  chain ends (`框选时能自动切换工具`). Which tool clears what still follows the
  General toggles; a pass is skipped when the box holds nothing for it, and an
  interrupted run (you walk, switch tools, leave) ends the chain instead of
  continuing it. Weeds obey the priority scythe > axe > pickaxe: the first of
  the three whose "clear weeds" toggle is on - and that you actually carry -
  does the weeds, so with all three toggles on the scythe sweeps them and the
  axe/pickaxe passes never touch them. New toggle in **General** (on by
  default); off behaves exactly like before.

## [0.5.60] — 2026-11-01

### Loading machines
- **Filling machines follows the same order as harvesting their produce with the
  scythe.** The loading queue was planned as row-segment S sweeps, which cut a
  keg field into one connected block per horizontal band - the rows of kegs
  already running separate the empty rows - plus a side column block, so the
  farmer swept one band, jumped to the column, then crossed back for the next
  band (`机器装填路径奇怪`). Loading now uses the scythe's machine-produce order:
  nearest by real walking steps, continuing next to the tile just filled, until
  each neighbourhood is empty.

## [0.5.59] — 2026-11-01

### Scythe
- **The next swing is picked by real walking steps, not by tile radius**
  (`依镰刀收割会乱跑`). The nearest-first picker measured distance in Chebyshev
  tiles, which counts a diagonal as one step - but the farmer walks in four
  directions, so a diagonal costs two. The distorted metric made diagonal
  targets look "nearer" than straight ones the same steps away, so leftover
  pockets beside the farmer kept losing to bigger swings elsewhere and were
  postponed into one field-length crossing at the end (iridium-scythe test:
  (3,18)-(3,20) skipped eight picks in a row while the farmer walked off; the
  last swing crossed 19 tiles back to a corner). Equal-Chebyshev candidates
  also let the coverage tiebreaker bounce the farmer between two crop rows
  (stand y = 12,15,12,15...). With Manhattan steps, candidates the same steps
  away are settled by "continue next to the last swing": pockets are finished
  on the spot and each row is swept before the next. Offline replay over nine
  field layouts: total walking -19%, direction reversals -46%, end-of-run
  crossings -33%.

## [0.5.58] — 2026-11-01

### Scythe
- **Harvesting picks the nearest next swing instead of sweeping rows.** Row
  sweeping cannot work for a scythe: one swing clears 3x3, so by the time the
  farmer reaches a row only scattered tiles are left, and each stray tile costs
  a 30-tile crossing mid-field. Offline sweep over five planting layouts
  (40x26, four starting positions), total walking per run - by row
  957 / 958 / 526 / 973 / 970 tiles, by nearest 561 / 512 / 484 / 458 / 255 -
  and crossings of 8+ tiles drop from 12-35 per run to 1-4. The long walk that
  remains is the last couple of tiles at the end of a field, not a mid-field
  detour.

## [0.5.57] — 2026-11-01

### General
- **Auto-open backpack when a task ends.** New toggle in **General** (on by
  default). When a queue finishes its own work it opens the inventory - in
  single player that pauses time, so nothing runs away while you are away
  (`任务结束自动打开背包`). It does not open when the run is interrupted or
  stops for lack of seeds, items or stamina, and never over an event,
  dialogue or a menu already on screen.

## [0.5.56] — 2026-11-01

### Scythe
- **Each row starts from the end nearest the farmer.** The row order was fixed
  when the queue was built, using the row's full extent - but one swing clears
  several tiles, so by the time the farmer reached a row only scattered tiles
  were left and the planned entry end could be 30 tiles away (`还是乱跑`).
  Entering a row now re-reads the tiles actually left in it and starts from the
  end nearest the farmer.

## [0.5.55] — 2026-11-01

### Queue
- **A running tool animation no longer makes the queue skip tiles.** The walk
  guard reported `挥动动画未结束` as a start failure, so three attempts inside one
  animation skipped the tile (`09:11:55` skipped (29,29)/(31,30)/(30,30) in a
  row). The queue now waits while the game is playing any tool animation, and
  that reason no longer counts as a failed attempt.
- **A detached walk driver is re-planned immediately instead of waiting four
  seconds.** At the pond edge the simulated-input driver could finish or detach
  while the head still needed walking; the queue then sat for 240 ticks and
  skipped the tile. It now re-plans from the current position (up to three
  times per tile) before falling back to the old retry/skip.

## [0.5.54] — 2026-11-01

### Scythe
- **Crop harvesting sweeps the whole selection as one row pass.** Crops are
  often a few per row, so splitting them into 8-connected patches made every
  small patch pick its own starting edge - the run looked like short
  disconnected hops instead of one S (`不是S型`). Crops now form a single row
  sweep: nearest selection edge first, one row in a single direction, and the
  next row enters from the end the previous row finished at, even across empty
  rows.

## [0.5.53] — 2026-10-31

### Scythe
- **Only the crop harvest uses the row sweep.** The row-by-row sweep applies to
  what is growing in the ground - harvesting crops, flowers and dead crops.
  Machine produce, fish ponds, weeds, grass, forage and moss keep their old
  order: nearest first, and machine produce also prefers the tile next to the
  previous swing the way 0.3.1 did.

## [0.5.52] — 2026-10-31

### Axe and pickaxe
- **Chopping and mining no longer swing from a diagonal tile.** With the
  cursor hidden the game takes the tool target from the farmer's facing
  direction, so a diagonally adjacent swing hit the neighbouring tile instead
  of the tree or rock - reported as "sometimes stuck swinging at nothing"
  while chopping and mining. Axe and pickaxe now walk to an orthogonally
  adjacent tile first (a diagonal is still allowed while the mouse is visible,
  since the click is honoured then).

## [0.5.51] — 2026-10-31

### Hoe and watering can
- **The along-row band can shift sideways so it fits the selection edge.** The
  band's near edge sat exactly on the head column, so a 13-wide field swept by
  three-wide bands left the last column of every row (e.g. x=27) to be fetched
  one by one later - the queue appeared to skip a few columns and come back
  (`正锄的好好的 突然跳过了三四列`). The along-row anchor may now shift up to two
  columns, so the last band reaches the edge column and the row finishes in one
  pass.

## [0.5.50] — 2026-10-31

### Hoe and watering can
- **The along-row band is used whenever it covers more pending tiles**, not only
  at the selection edge. With a two-row strip the along-row 3x6 band covers
  12 tiles per swing versus 10 for the up/down 5x5, yet the planner still kept
  the up/down band because of the 1.5x rule (`为什么还是这样 不是朝左右蓄力呢`).
  The along-row orientation now wins whenever its pending coverage is strictly
  greater; a vertical band that covers more (e.g. a three-row strip) still wins.

## [0.5.49] — 2026-10-31

### Watering can
- **Refilling no longer re-orders the job.** After the can was refilled at a
  water source the next take was chosen from the refill position: the near-work
  rule picked a tile beside the pond and the row was re-sorted from that side,
  so the route looked scrambled (`接完水之后路线就全乱了`). The first take after
  a refill now follows the existing queue order - no near-work swap and no row
  re-sort - so the sweep resumes where it stopped.

## [0.5.48] — 2026-10-31

### Hoe and watering can
- **A thin strip at the top or bottom edge is swept along the row.** With only
  two pending rows left at the selection edge, the up/down band still laid three
  already-finished rows into every swing (10 tiles per swing); the along-row 3x6
  band covers twelve of the same two rows. At the top/bottom edge the along-row
  orientation is now allowed whenever it covers at least as many tiles
  (`facing=1/3`, `power=4`).

## [0.5.47] — 2026-10-31

### Hoe and watering can
- **A new job starts at the selection edge nearest the farmer and sweeps one S
  pass to the opposite edge.** The sweep used to start on the farmer's own row
  and work outward to both sides, which left a notch around him and forced a
  second fold (`从脚下开始，玩家周围先挖出一块缺口`). The start row is now the
  closer of the selection's top/bottom edges, entered from the nearer end.
- **The near-work rule is held back for the very first tile**, so it cannot pull
  the queue back to the farmer's feet at the start of a job.

## [0.5.46] — 2026-10-31

### Hoe and watering can
- **Nearby work is taken before walking back for a small leftover.** After a row
  was swept, a three-tile strip at the row's edge could become the head while
  the farmer stood at the opposite end, and the queue walked the whole field
  back (`从 (31,23) 走 29 格回去补 (5,21)`). When the next pending tile is at
  least eight tiles away and another pending tile is at least three tiles
  closer, the nearer tile is moved to the head - the S sweep keeps going
  instead of turning back, and the small strip is picked up when the queue is
  next to it.

## [0.5.45] — 2026-10-31

### Hoe and watering can
- **An interrupted charge no longer ends the swing at whatever power happened
  to be reached.** `PauseForSingleAnimation` is only the game's animation flag;
  the mod charges by time, so a true value must not end the charge. In the field
  this turned a planned 5-charge swing into a one-step swing, missed the target
  (`⚠ 挥了锄头之后 (14, 26) 没干成`) and made the queue re-plan and walk again.
  Charging now only stops when the game really clears the tool state; that tile
  is put back at the head of the queue for an immediate retry (three times,
  then it is left to the normal rescan).

## [0.5.44] — 2026-10-31

### Hoe and watering can
- **A corner patch at the top or bottom edge of the selection is covered in
  one charge.** The charged area is three tiles high and extends away from the
  farmer, so with the anchor on the head row it either pokes outside the
  selection (the spill rule cancels the whole swing) or covers only the head
  row. At a selection edge the anchor may now move one row inward, which puts
  the same three-row band over both the head row and the edge row - a 2x3
  corner patch that used to take one swing for the lower row and a separate
  trip for the upper row is cleared at once (`左上角只浇了三格`).

## [0.5.43] — 2026-10-31

### Hoe and watering can
- **The planned stand must be a tile the farmer can actually stand on.** At the
  pond edge the planner picked a stand that was water (`stand=(27,28)`), while
  the whole-field picker already rejected such stands. The walk could never
  arrive, so the queue waited 240 ticks and skipped the tile - three times in a
  row along the water (`跳过 (28,27)：走不到位，怕挥错格`). A candidate whose
  stand is blocked by terrain or a placed object is now dropped, and the same
  row is swept from the dry side instead.
- **The simulated-input walker is stopped as soon as the farmer arrives.** The
  finished route stayed attached and kept pressing direction keys during the
  charge and swing animation (heartbeat showed `CanMove=False UsingTool=True`
  while pressing a key).

## [0.5.35] — 2026-10-31

### Hoe and watering can
- **The smaller side of the field is swept first.** After the starting row the
  sweep always went downward and folded back upward at the end; when only one or
  two rows were left above the start, that fold meant crossing the whole field at
  the very end for those two rows. The side with fewer rows is now taken first,
  so the return trip happens while little is left on the other side.

## [0.5.34] — 2026-10-31

### Diagnostics
- **A frame that takes 100 ms or more in the tile queue is logged** as
  `⚠ 队列卡顿：这一帧 XXXms（目标 …）`, together with the current target,
  queue position and walk/charge/swing flags. The occasional "hangs for a
  moment" is a one-off heavy tick (pathfinding, distance field, row re-order)
  and there was no record of which one.

## [0.5.32] — 2026-10-31

### Hoe and watering can
- **The last tiles are taken by walking distance, not by row order.** With fewer
  than 24 tiles left the leftovers are scattered over several rows, and strict
  row order walked away from a tile two tiles to the side because it belonged to
  the next row (`身边有活却要走远：玩家 (19,31)｜身边 (21,31)｜要去 (12,30)`).
  In that tail the queue now takes the tile with the shortest real walking
  distance, the same rule the axe and pickaxe already use; row order still
  applies to the body of the field.
- The charge-downgrade diagnostic now also says whether the blocking tile is
  inside the selection box (`在框内=是/否`).

## [0.5.30] — 2026-10-31

### Hoe and watering can
- **A row is re-ordered once more when the farmer stands next to work but the
  queue head is far.** A row ordered while the farmer stood at its right end kept
  that order after the farmer swept to the left, so the next target was ten tiles
  away with work two tiles away (`身边有活却要走远：玩家 (4,12)｜身边 (3,12)｜
  要去 (10,12)`). If the head is more than three tiles away and a workable tile in
  the same row is within two, that row is re-ordered once more.
- **An adjacent tile is no longer dropped when the step to its stand fails.**
  With the cursor visible the click is honoured, so the swing goes ahead from
  where the farmer stands instead of skipping the tile (`跳过 (3,12)：走不到位`
  while standing diagonally next to it — the "stuck" moment).

## [0.5.29] — 2026-10-31

### Hoe and watering can
- An unused-value cleanup for the row re-order introduced in 0.5.27 (no
  behaviour change).

## [0.5.28] — 2026-10-31

### Hoe and watering can
- **The facing is locked for the whole row.** At the end of a row the picker
  compared "keep facing up" with "face down" again and flipped when down covered
  a tile or two more, so the last band of a row changed direction mid-row (live
  log: `(31,17) facing=0` then `(33,17) facing=2`). The first swing of a row
  chooses the facing; the rest of that row keeps it as long as it still covers
  new ground.

## [0.5.27] — 2026-10-31

### Hoe and watering can
- **A row is only re-ordered when one of its tiles is actually about to be
  worked.** The re-order ran on every pass through the take loop, including the
  passes that skip tiles which are already done, so several upcoming rows were
  stamped with the farmer's position from that one moment: a live log shows rows
  13, 12, 11, 10 and 9 all ordered to start at x=34 while the farmer stood at
  x=30, and every one of those rows then crossed the field to the right. Now the
  re-order only runs when the head tile itself needs work.

## [0.5.26] — 2026-10-31

### Hoe and watering can
- **Start at the farmer's row again, not at a corner.** 0.5.23 sent the farmer
  to the nearest corner first; standing in the middle of a large field that
  meant a fourteen-tile walk with work two tiles away (the diagnostic printed
  `身边有活却要走远：玩家 (19,17)｜身边 (17,17)｜要去 (5,9)`). The sweep starts
  at the farmer's row, takes the nearer end first, runs to the edge, then folds
  back for the other half — one direction per pass, rows still alternating and
  never split. Also adds the row-reorder diagnostic line
  (`行重排 row=… px=… 范围 … nearLeft=… 起点=…`).

## [0.5.24] — 2026-10-31

### Hoe and watering can
- **The run no longer starts with the tile under the farmer.** The queue moved
  the farmer's own tile to the front for every run, so hoeing/watering always
  opened with a small patch at the farmer's feet before walking off to the
  corner. With the sweep now starting at the nearest corner, that patch only
  adds a detour and a stray tilled spot; the rule is kept for seeds, saplings,
  fertilizer and placeables.

## [0.5.23] — 2026-10-31

### Hoe and watering can
- **The sweep starts at the corner nearest the farmer and runs to the opposite
  edge.** It used to start at the farmer's row, run downward and then fold back
  upward, and the starting row was cut in two at the farmer's column. Now the
  direction is decided once from the nearest corner (farmer in the upper half →
  rows top to bottom, lower half → bottom to top), the first row is entered from
  the nearer end, every later row continues from the end where the previous one
  finished, and no row is split. One pass over the field, no backtracking.

## [0.5.22] — 2026-10-31

### Hoe and watering can
- **Only the starting row is split at the farmer's column.** Every row was being
  cut in two at the farmer's position ("near half first, then the far half"), so
  each new row jumped back into the middle of the field instead of continuing
  from the end where the previous row stopped (a live log: row 17 swept 21→34,
  then back to 18→3, and row 18 repeated the same split). The split is used once
  now, on the first row; later rows enter from the nearer end and sweep straight
  through.

## [0.5.21] — 2026-10-31

### Hoe and watering can
- **The anchor may line up a band up to two tiles away.** With a reaching
  enchantment the full charge is a 5x5 square, which cannot be aligned with a
  one-tile shift: a 100x100 field needed 29% more swings and re-covered 24% of
  the ground. The shift range is now two tiles (still bounded, still must cover
  the queue head); the same field is back to 1.02x / 1%.

## [0.5.20] — 2026-10-31

### Hoe and watering can
- **A shifted aim must still cover the queue head.** On small plots the swing
  could fall back to a one-tile charge at the shifted aim and leave the head tile
  untouched, so it was never finished (an offline sweep reproduced it from 2x2
  upwards). The shift is rejected when the resulting band does not contain the
  head.

## [0.5.19] — 2026-10-31

### Diagnostics
- **Logs when there is work within two tiles while the farmer walks four or more
  away.** Line format: `⚠ 身边有活却要走远：玩家 (x,y)｜身边 (x,y)｜要去 (x,y)`.
  It distinguishes "the row order demands a long walk" from "a nearby tile was
  left behind".

## [0.5.18] — 2026-10-31

### Hoe and watering can
- **The "can I reach it" test measures to the aim tile, not the queue head.**
  0.5.17 lets a swing aim one tile off the head; the stand is built around the
  aim, but the reach test still measured to the head, so a farmer standing
  correctly on the planned stand was told it was four tiles away, walked, was
  told again, and paced between two tiles
  (`够不着 (17,22) player (13,22) distance 4` repeated). Reach is now measured
  to the aim.

## [0.5.17] — 2026-10-31

### Hoe and watering can
- **A charged swing can aim one row off the queue head, so the rows at the
  top/bottom edge of the box can use left/right swings too.** A left/right band
  is three rows tall, so at the box's edge it either stuck outside the selection
  (and the swing was cancelled) or that row fell back to a 3-tile up/down swing
  — half the coverage, twice the swings. The aim tile is now kept separate from
  the bookkeeping tile: it may shift one row inside the box (even onto an
  already-worked tile) while the queue, the verification and the attempt
  counters still use the head, and the walker goes to the planned stand first.

## [0.5.12] — 2026-10-31

### Hoe and watering can
- **A target two tiles away is walked to again instead of looping.** The stand
  check lost its distance test: with the cursor visible, any non-orthogonal
  target counted as "no walking needed", so a tile two away was treated as
  reached after one tick and the act step then refused it as out of reach. A
  live log repeated `够不着 (30,14) player (28,13)` a dozen times without the
  farmer moving. Anything farther than one tile now walks.
- **Along-row swings only when they are clearly better.** Re-enabling
  left/right for the last row enabled them everywhere, and the anchor zig-zagged
  again (16 → 22 → 29 → 31 → 31 → 24 → 21). Up/down bands are the default;
  left/right is used only when it covers at least 1.5x more (a one- or two-row
  strip), otherwise the comb direction is kept.

## [0.5.11] — 2026-10-31

### Hoe and watering can
- **The target is no longer dropped every time the farmer walks one step.**
  When the act step found the tile two tiles away it cleared the current target,
  walked one tile, and the next tick took the *next* tile from the queue — a
  debug log showed `够不着 (14,14) distance 2 → walk → 够不着 (13,14)` repeated,
  the farmer sliding along the row without swinging. The target is put back
  before re-walking, so the farmer keeps walking to the same tile until it is
  adjacent.
- **All four facings are considered again, each with its own alignment.** Up and
  down are 3-wide bands centred on the anchor, so the anchor shifts one tile to
  line the band up with the first remaining column; left and right are 6-long
  bands running out from the anchor, so the anchor is the queue head itself and
  nothing shifts (the old code shifted them on the wrong axis, which is why
  along-row swings overlapped). The facing with the most new coverage wins, and
  the previous facing is kept only on a tie — so a single remaining row is
  worked with left/right swings (6 tiles each) instead of 3-tile up/down ones.

## [0.5.10] — 2026-10-31

### Hoe and watering can
- **The row is re-ordered when the queue actually reaches it.** The
  "start this row at the end where the farmer stands" step ran once, when the
  queue head was still the first tile of an already-finished row (a charged
  band covers three to six rows at once, so the rows in between are done before
  the queue gets to them). It sorted that finished row and never looked again,
  so the first live row several rows below kept its start-up direction and the
  farmer crossed the field to its far end. Ordering now happens per tile inside
  the take loop, so it fires on the row that is really about to be worked.
- The anchor/facing choice moved with it: it is now computed for the tile that
  was just confirmed as work, instead of for a queue head that may already be
  finished.

## [0.5.9] — 2026-10-31

### Diagnostics
- **A tile that becomes "no work" after the farmer walks to it is now logged.**
  The run silently dropped it (the farmer walked over and moved on with nothing
  in the log to say why), which made a stalled run look like a pathing problem.
  The line names the tile, the tool and the farmer's position.

## [0.5.8] — 2026-10-31

### Hoe and watering can
- **A charged swing only faces up or down now.** Facing along the row gives a
  6-wide x 3-tall slab, and the anchor can only be aligned along the slab's
  width axis, so two consecutive along-row swings overlapped heavily (a live log
  had `(16,17) facing right` then `(15,19) facing right`, sharing five columns
  of one row) and a whole band was wasted on every pair. The band now always
  runs across the row and is aligned so it starts exactly at the first remaining
  column; the facing only flips when the rows on that side run out.

## [0.5.7] — 2026-10-31

### Hoe and watering can
- **Each row starts at the end where the farmer is standing.** The row order
  was fixed at start-up (strictly alternating), but one charged band covers
  three rows (six when the charge runs along the column), so the next row that
  still has work is not the next row in the plan: the farmer finished a row and
  then crossed the whole field to reach the planned entry end of a row three or
  six below. When the queue enters a row, the remaining tiles of that row are
  now re-ordered around the farmer's column — near half first, then the far
  half. Standing at one end (the usual case) is the plain serpentine
  continuation; standing mid-field (the first row) is the feet-first split.

## [0.5.6] — 2026-10-30

### Hoe and watering can
- **Charged bands tile edge to edge instead of overlapping one column.** The
  anchor was always the queue head, i.e. the leftmost remaining column of the
  row; a 3-wide charge centred there re-tilled the column the previous band had
  just done, so each swing added 2 new columns instead of 3 (3x6 + 2x6 + …, one
  whole band lost every three swings). The anchor may now shift one tile along
  the sweep so the band starts exactly at the first remaining column; the shift
  is at most one tile, so it cannot turn into the old tile-hopping.

## [0.5.5] — 2026-10-30

### Hoe and watering can
- **The charge facing no longer flips mid-row.** Each swing picked whichever of
  the four facings covered the most new tiles, so one swing into a row could
  turn sideways (a 6-wide x 3-tall slab that eats the rows above and below) the
  moment that covered a few tiles more than continuing upward — the farmer then
  looked like it was running left and right while the field filled in patches.
  The previous swing's facing is now kept as long as it is not clearly worse
  (at least two thirds of the best coverage), so a row is combed in one
  direction with a steady facing.

## [0.5.4] — 2026-10-30

### Hoe and watering can
- **The sweep starts under the farmer's feet.** The row order used to always
  start at the top of the box, so a farmer standing mid-field first walked all
  the way up. Now the row the farmer stands on is swept first: from the
  farmer's column to the nearer end, then back across to the far end. Rows then
  continue downward, each row entering at the end the previous row finished
  (the charged swing's 3-wide band advances along the row, so it is not
  tile-by-tile); after the bottom row the sweep folds back upward from the row
  above the start row. Standing outside the box still enters at the nearest
  row's nearest end. Verified on a 12x6 box from (7,12):
  `(8,12)…(11,12) (7,12)…(0,12) (0,13)…(11,13) …`, rows 12,13,14,15,11,10.

## [0.5.3] — 2026-10-30

### Hoe and watering can
- **The swing anchor is the queue head again, not "whichever tile covers
  most".** Even with the row order in place the anchor picker still scanned the
  row and moved the best-covered tile to the front, so a row was worked
  23 → 26 → 29 → 32 → 17 → 14 → 11 → 8 → 5 → 34 instead of left to right. The
  anchor is now always the tile the queue points at; only the facing and charge
  level are chosen (four facings, largest coverage), and the charged area still
  spills into the rows below.
- **With the cursor hidden, a charged swing walks to the planned stand side
  first.** The area is laid out from "target tile + facing", so swinging from
  the opposite side shifted the whole block by one tile.

## [0.5.2] — 2026-10-30

### Hoe and watering can
- **A field split by a gap is finished block by block, not row by row across
  the gap.** The row sweep treated every row as one continuous run, so with two
  work blocks separated by a water channel or a path, the farmer reached the end
  of the right block's row and walked left across the gap instead of continuing
  down the right block. Work tiles are now split into 4-connected blocks first;
  blocks are taken nearest-first and each one is swept top-down on its own
  (verified on a 9+3+9 shape: the near block's 72 tiles finish before the other
  block is touched).

## [0.5.1] — 2026-10-30

### Hoe and watering can
- **Never swing at a diagonal target with the cursor hidden.** A swing resolves
  its tile at the *end* of the animation, in `Farmer.useTool()`, through
  `GetToolLocation()` — which only honours `player.lastClick` while the mouse
  cursor is visible and no gamepad button is held. With a gamepad (or a faded
  cursor) it aims 48 px along the facing direction instead, so a diagonal stand
  tills the orthogonal tile and the intended one stays untouched. A live log had
  143 of 256 swings from a diagonal stand. Hoe and watering can now walk to an
  orthogonally adjacent stand first in that mode; with the cursor visible a
  diagonal click is still honoured, so nothing changes there.
- **Row-by-row S order instead of the nearest-first ring.** The base queue used
  the walking distance to each tile's adjacent stand cell, which ties the whole
  first ring at distance 0 and produced concentric squares (an 11x9 field opened
  with a 3x3 block around the start, then a ring) — the "back and forth in all
  four directions" report. Hoe, watering can and machine loading now sweep row
  by row, top down, alternating direction, entering the first row from the
  nearer end. Missed tiles are re-inserted with the same order.
- **The charged-swing anchor is picked inside the current row.** It used to
  scan the next 48 queue slots and pull whichever anchor covered the most tiles
  to the front, so one field was worked as several separate patches. Coverage
  that spills into the rows below still counts; only the anchor is local now.

### Machine loading
- **Coffee beans (and other seeds that are also machine input) now load into
  kegs.** The tile-work classifier treated anything in the seed category as
  "plant only", so a keg tile was never even asked whether it accepts the item:
  the box found no work and nothing was dropped in. A seed now checks the
  machine on that tile first (accept-only probe, so standing next to a finished
  keg with seeds does not collect its output by accident), and the action
  dispatch follows the tile's planned work instead of the held item's category.
  A seed run whose targets are all machine loads also reports itself as
  "loading" instead of "planting".

### Scythe
- **Harvest follows rows inside a patch.** The picker ranked candidates by
  walking distance (x100000), so a lower row that was one step closer was taken
  before the upper rows still standing. Row (Y) is now the primary key and
  distance only orders tiles within a row; the patch-first penalty still
  outweighs any row difference.

### Object placement
- **A farm animal standing on the only stand tile no longer blocks loading.**
  Stand candidates came from `IsStandable`, which still counts farm animals as
  walls even though they are pushable; a keg beside a cow produced "no stand"
  and the queue retried other tiles (looked like running back and forth). Stand
  candidates now accept terrain-only tiles (strict ones are still preferred,
  solid characters still block).

## [0.5.0] — 2026-10-30

### Animal queue
- **Milking and shearing no longer need to walk around to the animal's head.**
  Vanilla `MilkPail`/`Shears` `beginUsing` ignores the click position it is given
  and recomputes its 64×64 hit box from `who.GetToolLocation()`; that call drops
  `player.lastClick` whenever the mouse cursor is hidden (gamepad play, faded
  cursor) and aims 48 px along the facing direction instead. Every aim point we
  picked was decorative in that mode, so only the stand tiles whose "tile in
  front" happened to overlap the harvest box worked — usually the head side.
  The swing now forces `Game1.wasMouseVisibleThisFrame` for the frame that starts
  it (restored on the next tick), and the "can I hit it" prediction uses the
  same clicked path, so prediction and the real swing agree.
- **Six aim points per facing, and the swing is retargeted to the queued
  animal.** Closest point on the harvest box plus its centre was not enough when
  two ready animals stand together and the game's
  `GetBestHarvestableFarmAnimal` returns the neighbour; the four box corners are
  now tried too (nearest first), and if the started swing still captured a
  different animal it is pointed back at the queued one before the animation
  ends.
- Stand-tile retries for a target that cannot be aimed at went from 5 to 8.

## [0.4.55] — 2026-10-29

### Localization
- **The sprinkler layout option no longer stores a Chinese value.** The config
  dropdown already displayed "Default / Trellis crops" in either language, but
  the value written to `config.json` was `默认` / `架子作物`, and the layout
  check compared against that Chinese string — so an English user's config file
  showed Chinese, and any hand-edit of the value silently fell back to the
  default layout. The stored values are now `default` / `trellis`; a config
  holding an old value is migrated once on load and written back.
- **The startup log line now comes from the translation files** instead of being
  hard-coded Chinese, so English users get an English line.
- Removed a stale claim from that line: fish pond produce is no longer collected
  by the animal queue (ponds moved to the tile queue in 0.4.20).

## [0.4.54] — 2026-10-28

### Animal queue
- **Already-petted animals are no longer queued in pet mode.** The drag
  overlay already painted them red ("nothing to do"), but the queue still
  added every animal inside the box plus 10 tiles, so re-selecting just the
  one animal that was missed started a 21-target run and the counter read 21
  until the 20 finished ones were skipped as "already petted". Collection now
  uses the overlay's own rule (skip an animal that was petted today unless a
  harvest tool is ready for it), so the counter matches the green markers; a
  box with nothing left to do reports "N 只都没产出、今天也摸过了" instead of
  starting an all-skip run.

## [0.4.53] — 2026-10-28

### Walking
- **A route no longer takes the long way around a flock.** The game's
  pathfinder treats farm animals as walls, so one animal standing in a gateway
  made the route go around the whole fence (a target three tiles away produced
  a 22-step path). When the plain path is clearly longer than the direct
  walking distance, the queue now builds a route from the "farm animals are
  pushable, solid characters are not" field and walks straight through,
  pushing the animals aside.

## [0.4.52] — 2026-10-28

### Animal queue
- **A flock of animals no longer makes the farmer circle and give up.** The
  walker treated every nearby animal as a dynamic obstacle: it re-routed
  around the flock and, after five seconds without progress, dropped the
  animal ("放弃 …：blocked"), then picked the next animal in the same flock and
  repeated. Animals are pushable, so when nothing solid (pet, horse, tractor,
  villager) is involved the walker now stops re-routing, re-plans once and
  pushes straight through; only a much longer stall (15 s) gives up.

## [0.4.51] — 2026-10-28

### Animal queue
- **Stand tiles blocked only by a character are still tried instead of being
  thrown away.** They were dropped from the candidate list as soon as the
  reachability field said "a character is in the way", so a target whose stand
  tiles were all beside animals could only be re-queued and waited on. They are
  now kept at the end of the list: the normal path search fails on them and the
  terrain-only "push through" route takes over (farm animals are pushable).
  Solid characters (pet, horse, villagers) still end up unvisited.
- The "re-queued" log now names the characters that were blocking the stand
  tiles ("挡路角色=名字(格)实心/可穿").

## [0.4.50] — 2026-10-28

### Animal queue
- **The queue no longer pauses when the game window is not focused.** Watching
  the game from a second monitor while another window is active made the
  farmer stand still with the "remaining" counter unchanged; the game world
  keeps running, so the queue now keeps working too. Menus and dialogs still
  pause it.
- **Skipped, deferred and given-up animals are logged.** These decisions were
  only written when debug logging was on, so a run that cycled through targets
  without acting produced no output. Each now logs one line with the reason
  ("跳过 …", "放回队尾 …", "放弃 …").

## [0.4.49] — 2026-10-28

### Animal queue
- **A gateway blocked by an animal no longer makes the queue give up.** The
  gate walk only accepted stand tiles that the game calls standable (which
  counts farm animals as blockers), and it tried to walk to the tile the
  farmer was already standing on — so with an animal parked in the gateway
  every candidate failed and the target was dropped. Gate stand tiles are now
  chosen by terrain only, and being next to the gate skips straight to opening
  it.
- The 2.5 s "no progress" diagnostic from 0.4.48 no longer fires during long
  walks (the farmer's tile is part of the progress check now).

## [0.4.48] — 2026-10-28

### Diagnostics
- **The animal queue now says why it is not moving.** It pauses silently while
  the game window is not focused or a menu/dialog is open, and a target that is
  retried also produced no output — both look like a freeze. A pause now logs
  once ("照看暂停：…"), and a run that makes no progress for 2.5 seconds logs
  its internal state (target, walking/action, cooldowns, tool/animation flags,
  stand-tile checks).

## [0.4.47] — 2026-10-28

### Animal queue
- **Farm animals no longer count as walls.** They are pushable: the game sets
  `farmerPassesThrough` on farm animals and nudges them aside when the farmer
  walks into them, yet its pathfinder treats their bounding box as a wall — so
  a pen with an animal parked in the gateway produced "no path" for every stand
  tile and the queue waited. The reachability field is back to the terrain
  rule, and when pathfinding fails while the terrain is walkable the queue now
  walks the terrain route and pushes the animals aside. (0.4.46 made this worse
  by marking animal-occupied tiles as blocked.)

## [0.4.46] — 2026-10-28

### Animal queue
- **The queue no longer stands still while it retries stand tiles that an
  animal is blocking.** The reachability field counted a farm animal as
  occupying only the tile under its feet, while the game's pathfinder uses the
  animal's bounding box. In a crowded pen (especially with one animal parked in
  a gateway) every stand tile looked reachable, so each attempt ran pathfinding
  for each of them, all came back empty, and the farmer waited. The field now
  uses the same bounding-box rule as the pathfinder, so those tiles count as
  "wait for the animal to move" and the target is deferred instead of retried.

## [0.4.45] — 2026-10-28

### Animal queue
- **A full inventory no longer repeats milking and shearing.** The capacity
  check assumed quality 0, so higher-quality milk or wool could look like it
  fit into a normal-quality partial stack; the tool animation then ran, failed
  to add the item and the animal was retried. The check now builds the exact
  produce item (quantity and quality), and if the bag still cannot take it
  after an attempt the animal is petted instead of retried.

## [0.4.44] — 2026-10-28

### Object placement
- **The placing stand prefers a pending tile inside the box over the outer
  aisle.** Standing on the aisle outside the box let the farmer fill a row
  while sealing the way to the rows beyond it: in a live 16×2 box the whole
  top row was placed from the upper aisle and the bottom row then had no path
  at all (15 tiles, every candidate stand "no path"). A stand that is itself
  a pending target now sorts ahead of a non-target stand, so the farmer walks
  into the box and keeps a way through to the far side; the outer aisle is
  still used for the last row, where nothing else is left.

## [0.4.43] — 2026-10-28

### Highlights
- **Ground work shows the green tile grid again.** Tilling, sowing,
  watering, fertilising, sprinkler/sapling placement and placing solid
  objects are marked with the same per-tile green grid as the selection
  preview, so it is visible which tiles the queue has not reached yet. Those
  tasks are ground-only, so they still get neither a task bubble nor the
  night-time entity glow.

## [0.4.42] — 2026-10-28

### Walking
- **A stalled walk no longer resets its stuck counter when the queue re-issues
  the same destination.** The queue re-sends the current destination every
  dozen ticks; each re-send used to clear the "hasn't moved" count, so the
  unstick steps could never fire and the farmer paced in place. The counter now
  carries over between identical attempts.

## [0.4.41] — 2026-10-28

### Walking
- **A walk that stalls beside an obstacle now squares the farmer up in the
  tile instead of only stepping sideways.** When the farmer stops against a
  sprinkler or a trellis, his collision box can sit a few pixels into the next
  tile, so the tile checks all say "walkable" while he cannot push through and
  the sideways step just bounces him back and forth. The driver now re-centres
  him on the tile before pushing again.
- **The path check uses the same collision rule as walking.** The
  path-simplification step no longer asks the pathfinding variant of the
  collision check (which ignores NPCs); it uses the movement variant, so a
  route segment that is not physically walkable is re-planned around.

## [0.4.40] — 2026-10-28

### Walking
- **The route no longer plans through NPCs.** The game's pathfinder skips NPC
  collision when it is pathfinding, and the parked tractor is an NPC, so routes
  ran straight through it and the farmer pushed against it. Routes that cross a
  character are now replanned with the mod's own avoiding search, and the
  path-simplification step treats character tiles as blocked so a detour is
  never collapsed back into a straight line through them.

## [0.4.39] — 2026-10-28

### Scythe
- **Hand-picking takes both sides of a path in one pass.** Without an iridium
  scythe the crops are picked one tile at a time, and the patch preference made
  the queue clear one column before the other, so the same gap had to be
  walked twice. Hand-pick tiles are now ordered purely by walking distance from
  the farmer, so everything within reach of the current spot is picked before
  moving on. Scythe swings still prefer the current patch.

## [0.4.38] — 2026-10-28

### Animal queue
- **Animals are ordered and approached by walking distance instead of
  straight-line distance.** An animal just across a fence looked nearest, so
  the queue picked it first and offered stand tiles on the far side of the
  fence. Target order, stand-tile order and stand-tile choice now all use a
  BFS distance field, and stand tiles that cannot be walked to are dropped
  before any pathfinding is attempted.
- **A closed fence gate is opened when it is what stands between the farmer
  and the animal.** A pen with a shut gate used to make the queue retry
  pathfinding a few times and give up. It now walks to the nearest closed gate,
  opens it and replans. Each gate is tried once per run.
- **Failed pathfinding is throttled.** A failed plan used to be recomputed on
  every tick (all stand tiles, one pathfind call each); it now waits a few
  ticks, growing with the number of attempts.

### Scythe
- **The scythe no longer walks past work that is right next to the farmer.**
  Tile selection was locked to the connected component the queue happened to
  be in, so a tile in another patch could be skipped even when it was a step
  away, and a far tile inside the current patch was taken instead. Selection
  now scores every remaining tile by walking distance from the farmer's actual
  position, with a small penalty for leaving the current patch. Tiles that
  already failed three times are no longer pulled back to the front.

## [0.4.37] — 2026-10-28

### Animal queue
- **A full inventory no longer stops petting.** In petting, shearing and
  milking runs, loose items that don't fit are skipped and animals whose
  produce doesn't fit are still petted. A full inventory now only stops the
  scythe collection mode, where collecting items is the whole job.

## [0.4.36] — 2026-10-28

### Machine collection
- **A full inventory now stops the queue instead of retrying the machine.**
  Machine and fish pond outputs share the hand-pick path; a failed pickup was
  re-queued at the tail and picked up again by the end-of-run rescan. Before
  reaching for the output, the queue now checks whether it fits in the
  player's inventory. If it doesn't, the run stops with "Inventory is full"
  and the remaining targets are cleared.

## [0.4.35] — 2026-10-28

### Hoe and watering can
- **A charged swing now covers the whole remaining patch when one charge is
  enough, instead of occasionally charging from a corner.** The iridium
  tool's full charge is a 3-wide x 6-long rectangle; anchoring it at a field
  corner covers the columns on one side and leaves the far column dry, which
  needed a second charge (the "3x6 field watered from the origin" case). The
  anchor picker now first checks the connected patch of remaining work around
  the queue front: if the patch fits under one charge, it picks the anchor
  that covers all of it (centre of the short edge, facing into the patch),
  using only the smallest power that covers it. Bigger fields keep the
  greedy, now ranked by tiles covered first and walking distance second — the
  old distance-weighted score could prefer a nearer corner anchor over a
  fuller one.
- **The planned power and facing are now what the swing actually uses.** The
  charge level used to be recomputed at swing time with a different
  objective, which could downgrade a planned full charge to a smaller area
  and leave a strip behind.
- **The farmer now works from an orthogonally adjacent tile** — standing on
  the target or diagonally no longer starts the swing. The planned stand is
  tried first and the old candidate list is kept as a fallback, so a blocked
  stand cannot strand the queue.

## [0.4.34] — 2026-10-28

### Walking
- **A farmer pressed against a static obstacle now side-steps instead of
  waiting out the 12-second walk timeout.** The side-step fallback only fired
  when an animal, NPC or pet stood nearby. Walking along a stone wall can
  leave the farmer's collision box overlapping the next column, so "down" is
  refused even though the target tile itself is free; with no character nearby
  the driver kept pushing into the wall. A live axe run sat on one tile for
  ~12 s two targets from the end before the hard timeout re-routed around the
  stones. The fallback now also covers static terrain: after ~0.8 s without
  progress it nudges one tile sideways; the blocker-aware reroute and the
  12-second timeout stay as before.

## [0.4.33] — 2026-10-28

### Object placement
- **Solid objects are always placed top-down.** The starting direction used to
  depend on where the farmer stood; standing in the lower half of the box made
  the sweep go bottom-up, and the last rows at the top ended up sealed by the
  keg wall and the farmer's own kegs (a live run retried the top-left tiles
  466 times with every stand answering "no path", 225 tiles left). The row
  order for solid placeables now always starts at the topmost planned row;
  the nearest end of that row is still the entry point, so the rest of the S
  is unchanged.

### Axe
- **A fruit tree that has already been felled is no longer treated as a
  stump target.** The last axe hit sets the fruit tree's health to -100 and
  the game stops drawing it at once, but the feature stays in the terrain
  list until the felling animation's leaves clear. During that window the
  queue re-checked the tile as "stump work" and kept swinging at an
  invisible, already-dead tree (up to 16 hits). Fruit trees now get the same
  `health <= -99` guard normal trees already had, and the stump follow-up no
  longer re-queues a dead tree.

## [0.4.32] — 2026-10-28

### Pickaxe work order
- **The pickaxe now gets the same live re-pick as the axe.** Stones, boulders
  and mine clumps are walls while they stand: a stone ringed by other stones
  had no reachable stand tile when the route was planned, so it was queued
  near the end, and after the outer ring was broken the farmer could stand
  next to it and still walk past. Before each pickaxe target the remaining
  jobs are re-ranked by real walking distance from the farmer's current tile
  and the nearest one moves to the front; equal distances keep the queue
  order.
- **The standability cache is flushed when a blocking pickaxe target is
  removed** (stones, boulders, resource clumps, fences, removable objects), so
  the opened ground counts on the next pick.

## [0.4.31] — 2026-10-28

### Axe work order
- **The axe now picks every target from where the farmer actually stands,
  instead of walking a route planned before any tree was felled.** The route
  was computed once at the start, when the trees themselves were still walls:
  a tree in the middle of a grove had no reachable stand tile, so it was
  queued near the end. Once the outer trees came down the farmer often stood
  right next to it and still walked past, returning only after the whole
  area — in a live run a target directly next to the farmer's stand at job 5
  was taken 48th of 49. Before each axe target the queue now re-ranks the
  remaining jobs by real walking distance from the farmer's current tile and
  moves the nearest one to the front; equal distances keep the old order.
- **The standability cache is flushed when a blocking axe target is removed**
  (standing trees, saplings, stumps, giant crops, wooden fences), so the newly
  opened ground counts on the very next pick instead of up to a second later.

## [0.4.30] — 2026-10-28

### Object placement
- **A split row no longer counts as "ahead", so switching to the other side
  starts at that block's top.** After the east bank was finished, the west row
  on the same level was treated as the next row and filled first; that sealed
  the bottom exit, and the farmer then walked up and swept down with the
  perimeter already closed by its own kegs. Only rows strictly further in the
  sweep direction count as ahead now; when none are left, the route restarts
  at the top row of the remaining block and sweeps downward, so the bottom
  row of every block is placed last.

## [0.4.29] — 2026-10-28

### Animal queue
- **"顺手抚摸" is gone from the report.** A milk/shear pass that found no
  produce still reported "completed 0, petted 18 along the way". When the
  only thing the run did was petting, the summary now reads
  "抚摸完成 18 只"; and the separate "petted along the way" clause is no
  longer appended to mixed runs.

## [0.4.28] — 2026-10-28

### Object placement
- **The sweep no longer turns around when the remaining work is behind it.**
  0.4.27 stopped the mid-route double-back, but once the east side was done
  and only the west remained (all of it above the farmer), the direction
  flipped and the west was filled bottom-up; the farmer ended at the top edge,
  sealed in by its own kegs and the pond, and the last tiles on the other side
  had no path (`findPath` empty for the last 3 tiles in a live run). When
  nothing is left ahead, the route now returns to the top row of the remaining
  block and sweeps downward again, so every block finishes at the bottom next
  to the open perimeter. Solid fields are unchanged.

## [0.4.27] — 2026-10-28

### Object placement
- **The row sweep no longer doubles back mid-route.** After the pond's east
  side was finished, the nearest-row rule walked back up (30 → 29 → 28) and
  the kegs just placed sealed the rows below: the farmer stood at the left
  edge and every remaining tile below answered "no path" (a live 329-tile run
  lost its last 29 tiles that way). While a run is still ahead in the current
  row direction, runs behind it are ignored; the direction only flips when
  nothing is left ahead. Solid fields keep the 0.4.26 order.

## [0.4.26] — 2026-10-28

### Fish pond / machines
- **Ponds are no longer left to the end of a scythe run.** A pond's centre is
  its own 8-connected patch, so the patch order deferred it until the field
  was finished. Pond targets are now kept out of the patch grouping and
  inserted into the crop route at the point of least detour, so the farmer
  collects them while passing instead of at the very end.
- **Hand-picked machine products now require standing next to the machine.**
  The queue used the scythe's two-tile sweep as "in range" and then called the
  machine's checkForAction directly, so a machine behind a solid tile could
  still be collected through it. Hand-pick targets now walk to Chebyshev
  distance 1 first.

## [0.4.25] — 2026-10-28

### Scythe harvest
- **0.4.24's patch lock could be wiped during queue start-up.** The shared
  start-of-run reset block could run after the patch map was built, leaving the
  scythe picker unconstrained again. The map is now cleared only when the queue
  stops, and the end-of-run trace line reports `seg=N` so the live log shows
  whether the picker ran with the patch map.

## [0.4.24] — 2026-10-28

### Scythe harvest
- **0.4.22's segment order was not enough: the scythe picker still scanned
  every remaining tile for the best swing.** Even with a segmented queue it
  could swap the next tile for a much farther one in another patch, so the
  farmer still crossed the box mid-field. The picker is now limited to the
  patch of the tile it was about to take. A contiguous field is a single
  patch, so its whole-field swing-coverage optimization is unchanged.

## [0.4.23] — 2026-10-28

### Object placement
- **Isolated patches are placed first, while the field is still empty.** The
  row-run order left a small strip on the far side of a pond to the end of the
  job; the live trace shows the farmer then walking ~56 tiles around the pond
  to reach it. A run that has no touching run in the row above or below is now
  collected up front (nearest first), and only then is the main body swept with
  the row-run S — so the long crossing happens while the ground is still open.
  Solid rectangles and layouts whose runs touch vertically keep the previous
  order exactly.

## [0.4.22] — 2026-10-28

### Scythe harvest
- **A scythe run now finishes one connected patch before moving to the next.**
  The queue picked the globally nearest next tile, so a box covering a large
  crop field plus nearby small patches could harvest a few tiles of the field,
  run 30–60 tiles away to a pond or a lone crop, then come back. Scythe work is
  now grouped by 8-connected patches, each patch is routed with the existing
  corridor-aware order, and only then does the farmer switch to the nearest
  remaining patch. Contiguous fields keep exactly the previous route.

## [0.4.21] — 2026-10-28

### Object placement
- **0.4.19's segment split never triggered in a real keg field.** It grouped
  the planned tiles by connected components, but when the two sides are still
  joined somewhere — a pond that stops above the bottom rows, or bank tiles
  that touch diagonally — everything is one component and the farmer crosses
  the pond once per row again. A live 409-tile keg run logged 9 crossings.
  The order is now built from **horizontal row runs**: runs in the same row
  merge when the gap between them is at most 2 tiles (a missing keg, a narrow
  path), and a wider gap (pond, building) ends the run. The farmer finishes
  the run on the current side, continues with the next row on that side, and
  only crosses when that side has no runs left. Reconstructed from the live
  field: 14 crossings before, 1 after. Solid rectangles and gapped layouts
  whose gaps are 1–2 tiles produce exactly the previous order.

## [0.4.20] — 2026-10-28

### Fish pond
- **Fish ponds are machines everywhere now.** The tile queue approaches a pond
  from the walkable ring around its 5×5 footprint instead of trying to stand
  next to its centre tile, which is 2 tiles inside the pond and always failed
  as "unreachable". A box that mixes crops or machines with ponds now harvests
  the ponds in the same run.
- **Ponds are no longer part of the animal collect mode.** An empty-hand drag
  no longer counts a pond as produce, the "harvested N pond(s)" report line is
  gone, and a pond-only drag with a scythe goes through the tile queue.
- **The selection overlay skips the pond's water tiles** (the output centre
  still gets its green marker), so a pond no longer paints the rest of its
  footprint red as "no work".

## [0.4.19] — 2026-10-28

### Object placement
- **A selection split by an obstacle is now filled one side at a time.** The
  planned tiles are grouped into connected segments, each segment is finished
  with the nearest-corner S sweep, and only then does the farmer move to the
  next segment (nearest one first). A keg field with a pond in the middle used
  to go around the pond once per row — in a 12×6 box that was 6 crossings; it
  is now one crossing per extra segment. Contiguous selections and layouts
  whose tiles touch diagonally still produce exactly the previous order, so
  the normal S sweep is unchanged.

## [0.4.18] — 2026-10-28

### HUD
- **Removed the "click: skip/add · double-click: this type only" hint**
  that was appended to the "N left" counter during both tile and animal
  runs. The counter is all that is left over the cursor.

## [0.4.17] — 2026-10-28

### Animal queue
- **Sleeping animals get their own report line.** A box dragged after 19:00
  skips every settled animal as "asleep", but the summary still read
  "completed 0, skipped 9", which says nothing about why. Sleeping skips are
  now counted separately: when nothing could be petted the report reads
  "9 只都睡着了，明早再来", and a mixed run appends ", N 只睡着了" instead of
  folding them into the generic skipped count.
- **Animals that come out of a coop/barn mid-run join the queue.** The area
  was only scanned once the queue ran out of targets, so a farm where some
  animals were still inside missed them for the whole run. The scan now also
  runs every 0.5 s while the queue is working and appends any animal that has
  just entered the area.
- **A retry that succeeds withdraws its "unreachable" count.** A target given
  up as unreachable but petted/harvested by the retry pass still showed up in
  the final "N unreachable" line; it is now counted once per animal and the
  count drops when that animal is finished.

## [0.4.16] — 2026-10-28

### Animal queue
- **The run now ends as soon as the last animal is handled.** The end-of-run
  re-check treated every animal still inside the area (the box plus 10 tiles)
  as a newcomer, so after the final pet it re-queued the whole barn for up to
  five rounds: the counter bounced `0 → N → 0`, every animal kept glowing, and
  the job lingered ~10 seconds before the report. The scan now only picks up
  animals that still need work and were never queued during this run
  (`QueuedIds`), and a scan that finds nothing finishes the job immediately
  instead of waiting out the remaining rounds.

## [0.4.15] — 2026-10-28

### Animal queue
- **Collecting ground produce no longer stops the run after the first item.**
  The queue stops when the held item changes (that is how a manual tool
  switch cancels it), but vanilla puts a picked-up item into the first empty
  slot from index 0 — often the selected empty-hand slot. The first egg /
  wool / truffle therefore replaced the "held item" baseline and the guard
  fired one tick later with "已暂停". Ground-item and fish-pond pickups now
  re-baseline the held item right after their own inventory write, so only a
  real manual switch stops the queue.
- **The three "已暂停" stop paths now log which one fired** (direction key /
  left the map / held item changed) instead of leaving just the bare
  message.

## [0.4.14] — 2026-10-28

### Object placement
- **Placing solid objects now walks to the corner of the selection nearest the
  farmer and fills the area row by row in an S-shaped sweep.** Kegs, chests,
  preserves jars and other solid placeables used to be queued by the
  corridor-aware nearest-tile route, which cut into the block and made the
  farmer circle around machines that were already placed. The order is now
  derived from the planned tiles: the nearest row first, starting from its
  nearest end, with each following row swept in the opposite direction.
  Only `IsPlaceableObjectItem && IsSolidPlaceItem` changed — seeds, saplings,
  fertilizer, sprinklers, machine loading, tools and the retry passes keep
  their existing orders.

## [0.4.13] — 2026-10-28

### Co-op
- **The animal queue no longer treats another player as an obstacle when
  picking a spot to stand on.** `TileUtil.IsStandableIgnoringCharacters` used
  mask 246, which is `All & ~(Buildings | Flooring)` — despite the name it
  still counted `Farmers` and NPCs, and it excluded buildings/flooring. Its
  only caller (`AnimalQueue.BlockedByCritters`) therefore skipped any approach
  tile another player stood on, so an animal whose only reachable spot was
  occupied got given up (`在水里够不着`) instead of deferred. The mask is now
  242 (the same semantics minus `Farmers`), matching the walking fix in 0.4.11.
  Consequence to expect: the animal queue will now walk to a spot a player is
  standing on and overlap them, the same way walking already does. NPCs,
  animals, objects, terrain and furniture still block.
- Note: with 242 the second half of that `!IsStandable || !Ignoring` test can
  no longer fail once the first passes, so the helper call is inert in that
  expression. It is kept in this release and can be removed after an in-game
  check.

## [0.4.12] — 2026-10-28

### 2:00 AM pass-out
- **The farmer now passes out at 2:00 AM like normal, instead of working on
  through the night.** The game only collapses the farmer when a whole row of
  conditions hold at once (`Game1.UpdateOther`: `canMove`, `!UsingTool`,
  `freezePause <= 0`, no pathing controller, no event, no menu, no minigame).
  A running queue holds several of those down, so the clock reached 2:00 and
  nothing happened. This version never had the hand-back code — it was written
  for the 0.3.1 line and lost when the 0.4.0 tree was rebuilt from the dll.
  The check now runs once per tick ahead of both queues (tile and animal), so
  it covers either one plus the walk driver: on reaching 2:00 (or stamina
  hitting -15) it stops the queue, detaches the simulated keys, drops the
  pathing controller, and calls the game's own
  `completelyStopAnimatingOrDoingAction()` so vanilla sees exactly the state a
  normally-controlled farmer would be in.
- **`AnimalQueue.Stop` no longer leaves the farmer mid-action.** Stopping the
  animal queue during a pet/milk/shear animation left `UsingTool` set and
  `CanMove` false, because the restore only ran on the normal completion path.
  That residual also blocked the 2:00 gate on its own.

## [0.4.11] — 2026-10-28

### Walking in co-op
- **Other players are no longer treated as solid obstacles.** The standability
  test used `CollisionMask` 255 (`All`), which includes `Farmers`, so a co-op
  partner's tile counted as unstandable and their presence fed the "I'm
  blocked" recovery path. The game itself does not do this: every movement
  collision check for a farmer passes `isFarmer: true`
  (`Farmer.cs`, `Character.MovePosition`), and `GameLocation.isCollidingPosition`
  skips its farmer-vs-farmer branch in that case; `PathFindController` likewise
  passes `character is Farmer`. Players walk through each other in vanilla.
  `IsStandable` now uses mask 251 (`All` minus `Farmers`); NPCs, animals,
  objects, terrain, buildings and furniture still block as before.
- `CollectBlockerTiles` no longer adds other farmers to the blocked set. That
  set feeds the re-route target and the side-step gate, so including players
  made the driver route around a wall that does not exist — which could burn
  all 4 re-routes, fall through to perpendicular side-step hops, and (because
  `StuckFrames` resets on any tile change) keep hopping until the 720-tick
  hard timeout. This was the reported "character flies to a random spot when
  a co-op partner is nearby".

### Diagnostics
- **`Stop()` now detaches the input simulator.** Only `Release()` used to
  detach, so the 18 `Stop()` call sites left `Game1.inputSimulator` attached;
  while it is attached the game keeps processing control input even when the
  window is not active.

### Not fixed in this version
- **Local split-screen still cross-talks.** `Game1.inputSimulator` is a single
  static and every screen's `UpdateControlInput` calls it, so each screen
  consumes the same simulated keys. No screen guard has been added yet.
- The general `StuckFrames` reset-on-tile-change issue is untouched; it still
  affects being blocked by real obstacles (animals/monsters) and needs its own
  verification round.
- `IsStandableIgnoringCharacters` is misnamed: its mask 246 is
  `All & ~(Buildings | Flooring)`, so it still counts farmers and NPCs and
  *excludes* buildings/flooring. Its single caller's double check is therefore
  redundant. Left as-is in this version.

## [0.4.10] — 2026-10-27

### Highlight
- Glow green brightened again on request: (140,190,75) -> (170,210,95), blend
  stays 0.82.

## [0.4.9] — 2026-10-27

### Highlight
- Glow green brightened one step (color 104,144,58 -> 140,190,75, blend 0.78 ->
  0.82) after the first match read too dark over crops and pond water.

## [0.4.8] — 2026-10-27

### Highlight
- **Glow color now matches the selection grid green.** The old neon green
  (30,250,50) is replaced by the grid marker's green, measured from a
  screenshot and compensated for the tint's 22% texture residue. Applies to
  every glowing entity (crops, machines, animals, ponds), since they share
  one glow color.

## [0.4.7] — 2026-10-27

### Highlight
- **Ponds glow in collect mode too.** The pond glow added in 0.4.6 only knew
  about the tile queue; a collect-mode run ("collect produce") left its ponds
  unlit. Collect mode now feeds the same glow set.

## [0.4.6] — 2026-10-27

### Highlight
- **Queued fish ponds now glow while waiting to be collected**, like machines
  and crops do (new draw patch on FishPond). Previously a pond pending in the
  tile queue had no visible highlight at all.

## [0.4.5] — 2026-10-27

### Fish ponds
- **Ponds with output now count as machine produce in the tile queue.** With a
  scythe in hand, a pond's output tile classifies as work (green marker in the
  selection, green grid while queued) and the farmer walks over and collects
  it like any machine, gated by the same "harvest machines" option. A box that
  contains crops AND ponds now collects both in one run.
- A box with ponds but no other tile work still routes to the care/collect
  mode (so animals and pets in the box are still handled).

## [0.4.4] — 2026-10-27

### Selection
- **A scythe box that touches a pond with output no longer hijacks the whole
  selection.** Both the drag-time markers and the confirm-time routing now
  check "does any tile in the box actually have work for this tool" first:
  if yes, the normal per-tile grid is drawn and the tile queue runs; only a
  box with no tile work at all switches to produce/pond collection. This
  restores the rule set on the 0.5.x line (previously one pond with output
  made every other marker disappear and the queue collect ponds only).

## [0.4.3] — 2026-10-27

### Highlight
- **Markers now ask the world, not the queue ledger.** Every 10 ticks the
  highlight re-checks each queued tile with the live work test, so a tile
  whose dirt is already tilled / watered loses its marker within ~0.17 s even
  when the queue bookkeeping hasn't popped it yet.
- New `SPLASH` walktrace line per verified swing (center, toolPower, tiles
  marked done) to settle whether charged swings really cover more than one
  tile in this build.

## [0.4.2] — 2026-10-27

### Highlight / counter
- **Tiles finished as part of a charged swing now drop out of the grid and the
  counter immediately.** A charged hoe/can finishes several tiles at once, but
  the queue only pops one tile per stop, so splash-finished tiles kept their
  green marker (and stayed in "remaining") until the farmer walked over them.
  After each verified swing the tiles around it that no longer have work are
  marked done, so both the grid and "remaining N" drop in one batch.

## [0.4.1] — 2026-10-27

### Highlight
- **The queue's remaining ground work is drawn as the same green tile grid
  used while dragging a box.** Tilling, watering, sowing, placing and
  fertilizing tiles still waiting in the queue show the grid, and each tile's
  marker disappears as soon as its work is done. Entity work (harvesting,
  chopping, machines, animals) keeps the green glow as before.

## [0.4.0] — 2026-10-26

### Animal care (new)
- **Box-select farm animals to pet, shear or milk them.** What the
  drag does depends on what you're holding: empty hands or any misc
  item with no farm-work meaning (food, wood, clothing…) queues
  petting; shears and the milk pail follow a "harvest if ready, pet
  otherwise" rule, and if the right tool (shears/milk pail) is
  anywhere in your inventory the farmer switches to it automatically —
  one drag pets everyone and harvests everything. The capture area
  extends 10 tiles beyond the drawn box (animals wander!), and before
  the queue finishes it re-checks the area once and picks up any
  animal that walked into range. Cats, dogs, turtles and other pets
  are petted too (vanilla checkAction: +12 friendship, possible
  gifts). Fish ponds with produce and ground produce (eggs, wool,
  duck feathers, rabbit's foot, truffles) are collected with the same
  drag — or box them with the scythe for a produce-only pass. While
  the queue runs, every pending animal glows green and
  Ctrl+click / double-click edits the queue (skip one / keep same
  kind) exactly like tile work. Animals that can't be reached (ducks
  in open water) are skipped and reported, with an automatic retry
  pass before the final report. When shearing or
  milking, the farmer also pets each animal it visits if it hasn't
  been petted yet (skipped silently for sleeping animals), so one trip
  earns both the petting and the harvest friendship. The farmer walks
  to each animal in nearest-first order and interacts exactly the way
  a vanilla click does — petting calls the game's own `pet()` logic
  (friendship, happiness, sounds and emotes included), while
  shearing/milking drive the real tool-use pipeline (`BeginUsingTool`),
  so animation, stamina cost, product quality and coop perks all match
  hand-play. Selection shows green markers on animals that will be
  visited and red ones on animals excluded (e.g. no wool yet), and the
  HUD reports "Petting: N left".
- **Animals that can't be reached are skipped, not stuck on.** Ducks
  (and other swimming animals) that wander into open water have no
  standable neighboring tile, so they're treated as unreachable and
  skipped — the end-of-run report says how many. Every unreachable or
  failed animal gets one automatic retry pass before the queue wraps
  up, since animals move around.
- Robustness details: already-petted and sleeping animals are skipped
  without opening the animal menu, the queue stops when you manually
  switch items (the auto tool-switch doesn't), move, open a menu or
  leave the map, milking/shearing respects the stamina threshold and
  stops with "not enough stamina", and a full inventory stops the
  harvest queue cleanly.

## [0.3.38] — 2026-10-25

### Queue editing
- **Double-click "keep same kind" no longer splits objects by tilled
  soil underneath.** The diagnostic probe settled it: the keg field sat
  partly on old HoeDirt — 114 kegs got a bare work key, 79 kegs on bare
  ground got an ID key, and one double-click kept only the 114. The kind
  key logic is now inverted and whitelist-driven: only works that belong
  to the terrain feature itself (watering/sowing/harvesting on dirt,
  removing flooring) are keyed by the work; everything else — objects on
  grass, flooring, bushes or tilled soil — falls through to the
  object-ID key. A keg now keys identically no matter what's under it.

## [0.3.37] — 2026-10-25

### Diagnostics
- Filter tracing: dropped tiles log their work type, kind key and what's
  underneath (feature type / object ID), and the FILTER line reports the
  double-clicked tile's full context. Deployed to settle the keg-field
  split definitively.

## [0.3.36] — 2026-10-25

### Queue editing
- **Double-click "keep same kind" no longer splits objects by what's
  underneath them — flooring included.** Keg fields are typically laid
  out on wood flooring, and flooring is a terrain feature: the 0.3.35
  fix only let grass-covered tiles fall through to the object-ID key,
  so kegs on flooring still got a bare key while kegs on bare ground
  got an ID key (212 kegs, double-click kept 127). Both grass and
  flooring are now treated as background layers for object works.

## [0.3.35] — 2026-10-25

### Queue editing
- **Double-click "keep same kind" no longer splits objects by whether
  grass covers them.** Grass is a terrain feature that grows over ground
  objects; the kind key resolved terrain features first and returned a
  bare work key for grass-covered tiles, while the same object on bare
  ground got an ID-suffixed key — one double-click kept only one of the
  two halves (213 tiles kept 127). Grass-family works now return a
  single "Grass" key up front, and grass-covered tiles with object work
  fall through to the object-ID branch. Trees, fruit trees, machines
  and clumps are unaffected.

## [0.3.34] — 2026-10-25

### Queue editing
- **Double-click "keep same kind" no longer splits trees by growth
  stage.** A single tree species yields four different work types
  (seed / sapling / mature / stump), and the kind key carried that work
  prefix — double-clicking one tree in a mixed-age forest kept only the
  trees of the same species *and* stage (24 tiles shrank to 3). Tree and
  fruit-tree kind keys are now species-only: one double-click keeps
  every tree of that species, any age, including its stumps.

## [0.3.33] — 2026-10-25

### Queue editing
- **Double-click "keep same kind" no longer drops bluegrass.** Grass and
  bluegrass are two separate internal work types, so double-clicking
  plain grass deleted every bluegrass tile in the queue (walktrace
  showed `kind=ScytheGrass rest=90` vs `kind=ScytheBluegrass rest=1`).
  The kind key now normalizes bluegrass to grass: one double-click on
  any grass keeps both. Tree species and machine IDs still split as
  before.

## [0.3.32] — 2026-10-25

### Work overlay
- **Greener glow.** The restored 0.3.25 tint read as too pale; the blend
  target is now a highly saturated green (RGB 30,250,50) at 78%, which
  renders queued trees as deep vivid green.

### Queue editing
- **Re-added tiles now keep their natural order instead of going to the
  back of the queue.** When you Ctrl+click a tile back in (or re-add the
  double-clicked tile after a same-kind filter), it is inserted by its
  geodesic distance from the job's start point — before the first
  remaining tile farther out — matching the position it would have had
  if it had been inside the original selection. Timed-out retry tiles
  keep the old near-neighborhood reinsertion.

## [0.3.31] — 2026-10-25

### Work overlay
- **Glow rolled back to the 0.3.25 look at the user's request.** The
  additive-overdraw experiment of 0.3.29–0.3.30 worked mechanically
  (diagnostics showed thousands of additive draws per second) but the
  user preferred the classic look: entities drawn with a color multiplier
  lerped 72% toward a saturated bright green (RGB 160,225,105), steady,
  no pulsing. The additive machinery (batch-state recorder, second
  additive draw pass) has been removed; the per-second glow diagnostic
  line stays.

## [0.3.30] — 2026-10-25

### Work overlay
- Fixed the 0.3.29 patch failures: the postfixes declared parameters
  named `b`/`layerDepth`, but Harmony injects by name and the real
  signatures use `spriteBatch`/`alpha` — every entity patch had failed
  to apply, which is why the glow still didn't show.

## [0.3.29] — 2026-10-25

### Work overlay
- **Real additive glow: queued entities are drawn a second time with
  additive blending.** Diagnostic logging revealed why 0.3.27's additive
  formula was invisible: `SpriteBatch.Draw`'s color argument is a
  multiplier, not an additive term — world sprites are drawn with
  `Color.White`, and White plus anything clamps right back to White.
  (It also explains the odd look of the earlier blend attempts: they
  were darkening the sprite, not lighting it.) Stardew's SpriteBatch has
  no engine-level highlight like DST's `SetHighlightColour`, so the glow
  is now achieved the only way possible: after an entity draws normally,
  the mod ends the batch, restarts it with `BlendState.Additive`, draws
  the whole entity again (each draw tinted by a green multiplier,
  RGB 70,225,100), and restores the exact original batch state
  (captured by a recorder hooked onto every `SpriteBatch.Begin`
  overload). The result reads as the entity emitting green light:
  canopies bloom bright green, trunks turn olive-green. Steady, no
  pulsing.

## [0.3.28] — 2026-10-25

### Diagnostics
- Added a once-per-second glow trace while a queue runs (pending tile
  count + tinted draw count) to pinpoint glow failures from the SMAPI
  log.

## [0.3.27] — 2026-10-25

### Work overlay
- **The glow now uses DST's actual formula: additive light, not a blend.**
  Read from the ActionQueue Reborn source: the mod highlights selected
  entities through the game's highlight component, which applies
  `AnimState:SetHighlightColour` — an additive color. The green is the
  palette color RGB(59,222,99) halved, i.e. every pixel of a selected
  entity gets +(30,111,50). Verified against the reference screenshot:
  its sampled mid-tones and highlights are exactly `base + (30,111,50)`.
  Blending toward any green (the approach of 0.3.24–0.3.26) washes the
  sprite toward one color; adding light keeps saturation, lifts shadows
  into green and lets highlights bloom pale green — which is what reads
  as "glowing". Steady, no pulsing.

## [0.3.26] — 2026-10-25

### Work overlay
- **Glow retuned to a bright milky green that actually reads as a glow.**
  The vivid chartreuse of 0.3.25 (and the lime of 0.3.24) barely changed
  tree canopies — Stardew foliage is already bright green, so blending
  green-into-green was nearly invisible (pixel-verified: queued canopy vs
  natural canopy differed by only ~20/255). The tint now blends toward a
  pale luminous green (RGB 205,255,165 — the DST ActionQueue hue ratios,
  pushed brighter for Stardew's sunny palette), which lifts queued
  entities clearly above the surrounding grass. Still steady, no pulsing.
- A side-by-side comparison image of every tint formula is saved at
  `glow-color-comparison.png` in the repo root.

## [0.3.25] — 2026-10-25

### Work overlay
- **Glow color retuned to the DST ActionQueue green.** Sampled from the
  reference screenshot, the highlight is a bright yellow-green
  (RGB 160,225,105) blended at 72% — noticeably stronger and more visible
  than the previous 52% lime. Still steady, no pulsing; shading inside the
  sprite stays readable.

## [0.3.24] — 2026-10-25

### Work overlay
- **Double-click "keep same kind" no longer eats same-work tiles.** The
  fine-grained kind key classified by tile contents, so double-clicking a
  plain tilled tile dropped every crop-bearing tile (and double-clicking
  grass dropped the weeds) — same work, different key. The key is now built
  on the work type, splitting further only where things look clearly
  different: tree species, fruit tree species, machine item ID (keg vs
  dehydrator), resource clump type. Crops are no longer split by seed
  (watering corn and watering pumpkin are both watering), grass no longer
  split by grass type.

## [0.3.23] — 2026-10-25

### Work overlay
- **Trees actually glow this time — the hook was on the wrong method.** The
  terrain-feature finder looked for a `draw` overload taking a Vector2
  coordinate; on this game version the real render entry is
  `draw(SpriteBatch)` with the feature reading its own tile (decompiled
  source confirms), so the hook had latched onto `drawInMenu` — a UI method
  that never runs in the world. The finder now matches `draw` whose first
  parameter is a SpriteBatch, and the tile comes from the feature itself.
  Fruit trees, soil/crops, grass and flooring are hooked the same way.

## [0.3.22] — 2026-10-25

### Work overlay
- **Trees glow now.** The entity patches were all applied, but trees draw
  through a SpriteBatch.Draw overload the five hand-written hooks missed
  (XNA has eight). All Draw overloads are now hooked by reflection, so any
  draw path gets tinted.
- **The glow is steady bright green** (ActionQueue-Reborn style), not a
  faint pulsing cast — roughly half-blended toward lime, sprite detail
  still readable.

## [0.3.21] — 2026-10-25

### Work overlay
- **Trees, grass, soil and flooring glow again, on every game version.**
  The five terrain patches failed to apply on the current build (signature
  drift — "Patching exception in method null" in the SMAPI log, so only
  machines/boulders glowed). They now locate their target method by name +
  "has a Vector2 argument" and read arguments positionally, immune to
  signature changes.
- **The glow is a glow, not a repaint.** The tint intensity drops from
  "solid green" to a soft pulsing green light (12–36% blend) so the tree's
  own detail stays readable.
- **The tile being worked right now glows too** (it left the pending list
  the moment work started; now it stays lit until actually finished).

## [0.3.20] — 2026-10-25

### Work overlay
- **Queued entities now glow in their own sprite, pixel-exact.** The
  rectangle tint (which spilled over trees and neighbours) is gone.
  Instead, Harmony hooks each entity's own draw call — trees, fruit trees,
  crops and soil, grass, flooring, machines and objects, boulders and giant
  crops — and multiplies the sprite's colour toward a pulsing green only
  while that exact entity renders. Unselected neighbours stay perfectly
  normal; the glow follows the sprite shape, sway and animation
  frame-by-frame. Patches apply per-class, so a miss on one entity type
  only loses that type's glow (logged), never the walking patch.

## [0.3.19] — 2026-10-25

### Work overlay
- **Selected entities are now tinted green in their actual pixels,
  Don't-Starve-queue style.** Rather than a translucent green sheet drawn
  over the footprint, the tint is multiplied into the entity's texture —
  the tree itself turns green with a soft breathing pulse, while everything
  not queued stays perfectly normal. Footprints still follow the entity
  (tree canopy, machine, boulder bounding box); falls back to the
  translucent sheet if the blend-state switch ever fails.

## [0.3.18] — 2026-10-25

### Work overlay
- **The green highlight now covers the whole entity, Don't-Starve-queue
  style, instead of just the ground tile under it.** Trees tint trunk and
  canopy (3 tiles tall), fruit trees and machines tint their full sprite,
  boulders/meteorites tint their real bounding box, and ground work keeps
  the per-tile tint — so at a glance you can tell exactly which tree,
  machine or clump is still queued.

## [0.3.17] — 2026-10-25

### New: fine-tune the queue while working
- **Every remaining work tile stays highlighted green while the job runs** —
  soil, trees, debris, machines, everything still queued.
- **Ctrl+click a highlighted tile to remove it** from the queue (the green
  disappears and the farmer never goes there). Click a removed tile — or
  something outside the original selection — to add it back / include it;
  it is classified with the tool in hand and inserted next to the nearby
  cluster.
- **Ctrl+double-click a tile to keep only the same kind — at the fine
  granularity.** Double-click an oak and only oaks stay (maple/pine drop
  out); the "kind" follows tree species, fruit tree species, object item
  ID, resource clump type, grass type — falling back to the work type
  (tree/twig/weed/stone…) when a finer key isn't available.
- Dragging while the queue runs still starts a fresh selection as before; a
  click only counts if you don't move past ~1 tile, and a double-click within
  350 ms.

## [0.3.16] — 2026-10-25

### Watering (interim)
- Internal build of the work-overlay groundwork; superseded by 0.3.17.

## [0.3.15] — 2026-10-25

### Watering
- **The spray now anchors like the hoe does.** The hoe walks to whichever
  nearby tile lets one charged swing cover the most; the can was stuck
  spraying from whatever tile came next in the queue — at a dry/wet boundary
  that wasted half the spray on already-watered ground while dry tiles just
  out of reach stayed dry ("move one tile right and it would water them").
  The can now also picks the best nearby anchor (within 6 tiles — the
  unlimited version caused field-crossing zigzags, so the can only shifts
  locally; the hoe keeps its full behaviour).

## [0.3.14] — 2026-10-25

### Watering
- **The watering can now uses the hoe's charge range control.** The hoe never
  tills outside your selection — any charge level whose affected tiles reach a
  workable tile outside the plan is rejected outright, so the charge and
  facing shrink to fit the selected area. The can now does the same: a spray
  shape that would reach still-dry tiles outside the queue disqualifies that
  level/direction entirely. Combined with live dry-tile counting, sprays now
  expand exactly as far as the dry selected area allows — big in the middle,
  trimmed at the edges, never spilling over.

## [0.3.13] — 2026-10-25

### Watering
- **The watering can no longer jumps queue entries to chase the biggest
  single spray.** With live coverage counting, each spray waters neighbouring
  queued tiles for free — but reordering the queue around the "best spray
  spot" moved the spray centre back and forth, leaving whole regions
  spray-starved until the only dry tiles left were across the field. Measured
  on a 432-tile field: 38 of 41 picks jumped 3+ tiles, several crossing the
  entire field (11–12 tiles) more than once. The can now follows the queue's
  geodesic order (a smooth nearest-first sweep) and simply picks the best
  facing per swing; the hoe keeps the widest-swing jump (single-strike, no
  collateral coverage to fight against).

## [0.3.12] — 2026-10-25

### Watering
- **Spray coverage now counts only tiles that are actually still dry.** The
  planner tallied coverage against the remaining queue — but tiles watered
  collaterally by an earlier spray stay in the queue until their turn comes,
  so the count included already-wet tiles. The planner believed a full-power
  spray would water ~10 tiles (worth charging), while on the ground only a
  couple of tiles darkened. Coverage is now re-verified live per tile, so a
  charge level is only taken when it really waters more, facing whichever
  direction covers the most dry tiles.

## [0.3.11] — 2026-10-25

### Watering
- **Removed the "finish everything in one spray" special case.** When the few
  remaining tiles happened to fit inside one iridium 18-tile spray, the old
  branch charged to full power regardless of value — 2.4s of charging to
  water two tiles that two quick splashes would handle ("charges full just to
  water those two, when turning the other way covers the whole dry patch").
  Every spray now goes through the same strict value rule: a higher charge
  level only when it covers strictly more per step, facing whichever
  direction covers the most.

## [0.3.10] — 2026-10-25

### Planning
- **Grass starters on water — actually fixed this time.** 0.3.8 added the
  water check to the execution-time re-check, but the queue builder has its
  own terrain-feature branch that never saw it — water tiles still entered
  the plan and lit up green. The water exclusion (Back + Buildings layers)
  now lives in the queue builder itself, which also skips tiles that already
  hold an object.

### Watering
- **A tie in the charge-level value no longer rounds up.** When covering a
  small leftover cluster, e.g. 3 tiles in a row, the previous rule saw
  "level 2 covers 3, net value 1 — ties with level 0" and took level 2:
  a 1.2s charge to water three tiles one at a time felt like. Net value now
  has to strictly win before a higher charge level is taken; dense fields
  (several tiles of headroom) still charge to full as before.

## [0.3.9] — 2026-10-25

### Placement routing
- **Placing solid objects no longer plans routes through the row it is about
  to fill.** The route simulation treated every planned tile as walkable
  until it was actually placed — so for a two-row band of kegs/dehydrators
  with one sealed end it ordered the second row to start at the far end
  (cheap in the simulation, a full wasted corridor pass in reality once the
  first row sealed itself). Solid placements (kegs, dehydrators, chests,
  sprinklers…) are now treated as walls from the moment they enter the plan,
  so the sweep enters the next row from the end you can actually reach —
  e.g. both rows right-to-left when only the right end is open (measured on
  the reported layout: 120 → 99 walking steps). Passable items (seeds,
  fertilizer, grass starters) are unaffected.

## [0.3.8] — 2026-10-25

### Planning
- **Grass starters no longer planned on water.** The "terrain feature" branch
  of the placement check only verified the tile was empty — a pond tile has no
  object and no terrain feature, so box-selecting over a pond planned every
  water tile (all highlighted green, then walked to and failed). Water tiles
  are now excluded explicitly.

### Watering
- **No more overcharging when coverage saturates.** The 0.3.6 rule picked the
  highest level whose coverage met the marginal bar — but when two levels
  cover the same tiles (e.g. the 3×3 and the 18-tile spray both catch the same
  9 remaining), it took the higher one and burned an extra charge step for
  nothing. The choice now maximises covered-minus-level, preferring the
  higher level only on genuine ties (fewer swings).
- **Refill trips pick the water source by real walking distance — and no
  longer need the pond inside the selection.** The old search only looked at
  water tiles inside the box you dragged, so a field next to a pond stopped
  with "refill failed" the moment the can ran dry. Water sources are now
  searched up to 10 tiles beyond the whole work area (the original selection,
  including finished parts — not the farmer's position when the can empties),
  and among the candidates the one with the fewest real walking steps to its
  edge wins (straight-line nearest can sit across a machine wall).

## [0.3.7] — 2026-10-25

### Walking
- **The farmer now walks diagonally.** The simulated keypresses only ever held
  one direction at a time and the route compressor refused to merge path
  points that weren't in the same row or column, so every diagonal move was
  walked as two separate cardinal stretches — up to twice as slow as a player
  holding two keys, since the engine moves both axes at full speed in the same
  frame. Route legs are now "diagonal first, then straight" (any slope), each
  leg taking max(|dx|, |dy|) steps instead of |dx| + |dy|. Measured on typical
  transfers: 33–50% fewer walking steps in the open, no change in tight
  corridors. A diagonal step is never planned through a pinched corner (both
  side tiles solid — the engine can't move either axis there); one-side-open
  corners slide through, matching player physics.

## [0.3.6] — 2026-10-25

### Watering
- **The watering can no longer charges to full power when it barely pays.**
  On partly-watered or scattered fields the old planner picked whatever power
  covered the most remaining tiles — usually the maximum (2.4s of charging)
  even when the 18-tile spray only caught 1–2 queued tiles. Each extra charge
  level now has to buy at least one additional queued tile (a level costs
  ~600ms of charging and one unit of water; a saved future swing is worth
  about the same), so dense fields still water at full power while sparse
  leftovers water instantly at level 0. Traced in the wild: 19 of 20 swings
  on a scattered 187-tile field charged to maximum.
- **Hoe/watering no longer walks across the field to chase a "best swing".**
  The widest-swing lookahead jumped queue entries to the front for a better
  spray shape without weighing the walk — on scattered patches it ping-ponged
  the farmer 15+ tiles between swings (measured: targets alternating
  (55,·)↔(71,·) across the whole field). The candidate score now subtracts
  the geodesic walking distance, so jumps only happen when the coverage gain
  actually pays for the trip.

## [0.3.5] — 2026-10-24

### Walking
- **The farmer now walks around farm animals, villagers and pets that block
  the path instead of pushing into them forever** (e.g. a pony standing in
  front of the keg row). The stuck handler only recognised monsters, and
  animals live in a different collection the engine still collides you with —
  so a horse in the way meant a permanent stall. When progress toward the next
  waypoint stops and a creature is nearby, the route is recomputed with the
  creature's tiles treated as walls (a real detour), falling back to a
  side-step hop, and finally waiting + a nearby retry when the corridor is
  truly plugged.
- Stuck detection now also measures progress toward the current waypoint, so
  being jostled back and forth between two tiles by an animal no longer
  resets the stall timers.

## [0.3.4] — 2026-10-24

### Routing
- **Every job type now plans its route by real walking distance (BFS geodesic
  from the farmer's actual stand), the same planner machine work has used
  since 0.3.0.** Previously:
  - tilling swept row-by-row with rows ordered by distance from the farmer's
    row and alternating outward — a large field starting mid-field zig-zagged
    up and down the field (measured ~40–45% more walking);
  - axe/pickaxe clearing and the initial scythe order used straight-line
    nearest-neighbour, which jumps through walls (measured ~2.5× more walking
    in corridor layouts like the mines).
- Scythe re-picking between swings now also ranks targets by geodesic
  distance instead of straight-line distance, so it no longer prefers a
  target "close as the crow flies" on the other side of a barn wall.
- **Failed tiles are retried next to where they sit, not at the very end of
  the queue.** A tile that timed out while walking (blocked by a monster or
  a villager) used to be pushed to the queue tail, so after finishing the
  whole area the farmer walked all the way back across the map for one tile.
  It is now re-inserted right after the nearest remaining cluster (within
  ~3 tiles), so the detour is a few steps — and the monster gets the
  processing time of that cluster to move away before the retry.
- **The route no longer strands one end of a curved run until the very end
  ("works the whole C, then comes back for the kegs on the inside").**
  When several targets were equally close, the old tie-break (top-left
  first) made the sweep fan out and then reverse, leaving the other end of
  the run for last — and anything queued behind it was walked past before
  the return trip. The tie-break now continues from the previously picked
  tile, so runs sweep in one direction and finish where the work continues.
  Measured on C-shaped keg walls: end-of-run return jumps gone (largest
  hop 6 tiles → 3), and in a "C followed by a long keg row" layout the
  total walk drops ~6%.
- **The tiles right next to where the job started are finished first.**
  Starting a machine-load run from the middle of a keg wall used to sweep
  one way and leave the block beside the start for the very end — the
  farmer walked the whole warehouse and then came back to the starting
  corner. While any work remains within 2 tiles of the start, the tie-break
  now prefers the nearest-to-start target (the start's neighbourhood is
  cleared before the sweep departs); after that the sweep continues in one
  direction as above. On a ~300-keg warehouse starting mid-wall: total walk
  −9%, and the start's surroundings are done by 1/3 through the run instead
  of near the end.
- **Collection now sweeps along a row instead of hopping between rows.**
  The tie-break used to expand in rings around the start: collect one keg,
  jump to the keg directly opposite in the next row, back to the first row,
  up again... — "a bit here, a bit there". On keg walls served from
  opposite aisles every hop meant walking around the wall (~3 s each).
  Ties near the start are now resolved row-first, so each row is finished
  before crossing to the next. On the warehouse layouts the visible
  up-down-up-down pattern drops by ~95% (row-alternation count 166→4) with
  the same total walking.
- **A failed keg/sprinkler/fertilizer/sapling placement is retried right
  where it failed** (inserted next to its neighbours a few tiles later)
  instead of being collected into a batch that only ran after the entire
  queue was done — the "finished everything, then walked back to place the
  ones inside the C" behaviour.

## [0.3.3] — 2026-10-23

### Sowing
- **Removed double-row sowing.** The option and all of its code
  (paired ordering, pair-aware stand picking, companion sowing) are gone.
  Sowing works exactly as it did in 0.3.0 / 0.3.1: one tile per stop, in the
  normal path-aware order.

## [0.3.1] — 2026-10-23

### Reliability
- **Grass starters / blue grass starters now report honestly.** Planting them
  finished the whole area but the mod claimed "placed 0 tiles" and "300 tiles
  failed". Cause: vanilla puts these two items into `terrainFeatures` (as a
  `Grass`), not into `objects`, while the placement check only looked at
  `objects` — so every tile really was planted yet always verified as failed.
  Placement is now verified by **what actually changed on that tile** (an object
  appeared, or the terrain feature was replaced), the same way machine loading
  and hand-picking are verified.
- **Already-planted tiles are no longer counted as work.** The vanilla
  "can this be placed here" check treats grass and flooring as passable, so a
  tile that already has grass still answered "yes, placeable" — which made the
  selection box green-light a field that was already done. Grass starters, blue
  grass starters and floor/path items now use the same rule vanilla's own
  placement uses: a tile holding an object or a terrain feature is finished.
  Same fix covers **flooring and paths**, which land in `terrainFeatures` too.
- Fixed the summary log saying "planned job: none" for object placement.

## [0.3.0] — 2026-10-22

### Walking & targeting
- **Path-aware routing for machine work**: loading machines, placing objects,
  sowing and watering now plan their route by *actual walking distance through
  corridors* (BFS geodesic from where the farmer is really standing), not
  straight-line distance. In keg walls, spiral/maze layouts and greenhouses the
  farmer now services one machine after another along the corridor instead of
  jumping through walls. Measured on a 216-keg spiral: **~82% less walking**.
- **Scythe machine collection uses the same route** as machine loading
  (collecting kegs/tappers now walks the same natural order as filling them).
  Crop/grass swathing is unchanged.
- Mixed scythe areas (grass + crops + machines) no longer zig-zag between
  rows: hand-collect targets are queued one-by-one along the sweep.

### Mines
- **No more freezing on monsters.** Monsters physically block movement but are
  invisible to pathfinding; the farmer now sidesteps around slimes / rock crabs
  like a player would, and if a corridor is fully blocked the tile is retried
  at the end of the queue instead of stalling for 4 seconds.

### Reliability
- **Machine collection is verified by fact**: if a product could not actually be
  taken (full inventory, etc.) the machine is re-queued instead of being
  silently marked done. Fixes "collects all but 1–2 obvious ones until the
  second pass".
- Machine loading verification (from 0.2.1) kept and regression-tested.

### Config menu
- Re-exposed **"Clear Weeds" for Pickaxe and Axe** (the switches existed but had
  no menu entry, so old configs could not turn them off).
- Removed **"Remove Bushes" (Axe)** — bushes on modern farms are mod content
  (CustomBush etc.) and the vanilla check never matched anything.
- Removed the empty **"Sprinklers"** section header; its two options live under
  "Layout".

## [0.2.1] — 2026-10-22

- Fixed axe/pickaxe "chasing clusters across the map": the widest-swing
  lookahead now only applies to hoe/watering can (tools that really cover an
  area per swing).
- Fixed machines being visited **twice** when loading: drop-in verification now
  checks the machine's actual state (held item / work timer) instead of trusting
  the call's return value alone.
- First path-aware ordering experiment (geodesic-difference heuristic) for
  machine and placement work.

## [0.1.0] — 2026-10-19

- Initial release: drag a rectangle while holding a tool or item and the farmer
  walks the area and completes every job inside it — tilling, watering
  (auto-refill), sowing, fertilizing, scything, chopping, mining, sprinkler
  planning, machine loading and object placement.
- Charged-swing planner (never tills outside your rectangle), per-tool config
  via Generic Mod Config Menu, EN/ZH localization.
