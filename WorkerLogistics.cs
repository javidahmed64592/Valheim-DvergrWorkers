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

        /// <summary>True if any input chest in range holds something matching <paramref name="wanted"/>.</summary>
        public bool InputHas(Func<ItemDrop.ItemData, bool> wanted)
        {
            foreach (WorkerChest chest in WorkerChest.Find(ChestKind.Input, _home(), _radius(), _transform.position))
            {
                if (Contains(chest.Container.GetInventory(), wanted))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Starts a trip to the closest input chest holding wanted items. False if there is none.</summary>
        public bool BeginFetch(Func<ItemDrop.ItemData, bool> wanted)
        {
            return Begin(ChestKind.Input, wanted, fetching: true, "No input chest with the items needed");
        }

        /// <summary>Starts a trip to the closest output chest that has room for something carried that matches the filter.</summary>
        public bool BeginDeposit(Func<ItemDrop.ItemData, bool> filter)
        {
            return Begin(ChestKind.Output, filter, fetching: false, "Output chests are full");
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
            foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(source.GetAllItems()))
            {
                if (!_filter(item))
                {
                    continue;
                }

                int amount = Math.Min(item.m_stack, Capacity(dest, item));
                if (amount <= 0)
                {
                    continue;
                }

                ItemDrop.ItemData copy = item.Clone();
                copy.m_stack = amount;
                if (dest.AddItem(copy))
                {
                    source.RemoveItem(item, amount);
                    moved += amount;
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
