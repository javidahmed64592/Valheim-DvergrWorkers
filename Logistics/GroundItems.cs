using System;
using System.Collections.Generic;

using DvergrWorkers.Movement;

using UnityEngine;

namespace DvergrWorkers.Logistics;

/// <summary>
/// Items lying loose on the ground that a worker may use instead of fetching from a chest, e.g. ore dropped next
/// to a smelter or seeds dropped on tilled soil.
/// </summary>
internal static class GroundItems
{
    /// <summary>No machine or fire is anywhere near this big, so drops farther from its centre skip the collider query.</summary>
    private const float SearchSlack = 6f;

    /// <summary>Loose drops of this item. Items placed as build pieces (decorations) are part of the base, not supplies.</summary>
    public static IEnumerable<ItemDrop> Loose(Func<ItemDrop.ItemData, bool> match)
    {
        foreach (ItemDrop drop in ItemDrop.s_instances)
        {
            if (drop != null && drop.m_itemData != null && !drop.IsPiece() && match(drop.m_itemData))
            {
                yield return drop;
            }
        }
    }

    /// <summary>
    /// Drops of this item within <see cref="WorkerConfig.GroundPickupRadius"/> of the edge of a piece (a machine,
    /// a fire...). Nothing when the setting is 0.
    /// </summary>
    public static IEnumerable<ItemDrop> Near(GameObject piece, string sharedName)
    {
        float radius = WorkerConfig.GroundPickupRadius.Value;
        if (radius <= 0f || piece == null)
        {
            yield break;
        }

        Vector3 centre = piece.transform.position;
        float coarse = (radius + SearchSlack) * (radius + SearchSlack);
        foreach (ItemDrop drop in ItemDrop.s_instances)
        {
            // Cheapest test first: there can be hundreds of drops in the world.
            if (drop == null || (drop.transform.position - centre).sqrMagnitude > coarse
                || drop.m_itemData == null || drop.m_itemData.m_shared.m_name != sharedName || drop.IsPiece())
            {
                continue;
            }

            Vector3 pos = drop.transform.position;
            if (Vector3.Distance(pos, WorkerNavigator.ClosestPointOnColliders(piece, pos)) <= radius)
            {
                yield return drop;
            }
        }
    }

    public static int CountNear(GameObject piece, string sharedName)
    {
        int total = 0;
        foreach (ItemDrop drop in Near(piece, sharedName))
        {
            total += drop.m_itemData.m_stack;
        }
        return total;
    }

    /// <summary>The first drop of this item near the piece, or null.</summary>
    public static ItemDrop FirstNear(GameObject piece, string sharedName)
    {
        foreach (ItemDrop drop in Near(piece, sharedName))
        {
            return drop;
        }
        return null;
    }

    /// <summary>
    /// Takes up to <paramref name="wanted"/> items from a drop into the worker's inventory, leaving the rest of the
    /// stack on the ground. Returns how many were taken; 0 while still waiting for ownership of the drop, so call
    /// it again a moment later.
    /// </summary>
    public static int Take(Humanoid worker, ItemDrop drop, int wanted = int.MaxValue)
    {
        // Drops are owned by whoever last touched them; ask for ownership and retry for a moment.
        drop.RequestOwn();
        ZNetView view = drop.GetComponent<ZNetView>();
        if (view == null || !view.IsValid() || !drop.CanPickup(false))
        {
            return 0;
        }

        drop.Load();
        Inventory carried = worker.GetInventory();
        ItemDrop.ItemData data = drop.m_itemData;
        int stack = data.m_stack;
        int room = carried.FindFreeStackSpace(data.m_shared.m_name, data.m_worldLevel)
            + (carried.GetEmptySlots() * data.m_shared.m_maxStackSize);
        int take = Math.Min(Math.Min(wanted, stack), room);
        if (take <= 0)
        {
            return 0;
        }

        if (take >= stack)
        {
            return worker.Pickup(drop.gameObject, false, false) ? stack : 0;
        }

        ItemDrop.ItemData part = data.Clone();
        part.m_stack = take;
        if (!carried.AddItem(part))
        {
            return 0;
        }
        drop.SetStack(stack - take);
        return take;
    }
}
