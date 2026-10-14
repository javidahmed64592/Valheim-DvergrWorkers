using System;
using System.Collections.Generic;
using UnityEngine;

namespace DvergrWorkers
{
    internal enum LogisticsResult
    {
        Moving,
        /// <summary>Arrived and moved whatever could be moved (see <see cref="WorkerLogistics.Transferred"/>).</summary>
        Done,
        /// <summary>Could not reach the chest; see <see cref="WorkerLogistics.Reason"/>.</summary>
        Failed,
    }

    /// <summary>
    /// Trips between a worker and its chests: fetch wanted items from the closest input chest that has
    /// some, or deposit items into the closest output chest that has room. A role runs a trip by calling
    /// <see cref="BeginFetch"/> or <see cref="BeginDeposit"/> and then <see cref="Step"/> each frame.
    /// Repeat the trip until it transfers nothing to spread items across several chests.
    /// </summary>
    internal sealed class WorkerLogistics
    {
        private readonly Humanoid _humanoid;
        private readonly WorkerNavigator _navigator;
        private readonly Func<Vector3> _home;
        private readonly Func<float> _radius;
        private readonly Transform _transform;

        private WorkerChest _chest;
        private Func<ItemDrop.ItemData, bool> _filter;
        private bool _fetching;
        private int _limit;

        /// <summary>Why the last Begin* returned false or a trip failed.</summary>
        public string Reason { get; private set; } = "";

        /// <summary>Number of items moved by the last completed trip.</summary>
        public int Transferred { get; private set; }

        public WorkerLogistics(Humanoid humanoid, WorkerNavigator navigator, Func<Vector3> home, Func<float> radius)
        {
            _humanoid = humanoid;
            _navigator = navigator;
            _home = home;
            _radius = radius;
            _transform = humanoid.transform;
        }

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
                && WorkerRoles.IsProducedNearby(item.m_shared.m_name, _home(), _radius() * 2f);
        }

        /// <summary>How many matching items can be fetched from chests: input chests plus coordinated output chests.</summary>
        public int CountAvailable(Func<ItemDrop.ItemData, bool> match)
        {
            return CountInChests(ChestKind.Input, match)
                + CountInChests(ChestKind.Output, i => match(i) && MayTakeFromOutput(i));
        }

        /// <summary>Fetches from input chests first, then from output chests holding items another worker produced.</summary>
        public bool BeginFetchAny(Func<ItemDrop.ItemData, bool> match, int maxCount = int.MaxValue)
        {
            if (CountInChests(ChestKind.Input, match) > 0 && BeginFetch(match, maxCount, ChestKind.Input))
            {
                return true;
            }

            Func<ItemDrop.ItemData, bool> coordinated = i => match(i) && MayTakeFromOutput(i);
            if (CountInChests(ChestKind.Output, coordinated) > 0)
            {
                return BeginFetch(coordinated, maxCount, ChestKind.Output);
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
            Transferred = 0;

            List<WorkerChest> chests = WorkerChest.Find(kind, _home(), _radius(), _transform.position);
            if (chests.Count == 0)
            {
                Reason = "Needs " + (kind == ChestKind.Input ? "an input" : "an output") + " chest nearby";
                return false;
            }

            Inventory from = fetching ? null : Carried;
            foreach (WorkerChest chest in chests)
            {
                if (chest.Container.IsInUse() || !HasSomethingToMove(chest, from))
                {
                    continue;
                }

                WorkerChest target = chest;
                if (_navigator.Begin(chest.name, p => WorkerNavigator.ClosestPointOnColliders(target.gameObject, p)))
                {
                    _chest = chest;
                    return true;
                }
            }

            Reason = noneReason;
            return false;
        }

        /// <summary>Whether a trip to this chest would move anything, given what is carried and what room there is.</summary>
        private bool HasSomethingToMove(WorkerChest chest, Inventory carried)
        {
            Inventory chestInv = chest.Container.GetInventory();
            Inventory source = _fetching ? chestInv : carried;
            Inventory dest = _fetching ? Carried : chestInv;
            foreach (ItemDrop.ItemData item in source.GetAllItems())
            {
                if (_filter(item) && Capacity(dest, item) > 0)
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

            int moved = 0;
            int remaining = _limit;
            foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(source.GetAllItems()))
            {
                if (!_filter(item))
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
                    source.RemoveItem(item, amount);
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
                + inventory.GetEmptySlots() * item.m_shared.m_maxStackSize;
        }
    }
}
