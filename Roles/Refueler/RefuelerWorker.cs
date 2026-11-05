using System;
using System.Collections.Generic;

using DvergrWorkers.Core;
using DvergrWorkers.Logistics;
using DvergrWorkers.Movement;

using UnityEngine;

namespace DvergrWorkers.Roles.Refueler;

/// <summary>
/// Refueler role: tops up the torches, fires, braziers, hearths, stone ovens, hot tubs and shield generators (including Home Bubbles) inside the job radius with resin, wood,
/// coal, greydwarf eyes or guck from the input chest (or the blacksmith's coal from an output chest), carrying a Dvergr lantern as it goes. It fetches fuel for as many fires
/// as it can in one trip and returns what is left over to the input chest.
/// </summary>
public class RefuelerWorker : WorkerBrain
{
    private enum Phase { Plan = 0, Fetch = 1, Walk = 2, Work = 3, Return = 4, PickUp = 5 }

    private const float AddInterval = 0.35f;
    private const float PickUpInterval = 0.3f;
    private const int MaxPickUpTries = 12;

    /// <summary>Item prefab names of the fuels the refueler uses. Fires that burn anything else are left alone.</summary>
    private static readonly string[] FuelPrefabs = ["Resin", "Coal", "Wood", "GreydwarfEye", "Guck", "BoneFragments", "CharredBone"];

    private Phase _phase = Phase.Plan;
    private Component _fire;
    private string _fuelShared;
    private int _toAdd;
    private float _timer;
    private int _pickUpTries;
    private bool _starved;
    private string _missingFuel = "";
    private int _refueledThisRound;
    private int _refueledLastRound;
    private readonly HashSet<Component> _served = [];
    private readonly HashSet<Component> _unreachable = [];
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
        return FuelSharedNames().Contains(sharedName);
    }

    private static HashSet<string> _fuelSharedNames;

    /// <summary>Shared names of <see cref="FuelPrefabs"/>. Every worker's supply check asks every worker this, so it is looked up once.</summary>
    private static HashSet<string> FuelSharedNames()
    {
        if (_fuelSharedNames != null)
        {
            return _fuelSharedNames;
        }

        HashSet<string> names = [];
        foreach (string name in FuelPrefabs)
        {
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(name.Trim()) : null;
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop != null)
            {
                _ = names.Add(drop.m_itemData.m_shared.m_name);
            }
        }

        // Only kept once the item database has loaded, so an early call does not leave it empty.
        if (names.Count > 0)
        {
            _fuelSharedNames = names;
        }
        return names;
    }

    private static ZNetView View(Component fire)
    {
        return fire switch
        {
            Fireplace f => f.m_nview,
            CookingStation c => c.m_nview,
            Smelter m => m.m_nview,
            ShieldGenerator g => g.m_nview,
            _ => null,
        };
    }

    private static bool IsValid(Component fire)
    {
        if (fire == null)
        {
            return false;
        }
        ZNetView view = View(fire);
        return view != null && view.IsValid();
    }

    private static float Fuel(Component fire)
    {
        float fallback = fire is ShieldGenerator shield ? shield.m_defaultFuel : 0f;
        return View(fire).GetZDO().GetFloat(ZDOVars.s_fuel, fallback);
    }

    private static float MaxFuel(Component fire)
    {
        return fire switch
        {
            Fireplace f => f.m_maxFuel,
            CookingStation c => c.m_maxFuel,
            Smelter m => m.m_maxFuel,
            ShieldGenerator g => g.m_maxFuel,
            _ => 0f,
        };
    }

    /// <summary>Every item a piece accepts as fuel. Most take one; shield generators take bones or charred bones.</summary>
    private static List<ItemDrop> FuelItems(Component fire)
    {
        List<ItemDrop> items = [];
        switch (fire)
        {
            case Fireplace f:
                items.Add(f.m_fuelItem);
                break;
            case CookingStation c:
                items.Add(c.m_fuelItem);
                break;
            case Smelter m:
                items.Add(m.m_fuelItem);
                break;
            case ShieldGenerator g:
                items.AddRange(g.m_fuelItems);
                break;
        }
        _ = items.RemoveAll(item => item == null);
        return items;
    }

    /// <summary>The fuel to use for a piece: the first one the refueler can get hold of, or failing that the first it accepts.</summary>
    private ItemDrop FuelItem(Component fire)
    {
        ItemDrop first = null;
        foreach (ItemDrop item in FuelItems(fire))
        {
            if (!IsAllowedFuel(item))
            {
                continue;
            }
            if (SupplyAt(fire, item.m_itemData.m_shared.m_name) > 0)
            {
                return item;
            }
            first ??= item;
        }
        return first;
    }

    private static bool IsAllowedFuel(ItemDrop item)
    {
        return Array.IndexOf(FuelPrefabs, item.gameObject.name.Trim()) >= 0;
    }

    private static string PrefabOf(Component fire)
    {
        return Utils.GetPrefabName(View(fire).gameObject);
    }

    private string FuelShared(Component fire)
    {
        return FuelItem(fire).m_itemData.m_shared.m_name;
    }

    private bool IsFuel(ItemDrop.ItemData item)
    {
        return item != null && _fuelNames.Contains(item.m_shared.m_name);
    }

    /// <summary>How many fuel items this fire can still take, or 0 if it should be left alone.</summary>
    private static int Room(Component fire)
    {
        float fuel = Fuel(fire);
        float max = MaxFuel(fire);
        float threshold = max * WorkerConfig.RefuelBelow.Value;
        return fuel <= threshold ? Mathf.FloorToInt(max - fuel) : 0;
    }

    /// <summary>Whether this is a fuel-burning piece the refueler could tend, before looking at which fuel it burns.</summary>
    private static bool IsRefillable(Component fire)
    {
        if (!IsValid(fire) || FuelItems(fire).Count == 0)
        {
            return false;
        }

        // Turned-off fires stay off. Stone ovens burn fuel, but cooking stations over an open fire do not. Smelters
        // only count if they do nothing but burn fuel, i.e. hot tubs: smelters, furnaces and refineries belong to
        // the machine tenders, which top up their fuel along with their ore.
        return fire switch
        {
            Fireplace f => !f.m_infiniteFuel && f.m_canRefill && f.m_secPerFuel > 0f
                && f.m_nview.GetZDO().GetInt(ZDOVars.s_state, 1) == 1,
            CookingStation c => c.m_useFuel && c.m_maxFuel > 0,
            Smelter m => m.m_maxFuel > 0 && m.m_maxOre == 0 && m.m_conversion.Count == 0,
            ShieldGenerator g => g.m_maxFuel > 0,
            _ => false,
        };
    }

    /// <summary>Fires the refueler looks after: player-built, refillable, using one of the configured fuels.</summary>
    private List<Component> FindFires()
    {
        List<Component> found = [];
        _fuelNames.Clear();

        List<Component> candidates = [];
        candidates.AddRange(FindObjectsByType<Fireplace>(FindObjectsSortMode.None));
        candidates.AddRange(FindObjectsByType<CookingStation>(FindObjectsSortMode.None));
        candidates.AddRange(FindObjectsByType<Smelter>(FindObjectsSortMode.None));
        candidates.AddRange(FindObjectsByType<ShieldGenerator>(FindObjectsSortMode.None));

        foreach (Component fire in candidates)
        {
            if (!IsRefillable(fire))
            {
                continue;
            }
            if (!InJobArea(fire.transform.position) || FuelItem(fire) == null)
            {
                continue;
            }

            // Fires that came with the world are not ours to tend.
            Piece piece = fire.GetComponentInParent<Piece>();
            if (piece == null || !piece.IsPlacedByPlayer())
            {
                continue;
            }

            found.Add(fire);
            foreach (ItemDrop item in FuelItems(fire))
            {
                if (IsAllowedFuel(item))
                {
                    _ = _fuelNames.Add(item.m_itemData.m_shared.m_name);
                }
            }
        }
        return found;
    }

    private int Supply(string sharedName)
    {
        return Logistics.Carried.CountItems(sharedName) + Logistics.AvailableTo(sharedName);
    }

    /// <summary>Fuel lying on the ground right next to this fire, e.g. wood a player dropped by a hearth.</summary>
    private int GroundAt(Component fire, string sharedName)
    {
        return GroundItems.CountNear(Role, View(fire).gameObject, sharedName);
    }

    /// <summary>All the fuel the refueler can get for this fire: carried, in chests, and dropped next to it.</summary>
    private int SupplyAt(Component fire, string sharedName)
    {
        return Supply(sharedName) + GroundAt(fire, sharedName);
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

        List<Component> fires = FindFires();
        bool work = false;
        foreach (Component fire in fires)
        {
            if (Room(fire) <= 0)
            {
                continue;
            }
            if (SupplyAt(fire, FuelShared(fire)) > 0)
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

    private string FuelName(Component fire)
    {
        return FuelItem(fire).gameObject.name.ToLowerInvariant();
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
            Phase.PickUp => PickUp(dt),
            _ => false,
        };
    }

    private bool Plan()
    {
        List<Component> fires = FindFires();

        // Nearest fire that needs fuel and that we can get fuel for.
        Component best = null;
        float bestDist = float.MaxValue;
        foreach (Component fire in fires)
        {
            if (_served.Contains(fire) || _unreachable.Contains(fire) || Room(fire) <= 0)
            {
                continue;
            }
            if (SupplyAt(fire, FuelShared(fire)) <= 0)
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
        foreach (Component fire in fires)
        {
            if (!_served.Contains(fire) && !_unreachable.Contains(fire) && FuelShared(fire) == _fuelShared)
            {
                wanted += Math.Max(0, Room(fire));
            }
        }
        // Fuel dropped next to this fire is picked up there, so it need not come from a chest.
        int ground = GroundAt(best, _fuelShared);
        int missing = Math.Min(wanted - ground, Supply(_fuelShared)) - Logistics.Carried.CountItems(_fuelShared);
        _toAdd = Math.Min(Room(best), Usable());

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

        _toAdd = IsValid(_fire) ? Math.Min(Room(_fire), Usable()) : 0;
        return BeginWalk();
    }

    /// <summary>Fuel for the current fire: what the worker carries plus what lies next to the fire.</summary>
    private int Usable()
    {
        return Logistics.Carried.CountItems(_fuelShared) + GroundAt(_fire, _fuelShared);
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

        Component fire = _fire;
        if (!Navigator.Begin(PrefabOf(fire), from => WorkerNavigator.ClosestPointOnColliders(View(fire).gameObject, from), rememberFailure: true))
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
                View(_fire).ClaimOwnership();
                _phase = Phase.PickUp;
                _pickUpTries = 0;
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

    /// <summary>
    /// Picks up fuel lying next to the fire, only as much as this fire takes; the rest stays on the ground for
    /// next time rather than being carried off.
    /// </summary>
    private bool PickUp(float dt)
    {
        if (!IsValid(_fire))
        {
            _phase = Phase.Plan;
            return true;
        }

        Ai.StopMoving();
        _timer -= dt;
        if (_timer > 0f)
        {
            return true;
        }
        _timer = PickUpInterval;

        int wanted = _toAdd - Logistics.Carried.CountItems(_fuelShared);
        ItemDrop drop = wanted > 0 ? GroundItems.FirstNear(Role, View(_fire).gameObject, _fuelShared) : null;
        if (drop == null || ++_pickUpTries > MaxPickUpTries)
        {
            _phase = Phase.Work;
            _timer = 0.1f;
            return true;
        }

        if (GroundItems.Take(Humanoid, drop, wanted) > 0)
        {
            _pickUpTries = 0;
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
        bool full = fuel == null || _toAdd <= 0 || Mathf.CeilToInt(Fuel(_fire)) >= MaxFuel(_fire);
        if (full)
        {
            _ = _served.Add(_fire);
            _phase = Phase.Plan;
            return true;
        }

        _ = Logistics.Carried.RemoveItem(fuel, 1);
        View(_fire).InvokeRPC("RPC_AddFuel");
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
