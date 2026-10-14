The goal is to add worker NPCs to the game to help maintain player base, defend against attacks, and make the game feel a bit more alive.

The decompiled game is in this workspace `/home/javid/SecondarySSD/Programming/GitHub/valheim-decompiled`.

## Core Design

- The workers will be Dvergr NPCs.
- Dvergers in game are passive and friendly towards players unless if attacked in the vanilla game. In the mod, they should be invincible towards player attacks, and they should maintain their defensive behaviour when attacked by other enemies.
- Alongside their vanilla behaviour, they should also complete various tasks around the base (outlined below).
- They should be spawned in by building dedicated beds for them.
- They should have dedicated inventory management systems, including chests for storing input items for jobs, and outputs from jobs.
- As a nice to have, they should feel autonomous and like they are part of the community. In between jobs, it would be cool if they wandered around, spoke to each other, and said specific voice lines when a player approaches such as to indicate an input resource running low or to greet the player.

## Tasks

- A repair NPC that goes around with a build hammer and repairs any damaged pieces - this is the only required one for the MVP. The rest are extensions.
- A woodcutter NPC that chops down trees and stores the logs in chests
- A blacksmith NPC that smelts ore and makes charcoal from the kiln
- A refueler NPC that refuels torches, fires and more with resin, coal and wood
- A farmer NPC that harvests and plants crops (food specifically)
- A tree farmer NPC that plants trees
- A barley NPC that puts barley into windmills, and the processed barley flour into chests
- An eitr NPC that puts sap and soft tissue into an eitr refinery, and the processed eitr into chests

## Spawning

- Workers are spawned by building dedicated beds for them.
- There should be one new bed as a buildable piece per role i.e. "Repair Bed", "Farmer Bed", "Blacksmith Bed", "Barley Bed", "Eitr Bed", "Woodcutter Bed", "Hunter Bed", "Fisherman Bed"
- These beds should use the existing (lowest tier) vanilla bed but modify its behaviour
- The beds should automatically spawn the corresponding worker NPC when built.
- The workers should automatically respawn at their corresponding beds if they are killed or otherwise removed from the game.
- The workers should not wander beyond a configurable radius from their corresponding beds.
- When completing tasks, all tasks should be done within the configurable radius from their corresponding beds (perhaps with a small tolerance for slightly exceeding the radius)
- Players should not be able to claim or sleep in these beds
- The text overlay when hovering over these beds should be customised to indicate the role of the worker NPC associated with the bed and their current task status (in progress, resting etc.)

## Inventory Management

- Workers should have a dedicated inventory for storing items related to their tasks.
- Any tools required for their tasks should just be stored as a non-physical item so they always have access to tools. Their tools should be indestructible.
- They should have a configurable inventory size so that they can retrieve input resources and process them into output resources.
- Two new dedicated chests for input and output resources should be added as buildable pieces. These should use the same requirements as the existing vanilla iron chest.
- The input chest should be used to store raw materials required for the worker's tasks, while the output chest should store the finished products. Workers retrieve from input chests and deposit into output chests.
- It should be easy to define which worker roles require which types of chest, since not all will require both.
- It should be easy to build these chests and assign them to the workers, and this is a pre-requisite before they start their task loop.
- If their input chest is empty, the worker should wait until it is replenished before continuing with their tasks.
- Workers should be able to handle multiple input and output chests if necessary, distributing their resources accordingly.
- Workers should prioritise using the closest available chest to minimise travel time and improve efficiency.

## General Behaviour

- Workers should perform their tasks periodically. This time period should be configurable per worker role.
- When it is time to perform the task, the worker should stop what it is doing and walk towards the location where the task needs to be performed.
- The worker should then perform the task and behave like a player in terms of walking and interacting with resources.
- The worker should also hold any necessary tools or items required for the task they are performing.
- Once the task is completed, the worker should return to an idle state and wait for the next task period.
- This idle state should reflect "socialising" and also "resting" (such as sleeping at night). In fact, workers should not complete tasks during night.
