# Dvergr Workers

Dvergr workers help around your base and defend it when it is under attack:

- **Repair Worker** - fixes damaged structures around the base.
- **Woodcutter Worker** - fells trees and collects wood.
- **Blacksmith Worker** - manages smelters, kilns, and blast furnaces.
- **Refueler Worker** - keeps torches, fires, and braziers topped up with fuel.
- **Farmer Worker** - harvests crops and plants your tilled soil.
- **Tree Farmer Worker** - plants trees from seeds.

_Note: This mod is an early work in progress!_

![Repair Worker](https://github.com/javidahmed64592/Valheim-DvergrWorkers/raw/main/images/repair.png)
![Woodcutter Worker](https://github.com/javidahmed64592/Valheim-DvergrWorkers/raw/main/images/woodcutter.png)
![Blacksmith Worker](https://github.com/javidahmed64592/Valheim-DvergrWorkers/raw/main/images/blacksmith.png)

## How It Works

Dvergr workers each have a job to do around the base.
They have designated beds that serve as their spawning point and their work area.
They use chests for storing resources and materials needed for their tasks.
They even fend off enemies attacking the base!
Players cannot hurt them, and they rest at night instead of working.

### Roles

Each job loop:

- **Repair Worker** - finds all damaged pieces within its work area and repairs them.
- **Woodcutter Worker** - fells trees, chops logs, clears leftover stumps, and collects wood within its work area.
- **Blacksmith Worker** - adds wood to kilns, refuels smelters and blast furnaces before adding ores/scrap to them, collects the finished products and delivers them to the output chests.
- **Farmer Worker** - holds a cultivator, harvests ripe crops growing on tilled soil and plants every free tilled spot within its work area. It never tills, so you decide where the farm is. It plants seeds from the input chest, spreading crops evenly, and plants raw ingredients (carrots, turnips...) to make more seeds when a crop's seeds run low. Each crop is only planted in the biome it grows in, and tree seeds are ignored. Crops go to the output chests and seeds back to the input chest.
- **Tree Farmer Worker** - holds a cultivator and plants tree saplings from the seeds, cones, and acorns in the input chest on suitable ground within its work area. It plants species evenly, only where each grows, keeps well clear of buildings and farmland, and stops once the area holds a configurable number of trees. The woodcutter collects the seeds that felled trees drop, so the two keep a forest going.
- **Refueler Worker** - carries a Dvergr lantern and tops up the torches, fires, and braziers you have built with resin, wood, and coal from the input chest, returning any spare fuel afterwards.

### Beds

Each role has a buildable bed that spawns the corresponding worker.
The worker will perform its tasks automatically within its designated area (a configurable radius around its bed).
Workers will respawn if they die as long as their bed remains intact, and destroying the bed will despawn the worker.
The bed's hover text indicates the worker's status (e.g., idle, working, resting, or waiting for resources).

### Chests

The workers use the Worker Input Chest and Worker Output Chest (buildable, same cost as the reinforced chest) to store and retrieve materials needed for their tasks.
These chests must be within the worker's job radius to be used.
Currently, the workers share input and output chests within their job radius.
This may be changed in the future to allow for more complex chest management or individual chest assignments.

Workers that need chests will not begin their jobs until they are built within the job radius.
If the output chests are full, the worker stops and the bed's hover text says so.

Workers also pass items along to each other.
If another worker nearby makes an item, a worker that needs it will take it from the output chests: the blacksmith takes the woodcutter's wood for the kilns, and the refueler takes the blacksmith's coal.
Items no worker produces are never taken from output chests, so your finished goods stay put.

| Role | Input Chest | Output Chest |
|------|-------------|--------------|
| Repair Worker | ❌ | ❌ |
| Woodcutter Worker | ❌ | ✅ |
| Blacksmith Worker | ✅ | ✅ |
| Refueler Worker | ✅ | ❌ |
| Farmer Worker | ✅ | ✅ |
| Tree Farmer Worker | ✅ | ❌ |

## Configuration

Settings are in the BepInEx config file `javidahmed64592.dvergrworkers.cfg`.
You can change the work radius, how often each role runs, how close workers need to get to their targets, how fast they respawn, whether they work at night, their inventory size, and whether workers take each other's items from output chests.

## Known limitations

- The Dvergr workers do not have animations for their specific tasks so the visual representation may not accurately reflect their actions.
- Items a worker is carrying are lost if it dies or unloads mid-trip.
- Occasionally workers may get stuck or be prevented from reaching their work stations. If a worker cannot quite get close enough to a target, it will work from a little further away than its normal reach. Clear pathing around the base minimises how often this happens!

## Roadmap

More roles (a farmer that plants on soil you have tilled, a tree farmer, and barley and eitr workers) and social behaviour such as chatting and greeting players.
