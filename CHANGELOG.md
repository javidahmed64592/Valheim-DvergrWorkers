# Changelog

## 0.2.0

- Added Worker Input Chest and Worker Output Chest pieces (reinforced chest cost), tinted blue and green.
- Workers use every chest of the right kind inside their job radius, closest first, and spread items across several chests.
- Roles can declare which chests they need; until they exist the bed hover text reads "Needs an input/output chest nearby".
- Shared chest trip logic for roles: fetch from the closest input chest holding wanted items, deposit into the closest output chest with room.
- Configurable worker inventory size (`InventoryWidth` / `InventoryHeight`).

## 0.1.0

- Added the Repair Bed, which spawns, respawns and houses a Dvergr repairman.
- Repair worker job loop: finds damaged pieces within the job radius, paths to each and repairs it with a hammer.
- Workers are invincible to players but defend themselves against other enemies.
- A single job radius for both idle wandering and tasks, rest at night, and bed hover text showing the worker's status.
- Configurable job radius, repair interval, reach, respawn delay and more.
