using System.Collections.Generic;

using DvergrWorkers.Core;
using DvergrWorkers.Logistics;
using DvergrWorkers.Movement;

using UnityEngine;

namespace DvergrWorkers.Roles.MachineTender;

/// <summary>
/// <para>
/// Works the Deep North casting line. It keeps frigid kilns topped up with ice like the blacksmith keeps a kiln
/// fed, and collects the liquid frost. Then it tends the frost foundries: it takes out finished gear, tops up their
/// liquid frost (what the kilns just made first, then what is in the chests) and puts in a cast players have
/// prepared from the input chest. Liquid frost and gear go to output chests; spare casts go back to the input chest.
/// </para>
/// <para>
/// A foundry is a cooking station with one slot: the cast goes in, and when it is done the gear is thrown out at
/// its spawn point. The game records whoever added the cast as the crafter, which would be whoever runs this worker,
/// so the worker remembers the player who prepared the cast and puts their name on the gear.
/// </para>
/// </summary>
public class FrostWorker : MachineTenderWorker
{
    private enum FoundryStage { Fetch = 0, Walk = 1, Empty = 2, Collect = 3, Feed = 4 }

    private const string FoundryPrefab = "piece_FrostFoundry";
    private const float EmptyWait = 0.8f;
    private const float CollectInterval = 0.3f;
    private const float FeedInterval = 0.35f;
    private const int MaxCollectTries = 12;

    /// <summary>The foundry throws its gear out with some force, so it can land a few metres from the spawn point.</summary>
    private const float OutputRadius = 5f;

    /// <summary>On the foundry: the cast this worker put in, and who prepared it, to name them as the gear's crafter.</summary>
    private const string CastKey = "DW_Cast";
    private const string CrafterIdKey = "DW_CastCrafterId";
    private const string CrafterNameKey = "DW_CastCrafterName";

    private readonly HashSet<CookingStation> _visited = [];
    private readonly HashSet<CookingStation> _unreachableFoundries = [];
    private CookingStation _foundry;
    private FoundryStage _stage;
    private int _fetchStep;
    private bool _fetching;
    private float _timer;
    private int _collectTries;

    internal override WorkerRole Role => WorkerRoles.Frost;

    protected override string AllowedMachines => "piece_FrostKiln";

    protected override string NoMachinesNote => "$dw_status_no_frost_kiln";

    protected override bool GatherAfterMachines => true;

    internal override bool WantsItem(string sharedName)
    {
        return base.WantsItem(sharedName) || IsFuelName(sharedName) || IsCastName(sharedName);
    }

    protected override bool IsGatheredProduct(ItemDrop.ItemData item)
    {
        return Catalogue.Gear.Contains(item.m_shared.m_name);
    }

    /// <summary>Casts a foundry had no room for go back to the input chest.</summary>
    protected override bool IsSurplus(ItemDrop.ItemData item)
    {
        return IsCastName(item.m_shared.m_name);
    }

    protected override void OnRoundEnded(bool finished)
    {
        base.OnRoundEnded(finished);
        _foundry = null;
    }

    // ---------------------------------------------------------------- what a foundry takes and makes

    /// <summary>The foundry's casts, fuel and gear, read once from its prefab so modded casts count too.</summary>
    private static class Catalogue
    {
        public static readonly HashSet<string> Gear = [];

        /// <summary>Cast shared name to prefab name, which is what the foundry is told to cook.</summary>
        public static readonly Dictionary<string, string> CastPrefabs = [];

        /// <summary>Cast prefab name to the prefab name of the gear it becomes.</summary>
        public static readonly Dictionary<string, string> GearOf = [];

        public static string Fuel = "";

        public static void Load(CookingStation foundry)
        {
            if (CastPrefabs.Count > 0)
            {
                return;
            }
            foreach (CookingStation.ItemConversion conversion in foundry.m_conversion)
            {
                if (conversion.m_from == null || conversion.m_to == null)
                {
                    continue;
                }
                _ = Gear.Add(conversion.m_to.m_itemData.m_shared.m_name);
                CastPrefabs[conversion.m_from.m_itemData.m_shared.m_name] = conversion.m_from.gameObject.name;
                GearOf[conversion.m_from.gameObject.name] = conversion.m_to.gameObject.name;
            }
            if (foundry.m_fuelItem != null)
            {
                Fuel = foundry.m_fuelItem.m_itemData.m_shared.m_name;
            }
        }
    }

    private static bool IsFuelName(string sharedName)
    {
        return Catalogue.Fuel.Length > 0 && sharedName == Catalogue.Fuel;
    }

    private static bool IsCastName(string sharedName)
    {
        return Catalogue.CastPrefabs.ContainsKey(sharedName);
    }

    private static bool IsFuel(ItemDrop.ItemData item)
    {
        return IsFuelName(item.m_shared.m_name);
    }

    private static bool IsCast(ItemDrop.ItemData item)
    {
        return IsCastName(item.m_shared.m_name);
    }

    private static bool IsValid(CookingStation foundry)
    {
        return foundry != null && foundry.m_nview != null && foundry.m_nview.IsValid();
    }

    private List<CookingStation> FindFoundries()
    {
        List<CookingStation> found = [];
        foreach (CookingStation station in Scene.CookingStations.All)
        {
            if (SceneObjects<CookingStation>.IsLive(station) && IsValid(station)
                && Utils.GetPrefabName(station.m_nview.gameObject) == FoundryPrefab
                && InJobArea(station.transform.position))
            {
                Catalogue.Load(station);
                found.Add(station);
            }
        }
        return found;
    }

    private static int FuelRoom(CookingStation foundry)
    {
        return foundry.m_useFuel ? Mathf.FloorToInt(foundry.m_maxFuel - foundry.GetFuel()) : 0;
    }

    /// <summary>A slot is free now or will be once the finished gear is taken out.</summary>
    private static bool WillHaveFreeSlot(CookingStation foundry)
    {
        return foundry.GetFreeSlot() != -1 || foundry.HaveDoneItem();
    }

    private static IEnumerable<ItemDrop> GearNear(CookingStation foundry)
    {
        Vector3 point = foundry.m_spawnPoint != null ? foundry.m_spawnPoint.position : foundry.transform.position;
        foreach (ItemDrop drop in ItemDrop.s_instances)
        {
            if (drop != null && drop.m_itemData != null && Catalogue.Gear.Contains(drop.m_itemData.m_shared.m_name)
                && Vector3.Distance(drop.transform.position, point) < OutputRadius)
            {
                yield return drop;
            }
        }
    }

    private ItemDrop.ItemData CarriedCast()
    {
        foreach (ItemDrop.ItemData item in Logistics.Carried.GetAllItems())
        {
            if (IsCast(item))
            {
                return item;
            }
        }
        return null;
    }

    private int FuelSupply()
    {
        return Catalogue.Fuel.Length == 0 ? 0 : Logistics.Carried.CountItems(Catalogue.Fuel) + Logistics.AvailableTo(Catalogue.Fuel);
    }

    private bool HasWork(CookingStation foundry)
    {
        if (foundry.HaveDoneItem())
        {
            return true;
        }
        foreach (ItemDrop _ in GearNear(foundry))
        {
            return true;
        }
        return (FuelRoom(foundry) > 0 && FuelSupply() > 0)
            || (WillHaveFreeSlot(foundry) && (CarriedCast() != null || Logistics.CountAvailable(IsCast) > 0));
    }

    private CookingStation NextFoundry()
    {
        CookingStation best = null;
        float bestDist = float.MaxValue;
        foreach (CookingStation foundry in FindFoundries())
        {
            if (_visited.Contains(foundry) || _unreachableFoundries.Contains(foundry) || !HasWork(foundry))
            {
                continue;
            }
            float d = Vector3.Distance(foundry.transform.position, transform.position);
            if (d < bestDist)
            {
                bestDist = d;
                best = foundry;
            }
        }
        return best;
    }

    // ---------------------------------------------------------------- tending foundries

    protected override bool BeginGather()
    {
        _visited.Clear();
        _unreachableFoundries.Clear();
        _foundry = null;
        return NextFoundry() != null;
    }

    protected override bool Gather(float dt)
    {
        if (_foundry == null)
        {
            _foundry = NextFoundry();
            if (_foundry == null)
            {
                return false;
            }
            _stage = FoundryStage.Fetch;
            _fetchStep = 0;
            _fetching = false;
        }

        if (!IsValid(_foundry))
        {
            _foundry = null;
            return true;
        }

        if (_stage == FoundryStage.Fetch)
        {
            Fetch(dt);
            return true;
        }
        if (_stage == FoundryStage.Walk)
        {
            Walk(dt);
            return true;
        }

        Ai.StopMoving();
        Face();
        if ((_timer -= dt) > 0f)
        {
            return true;
        }

        switch (_stage)
        {
            case FoundryStage.Empty:
                if (_foundry.HaveDoneItem())
                {
                    _foundry.m_nview.InvokeRPC("RPC_RemoveDoneItem", transform.position, 1);
                    _timer = EmptyWait;
                }
                _stage = FoundryStage.Collect;
                _collectTries = 0;
                break;

            case FoundryStage.Collect:
                CollectOne();
                break;

            case FoundryStage.Feed:
                FeedOne();
                break;
        }
        return true;
    }

    /// <summary>Fetches liquid frost to fill the foundry, then a cast if its slot will be free.</summary>
    private void Fetch(float dt)
    {
        if (_fetching)
        {
            if (Logistics.Step(dt) == LogisticsResult.Moving)
            {
                return;
            }
            _fetching = false;
            _fetchStep++;
        }

        while (_fetchStep < 2)
        {
            if (_fetchStep == 0)
            {
                int wanted = Mathf.Min(FuelRoom(_foundry) - Logistics.Carried.CountItems(Catalogue.Fuel),
                    Catalogue.Fuel.Length > 0 ? Logistics.AvailableTo(Catalogue.Fuel) : 0);
                if (wanted > 0 && Logistics.BeginFetchAny(IsFuel, wanted))
                {
                    _fetching = true;
                    SetStatus(Text.Of("$dw_status_fetching_from_chest", Catalogue.Fuel));
                    return;
                }
            }
            else if (WillHaveFreeSlot(_foundry) && CarriedCast() == null && Logistics.CountAvailable(IsCast) > 0
                && Logistics.BeginFetchAny(IsCast, 1))
            {
                _fetching = true;
                SetStatus("$dw_status_fetching_cast");
                return;
            }
            _fetchStep++;
        }

        CookingStation foundry = _foundry;
        if (!Navigator.Begin(FoundryPrefab, from => WorkerNavigator.ClosestPointOnColliders(foundry.m_nview.gameObject, from)))
        {
            DvergrWorkersPlugin.Log.LogInfo("No reachable spot next to " + FoundryPrefab + ", skipping");
            _ = _unreachableFoundries.Add(foundry);
            _foundry = null;
            return;
        }
        _stage = FoundryStage.Walk;
        SetStatus(Text.Of("$dw_status_walking_to_the", Text.NameOf(foundry.gameObject)));
    }

    private void Walk(float dt)
    {
        switch (Navigator.Step(dt))
        {
            case NavResult.InReach:
            case NavResult.CloseEnough:
                _foundry.m_nview.ClaimOwnership();
                _stage = FoundryStage.Empty;
                _timer = 0.2f;
                SetStatus(Text.Of("$dw_status_tending", Text.NameOf(_foundry.gameObject)));
                break;

            case NavResult.Failed:
                _ = _unreachableFoundries.Add(_foundry);
                _foundry = null;
                break;
        }
    }

    private void CollectOne()
    {
        _timer = CollectInterval;

        ItemDrop target = null;
        foreach (ItemDrop drop in GearNear(_foundry))
        {
            target = drop;
            break;
        }

        if (target == null || ++_collectTries > MaxCollectTries)
        {
            _stage = FoundryStage.Feed;
            _timer = 0.1f;
            return;
        }

        // Drops are owned by whoever last touched them; ask for ownership and retry for a moment.
        target.RequestOwn();
        bool named = NameCrafter(target);
        if (Humanoid.Pickup(target.gameObject, false, false))
        {
            if (named)
            {
                _foundry.m_nview.GetZDO().Set(CastKey, "");
            }
            AddHandled(1);
            _collectTries = 0;
            Animation.PlayPickup();
        }
    }

    /// <summary>Puts the name of the player who prepared the cast on the gear it became. True if it did.</summary>
    private bool NameCrafter(ItemDrop gear)
    {
        ZDO zdo = _foundry.m_nview.GetZDO();
        string cast = zdo.GetString(CastKey, "");
        if (cast.Length == 0 || !Catalogue.GearOf.TryGetValue(cast, out string gearPrefab)
            || Utils.GetPrefabName(gear.gameObject) != gearPrefab)
        {
            return false;
        }

        string crafter = zdo.GetString(CrafterNameKey, "");
        if (crafter.Length > 0)
        {
            gear.m_itemData.m_crafterID = zdo.GetLong(CrafterIdKey, 0L);
            gear.m_itemData.m_crafterName = crafter;
        }
        return true;
    }

    private void FeedOne()
    {
        _timer = FeedInterval;

        ItemDrop.ItemData fuel = null;
        foreach (ItemDrop.ItemData item in Logistics.Carried.GetAllItems())
        {
            if (IsFuel(item))
            {
                fuel = item;
                break;
            }
        }
        if (fuel != null && _foundry.m_useFuel && _foundry.GetFuel() <= _foundry.m_maxFuel - 1)
        {
            _ = Logistics.Carried.RemoveItem(fuel, 1);
            _foundry.m_nview.InvokeRPC("RPC_AddFuel");
            AddHandled(1);
            Animation.PlaySwing();
            return;
        }

        ItemDrop.ItemData cast = CarriedCast();
        if (cast != null && _foundry.GetFreeSlot() != -1
            && Catalogue.CastPrefabs.TryGetValue(cast.m_shared.m_name, out string prefab) && _foundry.IsItemAllowed(prefab))
        {
            ZDO zdo = _foundry.m_nview.GetZDO();
            zdo.Set(CastKey, prefab);
            zdo.Set(CrafterIdKey, cast.m_crafterID);
            zdo.Set(CrafterNameKey, cast.m_crafterName ?? "");
            bool cheated = cast.m_cheated;
            _ = Logistics.Carried.RemoveItem(cast, 1);
            _foundry.m_nview.InvokeRPC("RPC_AddItem", prefab, cheated);
            AddHandled(1);
            Animation.PlaySwing();
            return;
        }

        _ = _visited.Add(_foundry);
        _foundry = null;
    }

    private void Face()
    {
        Vector3 dir = _foundry.transform.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.01f)
        {
            Character.SetLookDir(dir.normalized);
            Ai.LookTowards(dir);
        }
    }
}
