# Changelog

## 0.5.0

- Added the **Barley Bed**: its Dvergr feeds windmills with barley from the input chest, collects the flour, and delivers it to output chests.
- Added the **Flax Bed**: its Dvergr feeds spinning wheels with flax from the input chest, collects the linen thread, and delivers it to output chests.
- Both take the farmer's barley and flax from output chests, so the farm, the mill and the spinning wheel run without you moving anything.
- Workers that use the same item now split the stock in the chests equally instead of one depleting it, so the farmer can replant barley or flax while the barley or flax worker processes the rest.
- Added settings for which windmills and spinning wheels they tend.

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
