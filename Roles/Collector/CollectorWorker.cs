using System.Collections.Generic;

using DvergrWorkers.Core;
using DvergrWorkers.Logistics;
using DvergrWorkers.Movement;

using UnityEngine;

namespace DvergrWorkers.Roles.Collector;

/// <summary>
/// Base for roles that look after stations which make something over time and hand it out when used: the mead
/// worker (fermenters) and the beekeeper (beehives). Each round the worker visits every station in its job area that
/// has something ready, or that can take a new input it can get hold of. At each one it releases what is ready the
/// way a player would, picks up what drops, and puts in a new input if the station takes one. The product goes to
/// output chests; inputs it could not use go back to an input chest.
/// </summary>
/// <typeparam name="T">The game's component for the station, e.g. <see cref="Beehive"/>.</typeparam>
public abstract class CollectorWorker<T> : WorkerBrain where T : Component
{
    private enum Phase { Plan = 0, Fetch = 1, Walk = 2, Work = 3, Deposit = 4 }
    private enum Stage { Release = 0, Collect = 1, Feed = 2 }

    private const float CollectInterval = 0.3f;
    private const float FeedInterval = 0.35f;
    private const int MaxCollectTries = 12;
    private const int FullSlots = 2;

    private readonly HashSet<T> _visited = [];
    private readonly HashSet<T> _unreachable = [];
    private Phase _phase;
    private Stage _stage;
    private T _station;
    private float _timer;
    private int _collectTries;
    private bool _fetching;
    private bool _deliverFirst;
    private bool _depositSurplus;
    private int _handled;
    private int _handledLastRound;

    // ---------------------------------------------------------------- role hooks

    /// <summary>Every loaded station of this kind, from its <see cref="Scene"/> registry.</summary>
    protected abstract List<T> AllStations { get; }

    protected abstract ZNetView ViewOf(T station);

    /// <summary>Shown on the bed when there is no station in range.</summary>
    protected abstract string NoStationsNote { get; }

    /// <summary>What the station makes; picked up next to it and delivered to output chests.</summary>
    protected abstract bool IsProduct(ItemDrop.ItemData item);

    /// <summary>Whether the station has something ready to release.</summary>
    protected abstract bool HasOutput(T station);

    /// <summary>Releases what is ready, as a player using the station would. Returns how long until it has dropped.</summary>
    protected abstract float Release(T station);

    /// <summary>Where the product drops.</summary>
    protected abstract Vector3 OutputPoint(T station);

    protected virtual float OutputRadius => 3f;

    /// <summary>What the station takes in, if anything. Inputs come from input chests (or coordinated output chests).</summary>
    protected virtual bool IsInput(ItemDrop.ItemData item)
    {
        return false;
    }

    /// <summary>Whether the station will take an input once what is ready has been released.</summary>
    protected virtual bool WillTakeInput(T station)
    {
        return false;
    }

    /// <summary>Puts one input into the station. True if it went in.</summary>
    protected virtual bool Feed(T station, ItemDrop.ItemData input)
    {
        return false;
    }

    /// <summary>Why a round with stations in range found nothing to do, e.g. waiting for inputs; null for plain idle.</summary>
    protected virtual string NothingToDoNote(List<T> stations)
    {
        return null;
    }

    internal override bool WantsItem(string sharedName)
    {
        return false;
    }

    protected override string IdleStatus()
    {
        return _handledLastRound > 0 ? Text.Of("$dw_status_idle_handled", _handledLastRound) : "$dw_status_idle";
    }

    protected override void OnRoundEnded(bool finished)
    {
        _station = null;
        _handledLastRound = _handled;
    }

    // ---------------------------------------------------------------- helpers

    protected bool IsValid(T station)
    {
        if (station == null)
        {
            return false;
        }
        ZNetView view = ViewOf(station);
        return view != null && view.IsValid();
    }

    protected List<T> FindStations()
    {
        List<T> found = [];
        foreach (T station in AllStations)
        {
            if (SceneObjects<T>.IsLive(station) && IsValid(station) && InJobArea(station.transform.position))
            {
                found.Add(station);
            }
        }
        return found;
    }

    private IEnumerable<ItemDrop> ProductsNear(T station)
    {
        Vector3 point = OutputPoint(station);
        foreach (ItemDrop drop in ItemDrop.s_instances)
        {
            if (drop != null && drop.m_itemData != null && IsProduct(drop.m_itemData)
                && Vector3.Distance(drop.transform.position, point) < OutputRadius)
            {
                yield return drop;
            }
        }
    }

    protected ItemDrop.ItemData CarriedInput()
    {
        foreach (ItemDrop.ItemData item in Logistics.Carried.GetAllItems())
        {
            if (IsInput(item))
            {
                return item;
            }
        }
        return null;
    }

    /// <summary>Whether the worker has, or can fetch, an input.</summary>
    protected bool InputAvailable()
    {
        return CarriedInput() != null || Logistics.CountAvailable(IsInput) > 0;
    }

    private bool Carrying(System.Func<ItemDrop.ItemData, bool> match)
    {
        foreach (ItemDrop.ItemData item in Logistics.Carried.GetAllItems())
        {
            if (match(item))
            {
                return true;
            }
        }
        return false;
    }

    private bool HasWork(T station)
    {
        if (HasOutput(station))
        {
            return true;
        }
        foreach (ItemDrop _ in ProductsNear(station))
        {
            return true;
        }
        return WillTakeInput(station) && InputAvailable();
    }

    // ---------------------------------------------------------------- rounds

    protected override bool BeginRound()
    {
        _visited.Clear();
        _unreachable.Clear();
        _handled = 0;
        _station = null;
        _fetching = false;

        List<T> stations = FindStations();
        if (stations.Count == 0)
        {
            _handledLastRound = 0;
            IdleNote = NoStationsNote;
            return false;
        }

        _deliverFirst = Carrying(IsProduct);
        bool work = _deliverFirst;
        foreach (T station in stations)
        {
            work = work || HasWork(station);
        }
        if (!work)
        {
            _handledLastRound = 0;
            IdleNote = NothingToDoNote(stations);
            return false;
        }

        _phase = Phase.Plan;
        DvergrWorkersPlugin.Log.LogInfo(Role.Name + " round started with " + stations.Count + " station(s)");
        return true;
    }

    protected override bool RunTask(float dt)
    {
        switch (_phase)
        {
            case Phase.Plan:
                return Plan();
            case Phase.Fetch:
                Fetch(dt);
                return true;
            case Phase.Walk:
                Walk(dt);
                return true;
            case Phase.Work:
                Work(dt);
                return true;
            case Phase.Deposit:
                return Deposit(dt);
            default:
                return false;
        }
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
        if (Carrying(IsProduct) && (_deliverFirst || Logistics.Carried.GetEmptySlots() <= FullSlots))
        {
            _deliverFirst = false;
            return StartDeposit();
        }

        T best = null;
        float bestDist = float.MaxValue;
        foreach (T station in FindStations())
        {
            if (_visited.Contains(station) || _unreachable.Contains(station) || !HasWork(station))
            {
                continue;
            }
            float d = Vector3.Distance(station.transform.position, transform.position);
            if (d < bestDist)
            {
                bestDist = d;
                best = station;
            }
        }

        if (best != null)
        {
            _station = best;
            _fetching = false;
            _phase = Phase.Fetch;
            return true;
        }

        if (Carrying(IsProduct))
        {
            return StartDeposit();
        }
        if (Carrying(IsInput) && StartSurplusDeposit())
        {
            return true;
        }
        EndRound(finished: true);
        return false;
    }

    private void Fetch(float dt)
    {
        if (!IsValid(_station))
        {
            _phase = Phase.Plan;
            return;
        }

        if (_fetching)
        {
            LogisticsResult result = Logistics.Step(dt);
            if (result == LogisticsResult.Moving)
            {
                return;
            }
            if (result == LogisticsResult.Failed)
            {
                DvergrWorkersPlugin.Log.LogInfo(Role.Name + " fetch failed: " + Text.Show(Logistics.Reason));
            }
            _fetching = false;
        }
        else if (WillTakeInput(_station) && CarriedInput() == null && Logistics.CountAvailable(IsInput) > 0
            && Logistics.BeginFetchAny(IsInput, 1))
        {
            _fetching = true;
            SetStatus("$dw_status_fetching_supplies");
            return;
        }

        T station = _station;
        GameObject target = ViewOf(station).gameObject;
        if (!Navigator.Begin(Utils.GetPrefabName(target), from => WorkerNavigator.ClosestPointOnColliders(target, from)))
        {
            DvergrWorkersPlugin.Log.LogInfo("No reachable spot next to " + Utils.GetPrefabName(target) + ", skipping");
            _ = _unreachable.Add(station);
            _phase = Phase.Plan;
            return;
        }
        _phase = Phase.Walk;
        SetStatus(Text.Of("$dw_status_walking_to_the", Text.NameOf(station.gameObject)));
    }

    private void Walk(float dt)
    {
        if (!IsValid(_station))
        {
            _phase = Phase.Plan;
            return;
        }

        switch (Navigator.Step(dt))
        {
            case NavResult.InReach:
            case NavResult.CloseEnough:
                ViewOf(_station).ClaimOwnership();
                _phase = Phase.Work;
                _stage = Stage.Release;
                _timer = 0.2f;
                SetStatus(Text.Of("$dw_status_tending", Text.NameOf(_station.gameObject)));
                break;

            case NavResult.Failed:
                _ = _unreachable.Add(_station);
                _phase = Phase.Plan;
                break;
        }
    }

    private void Work(float dt)
    {
        if (!IsValid(_station))
        {
            _phase = Phase.Plan;
            return;
        }

        Ai.StopMoving();
        Face();
        if ((_timer -= dt) > 0f)
        {
            return;
        }

        switch (_stage)
        {
            case Stage.Release:
                if (HasOutput(_station))
                {
                    _timer = Release(_station);
                    Animation.PlaySwing();
                }
                _stage = Stage.Collect;
                _collectTries = 0;
                break;

            case Stage.Collect:
                CollectOne();
                break;

            case Stage.Feed:
                ItemDrop.ItemData input = WillTakeInput(_station) ? CarriedInput() : null;
                if (input != null && Feed(_station, input))
                {
                    _handled++;
                    Animation.PlaySwing();
                    _timer = FeedInterval;
                }
                _ = _visited.Add(_station);
                _phase = Phase.Plan;
                break;
        }
    }

    private void CollectOne()
    {
        _timer = CollectInterval;

        ItemDrop target = null;
        foreach (ItemDrop drop in ProductsNear(_station))
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
        int stack = target.m_itemData.m_stack;
        if (Humanoid.Pickup(target.gameObject, false, false))
        {
            _handled += stack;
            _collectTries = 0;
            Animation.PlayPickup();
        }
    }

    private void Face()
    {
        Vector3 dir = _station.transform.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.01f)
        {
            Character.SetLookDir(dir.normalized);
            Ai.LookTowards(dir);
        }
    }

    // ---------------------------------------------------------------- delivering

    private bool StartDeposit()
    {
        if (!Logistics.BeginDeposit(IsProduct))
        {
            return Abort(Logistics.Reason);
        }
        _depositSurplus = false;
        _phase = Phase.Deposit;
        SetStatus("$dw_status_delivering_product");
        return true;
    }

    /// <summary>Stores inputs it could not use, preferably in an input chest where they will be fetched again.</summary>
    private bool StartSurplusDeposit()
    {
        if (!Logistics.BeginDeposit(IsInput, ChestKind.Input) && !Logistics.BeginDeposit(IsInput, ChestKind.Output))
        {
            return false;
        }
        _depositSurplus = true;
        _phase = Phase.Deposit;
        SetStatus("$dw_status_storing_spares");
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
                // No chest has room for the spare inputs; they stay with the worker.
                EndRound(finished: true);
                return false;
            }
            return Abort("$dw_reason_output_full");
        }

        // Still carrying product: the chest filled up, so try the next closest one.
        if (!_depositSurplus && Carrying(IsProduct))
        {
            return StartDeposit();
        }
        _phase = Phase.Plan;
        return true;
    }
}
