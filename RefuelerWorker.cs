using System;
using System.Collections.Generic;

using UnityEngine;

namespace DvergrWorkers;

/// <summary>
/// Refueler role: tops up the torches, fires, braziers and hearths inside the job radius with resin, wood,
/// coal, greydwarf eyes or guck from the input chest (or the blacksmith's coal from an output chest), carrying a Dvergr lantern as it goes. It fetches fuel for as many fires
/// as it can in one trip and returns what is left over to the input chest.
/// </summary>
public class RefuelerWorker : WorkerBrain
{
    private enum Phase { Plan = 0, Fetch = 1, Walk = 2, Work = 3, Return = 4 }

    private const float AddInterval = 0.35f;

    /// <summary>Item prefab names of the fuels the refueler uses. Fires that burn anything else are left alone.</summary>
    private static readonly string[] FuelPrefabs = ["Resin", "Coal", "Wood", "GreydwarfEye", "Guck"];

    private Phase _phase = Phase.Plan;
    private Fireplace _fire;
    private string _fuelShared;
    private int _toAdd;
    private float _timer;
    private bool _starved;
    private string _missingFuel = "";
    private int _refueledThisRound;
    private int _refueledLastRound;
    private readonly HashSet<Fireplace> _served = [];
    private readonly HashSet<Fireplace> _unreachable = [];
    private readonly HashSet<string> _fuelNames = [];

    internal override WorkerRole Role => WorkerRoles.Refueler;

    protected override string IdleStatus()
    {
        return _refueledLastRound > 0
            ? "Idle (refuelled " + _refueledLastRound + " fires last round)"
            : "Idle";
    }

    protected override void OnRoundEnded(bool finished)
    {
        _fire = null;
        _refueledLastRound = _refueledThisRound;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The fuels the refueler is allowed to use.</summary>
    internal override bool WantsItem(string sharedName)
    {
        foreach (string name in FuelPrefabs)
        {
            GameObject prefab = ObjectDB.instance.GetItemPrefab(name.Trim());
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop != null && drop.m_itemData.m_shared.m_name == sharedName)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsValid(Fireplace fire)
    {
        return fire != null && fire.m_nview != null && fire.m_nview.IsValid();
    }

    private static float Fuel(Fireplace fire)
    {
        return fire.m_nview.GetZDO().GetFloat(ZDOVars.s_fuel);
    }

    private static string PrefabOf(Fireplace fire)
    {
        return Utils.GetPrefabName(fire.m_nview.gameObject);
    }

    private static string FuelShared(Fireplace fire)
    {
        return fire.m_fuelItem.m_itemData.m_shared.m_name;
    }

    private bool IsFuel(ItemDrop.ItemData item)
    {
        return item != null && _fuelNames.Contains(item.m_shared.m_name);
    }

    /// <summary>How many fuel items this fire can still take, or 0 if it should be left alone.</summary>
    private static int Room(Fireplace fire)
    {
        float fuel = Fuel(fire);
        float threshold = fire.m_maxFuel * WorkerConfig.RefuelBelow.Value;
        return fuel <= threshold ? Mathf.FloorToInt(fire.m_maxFuel - fuel) : 0;
    }

    /// <summary>Fires the refueler looks after: player-built, refillable, using one of the configured fuels.</summary>
    private List<Fireplace> FindFires()
    {
        HashSet<string> allowed = [.. FuelPrefabs];
        List<Fireplace> found = [];
        _fuelNames.Clear();

        foreach (Fireplace fire in FindObjectsByType<Fireplace>(FindObjectsSortMode.None))
        {
            if (!IsValid(fire) || fire.m_infiniteFuel || !fire.m_canRefill || fire.m_fuelItem == null || fire.m_secPerFuel <= 0f)
            {
                continue;
            }
            if (!InJobArea(fire.transform.position) || !allowed.Contains(fire.m_fuelItem.gameObject.name.Trim()))
            {
                continue;
            }

            // Turned-off fires stay off, and fires that came with the world are not ours to tend.
            Piece piece = fire.GetComponentInParent<Piece>();
            if (piece == null || !piece.IsPlacedByPlayer() || fire.m_nview.GetZDO().GetInt(ZDOVars.s_state, 1) != 1)
            {
                continue;
            }

            found.Add(fire);
            _ = _fuelNames.Add(FuelShared(fire));
        }
        return found;
    }

    private int Supply(string sharedName)
    {
        return Logistics.Carried.CountItems(sharedName) + Logistics.AvailableTo(sharedName);
    }

    private bool CarryingFuel()
    {
        foreach (ItemDrop.ItemData item in Logistics.Carried.GetAllItems())
        {
            if (IsFuel(item))
            {
                return true;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- rounds

    protected override bool BeginRound()
    {
        _served.Clear();
        _unreachable.Clear();
        _starved = false;
        _missingFuel = "";
        _refueledThisRound = 0;
        _fire = null;

        List<Fireplace> fires = FindFires();
        bool work = false;
        foreach (Fireplace fire in fires)
        {
            if (Room(fire) <= 0)
            {
                continue;
            }
            if (Supply(FuelShared(fire)) > 0)
            {
                work = true;
                break;
            }
            _starved = true;
            _missingFuel = FuelName(fire);
        }

        if (!work)
        {
            _refueledLastRound = 0;
            IdleNote = _starved ? "Waiting for " + _missingFuel + " in a chest" : null;
            return false;
        }

        Equipment.EquipTool();
        _phase = Phase.Plan;
        DvergrWorkersPlugin.Log.LogInfo("Refueler round started");
        return true;
    }

    private static string FuelName(Fireplace fire)
    {
        return fire.m_fuelItem.gameObject.name.ToLowerInvariant();
    }

    protected override bool RunTask(float dt)
    {
        return _phase switch
        {
            Phase.Plan => Plan(),
            Phase.Fetch => Fetch(dt),
            Phase.Walk => Walk(dt),
            Phase.Work => Work(dt),
            Phase.Return => Return(dt),
            _ => false,
        };
    }

    private bool Plan()
    {
        List<Fireplace> fires = FindFires();

        // Nearest fire that needs fuel and that we can get fuel for.
        Fireplace best = null;
        float bestDist = float.MaxValue;
        foreach (Fireplace fire in fires)
        {
            if (_served.Contains(fire) || _unreachable.Contains(fire) || Room(fire) <= 0)
            {
                continue;
            }
            if (Supply(FuelShared(fire)) <= 0)
            {
                _starved = true;
                _missingFuel = FuelName(fire);
                continue;
            }

            float d = Vector3.Distance(fire.transform.position, transform.position);
            if (d < bestDist)
            {
                bestDist = d;
                best = fire;
            }
        }

        if (best == null)
        {
            // All done (or out of fuel): put leftovers back in the input chest.
            if (CarryingFuel())
            {
                return StartReturn();
            }
            if (_refueledThisRound == 0 && _starved)
            {
                IdleNote = "Waiting for " + _missingFuel + " in a chest";
            }
            EndRound(finished: true);
            return false;
        }

        _fire = best;
        _fuelShared = FuelShared(best);

        // Fetch enough for this fire and every other one that burns the same fuel, in a single trip.
        int wanted = 0;
        foreach (Fireplace fire in fires)
        {
            if (!_served.Contains(fire) && !_unreachable.Contains(fire) && FuelShared(fire) == _fuelShared)
            {
                wanted += Math.Max(0, Room(fire));
            }
        }
        int missing = Math.Min(wanted, Supply(_fuelShared)) - Logistics.Carried.CountItems(_fuelShared);
        _toAdd = Math.Min(Room(best), Logistics.Carried.CountItems(_fuelShared));

        if (missing > 0)
        {
            string shared = _fuelShared;
            if (Logistics.BeginFetchAny(i => i.m_shared.m_name == shared, missing))
            {
                _phase = Phase.Fetch;
                SetStatus("Fetching fuel from a chest");
                return true;
            }
            DvergrWorkersPlugin.Log.LogInfo("Refueler fetch failed: " + Logistics.Reason);
        }
        return BeginWalk();
    }

    private bool Fetch(float dt)
    {
        switch (Logistics.Step(dt))
        {
            case LogisticsResult.Moving:
                return true;
            case LogisticsResult.Failed:
                DvergrWorkersPlugin.Log.LogInfo("Refueler fetch failed: " + Logistics.Reason);
                break;
        }

        _toAdd = IsValid(_fire) ? Math.Min(Room(_fire), Logistics.Carried.CountItems(_fuelShared)) : 0;
        return BeginWalk();
    }

    private bool BeginWalk()
    {
        if (!IsValid(_fire) || _toAdd <= 0)
        {
            if (_fire != null)
            {
                _ = _unreachable.Add(_fire);
            }
            _phase = Phase.Plan;
            return true;
        }

        Fireplace fire = _fire;
        if (!Navigator.Begin(PrefabOf(fire), from => WorkerNavigator.ClosestPointOnColliders(fire.m_nview.gameObject, from), rememberFailure: true))
        {
            DvergrWorkersPlugin.Log.LogInfo("No reachable spot next to " + PrefabOf(fire) + ", skipping");
            _ = _unreachable.Add(fire);
            _phase = Phase.Plan;
            return true;
        }

        _phase = Phase.Walk;
        SetStatus("Walking to a " + PrefabOf(fire).Replace('_', ' '));
        return true;
    }

    private bool Walk(float dt)
    {
        if (!IsValid(_fire))
        {
            _phase = Phase.Plan;
            return true;
        }

        switch (Navigator.Step(dt))
        {
            case NavResult.InReach:
            case NavResult.CloseEnough:
                _fire.m_nview.ClaimOwnership();
                _phase = Phase.Work;
                _timer = 0.2f;
                SetStatus("Refuelling a " + PrefabOf(_fire).Replace('_', ' '));
                break;

            case NavResult.Failed:
                _ = _unreachable.Add(_fire);
                _phase = Phase.Plan;
                break;
        }
        return true;
    }

    private bool Work(float dt)
    {
        if (!IsValid(_fire))
        {
            _phase = Phase.Plan;
            return true;
        }

        Ai.StopMoving();
        Vector3 dir = _fire.transform.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.01f)
        {
            Character.SetLookDir(dir.normalized);
            Ai.LookTowards(dir);
        }

        _timer -= dt;
        if (_timer > 0f)
        {
            return true;
        }
        _timer = AddInterval;

        ItemDrop.ItemData fuel = null;
        foreach (ItemDrop.ItemData candidate in Logistics.Carried.GetAllItems())
        {
            if (candidate.m_shared.m_name == _fuelShared)
            {
                fuel = candidate;
                break;
            }
        }

        // Same limit the game applies when a player adds fuel.
        bool full = fuel == null || _toAdd <= 0 || Mathf.CeilToInt(Fuel(_fire)) >= _fire.m_maxFuel;
        if (full)
        {
            _ = _served.Add(_fire);
            _phase = Phase.Plan;
            return true;
        }

        _ = Logistics.Carried.RemoveItem(fuel, 1);
        _fire.m_nview.InvokeRPC("RPC_AddFuel");
        _toAdd--;
        _refueledThisRound++;
        return true;
    }

    // ---------------------------------------------------------------- returning leftovers

    private bool StartReturn()
    {
        if (!Logistics.BeginDeposit(IsFuel, ChestKind.Input))
        {
            // Nowhere to put it: keep it, and use it as supply next round.
            EndRound(finished: true);
            return false;
        }

        _phase = Phase.Return;
        SetStatus("Returning spare fuel to a chest");
        return true;
    }

    private bool Return(float dt)
    {
        switch (Logistics.Step(dt))
        {
            case LogisticsResult.Moving:
                return true;
            case LogisticsResult.Failed:
                EndRound(finished: true);
                return false;
        }

        if (Logistics.Transferred > 0 && CarryingFuel())
        {
            return StartReturn();
        }
        EndRound(finished: true);
        return false;
    }
}
