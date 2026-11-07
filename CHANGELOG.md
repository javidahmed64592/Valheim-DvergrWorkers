# Changelog

## 0.13.0

Workers no longer drop their task just because an enemy is around.

- A worker only stops working to defend the base when an enemy actually hurts someone close by: the worker itself, another worker or a player within `ThreatRadius`. An enemy that is merely nearby, or that has spotted someone but not attacked, no longer interrupts anyone, so a neck in the river or a greyling at the edge of the base leaves the work going. Damage that does not come from an enemy, such as falling, no longer interrupts a worker either. This works the same in multiplayer, whichever player's game is running the worker.
- The training dummy is no longer treated as an enemy, so a worker next to one keeps working and does not attack it.
- When a worker's round is cut short, the reason is written to the BepInEx log (which enemy hurt whom, how long ago and how far away, or nightfall).
- Workers stay out of water. Their paths never lead into a lake, river or the sea, not even shallow water, and they do not pick spots in water to stand on while working or strolling. A worker that ends up in the water anyway (knocked back by an enemy, say) climbs straight out at the nearest dry ground.
- Workers give up on far fewer tasks. When the game had not yet worked out paths around a place (it does that on demand, which takes a few seconds), a worker would take the missing path as "arrived", find itself nowhere near, and write the spot off as unreachable for five minutes. Now it waits for the path, then heads straight there if none comes. Long walks are also given time in proportion to their length instead of a fixed 25 seconds.
- The Woodcutter Worker is much keener:
  - It goes for whatever is closest: a tree, a log or wood on the ground. It no longer walks past standing trees to reach a log or stump on the far side of its area. It still finishes chopping up and collecting a tree it felled, and it fells trees before clearing stumps.
  - A tree it could not find a way to is tried again after a few seconds, instead of being skipped for the rest of the round.
  - If an output chest will not take some of its wood, it keeps that wood and carries on cutting, trying again once its inventory is nearly full. Before, it gave up the whole round, and as it started every round by delivering, it could end up never cutting at all.
  - When a round ends because it has felled `TreesPerRound` trees, the next round starts after 15 seconds instead of a whole task interval.
- When a worker cannot get to a chest, its bed now says so ("Could not find a way to an output chest") instead of claiming the chests are full.
- Removed the `NoticeRadius` config option, which no longer has any effect.

## 0.12.0

Workers now tidy up and use items lying on the ground, so you no longer have to carry everything to the input chest.

- The Blacksmith, Barley, Flax and Eitr Workers use input items lying right next to their machines, e.g. ore dropped by a smelter. The Refueler Worker does the same with fuel lying next to the fire it is refuelling. Items on the ground are used before those in chests, so anything you drop does not linger. They only pick up as many as the machine or fire will take on that visit; the rest stays on the ground for later. New config option: `GroundPickupRadius` (metres from the machine's or fire's edge, default 4, up to 8, 0 turns it off). Keep it small so workers leave alone items dropped for other reasons, like wood while building.
- The Farmer Worker tidies up seeds and crops lying on tilled soil in its job area. Seeds are planted before anything else and spares go to the input chest; crops such as carrots and turnips are planted when their seeds run low and otherwise go to the output chest. New config option: `[Farmer] PlantLooseSeeds`.
- The Tree Farmer Worker picks up tree seeds, cones and acorns lying in its job area near somewhere it would plant a tree itself, so a stack dropped on a path next to open ground is still used. It plants as many as it can, nearest where they lay first, and returns the rest to the input chest. Seeds with no good ground nearby (deep inside the base, on farmland...) are left alone. New config options: `[TreeFarmer] PlantLooseSeeds` and `[TreeFarmer] LooseSeedRadius` (default 10 metres).
- Workers without a swinging tool (Blacksmith, Barley, Flax, Eitr and Refueler Workers) now play the use animation when they pick items up, the same one they play when filling a machine.

## 0.11.2

- Fixed the game freezing for a few seconds at a time with several workers out. The main causes were workers searching for a spot to stand next to targets they could not reach (such as a damaged roof), and the bed searching the whole world for its worker in a single frame. Path searches are now capped, unreachable targets are remembered instead of being retried every round, and the bed's search is spread over several frames.
- Idle wandering, planting and fuel planning now do far fewer path and chest checks per frame.
- The Refueler Worker now also refuels hot tubs and stone ovens with wood, and shield generators (including the Home Bubble mod's bubbles) with bone fragments or charred bones, alongside torches, fires and braziers.

## 0.11.1

- The Tree Farmer Worker now only plants trees in dirt and grass, not stone-paved paths or cultivated soil.
- Workers are no longer spooked by every enemy within range. They only drop their task for an enemy that is close, or that is hunting them or the player, and then go back to work shortly after instead of waiting for the next task interval. New config options: `NoticeRadius` and `ThreatRetryDelay`.

## 0.11.0

- The Eitr Worker now taps sap extractors in its job area: it empties them, picks up the sap that drops, tops up the eitr refineries with it, and stores any spare sap in a chest.
- Restructured the codebase for better maintainability.

## 0.10.1

- Updated the BepInExPack_Valheim dependency to 5.4.2360.

## 0.10.0

- Idle workers now wander deliberately instead of using the vanilla idle AI: they stand still for a while, then stroll to a reachable, open spot, favouring spots near their bed.
- Idle workers step out of the player's way and no longer walk up to and surround them.
- Fixed idle workers wiggling in place: they only turn to face a nearby player while standing still, never while walking.
- Workers now only drop their idle behaviour when an enemy is within `ThreatRadius` or they have been hurt.
- Fixed mages holding a staff at the same time as their tool, and going to bed with a staff in hand: the tool slot is now kept free of other weapons while a tool is out, and weapons stay put away while a worker is in bed. Stashed weapons also survive a second task in the same round. Workers without a tool (barley, flax, blacksmith...) now put their staff away while working too.
- Added `WanderRadius`, `PauseMin`, `PauseMax` and `PersonalSpace` settings under `[Idle]`.

## 0.9.0

- Added an `.editorconfig`, `Directory.Build.props` and a CI workflow that runs `dotnet format`, a Release build and a NuGet vulnerability check.
- Reformatted the code and fixed analyzer warnings to match the new code style rules (no behaviour change).
- Tidied up packaging scripts and workflow files.
- Added a `.gitattributes` to keep line endings consistent.
- Moved the local Valheim install paths into `Directory.Build.props`; set `VALHEIM_DIR` and `BEPINEX_PROFILE_DIR` once to build every mod without editing the `.csproj`.
- Updated README and icon.

## 0.8.1

- Workers no longer damage player-built structures when attacking enemies.

## 0.8.0

- Workers now use the player's own animations when working: the repairer swings the hammer, the woodcutter swings the axe, the farmers use the cultivator, and the machine workers use the player's interact animation.
- Removed the `AnimationTrigger` setting from every role, as workers always use the vanilla tool for their job.

## 0.7.0

- Workers now come in a variety of Dvergr looks. Each bed picks one at random the first time it spawns its worker (the original crossbow Dvergr, or one of the four mages) and the worker keeps that look every time it respawns.
- Workers from earlier versions are unaffected. Rebuild your beds to get new Dvergrs!

## 0.6.0

- Workers now go to bed at night: when it is time to rest, the Dvergr walks to its bed, lies down on it and stays there until morning. The bed shows "Sleeping" while it does.
- Workers put their weapons away when they get into bed and take them back when they wake up.
- A sleeping worker still gets up to defend itself if an enemy comes near or it is hurt, and returns to bed afterwards.
- Workers get into bed from whichever side is closest. One that cannot get within reach of its bed retries once a minute, up to 5 times, then rests where it is for the night.
- The woodcutter now also picks up the resin and feathers that fall out of trees and delivers them to output chests.

## 0.5.2

- Workers now look for chests which contain the item they are depositing, then for an empty chest to start a new category in, and fall back to the nearest chest only if neither is found. They deposit one kind of item at a time so different items are not mixed together. This helps keep chests better organised.

## 0.5.1

- Workers no longer keep walking up to places they cannot reach. The farmer and tree farmer check that a planting spot can be walked to (so nothing is planted behind a wall or fence), and all workers remember targets they gave up on for a few minutes. The game builds its navigation mesh a few seconds after a place is first checked, so a spot that fails once is checked again before it is written off, and the farmers look again within seconds.
- Updated bed descriptions.
- Removed the `Machines` and `Fuels` settings: workers now always tend the vanilla machines and use the vanilla fuels.
- The refueler now uses every vanilla fuel, including greydwarf eyes and guck, so it also tends the blue and green iron torches and the blue brazier.

## 0.5.0

- Added the **Barley Bed**: its Dvergr feeds windmills with barley from the input chest, collects the flour, and delivers it to output chests.
- Added the **Flax Bed**: its Dvergr feeds spinning wheels with flax from the input chest, collects the linen thread, and delivers it to output chests.
- Both take the farmer's barley and flax from output chests, so the farm, the mill and the spinning wheel run without you moving anything.
- Added the **Eitr Bed**: its Dvergr feeds eitr refineries with sap and soft tissue from the input chest, collects the eitr, and delivers it to output chests.
- Workers that use the same item now split the stock in the chests equally instead of one depleting it, so the farmer can replant barley or flax while the barley or flax worker processes the rest.
- Workers now drop what they are carrying when they die or their bed is removed, and keep it when the world is saved and reloaded mid-trip.
- Added settings for which windmills, spinning wheels and eitr refineries they tend.

## 0.4.0

- Added the **Farmer Bed**: its Dvergr holds a cultivator, harvests ripe crops on your tilled soil, and plants every free spot, spreading the crops evenly. It uses seeds from the input chest, plants raw ingredients (carrots, turnips...) to make more seeds when they run low, respects each crop's biome, and ignores tree seeds. Crops go to output chests and seeds back to the input chest.
- Added the **Tree Farmer Bed**: its Dvergr holds a cultivator and plants tree saplings from seeds, cones and acorns in the input chest. It plants species evenly, only in biomes where they grow, keeps clear of buildings and farmland, and stops at a configurable tree limit.
- The woodcutter now also collects the seeds felled trees drop, so the tree farmer can use them.
- Added settings for the farmer's seed reserve and how many of a crop it plants per trip.

## 0.3.0

- Added the **Refueler Bed**: its Dvergr carries a Dvergr lantern and tops up torches, fires, braziers and hearths with resin, wood and coal from the input chest, returning any spare fuel afterwards.
- Workers now pass items to each other through output chests: the blacksmith takes the woodcutter's wood for the kilns, and the blacksmith and refueler can use the blacksmith's coal. Only items another nearby worker makes are taken (`TakeFromOutputChests` setting), replacing the blacksmith's `FuelFromOutputChests` setting.
- Added settings for which fuels the refueler uses and how low a fire must burn before it is topped up.

## 0.2.0

- Added the **Woodcutter Bed**: its Dvergr fells trees, chops logs, clears leftover stumps, and delivers the wood to output chests.
- Added the **Blacksmith Bed**: its Dvergr feeds charcoal kilns, smelters and blast furnaces from input chests, collects the finished coal and metal, and delivers it to output chests. Coal from the output chests can be used as fuel.
- Added **Worker Input Chest** and **Worker Output Chest**. Workers use every chest of the right kind within their job radius, closest first.
- Beds now show the worker's status, including which chests are missing or full.
- Added settings for worker inventory size, trees and stumps per round, and which machines the blacksmith tends.

## 0.1.0

- Added the **Repair Bed**: its Dvergr walks around the base and repairs damaged pieces with a hammer.
- Workers respawn if killed, stay within a set radius of their bed, rest at night, and are invincible to players while still defending themselves against enemies.
- Added settings for job radius, repair interval, reach and respawn delay.
