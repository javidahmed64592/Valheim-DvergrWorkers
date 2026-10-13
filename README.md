# Dvergr Workers

Dvergr workers help around your base and defend it when it is under attack.

_Note: This mod is an early work in progress. The initial release only includes a basic repair NPC._

![Dvergr Workers Example](https://github.com/javidahmed64592/Valheim-DvergrWorkers/raw/main/game_screenshot.png)

## Features

- **Repair Bed** - a new buildable piece (Hammer menu, same cost as the basic bed). Building one spawns a Dvergr repairman who lives there. Players cannot claim or sleep in it.
- **Repair job loop** - at a configurable interval, in daylight, the Dvergr picks up a hammer, walks to each damaged piece in range (nearest first, using the game's pathfinding) and repairs it.
- **Stays home** - the Dvergr wanders and works only within one configurable radius of its bed.
- **Respawns** - if the Dvergr is killed or lost, its bed spawns a replacement. Deconstructing the bed removes the Dvergr.
- **Invincible to players, not to monsters** - players can't hurt a worker, but it drops its job and defends itself when enemies approach.
- **Woodcutter Bed** - spawns a Dvergr woodcutter. Each round it fells a few trees inside its job radius, chops the logs, picks up the wood and delivers it to the closest Worker Output Chest. It needs at least one output chest in range, and the bed's hover text shows how many chests it has, and why it stopped if the chests or its own inventory are full.
- **Hover text** - the bed shows the worker's role and what it is doing (idle, resting, walking, repairing).
- **Worker Input / Output Chests** - two new buildable chests (same cost as the reinforced chest), tinted blue and green. Workers need no manual setup: they use every chest of the right kind inside their job radius, closest first, and spread items across several chests. Roles that need chests wait, and say so on the bed's hover text, until they are built. The repair worker needs none; the woodcutter needs an output chest.

## Known limitations

- The woodcutter takes loose wood lying inside its radius, including wood a player dropped there.
- Items a worker is carrying are lost if it dies or unloads mid-trip.

- The Dvergr animator has only crossbow animations, so the hammer swing is a poke and the idle pose stays crossbow-style.
- Workers can occasionally get stuck against obstacles; they hammer from where they stand if close enough, or skip the piece until the next round.

## Roadmap

More roles (farmer, blacksmith, woodcutter, hunter, fisherman and others), and social behaviour such as chatting, sleeping at night and greeting players.
