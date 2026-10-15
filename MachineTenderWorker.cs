using System;
using System.Collections.Generic;
using UnityEngine;

namespace DvergrWorkers
{
    /// <summary>
    /// Base for the roles that keep machines fed and emptied: the blacksmith (charcoal kilns, smelters and blast
    /// furnaces), the barley worker (windmills) and the flax worker (spinning wheels). All of those machines are the
    /// game's Smelter component, so one implementation serves them all; a role only says which machines it tends.
    /// It keeps the machines inside the job radius fed and
    /// emptied. Each visit it collects finished product, then tops up fuel and ore from what it carries,
    /// fetching from input chests first, then from output chests holding something another worker makes
    /// (the woodcutter's wood for the kiln, its own coal for the smelters). Finished coal and metal go to output chests.
    /// </summary>
    public abstract class MachineTenderWorker : WorkerBrain
    {
        private enum Phase { Plan, Fetch, Walk, Work, Deposit }
        private enum Stage { Empty, Collect, Feed }

        /// <summary>Something to put into a machine: coal for its fuel slot, or one of the items it can process.</summary>
        private sealed class Need
        {
            public string SharedName;
            public string PrefabName;
            public bool Fuel;
            public int Count;
        }

        private const float FeedInterval = 0.35f;
        private const float EmptyWait = 0.8f;
        private const float CollectInterval = 0.3f;
        private const int MaxCollectTries = 12;
        private const float OutputRadius = 3.5f;
        private const int FullSlots = 2;

        private static readonly HashSet<string> LoggedUnknown = new HashSet<string>();

        private Phase _phase = Phase.Plan;
        private Stage _stage;
        private Smelter _machine;
        private List<Need> _needs = new List<Need>();
        private int _needIndex;
        private bool _fetchActive;
        private bool _pendingOutput;
        private float _timer;
        private int _collectTries;
        private bool _starved;
        private string _missingFuel = "";
        private bool _missingOre;
        private bool _deliverFirst;
        private int _handled;
        private int _handledLastRound;
        private readonly HashSet<Smelter> _served = new HashSet<Smelter>();
        private readonly HashSet<Smelter> _unreachable = new HashSet<Smelter>();
        private readonly HashSet<string> _productNames = new HashSet<string>();

        /// <summary>Comma-separated prefab names of the machines this role tends.</summary>
        protected abstract string AllowedMachines { get; }

        /// <summary>Shown on the bed when none of the role's machines are in range.</summary>
        protected abstract string NoMachinesNote { get; }

        protected override string IdleStatus()
        {
            return _handledLastRound > 0
                ? "Idle (handled " + _handledLastRound + " items last round)"
                : "Idle";
        }

        protected override void OnRoundEnded(bool finished)
        {
            _machine = null;
            _handledLastRound = _handled;
        }

        // ---------------------------------------------------------------- helpers

        private bool IsProduct(ItemDrop.ItemData item)
        {
            return item != null && _productNames.Contains(item.m_shared.m_name);
        }

        private bool CarryingProduct()
        {
            foreach (ItemDrop.ItemData item in Logistics.Carried.GetAllItems())
            {
                if (IsProduct(item))
                {
                    return true;
                }
            }
            return false;
        }

        private static string PrefabOf(Smelter machine)
        {
            ZNetView view = machine.m_nview;
            return view != null ? Utils.GetPrefabName(view.gameObject) : Utils.GetPrefabName(machine.gameObject);
        }

        private static bool IsValid(Smelter machine)
        {
            return machine != null && machine.m_nview != null && machine.m_nview.IsValid();
        }

        private static string DisplayName(Smelter machine)
        {
            return PrefabOf(machine).Replace('_', ' ');
        }

        private static IEnumerable<string> OutputNames(Smelter machine)
        {
            foreach (Smelter.ItemConversion conversion in machine.m_conversion)
            {
                if (conversion.m_to != null)
                {
                    yield return conversion.m_to.m_itemData.m_shared.m_name;
                }
            }
        }

        /// <summary>This role's machines in the job area. Other Smelter machines (refineries and so on) belong to other roles and are ignored.</summary>
        private List<Smelter> FindMachines()
        {
            var allowed = new HashSet<string>(
                AllowedMachines.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            var found = new List<Smelter>();
            _productNames.Clear();

            foreach (Smelter machine in UnityEngine.Object.FindObjectsByType<Smelter>(FindObjectsSortMode.None))
            {
                if (!IsValid(machine) || !InJobArea(machine.transform.position))
                {
                    continue;
                }

                string prefab = PrefabOf(machine);
                if (!allowed.Contains(prefab.Trim()))
                {
                    if (LoggedUnknown.Add(prefab))
                    {
                        DvergrWorkersPlugin.Log.LogInfo(Role.Name + " ignoring '" + prefab
                            + "' (add it to " + Role.Name + ".Machines to have this worker tend it)");
                    }
                    continue;
                }

                found.Add(machine);
                foreach (string output in OutputNames(machine))
                {
                    _productNames.Add(output);
                }
            }
            return found;
        }

        private IEnumerable<ItemDrop> ProductDropsNear(Smelter machine)
        {
            if (machine.m_outputPoint == null)
            {
                yield break;
            }

            var outputs = new HashSet<string>(OutputNames(machine));
            foreach (ItemDrop drop in ItemDrop.s_instances)
            {
                if (drop != null && drop.m_itemData != null && outputs.Contains(drop.m_itemData.m_shared.m_name)
                    && Vector3.Distance(drop.transform.position, machine.m_outputPoint.position) < OutputRadius)
                {
                    yield return drop;
                }
            }
        }

        private bool HasPendingOutput(Smelter machine)
        {
            if (machine.GetProcessedQueueSize() > 0)
            {
                return true;
            }
            foreach (ItemDrop _ in ProductDropsNear(machine))
            {
                return true;
            }
            return false;
        }

        private int Supply(string sharedName)
        {
            return Logistics.Carried.CountItems(sharedName)
                + Logistics.CountAvailable(i => i.m_shared.m_name == sharedName);
        }

        /// <summary>Works out what a machine can take right now, limited by what the worker can get hold of.</summary>
        /// <param name="fuelUsers">
        /// How many machines still want each kind of fuel this round. Scarce fuel is split between them
        /// instead of all going to the first machine visited.
        /// </param>
        private List<Need> BuildNeeds(Smelter machine, Dictionary<string, int> fuelUsers = null)
        {
            var needs = new List<Need>();

            if (machine.m_fuelItem != null && machine.m_maxFuel > 0)
            {
                int room = Mathf.FloorToInt(machine.m_maxFuel - machine.GetFuel());
                string shared = machine.m_fuelItem.m_itemData.m_shared.m_name;
                if (room > 0)
                {
                    int supply = Supply(shared);
                    int users = 1;
                    if (fuelUsers != null && fuelUsers.TryGetValue(shared, out int counted))
                    {
                        users = Math.Max(1, counted);
                    }
                    if (supply > 0)
                    {
                        supply = Math.Min(supply, (supply + users - 1) / users);
                        needs.Add(new Need
                        {
                            SharedName = shared,
                            PrefabName = machine.m_fuelItem.gameObject.name,
                            Fuel = true,
                            Count = Math.Min(room, supply),
                        });
                    }
                    else
                    {
                        _starved = true;
                        _missingFuel = shared.Replace("$item_", "");
                    }
                }
            }

            if (machine.m_maxOre > 0)
            {
                int room = machine.m_maxOre - machine.GetQueueSize();
                bool any = false;
                foreach (Smelter.ItemConversion conversion in machine.m_conversion)
                {
                    if (room <= 0)
                    {
                        break;
                    }
                    if (conversion.m_from == null)
                    {
                        continue;
                    }

                    string shared = conversion.m_from.m_itemData.m_shared.m_name;
                    int take = Math.Min(room, Supply(shared));
                    if (take > 0)
                    {
                        any = true;
                        room -= take;
                        needs.Add(new Need { SharedName = shared, PrefabName = conversion.m_from.gameObject.name, Count = take });
                    }
                }
                if (!any && room > 0)
                {
                    _starved = true;
                    _missingOre = true;
                }
            }
            return needs;
        }

        private static bool IsFuelAnywhere(List<Smelter> machines, string sharedName)
        {
            foreach (Smelter machine in machines)
            {
                if (machine.m_fuelItem != null && machine.m_maxFuel > 0
                    && machine.m_fuelItem.m_itemData.m_shared.m_name == sharedName)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Says what the machines are short of, e.g. "Waiting for coal and ore in a chest".</summary>
        private string WaitingNote()
        {
            var parts = new List<string>();
            if (_missingFuel.Length > 0)
            {
                parts.Add(_missingFuel);
            }
            if (_missingOre)
            {
                parts.Add("ore");
            }
            return "Waiting for " + string.Join(" and ", parts) + " in a chest";
        }

        // ---------------------------------------------------------------- rounds

        protected override bool BeginRound()
        {
            _served.Clear();
            _unreachable.Clear();
            _starved = false;
            _missingFuel = "";
            _missingOre = false;
            _handled = 0;
            _machine = null;
            _fetchActive = false;

            List<Smelter> machines = FindMachines();
            if (machines.Count == 0)
            {
                _handledLastRound = 0;
                IdleNote = NoMachinesNote;
                return false;
            }

            _deliverFirst = CarryingProduct();
            bool work = _deliverFirst;
            foreach (Smelter machine in machines)
            {
                if (HasPendingOutput(machine) || BuildNeeds(machine).Count > 0)
                {
                    work = true;
                    break;
                }
            }

            if (!work)
            {
                _handledLastRound = 0;
                IdleNote = _starved ? WaitingNote() : null;
                return false;
            }

            _phase = Phase.Plan;
            DvergrWorkersPlugin.Log.LogInfo(Role.Name + " round started with " + machines.Count + " machine(s)");
            return true;
        }

        protected override bool RunTask(float dt)
        {
            switch (_phase)
            {
                case Phase.Plan:
                    return Plan();
                case Phase.Fetch:
                    return Fetch(dt);
                case Phase.Walk:
                    return Walk(dt);
                case Phase.Work:
                    return Work(dt);
                case Phase.Deposit:
                    return Deposit(dt);
            }
            return false;
        }

        private bool Abort(string reason)
        {
            DvergrWorkersPlugin.Log.LogInfo(Role.Name + " stopping: " + reason);
            IdleNote = reason;
            EndRound(finished: false);
            return false;
        }

        private bool Plan()
        {
            List<Smelter> machines = FindMachines();

            if (CarryingProduct() && (_deliverFirst || Logistics.Carried.GetEmptySlots() <= FullSlots))
            {
                _deliverFirst = false;
                return StartDeposit();
            }

            // Fuel users still waiting, and which fuels some machine in range makes.
            var fuelUsers = new Dictionary<string, int>();
            var fuelMade = new HashSet<string>();
            foreach (Smelter machine in machines)
            {
                if (machine.m_fuelItem == null || machine.m_maxFuel <= 0)
                {
                    continue;
                }
                string fuel = machine.m_fuelItem.m_itemData.m_shared.m_name;
                if (!_served.Contains(machine) && !_unreachable.Contains(machine)
                    && Mathf.FloorToInt(machine.m_maxFuel - machine.GetFuel()) > 0)
                {
                    fuelUsers[fuel] = fuelUsers.TryGetValue(fuel, out int n) ? n + 1 : 1;
                }
            }
            foreach (Smelter machine in machines)
            {
                if (machine.m_fuelItem == null || machine.m_maxFuel <= 0)
                {
                    foreach (string output in OutputNames(machine))
                    {
                        fuelMade.Add(output);
                    }
                }
            }

            // Machines that make fuel (the kiln) come first so coal starts cooking as early as possible;
            // otherwise the nearest machine that has something to do.
            Smelter best = null;
            List<Need> bestNeeds = null;
            bool bestPending = false;
            int bestPriority = int.MaxValue;
            float bestDist = float.MaxValue;
            foreach (Smelter machine in machines)
            {
                if (_served.Contains(machine) || _unreachable.Contains(machine))
                {
                    continue;
                }

                bool pending = HasPendingOutput(machine);
                List<Need> needs = BuildNeeds(machine, fuelUsers);
                if (!pending && needs.Count == 0)
                {
                    continue;
                }

                bool makesFuel = false;
                foreach (string output in OutputNames(machine))
                {
                    makesFuel |= fuelMade.Contains(output) && IsFuelAnywhere(machines, output);
                }
                int priority = makesFuel ? 0 : 1;
                float d = Vector3.Distance(machine.transform.position, transform.position);
                if (priority < bestPriority || (priority == bestPriority && d < bestDist))
                {
                    bestPriority = priority;
                    bestDist = d;
                    best = machine;
                    bestNeeds = needs;
                    bestPending = pending;
                }
            }

            if (best != null)
            {
                _machine = best;
                _needs = bestNeeds;
                _pendingOutput = bestPending;
                _needIndex = 0;
                _fetchActive = false;
                _phase = Phase.Fetch;
                return true;
            }

            // Nothing left to tend: hand in whatever was collected.
            if (CarryingProduct())
            {
                return StartDeposit();
            }
            if (_handled == 0 && _starved)
            {
                IdleNote = WaitingNote();
            }
            EndRound(finished: true);
            return false;
        }

        // ---------------------------------------------------------------- fetching supplies

        private bool Fetch(float dt)
        {
            if (!IsValid(_machine))
            {
                _phase = Phase.Plan;
                return true;
            }

            if (_fetchActive)
            {
                switch (Logistics.Step(dt))
                {
                    case LogisticsResult.Moving:
                        return true;
                    case LogisticsResult.Failed:
                        DvergrWorkersPlugin.Log.LogInfo(Role.Name + " fetch failed: " + Logistics.Reason);
                        break;
                }
                _fetchActive = false;
                _needIndex++;
            }

            while (_needIndex < _needs.Count)
            {
                Need need = _needs[_needIndex];
                Func<ItemDrop.ItemData, bool> match = i => i.m_shared.m_name == need.SharedName;
                int missing = need.Count - Logistics.Carried.CountItems(need.SharedName);

                if (missing > 0)
                {
                    if (Logistics.BeginFetchAny(match, missing))
                    {
                        _fetchActive = true;
                        SetStatus("Fetching " + need.SharedName.TrimStart('$').Replace("item_", "") + " from a chest");
                        return true;
                    }
                }
                _needIndex++;
            }

            return BeginWalk();
        }

        private bool BeginWalk()
        {
            // Only what could actually be carried over is worth the trip.
            _needs.RemoveAll(n => Logistics.Carried.CountItems(n.SharedName) == 0);
            foreach (Need need in _needs)
            {
                need.Count = Math.Min(need.Count, Logistics.Carried.CountItems(need.SharedName));
            }

            if (_needs.Count == 0 && !_pendingOutput)
            {
                _served.Add(_machine);
                _phase = Phase.Plan;
                return true;
            }

            Smelter machine = _machine;
            if (!Navigator.Begin(PrefabOf(machine), from => WorkerNavigator.ClosestPointOnColliders(machine.m_nview.gameObject, from)))
            {
                DvergrWorkersPlugin.Log.LogInfo("No reachable spot next to " + PrefabOf(machine) + ", skipping");
                _unreachable.Add(machine);
                _phase = Phase.Plan;
                return true;
            }

            _phase = Phase.Walk;
            SetStatus("Walking to the " + DisplayName(machine));
            return true;
        }

        private bool Walk(float dt)
        {
            if (!IsValid(_machine))
            {
                _phase = Phase.Plan;
                return true;
            }

            switch (Navigator.Step(dt))
            {
                case NavResult.InReach:
                case NavResult.CloseEnough:
                    _machine.m_nview.ClaimOwnership();
                    _phase = Phase.Work;
                    _stage = Stage.Empty;
                    _timer = 0.2f;
                    SetStatus("Tending the " + DisplayName(_machine));
                    break;

                case NavResult.Failed:
                    _unreachable.Add(_machine);
                    _phase = Phase.Plan;
                    break;
            }
            return true;
        }

        // ---------------------------------------------------------------- tending a machine

        private bool Work(float dt)
        {
            if (!IsValid(_machine))
            {
                _phase = Phase.Plan;
                return true;
            }

            Ai.StopMoving();
            FaceMachine();
            _timer -= dt;
            if (_timer > 0f)
            {
                return true;
            }

            switch (_stage)
            {
                case Stage.Empty:
                    if (_machine.GetProcessedQueueSize() > 0)
                    {
                        _machine.m_nview.InvokeRPC("RPC_EmptyProcessed");
                        _timer = EmptyWait;
                    }
                    _stage = Stage.Collect;
                    _collectTries = 0;
                    break;

                case Stage.Collect:
                    CollectOne();
                    break;

                case Stage.Feed:
                    FeedOne();
                    break;
            }
            return true;
        }

        private void CollectOne()
        {
            _timer = CollectInterval;

            ItemDrop target = null;
            foreach (ItemDrop drop in ProductDropsNear(_machine))
            {
                target = drop;
                break;
            }

            if (target == null || ++_collectTries > MaxCollectTries)
            {
                _stage = Stage.Feed;
                _timer = 0.1f;
                return;
            }

            // Drops are owned by whoever last touched them; ask for ownership and retry for a moment.
            target.RequestOwn();
            ItemDrop.ItemData data = target.m_itemData;
            int stack = data.m_stack;
            if (Humanoid.Pickup(target.gameObject, false, false))
            {
                _handled += stack;
                _collectTries = 0;
            }
        }

        private void FeedOne()
        {
            _timer = FeedInterval;

            Need need = _needs.Find(n => n.Count > 0);
            if (need == null)
            {
                _served.Add(_machine);
                _phase = Phase.Plan;
                return;
            }

            ItemDrop.ItemData item = null;
            foreach (ItemDrop.ItemData candidate in Logistics.Carried.GetAllItems())
            {
                if (candidate.m_shared.m_name == need.SharedName)
                {
                    item = candidate;
                    break;
                }
            }

            bool full = item == null
                || (need.Fuel
                    ? _machine.GetFuel() > _machine.m_maxFuel - 1
                    : _machine.GetQueueSize() >= _machine.m_maxOre || !_machine.IsItemAllowed(item.m_dropPrefab.name));
            if (full)
            {
                need.Count = 0;
                return;
            }

            Logistics.Carried.RemoveItem(item, 1);
            if (need.Fuel)
            {
                _machine.m_nview.InvokeRPC("RPC_AddFuel");
            }
            else
            {
                _machine.m_nview.InvokeRPC("RPC_AddOre", item.m_dropPrefab.name, item.m_cheated);
            }
            need.Count--;
            _handled++;
            Animation.PlaySwing();
        }

        private void FaceMachine()
        {
            Vector3 dir = _machine.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
            {
                Character.SetLookDir(dir.normalized);
                Ai.LookTowards(dir);
            }
        }

        // ---------------------------------------------------------------- delivering product

        private bool StartDeposit()
        {
            if (!CarryingProduct())
            {
                _phase = Phase.Plan;
                return true;
            }

            if (!Logistics.BeginDeposit(IsProduct))
            {
                return Abort(Logistics.Reason);
            }

            _phase = Phase.Deposit;
            SetStatus("Delivering product to a chest");
            return true;
        }

        private bool Deposit(float dt)
        {
            switch (Logistics.Step(dt))
            {
                case LogisticsResult.Moving:
                    return true;
                case LogisticsResult.Failed:
                    return Abort(Logistics.Reason);
            }

            if (Logistics.Transferred == 0)
            {
                return Abort("Output chests are full");
            }

            // Still carrying product: the chest filled up, so try the next closest one.
            if (CarryingProduct())
            {
                return StartDeposit();
            }
            _phase = Phase.Plan;
            return true;
        }
    }
}
