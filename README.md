# Dvergr Workers

Dvergr workers help around your base and defend it when it is under attack:

- **Repair Worker** - fixes damaged structures around the base.
- **Woodcutter Worker** - fells trees and collects wood.
- **Blacksmith Worker** - manages smelters, kilns, and blast furnaces.

_Note: This mod is an early work in progress!_

![Repair Worker](https://github.com/javidahmed64592/Valheim-DvergrWorkers/raw/main/images/repair.png)
![Woodcutter Worker](https://github.com/javidahmed64592/Valheim-DvergrWorkers/raw/main/images/woodcutter.png)
![Blacksmith Worker](https://github.com/javidahmed64592/Valheim-DvergrWorkers/raw/main/images/blacksmith.png)

## How It Works

Dvergr workers each have a job to do around the base.
They have designated beds that serve as their spawning point and their work area.
They use chests for storing resources and materials needed for their tasks.
They even fend off enemies attacking the base!

### Roles

Each job loop:

- **Repair Worker** - finds all damaged pieces within its work area and repairs them.
- **Woodcutter Worker** - fells trees, chops logs, clears leftover stumps, and collects wood within its work area.
- **Blacksmith Worker** - adds wood to kilns, refuels smelters and blast furnaces before adding ores/scrap to them, collects the finished products and delivers them to the output chests.

### Beds

Each role has a buildable bed that spawns the corresponding worker.
The worker will perform its tasks automatically within its designated area (a configurable radius around its bed).
Workers will respawn if they die as long as their bed remains intact, and destroying the bed will despawn the worker.
The bed's hover text indicates the worker's status (e.g., idle, working, resting, or waiting for resources).

### Chests

The workers use input and output chests to store and retrieve materials needed for their tasks.
These chests must be within the worker's job radius to be used.
Currently, the workers share input and output chests within their job radius.
This may be changed in the future to allow for more complex chest management or individual chest assignments.

Workers that need chests will not begin their jobs until they are built within the job radius.
If the output chests are full, the worker stops and the bed's hover text says so.

Workers also pass items along to each other.
If another worker nearby makes an item, a worker that needs it will take it from the output chests: the blacksmith takes the woodcutter's wood for the kilns, and the refueler takes the blacksmith's coal.

| Role | Input Chest | Output Chest |
|------|-------------|--------------|
| Repair Worker | ❌ | ❌ |
| Woodcutter Worker | ❌ | ✅ |
| Blacksmith Worker | ✅ | ✅ |
| Refueler Worker | ✅ | ❌ |

## Known limitations

- The Dvergr workers do not have animations for their specific tasks so the visual representation may not accurately reflect their actions.
- Items a worker is carrying are lost if it dies or unloads mid-trip.
- Occasionally workers may get stuck or be prevented from reaching their work stations. If a worker cannot quite get close enough to a target, it will work from a little further away than its normal reach. Clear pathing around the base minimises how often this happens!

## Roadmap

More roles, social behaviour such as chatting, sleeping at night and greeting players.
