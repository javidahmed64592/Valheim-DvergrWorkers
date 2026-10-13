# Dvergr Workers

Dvergr workers help around your base and defend it when it is under attack.

![Dvergr Workers Example](https://github.com/javidahmed64592/Valheim-DvergrWorkers/raw/main/game_screenshot.png)

## Features

- **Repair Bed** - a new buildable piece (Hammer menu, same cost as the basic bed). Building one spawns a Dvergr repairman who lives there. Players cannot claim or sleep in it.
- **Repair job loop** - at a configurable interval, in daylight, the Dvergr picks up a hammer, walks to each damaged piece in range (nearest first, using the game's pathfinding) and repairs it.
- **Stays home** - the Dvergr wanders only within a set radius of its bed and only works on pieces within the job radius.
- **Respawns** - if the Dvergr is killed or lost, its bed spawns a replacement. Deconstructing the bed removes the Dvergr.
- **Invincible to players, not to monsters** - players can't hurt a worker, but it drops its job and defends itself when enemies approach.
- **Hover text** - the bed shows the worker's role and what it is doing (idle, resting, walking, repairing).

## Configuration

Settings are in the BepInEx config file `javidahmed64592.dvergrworkers.cfg`.

| Section | Setting | Default | Description |
| --- | --- | --- | --- |
| Workers | JobRadius | 30 | Workers only act on pieces within this distance (m) of their bed. |
| Workers | WanderRadius | 12 | Idle workers stay within this distance (m) of their bed. |
| Workers | RespawnDelay | 30 | Seconds before a lost worker is replaced. |
| Workers | ThreatRadius | 25 | Enemies within this distance (m) make a worker drop its job and defend itself. |
| Workers | WorkAtNight | false | Allow work at night. |
| Repair | RepairInterval | 60 | Seconds between repair rounds. |
| Repair | ReachDistance | 7 | How close (m) the worker needs to be to a piece to hammer it. |
| Repair | AnimationTrigger | (empty) | Override the animator trigger used for the hammer swing. |

## Known limitations

- The Dvergr animator has only crossbow animations, so the hammer swing is a poke and the idle pose stays crossbow-style.
- Workers can occasionally get stuck against obstacles; they hammer from where they stand if close enough, or skip the piece until the next round.

## Roadmap

More roles (farmer, blacksmith, woodcutter, hunter, fisherman and others), input/output chests for workers, and social behaviour such as chatting, sleeping at night and greeting players.

## Building

Set `ValheimDir` and `BepInExProfileDir` in `DvergrWorkers.csproj` (or pass them with `-p:`), then run `dotnet build -c Release`. Requires BepInEx and Jotunn.
