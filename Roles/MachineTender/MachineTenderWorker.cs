using System;
using System.Collections.Generic;

using DvergrWorkers.Core;
using DvergrWorkers.Logistics;
using DvergrWorkers.Movement;

using UnityEngine;

namespace DvergrWorkers.Roles.MachineTender;

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
    private enum Phase { Plan = 0, Fetch = 1, Walk = 2, Work = 3, Deposit = 4, Gather = 5 }
    private enum Stage { Empty = 0, Collect = 1, PickUp = 2, Feed = 3 }

    /// <summary>Something to put into a machine: coal for its fuel slot, or one of the items it can process.</summary>
    private sealed class Need
    {
        public string SharedName;
        public string PrefabName;
        public bool Fuel;
        public int Count;

        /// <summary>How many were picked up off the ground next to the machine on this visit.</summary>
        public int PickedUp;
    }

    private const float FeedInterval = 0.35f;
    private const float EmptyWait = 0.8f;
    private const float CollectInterval = 0.3f;
    private const int MaxCollectTries = 12;
    private const float OutputRadius = 3.5f;
    private const int FullSlots = 2;

    private static readonly HashSet<string> LoggedUnknown = [];

    private Phase _phase = Phase.Plan;
    private Stage _stage;
    private Smelter _machine;
    private List<Need> _needs = [];
    private int _needIndex;
    private bool _fetchActive;
    private bool _pendingOutput;
    private float _timer;
    private int _collectTries;
    private bool _starved;
    private string _missingFuel = "";
    private readonly HashSet<string> _missingOre = [];
    private float _lastDiagnostic = -1000f;
    private bool _deliverFirst;
    private bool _gatherPending;
    private bool _depositSurplus;
    private int _handled;
    private int _handledLastRound;
    private readonly HashSet<Smelter> _served = [];
    private readonly HashSet<Smelter> _unreachable = [];
    private readonly HashSet<string> _productNames = [];

    private HashSet<string> _wanted = [];
    private float _wantedAt = -1000f;

    /// <summary>Everything this worker's machines take: their ores and their fuel.</summary>
    internal override bool WantsItem(string sharedName)
    {
        if (Time.time - _wantedAt > 10f)
        {
            _wantedAt = Time.time;
            HashSet<string> wanted = [];
            foreach (Smelter machine in FindMachines())
            {
                foreach (Smelter.ItemConversion conversion in machine.m_conversion)
                {
                    if (conversion.m_from != null)
                    {
                        _ = wanted.Add(conversion.m_from.m_itemData.m_shared.m_name);
                    }
                }
                if (machine.m_fuelItem != null && machine.m_maxFuel > 0)
                {
                    _ = wanted.Add(machine.m_fuelItem.m_itemData.m_shared.m_name);
                }
            }
            _wanted = wanted;
        }
        return _wanted.Contains(sharedName);
    }

    /// <summary>Comma-separated prefab names of the vanilla machines this role tends.</summary>
    protected abstract string AllowedMachines { get; }

    /// <summary>Shown on the bed when none of the role's machines are in range.</summary>
    protected abstract string NoMachinesNote { get; }

    /// <summary>
    /// Called at the start of a round. A role that collects its own inputs (the eitr worker tapping sap
    /// extractors) returns true if it has something to gather, after resetting any state from earlier rounds.
    /// </summary>
    protected virtual bool BeginGather()
    {
        return false;
    }

    /// <summary>Runs the gathering once per round, before the machines are tended. Returns true while still busy.</summary>
    protected virtual bool Gather(float dt)
    {
        return false;
    }

    /// <summary>Gathered inputs the machines had no room for. They are stored in a chest once the machines are tended.</summary>
    protected virtual bool IsSurplus(ItemDrop.ItemData item)
    {
        return false;
    }

    protected override string IdleStatus()
    {
        return _handledLastRound > 0
            ? Text.Of("$dw_status_idle_handled", _handledLastRound)
            : "$dw_status_idle";
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

    private bool CarryingSurplus()
    {
        foreach (ItemDrop.ItemData item in Logistics.Carried.GetAllItems())
        {
            if (IsSurplus(item))
            {
                return true;
            }
        }
        return false;
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
        return Text.NameOf(machine.gameObject);
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
        HashSet<string> allowed = [.. AllowedMachines.Split([','], StringSplitOptions.RemoveEmptyEntries)];
        List<Smelter> found = [];
        _productNames.Clear();

        foreach (Smelter machine in Scene.Smelters.All)
        {
            if (!SceneObjects<Smelter>.IsLive(machine) || !IsValid(machine) || !InJobArea(machine.transform.position))
            {
                continue;
            }

            string prefab = PrefabOf(machine);
            if (!allowed.Contains(prefab.Trim()))
            {
                if (LoggedUnknown.Add(prefab))
                {
                    DvergrWorkersPlugin.Log.LogDebug(Role.Name + " ignoring '" + prefab + "' (not one of its machines)");
                }
                continue;
            }

            found.Add(machine);
            foreach (string output in OutputNames(machine))
            {
                _ = _productNames.Add(output);
            }
        }
        return found;
    }

    private static IEnumerable<ItemDrop> ProductDropsNear(Smelter machine)
    {
        if (machine.m_outputPoint == null)
        {
            yield break;
        }

        HashSet<string> outputs = [.. OutputNames(machine)];
        foreach (ItemDrop drop in ItemDrop.s_instances)
        {
            if (drop != null && drop.m_itemData != null && outputs.Contains(drop.m_itemData.m_shared.m_name)
                && Vector3.Distance(drop.transform.position, machine.m_outputPoint.position) < OutputRadius)
            {
                yield return drop;
            }
        }
    }

    private static bool HasPendingOutput(Smelter machine)
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
        return Logistics.Carried.CountItems(sharedName) + Logistics.AvailableTo(sharedName);
    }

    /// <summary>Inputs lying on the ground right next to the machine, e.g. ore a player dropped by a smelter.</summary>
    private static int GroundCount(Smelter machine, string sharedName)
    {
        return GroundItems.CountNear(machine.m_nview.gameObject, sharedName);
    }

    /// <summary>Works out what a machine can take right now, limited by what the worker can get hold of.</summary>
    /// <param name="fuelUsers">
    /// How many machines still want each kind of fuel this round. Scarce fuel is split between them
    /// instead of all going to the first machine visited.
    /// </param>
    private List<Need> BuildNeeds(Smelter machine, Dictionary<string, int> fuelUsers = null)
    {
        List<Need> needs = [];

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
                // Chest stock is shared between the machines; fuel dropped next to this one is all for it.
                supply = Math.Min(supply, (supply + users - 1) / users) + GroundCount(machine, shared);
                if (supply > 0)
                {
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
                    _missingFuel = shared;
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
                int take = Math.Min(room, Supply(shared) + GroundCount(machine, shared));
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
                foreach (Smelter.ItemConversion conversion in machine.m_conversion)
                {
                    if (conversion.m_from != null)
                    {
                        _ = _missingOre.Add(conversion.m_from.m_itemData.m_shared.m_name);
                    }
                }
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

    private static string JoinNames(IEnumerable<string> names)
    {
        return Text.JoinOr([.. names]);
    }

    /// <summary>Says what the machines are short of, e.g. "Waiting for coal and ore in a chest".</summary>
    private string WaitingNote()
    {
        List<string> parts = [];
        if (_missingFuel.Length > 0)
        {
            parts.Add(_missingFuel);
        }
        if (_missingOre.Count > 0)
        {
            // Name the items when there are only a few ("barley, oat seeds or oat"); a smelter takes many, so say "items".
            parts.Add(_missingOre.Count <= 3 ? JoinNames(_missingOre) : "$dw_items");
        }
        return Text.Of("$dw_status_waiting_for", Text.JoinAnd(parts));
    }

    /// <summary>Explains, at most every couple of minutes, why a round did not start.</summary>
    private void LogSupply(List<Smelter> machines)
    {
        if (Time.time - _lastDiagnostic < 120f)
        {
            return;
        }
        _lastDiagnostic = Time.time;

        foreach (Smelter machine in machines)
        {
            List<string> lines = [];
            foreach (Smelter.ItemConversion conversion in machine.m_conversion)
            {
                if (conversion.m_from == null)
                {
                    continue;
                }
                string shared = conversion.m_from.m_itemData.m_shared.m_name;
                bool Match(ItemDrop.ItemData i)
                {
                    return i.m_shared.m_name == shared;
                }

                lines.Add(shared + " carried " + Logistics.Carried.CountItems(shared)
                    + ", input " + Logistics.CountInChests(ChestKind.Input, Match)
                    + ", output " + Logistics.CountInChests(ChestKind.Output, Match)
                    + ", usable " + Logistics.CountAvailable(Match)
                    + ", on the ground " + GroundCount(machine, shared));
            }
            DvergrWorkersPlugin.Log.LogInfo(Role.Name + " has nothing to do at " + PrefabOf(machine) + " (queue "
                + machine.GetQueueSize() + "/" + machine.m_maxOre + "): " + string.Join("; ", lines));
        }
    }

    // ---------------------------------------------------------------- rounds

    protected override bool BeginRound()
    {
        _served.Clear();
        _unreachable.Clear();
        _starved = false;
        _missingFuel = "";
        _missingOre.Clear();
        _handled = 0;
        _machine = null;
        _fetchActive = false;

        _gatherPending = BeginGather();
        _depositSurplus = false;

        List<Smelter> machines = FindMachines();
        if (machines.Count == 0 && !_gatherPending)
        {
            _handledLastRound = 0;
            IdleNote = NoMachinesNote;
            return false;
        }

        _deliverFirst = CarryingProduct();
        bool work = _deliverFirst || _gatherPending;
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
            LogSupply(machines);
            return false;
        }

        _phase = Phase.Plan;
        DvergrWorkersPlugin.Log.LogInfo(Role.Name + " round started with " + machines.Count + " machine(s)");
        return true;
    }

    protected override bool RunTask(float dt)
    {
        return _phase switch
        {
            Phase.Plan => Plan(),
            Phase.Fetch => Fetch(dt),
            Phase.Walk => Walk(dt),
            Phase.Work => Work(dt),
            Phase.Deposit => Deposit(dt),
            Phase.Gather => GatherPhase(dt),
            _ => false,
        };
    }

    private bool Abort(string reason)
    {
        DvergrWorkersPlugin.Log.LogInfo(Role.Name + " stopping: " + Text.Show(reason));
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

        if (_gatherPending)
        {
            _gatherPending = false;
            _phase = Phase.Gather;
            return true;
        }

        // Fuel users still waiting, and which fuels some machine in range makes.
        Dictionary<string, int> fuelUsers = [];
        HashSet<string> fuelMade = [];
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
                    _ = fuelMade.Add(output);
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
        if (CarryingSurplus() && StartSurplusDeposit())
        {
            return true;
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
                    DvergrWorkersPlugin.Log.LogInfo(Role.Name + " fetch failed: " + Text.Show(Logistics.Reason));
                    break;
            }
            _fetchActive = false;
            _needIndex++;
        }

        while (_needIndex < _needs.Count)
        {
            Need need = _needs[_needIndex];
            bool Match(ItemDrop.ItemData i)
            {
                return i.m_shared.m_name == need.SharedName;
            }

            // Whatever lies next to the machine is picked up there, so only the rest comes from a chest.
            int missing = need.Count - Usable(need.SharedName);

            if (missing > 0 && Logistics.BeginFetchAny(Match, missing))
            {
                _fetchActive = true;
                SetStatus(Text.Of("$dw_status_fetching_from_chest", need.SharedName));
                return true;
            }
            _needIndex++;
        }

        return BeginWalk();
    }

    private bool BeginWalk()
    {
        // Only what could actually be carried over is worth the trip.
        _ = _needs.RemoveAll(n => Usable(n.SharedName) == 0);
        foreach (Need need in _needs)
        {
            need.Count = Math.Min(need.Count, Usable(need.SharedName));
        }

        if (_needs.Count == 0 && !_pendingOutput)
        {
            _ = _served.Add(_machine);
            _phase = Phase.Plan;
            return true;
        }

        Smelter machine = _machine;
        if (!Navigator.Begin(PrefabOf(machine), from => WorkerNavigator.ClosestPointOnColliders(machine.m_nview.gameObject, from)))
        {
            DvergrWorkersPlugin.Log.LogInfo("No reachable spot next to " + PrefabOf(machine) + ", skipping");
            _ = _unreachable.Add(machine);
            _phase = Phase.Plan;
            return true;
        }

        _phase = Phase.Walk;
        SetStatus(Text.Of("$dw_status_walking_to_the", DisplayName(machine)));
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
                SetStatus(Text.Of("$dw_status_tending", DisplayName(_machine)));
                break;

            case NavResult.Failed:
                _ = _unreachable.Add(_machine);
                _phase = Phase.Plan;
                break;
        }
        return true;
    }

    /// <summary>What the worker can put into the current machine: what it carries plus what lies next to the machine.</summary>
    private int Usable(string sharedName)
    {
        return Logistics.Carried.CountItems(sharedName) + GroundCount(_machine, sharedName);
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

            case Stage.PickUp:
                PickUpOne();
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
            _stage = Stage.PickUp;
            _collectTries = 0;
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
            Animation.PlayPickup();
        }
    }

    /// <summary>
    /// Picks up inputs lying next to the machine, only as many as it will take this visit; the rest stays on
    /// the ground for later rounds rather than being carried around. These are fed in before anything carried.
    /// </summary>
    private void PickUpOne()
    {
        _timer = CollectInterval;

        foreach (Need need in _needs)
        {
            // The ground pile is used before anything carried (such as coal fresh from the kiln, which then goes to
            // an output chest instead), so items players drop by a machine are used up quickly and do not linger.
            int wanted = need.Count - need.PickedUp;
            if (wanted <= 0)
            {
                continue;
            }

            ItemDrop drop = GroundItems.FirstNear(_machine.m_nview.gameObject, need.SharedName);
            if (drop == null || ++_collectTries > MaxCollectTries)
            {
                continue;
            }
            int taken = GroundItems.Take(Humanoid, drop, wanted);
            if (taken > 0)
            {
                need.PickedUp += taken;
                _collectTries = 0;
                Animation.PlayPickup();
            }
            // One drop per tick, like a player picking things up.
            return;
        }

        _stage = Stage.Feed;
        _timer = 0.1f;
    }

    private void FeedOne()
    {
        _timer = FeedInterval;

        Need need = _needs.Find(n => n.Count > 0);
        if (need == null)
        {
            _ = _served.Add(_machine);
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

        _ = Logistics.Carried.RemoveItem(item, 1);
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

        _depositSurplus = false;
        _phase = Phase.Deposit;
        SetStatus("$dw_status_delivering_product");
        return true;
    }

    /// <summary>Stores leftover gathered inputs, preferably in an input chest where they will be fetched again.</summary>
    private bool StartSurplusDeposit()
    {
        if (!Logistics.BeginDeposit(IsSurplus, ChestKind.Input) && !Logistics.BeginDeposit(IsSurplus, ChestKind.Output))
        {
            return false;
        }

        _depositSurplus = true;
        _phase = Phase.Deposit;
        SetStatus("$dw_status_storing_spares");
        return true;
    }

    private bool GatherPhase(float dt)
    {
        if (!Gather(dt))
        {
            _phase = Phase.Plan;
        }
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
            if (_depositSurplus)
            {
                // No chest has room for the spare supplies; they stay with the worker.
                EndRound(finished: true);
                return false;
            }
            return Abort("$dw_reason_output_full");
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
