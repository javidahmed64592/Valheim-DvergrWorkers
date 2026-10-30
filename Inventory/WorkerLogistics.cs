using System;
using System.Collections.Generic;

using UnityEngine;

namespace DvergrWorkers;

internal enum LogisticsResult
{
    Moving = 0,
    /// <summary>Arrived and moved whatever could be moved (see <see cref="WorkerLogistics.Transferred"/>).</summary>
    Done = 1,
    /// <summary>Could not reach the chest; see <see cref="WorkerLogistics.Reason"/>.</summary>
    Failed = 2,
}

/// <summary>
/// Trips between a worker and its chests: fetch wanted items from the closest input chest that has
/// some, or deposit items into the closest output chest that has room. A role runs a trip by calling
/// <see cref="BeginFetch"/> or <see cref="BeginDeposit"/> and then <see cref="Step"/> each frame.
/// Repeat the trip until it transfers nothing to spread items across several chests.
/// </summary>
internal sealed class WorkerLogistics(Humanoid humanoid, WorkerNavigator navigator, Func<Vector3> home, Func<float> radius)
{
    private readonly Humanoid _humanoid = humanoid;
    private readonly WorkerNavigator _navigator = navigator;
    private readonly Func<Vector3> _home = home;
    private readonly Func<float> _radius = radius;
    private readonly Transform _transform = humanoid.transform;

    private WorkerChest _chest;
    private Func<ItemDrop.ItemData, bool> _filter;
    private bool _fetching;

    /// <summary>For a deposit, the kinds of item (by shared name) that go into the chosen chest; others stay for the next trip.</summary>
    private HashSet<string> _onlyNames;
    private int _limit;

    /// <summary>Why the last Begin* returned false or a trip failed.</summary>
    public string Reason { get; private set; } = "";

    /// <summary>Number of items moved by the last completed trip.</summary>
    public int Transferred { get; private set; }

    /// <summary>The worker's own inventory.</summary>
    public Inventory Carried => _humanoid.GetInventory();

    public bool HasChest(ChestKind kind)
    {
        return WorkerChest.Find(kind, _home(), _radius(), _transform.position).Count > 0;
    }

    /// <summary>How many items matching <paramref name="filter"/> sit in chests of this kind in range.</summary>
    public int CountInChests(ChestKind kind, Func<ItemDrop.ItemData, bool> filter)
    {
        int total = 0;
        foreach (WorkerChest chest in WorkerChest.Find(kind, _home(), _radius(), _transform.position))
        {
            foreach (ItemDrop.ItemData item in chest.Container.GetInventory().GetAllItems())
            {
                if (filter(item))
                {
                    total += item.m_stack;
                }
            }
        }
        return total;
    }

    /// <summary>
    /// Whether an item in an output chest is fair game for this worker: another worker nearby makes it,
    /// so it is meant to be passed on. Anything else in an output chest belongs to the player.
    /// </summary>
    public bool MayTakeFromOutput(ItemDrop.ItemData item)
    {
        return WorkerConfig.TakeFromOutputChests.Value
            && WorkerRoles.IsProducedNearby(item.m_shared.m_name, _home(), _radius() * 3f);
    }

    /// <summary>How many matching items can be fetched from chests: input chests plus Coordinated output chests.</summary>
    public int CountAvailable(Func<ItemDrop.ItemData, bool> match)
    {
        return CountInChests(ChestKind.Input, match)
            + CountInChests(ChestKind.Output, i => match(i) && MayTakeFromOutput(i));
    }

    /// <summary>
    /// How much of an item this worker may still take from the chests. When several workers use the same item
    /// they split the whole stock (chests plus what each is carrying) equally, so none of them can deplete
    /// it. That leaves the rest for the others, for example the farmer replanting barley while the barley
    /// worker mills it.
    /// </summary>
    public int AvailableTo(string sharedName)
    {
        int inChests = CountAvailable(i => i.m_shared.m_name == sharedName);
        if (inChests <= 0)
        {
            return 0;
        }

        WorkerBrain.CountConsumers(sharedName, _home(), _radius() * 3f, out int workers, out int carriedByAll);
        if (workers <= 1)
        {
            return inChests;
        }

        int pool = inChests + carriedByAll;
        int share = (pool + workers - 1) / workers;
        int mine = Carried.CountItems(sharedName);
        return Math.Min(inChests, Math.Max(0, share - mine));
    }

    /// <summary>Fetches from input chests first, then from output chests holding items another worker produced.</summary>
    public bool BeginFetchAny(Func<ItemDrop.ItemData, bool> match, int maxCount = int.MaxValue)
    {
        if (CountInChests(ChestKind.Input, match) > 0 && BeginFetch(match, maxCount, ChestKind.Input))
        {
            return true;
        }

        bool Coordinated(ItemDrop.ItemData i)
        {
            return match(i) && MayTakeFromOutput(i);
        }

        if (CountInChests(ChestKind.Output, Coordinated) > 0)
        {
            return BeginFetch(Coordinated, maxCount, ChestKind.Output);
        }

        Reason = "No chest with the items needed";
        return false;
    }

    /// <summary>
    /// Starts a trip to the closest chest of the given kind (input by default) holding wanted items,
    /// moving at most <paramref name="maxCount"/> of them. False if there is none.
    /// </summary>
    public bool BeginFetch(Func<ItemDrop.ItemData, bool> wanted, int maxCount = int.MaxValue, ChestKind from = ChestKind.Input)
    {
        _limit = maxCount;
        return Begin(from, wanted, fetching: true, "No " + from.ToString().ToLowerInvariant() + " chest with the items needed");
    }

    /// <summary>
    /// Starts a trip to the closest chest of the given kind (output by default) that has room for
    /// something carried that matches the filter.
    /// </summary>
    public bool BeginDeposit(Func<ItemDrop.ItemData, bool> filter, ChestKind to = ChestKind.Output)
    {
        _limit = int.MaxValue;
        return Begin(to, filter, fetching: false, to == ChestKind.Output ? "Output chests are full" : "Input chests are full");
    }

    public LogisticsResult Step(float dt)
    {
        if (_chest == null || !_chest.IsUsable)
        {
            Reason = "Chest disappeared";
            return LogisticsResult.Failed;
        }

        switch (_navigator.Step(dt))
        {
            case NavResult.Moving:
                return LogisticsResult.Moving;

            case NavResult.Failed:
                Reason = "Could not reach the chest";
                _chest = null;
                return LogisticsResult.Failed;
        }

        Transferred = Transfer();
        _chest = null;
        return LogisticsResult.Done;
    }

    // ------------------------------------------------------------ internals

    private bool Begin(ChestKind kind, Func<ItemDrop.ItemData, bool> filter, bool fetching, string noneReason)
    {
        _filter = filter;
        _fetching = fetching;
        _onlyNames = null;
        Transferred = 0;

        List<WorkerChest> chests = WorkerChest.Find(kind, _home(), _radius(), _transform.position);
        if (chests.Count == 0)
        {
            Reason = "Needs " + (kind == ChestKind.Input ? "an input" : "an output") + " chest nearby";
            return false;
        }

        if (fetching)
        {
            foreach (WorkerChest chest in chests)
            {
                if (!chest.Container.IsInUse() && HasSomethingToMove(chest, null, null) && TryGoTo(chest, null))
                {
                    return true;
                }
            }
        }
        else if (BeginDeposit(chests))
        {
            return true;
        }

        Reason = noneReason;
        return false;
    }

    /// <summary>
    /// Plans one deposit trip, one kind of item at a time, so chests stay organised. For each kind carried, the
    /// worker looks for a chest that already holds it (even if it is further away), then for an empty chest to
    /// start a new category in, and only then for the nearest chest with room.
    /// </summary>
    private bool BeginDeposit(List<WorkerChest> nearestFirst)
    {
        // Largest stacks first, so the bulkiest cargo is dealt with first.
        Dictionary<string, int> counts = [];
        foreach (ItemDrop.ItemData item in Carried.GetAllItems())
        {
            if (_filter(item))
            {
                counts[item.m_shared.m_name] = (counts.TryGetValue(item.m_shared.m_name, out int n) ? n : 0) + item.m_stack;
            }
        }
        List<string> kinds = [.. counts.Keys];
        kinds.Sort((x, y) => counts[y].CompareTo(counts[x]));

        foreach (string kind in kinds)
        {
            List<WorkerChest> holding = [];
            List<WorkerChest> empty = [];
            List<WorkerChest> others = [];
            foreach (WorkerChest chest in nearestFirst)
            {
                Inventory inventory = chest.Container.GetInventory();
                if (HoldsKind(inventory, kind))
                {
                    holding.Add(chest);
                }
                else if (inventory.NrOfItems() == 0)
                {
                    empty.Add(chest);
                }
                else
                {
                    others.Add(chest);
                }
            }

            foreach (WorkerChest chest in holding)
            {
                // A chest that already holds this also takes any other carried item it already holds.
                HashSet<string> names = SharedWithCarried(chest);
                _ = names.Add(kind);
                if (!chest.Container.IsInUse() && HasSomethingToMove(chest, Carried, names) && TryGoTo(chest, names))
                {
                    return true;
                }
            }

            foreach (List<WorkerChest> fallback in new[] { empty, others })
            {
                foreach (WorkerChest chest in fallback)
                {
                    HashSet<string> names = [kind];
                    if (!chest.Container.IsInUse() && HasSomethingToMove(chest, Carried, names) && TryGoTo(chest, names))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private bool TryGoTo(WorkerChest chest, HashSet<string> onlyNames)
    {
        WorkerChest target = chest;
        if (!_navigator.Begin(chest.name, p => WorkerNavigator.ClosestPointOnColliders(target.gameObject, p)))
        {
            return false;
        }

        _chest = chest;
        _onlyNames = onlyNames;
        return true;
    }

    private static bool HoldsKind(Inventory inventory, string sharedName)
    {
        foreach (ItemDrop.ItemData item in inventory.GetAllItems())
        {
            if (item.m_shared.m_name == sharedName)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The names of the items being deposited that this chest already holds.</summary>
    private HashSet<string> SharedWithCarried(WorkerChest chest)
    {
        HashSet<string> carriedNames = [];
        foreach (ItemDrop.ItemData item in Carried.GetAllItems())
        {
            if (_filter(item))
            {
                _ = carriedNames.Add(item.m_shared.m_name);
            }
        }

        HashSet<string> shared = [];
        foreach (ItemDrop.ItemData item in chest.Container.GetInventory().GetAllItems())
        {
            if (carriedNames.Contains(item.m_shared.m_name))
            {
                _ = shared.Add(item.m_shared.m_name);
            }
        }
        return shared;
    }

    /// <summary>Whether a trip to this chest would move anything (only of the given kinds, if set), given the room there is.</summary>
    private bool HasSomethingToMove(WorkerChest chest, Inventory carried, HashSet<string> onlyNames)
    {
        Inventory chestInv = chest.Container.GetInventory();
        Inventory source = _fetching ? chestInv : carried;
        Inventory dest = _fetching ? Carried : chestInv;
        foreach (ItemDrop.ItemData item in source.GetAllItems())
        {
            if (_filter(item) && (onlyNames == null || onlyNames.Contains(item.m_shared.m_name)) && Capacity(dest, item) > 0)
            {
                return true;
            }
        }
        return false;
    }

    private int Transfer()
    {
        // The chest's ZDO may be owned by another player; take ownership so the change is saved.
        ZNetView chestView = _chest.GetComponent<ZNetView>();
        chestView.ClaimOwnership();

        Inventory chestInv = _chest.Container.GetInventory();
        Inventory source = _fetching ? chestInv : Carried;
        Inventory dest = _fetching ? Carried : chestInv;

        HashSet<string> only = _fetching ? null : _onlyNames;
        int moved = 0;
        int remaining = _limit;
        foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(source.GetAllItems()))
        {
            if (!_filter(item) || (only != null && !only.Contains(item.m_shared.m_name)))
            {
                continue;
            }

            int amount = Math.Min(Math.Min(item.m_stack, Capacity(dest, item)), remaining);
            if (amount <= 0)
            {
                continue;
            }

            ItemDrop.ItemData copy = item.Clone();
            copy.m_stack = amount;
            if (amount > 0 && dest.AddItem(copy))
            {
                _ = source.RemoveItem(item, amount);
                moved += amount;
                remaining -= amount;
            }
        }

        DvergrWorkersPlugin.Log.LogInfo((_fetching ? "Fetched " : "Deposited ") + moved + " item(s) "
            + (_fetching ? "from " : "into ") + _chest.name);
        return moved;
    }

    private static bool Contains(Inventory inventory, Func<ItemDrop.ItemData, bool> filter)
    {
        foreach (ItemDrop.ItemData item in inventory.GetAllItems())
        {
            if (filter(item))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>How many of this item the inventory can still take.</summary>
    private static int Capacity(Inventory inventory, ItemDrop.ItemData item)
    {
        return inventory.FindFreeStackSpace(item.m_shared.m_name, item.m_worldLevel)
            + (inventory.GetEmptySlots() * item.m_shared.m_maxStackSize);
    }
}
