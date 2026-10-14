# Changelog

## 0.2.0

- Woodcutter now clears leftover tree stumps (`StumpsPerRound` setting, 0 to leave them) and measures reach to the trunk instead of the whole tree, so it no longer chops from far away. Default reach is now 2.5m.
- Default repair reach is back down to 6m, and a worker that cannot quite reach a target now accepts being 25% further away instead of 75%.
- Fixed workers dithering and wiggling at chests and machines: the vanilla Dvergr "face the nearest player" behaviour no longer overrides their steering while they work. Workers also escape when wedged and choose clear standing spots.
- Blacksmith tends fuel-making machines first and shares scarce coal between machines.

- Fixed duplicate workers after loading a world: beds and workers are now linked by a persistent id instead of ZDOIDs (which the game reassigns on every load), beds check all existing workers before spawning, and any worker that belongs to no bed, or is a duplicate for its bed, removes itself.
- Added Worker Input Chest and Worker Output Chest pieces (reinforced chest cost), tinted blue and green.
- Workers use every chest of the right kind inside their job radius, closest first, and spread items across several chests.
- Roles can declare which chests they need; until they exist the bed hover text reads "Needs an input/output chest nearby".
- Shared chest trip logic for roles: fetch from the closest input chest holding wanted items, deposit into the closest output chest with room.
- Added the Woodcutter Bed and woodcutter role: fells trees, chops logs, collects wood and delivers it to output chests (`TreesPerRound` setting).
- Added the Blacksmith Bed and blacksmith role: tends charcoal kilns, smelters and blast furnaces (collects product, adds fuel and ore from input chests, delivers coal and metal to output chests). Settings: `Machines`, `FuelFromOutputChests`.
- Chest trips can now take a limited amount (`maxCount`) from, or deposit to, either chest kind.
- Bed hover text shows chests in range for roles that need them, and reports "Needs an output chest nearby", "Output chests are full" or "Inventory full".
- Tool swing animation is shared by all roles; `AnimationTrigger` is now a per-role setting.
- Configurable worker inventory size (`InventoryWidth` / `InventoryHeight`).

## 0.1.0

- Added the Repair Bed, which spawns, respawns and houses a Dvergr repairman.
- Repair worker job loop: finds damaged pieces within the job radius, paths to each and repairs it with a hammer.
- Workers are invincible to players but defend themselves against other enemies.
- A single job radius for both idle wandering and tasks, rest at night, and bed hover text showing the worker's status.
- Configurable job radius, repair interval, reach, respawn delay and more.
