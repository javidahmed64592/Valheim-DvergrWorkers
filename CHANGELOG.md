# Changelog

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
