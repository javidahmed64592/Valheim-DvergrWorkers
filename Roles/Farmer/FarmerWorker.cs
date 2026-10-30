using System;
using System.Collections.Generic;

using DvergrWorkers.Core;
using DvergrWorkers.Logistics;
using DvergrWorkers.Movement;

using UnityEngine;

namespace DvergrWorkers.Roles.Farmer;

/// <summary>
/// Farmer role: harvests ripe crops growing on tilled soil and plants every free tilled spot in its job
/// radius, holding a cultivator. It never tills (the player decides where the farm is) and ignores tree
/// seeds. Planting is balanced across crops, respects each plant's biome, plants seeds first, and falls
/// back to raw ingredients (a carrot, a turnip...) to grow more seeds when the seeds run low. Seeds go
/// back to the input chest and everything else to output chests.
/// </summary>
public class FarmerWorker : WorkerBrain
{
    private enum Phase { Plan = 0, Fetch = 1, Walk = 2, Plant = 3, Harvest = 4, Collect = 5, Deposit = 6 }

    private const float PlantTime = 0.6f;

    /// <summary>Most path checks made while choosing spots, so one planning pass cannot stall the game.</summary>
    private const int MaxReachChecks = 30;
    private const float HarvestWait = 0.9f;
    private const float CollectInterval = 0.3f;
    private const int MaxCollectTries = 12;
    private const float DropRadius = 3f;
    private const int FullSlots = 2;
    private const float FineStep = 0.5f;
    private const float CoarseStep = 1f;

    private Phase _phase = Phase.Plan;
    private PlantType _batchType;
    private readonly List<Vector3> _batchSpots = [];
    private Pickable _harvest;
    private Vector3 _collectPos;
    private float _timer;
    private int _collectTries;
    private int _planted;
    private int _harvested;
    private int _handledLastRound;
    private bool _noSpots;
    private int _pendingChecks;
    private bool _noSupply;
    private readonly HashSet<PlantType> _loggedBiome = [];
    private readonly HashSet<PlantType> _failedTypes = [];
    private readonly HashSet<Pickable> _failedPickables = [];
    private readonly HashSet<string> _ours = [];
    private readonly Dictionary<string, int> _plantedByFamily = [];
    private readonly List<Occupied> _plantedSpots = [];

    internal override WorkerRole Role => WorkerRoles.Farmer;

    protected override string IdleStatus()
    {
        return _handledLastRound > 0
            ? "Idle (planted/harvested " + _handledLastRound + " last round)"
            : "Idle";
    }

    protected override void OnRoundEnded(bool finished)
    {
        _harvest = null;
        _batchSpots.Clear();
        _handledLastRound = _planted + _harvested;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The seeds and raw ingredients it plants.</summary>
    internal override bool WantsItem(string sharedName)
    {
        return System.Linq.Enumerable.Any(PlantCatalogue.Crops, t => t.RequiredShared == sharedName);
    }

    private int Supply(string sharedName)
    {
        return Logistics.Carried.CountItems(sharedName) + Logistics.AvailableTo(sharedName);
    }

    private bool IsOurs(ItemDrop.ItemData item)
    {
        return item != null && _ours.Contains(item.m_shared.m_name);
    }

    private bool CarryingSeeds()
    {
        foreach (ItemDrop.ItemData item in Logistics.Carried.GetAllItems())
        {
            if (IsOurs(item) && PlantCatalogue.IsSeedName(item.m_shared.m_name))
            {
                return true;
            }
        }
        return false;
    }

    private bool CarryingOther()
    {
        foreach (ItemDrop.ItemData item in Logistics.Carried.GetAllItems())
        {
            if (IsOurs(item) && !PlantCatalogue.IsSeedName(item.m_shared.m_name))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsCultivatedSpot(Vector3 p, out Heightmap heightmap)
    {
        heightmap = Heightmap.FindHeightmap(p);
        return heightmap != null && heightmap.IsCultivated(p);
    }

    /// <summary>Ripe crops standing on tilled soil inside the job area. Wild pickables are left alone.</summary>
    private List<Pickable> RipeCrops()
    {
        List<Pickable> found = [];
        foreach (Pickable pickable in FindObjectsByType<Pickable>(FindObjectsSortMode.None))
        {
            if (pickable == null || pickable.m_nview == null || !pickable.m_nview.IsValid() || pickable.m_picked
                || _failedPickables.Contains(pickable) || !InJobArea(pickable.transform.position))
            {
                continue;
            }
            if (IsCultivatedSpot(pickable.transform.position, out _))
            {
                found.Add(pickable);
            }
        }
        return found;
    }

    /// <summary>How many of each crop are already growing or ready, so planting can even them out.</summary>
    private Dictionary<string, int> CountFamilies()
    {
        Dictionary<string, int> counts = [];
        foreach (PlantType type in PlantCatalogue.Crops)
        {
            counts[type.Family] = 0;
        }

        foreach (Plant plant in FindObjectsByType<Plant>(FindObjectsSortMode.None))
        {
            if (plant != null && InJobArea(plant.transform.position))
            {
                PlantType type = PlantCatalogue.Of(plant.gameObject);
                if (type != null && type.IsTree)
                {
                    type = null;
                }
                if (type != null)
                {
                    counts[type.Family]++;
                }
            }
        }

        foreach (Pickable pickable in FindObjectsByType<Pickable>(FindObjectsSortMode.None))
        {
            if (pickable == null || pickable.m_itemPrefab == null || !InJobArea(pickable.transform.position)
                || !IsCultivatedSpot(pickable.transform.position, out _))
            {
                continue;
            }
            string yield = pickable.m_itemPrefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
            foreach (PlantType type in PlantCatalogue.Crops)
            {
                if (type.YieldShared == yield)
                {
                    counts[type.Family]++;
                    break;
                }
            }
        }

        foreach (KeyValuePair<string, int> planted in _plantedByFamily)
        {
            if (counts.ContainsKey(planted.Key))
            {
                counts[planted.Key] += planted.Value;
            }
        }
        return counts;
    }

    /// <summary>
    /// Free tilled spots, nearest first, where this crop can actually grow: right biome, tilled soil,
    /// open sky and room around it.
    /// </summary>
    private List<Vector3> FindSpots(PlantType type, int max)
    {
        int spaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid");
        int roofMask = LayerMask.GetMask("Default", "static_solid", "piece");
        float radius = WorkerConfig.JobRadius.Value;
        List<Vector3> spots = [];
        int wrongBiome = 0;

        // Every plant needs a clear circle around itself to grow, so a new plant must be at least as far
        // from each neighbour as the larger of the two circles. Checking only the new plant's own circle
        // would let it squeeze in next to a plant that needs more room, and starve that one.
        List<Occupied> occupied = [.. _plantedSpots];
        foreach (Plant plant in FindObjectsByType<Plant>(FindObjectsSortMode.None))
        {
            if (plant != null && Utils.DistanceXZ(plant.transform.position, Home) <= radius + 5f)
            {
                occupied.Add(new Occupied { Position = plant.transform.position, Radius = plant.m_growRadius });
            }
        }

        // Fully grown crops (pickables) block growth around them too.
        foreach (Pickable pickable in FindObjectsByType<Pickable>(FindObjectsSortMode.None))
        {
            if (pickable != null && Utils.DistanceXZ(pickable.transform.position, Home) <= radius + 5f
                && IsCultivatedSpot(pickable.transform.position, out _))
            {
                occupied.Add(new Occupied { Position = pickable.transform.position, Radius = type.Plant.m_growRadius });
            }
        }

        for (float x = -radius; x <= radius; x += CoarseStep)
        {
            for (float z = -radius; z <= radius; z += CoarseStep)
            {
                Vector3 coarse = new(Home.x + x, Home.y, Home.z + z);
                if (!InJobArea(coarse) || !IsCultivatedSpot(coarse, out _))
                {
                    continue;
                }

                foreach (Vector2 offset in new[] { Vector2.zero, new Vector2(FineStep, 0f), new Vector2(0f, FineStep), new Vector2(FineStep, FineStep) })
                {
                    Vector3 p = new(coarse.x + offset.x, 0f, coarse.z + offset.y);
                    p.y = ZoneSystem.instance.GetGroundHeight(p);

                    if (!IsCultivatedSpot(p, out _))
                    {
                        continue;
                    }
                    if (!PlantCatalogue.BiomeAllows(type, p))
                    {
                        wrongBiome++;
                        continue;
                    }
                    if (Physics.CheckSphere(p + (Vector3.up * 0.15f), type.Plant.m_growRadius + 0.2f, spaceMask)
                        || Physics.Raycast(p + (Vector3.up * 0.2f), Vector3.up, 100f, roofMask))
                    {
                        continue;
                    }

                    bool crowded = false;
                    foreach (Occupied other in occupied)
                    {
                        crowded |= Utils.DistanceXZ(other.Position, p) < Mathf.Max(type.Plant.m_growRadius, other.Radius) + PlantCatalogue.SpacingMargin;
                    }
                    if (!crowded)
                    {
                        spots.Add(p);
                    }
                }
            }
        }

        if (spots.Count == 0 && wrongBiome > 0 && _loggedBiome.Add(type))
        {
            DvergrWorkersPlugin.Log.LogInfo("Farmer: " + type.Prefab.name + " cannot be planted here, wrong biome ("
                + Heightmap.FindBiome(Home) + " is not in plant biomes " + type.Plant.m_biome
                + (type.Piece.m_onlyInBiome != Heightmap.Biome.None ? ", piece biomes " + type.Piece.m_onlyInBiome : "") + ")");
        }

        Vector3 here = transform.position;
        spots.Sort((a, b) => Vector3.Distance(a, here).CompareTo(Vector3.Distance(b, here)));

        // Keep chosen spots from crowding each other: they are planted one after another.
        List<Vector3> chosen = [];
        int checks = 0;
        foreach (Vector3 spot in spots)
        {
            bool crowded = false;
            foreach (Vector3 other in chosen)
            {
                crowded |= Utils.DistanceXZ(other, spot) < type.Plant.m_growRadius + PlantCatalogue.SpacingMargin;
            }
            if (crowded || UnreachableSpots.IsKnown(spot))
            {
                continue;
            }

            // Ground behind a wall or fence is no use: check the worker can walk there. The first failure
            // may only mean the game has not built the navigation mesh there yet, so look again later.
            if (UnreachableSpots.IsPending(spot))
            {
                _pendingChecks++;
                continue;
            }
            if (checks++ >= MaxReachChecks)
            {
                break;
            }
            Vector3 target = spot;
            if (!Navigator.CanReach(_ => target))
            {
                UnreachableSpots.Suspect(spot);
                _pendingChecks++;
                continue;
            }

            chosen.Add(spot);
            if (chosen.Count >= max)
            {
                break;
            }
        }
        return chosen;
    }

    /// <summary>
    /// Picks what to plant next: the crop with the fewest plants so far, using seeds while there are more
    /// than the reserve, then raw ingredients to make more seeds, and the reserve only as a last resort.
    /// </summary>
    private PlantType ChooseType(string family)
    {
        PlantType seed = null;
        PlantType raw = null;
        foreach (PlantType type in PlantCatalogue.Crops)
        {
            if (type.Family != family || _failedTypes.Contains(type))
            {
                continue;
            }
            if (type.Raw)
            {
                raw = type;
            }
            else
            {
                seed = type;
            }
        }

        int seedSupply = seed != null ? Supply(seed.RequiredShared) : 0;
        int rawSupply = raw != null ? Supply(raw.RequiredShared) : 0;
        bool preferSeed = seed != null && seedSupply > WorkerConfig.SeedReserve.Value;
        bool useRaw = raw != null && rawSupply >= raw.Amount;
        bool useSeed = seed != null && seedSupply >= seed.Amount;
        return (preferSeed, useRaw, useSeed) switch
        {
            (true, _, _) => seed,
            (_, true, _) => raw,
            (_, _, true) => seed,
            _ => null,
        };
    }

    // ---------------------------------------------------------------- rounds

    protected override bool BeginRound()
    {
        _planted = 0;
        _harvested = 0;
        _noSpots = false;
        _pendingChecks = 0;
        _noSupply = false;
        _failedTypes.Clear();
        _loggedBiome.Clear();
        _failedPickables.Clear();
        _plantedByFamily.Clear();
        _plantedSpots.Clear();
        _batchSpots.Clear();
        _batchType = null;
        _harvest = null;

        if (!System.Linq.Enumerable.Any(PlantCatalogue.Crops))
        {
            IdleNote = "No crops found in the cultivator";
            return false;
        }

        bool tilled = false;
        bool work = RipeCrops().Count > 0 || CarryingOther() || CarryingSeeds();
        if (!work)
        {
            Dictionary<string, int> counts = CountFamilies();
            foreach (string family in OrderedFamilies(counts))
            {
                PlantType type = ChooseType(family);
                if (type == null)
                {
                    _noSupply = true;
                    continue;
                }
                List<Vector3> spots = FindSpots(type, 1);
                tilled |= spots.Count > 0;
                if (spots.Count > 0)
                {
                    work = true;
                    break;
                }
                _noSpots = true;
            }
        }

        if (!work)
        {
            _handledLastRound = 0;
            if (_pendingChecks > 0)
            {
                // The game is still building the navigation mesh around some spots; look again shortly.
                RetrySoon(15f);
            }
            IdleNote = NothingToDoNote();
            return false;
        }

        Equipment.EquipTool();
        _phase = Phase.Plan;
        DvergrWorkersPlugin.Log.LogInfo("Farmer round started");
        return true;
    }

    private string NothingToDoNote()
    {
        return (_noSupply, _noSpots) switch
        {
            (true, false) => "Waiting for seeds in a chest",
            (false, true) => "No free tilled soil to plant",
            _ => null,
        };
    }

    private static List<string> OrderedFamilies(Dictionary<string, int> counts)
    {
        List<string> families = [.. counts.Keys];
        families.Sort((a, b) => counts[a].CompareTo(counts[b]));
        return families;
    }

    protected override bool RunTask(float dt)
    {
        return _phase switch
        {
            Phase.Plan => Plan(),
            Phase.Fetch => Fetch(dt),
            Phase.Walk => Walk(dt),
            Phase.Plant => PlantNow(dt),
            Phase.Harvest => HarvestNow(dt),
            Phase.Collect => Collect(dt),
            Phase.Deposit => Deposit(dt),
            _ => false,
        };
    }

    private bool Abort(string reason)
    {
        DvergrWorkersPlugin.Log.LogInfo("Farmer stopping: " + reason);
        IdleNote = reason;
        EndRound(finished: false);
        return false;
    }

    // ---------------------------------------------------------------- planning

    private bool Plan()
    {
        // Inventory nearly full of produce: hand it in before carrying on.
        if ((CarryingOther() || CarryingSeeds()) && Logistics.Carried.GetEmptySlots() <= FullSlots)
        {
            return StartDeposit();
        }

        // Carry on with a planting batch that is already fetched.
        if (_batchSpots.Count > 0 && _batchType != null
            && Logistics.Carried.CountItems(_batchType.RequiredShared) >= _batchType.Amount)
        {
            return BeginWalk(_batchSpots[0], "Walking to plant " + _batchType.Prefab.name.Replace("sapling_", ""), Phase.Plant);
        }
        _batchSpots.Clear();

        // Harvest first: it frees up tilled soil and produces seeds.
        Pickable ripe = NearestRipe();
        if (ripe != null)
        {
            _harvest = ripe;
            Pickable target = ripe;
            if (Navigator.Begin(ripe.name, from => WorkerNavigator.ClosestPointOnColliders(target.gameObject, from), rememberFailure: true))
            {
                _phase = Phase.Walk;
                _nextAfterWalk = Phase.Harvest;
                SetStatus("Walking to ripe " + ripe.name.Replace("Pickable_", "").Replace("(Clone)", ""));
            }
            else
            {
                _ = _failedPickables.Add(ripe);
                _harvest = null;
            }
            return true;
        }

        // Then plant a new batch of whichever crop there is least of.
        if (StartBatch())
        {
            return true;
        }

        // All done: put produce and spare seeds away.
        if (CarryingOther() || CarryingSeeds())
        {
            return StartDeposit();
        }
        if (_planted + _harvested == 0)
        {
            IdleNote = NothingToDoNote();
        }
        EndRound(finished: true);
        return false;
    }

    private Phase _nextAfterWalk = Phase.Plant;

    private Pickable NearestRipe()
    {
        Pickable best = null;
        float bestDist = float.MaxValue;
        foreach (Pickable pickable in RipeCrops())
        {
            float d = Vector3.Distance(pickable.transform.position, transform.position);
            if (d < bestDist)
            {
                bestDist = d;
                best = pickable;
            }
        }
        return best;
    }

    private bool StartBatch()
    {
        Dictionary<string, int> counts = CountFamilies();
        foreach (string family in OrderedFamilies(counts))
        {
            PlantType type = ChooseType(family);
            if (type == null)
            {
                _noSupply = true;
                continue;
            }

            List<Vector3> spots = FindSpots(type, WorkerConfig.PlantsPerTrip.Value);
            if (spots.Count == 0)
            {
                _noSpots = true;
                continue;
            }

            int supply = Supply(type.RequiredShared);
            int count = Math.Min(spots.Count, supply / type.Amount);
            if (count <= 0)
            {
                continue;
            }

            _batchType = type;
            _batchSpots.Clear();
            _batchSpots.AddRange(spots.GetRange(0, count));
            _ = _ours.Add(type.RequiredShared);

            int missing = (count * type.Amount) - Logistics.Carried.CountItems(type.RequiredShared);
            if (missing > 0)
            {
                string shared = type.RequiredShared;
                if (Logistics.BeginFetchAny(i => i.m_shared.m_name == shared, missing))
                {
                    _phase = Phase.Fetch;
                    SetStatus("Fetching " + shared.TrimStart('$').Replace("item_", "") + " to plant");
                    return true;
                }
                DvergrWorkersPlugin.Log.LogInfo("Farmer fetch failed: " + Logistics.Reason);
                _ = _failedTypes.Add(type);
                _batchSpots.Clear();
                continue;
            }

            _phase = Phase.Plan;
            return true;
        }
        return false;
    }

    private bool Fetch(float dt)
    {
        switch (Logistics.Step(dt))
        {
            case LogisticsResult.Moving:
                return true;
            case LogisticsResult.Failed:
                DvergrWorkersPlugin.Log.LogInfo("Farmer fetch failed: " + Logistics.Reason);
                if (_batchType != null)
                {
                    _ = _failedTypes.Add(_batchType);
                }
                break;
        }

        _phase = Phase.Plan;
        return true;
    }

    // ---------------------------------------------------------------- walking and acting

    private bool BeginWalk(Vector3 spot, string status, Phase next)
    {
        if (!Navigator.Begin("spot", _ => spot, rememberFailure: true))
        {
            DvergrWorkersPlugin.Log.LogInfo("No reachable spot next to a planting spot, skipping");
            if (_batchSpots.Count > 0)
            {
                _batchSpots.RemoveAt(0);
            }
            _phase = Phase.Plan;
            return true;
        }

        _nextAfterWalk = next;
        _phase = Phase.Walk;
        SetStatus(status);
        return true;
    }

    private bool Walk(float dt)
    {
        if (_nextAfterWalk == Phase.Harvest && (_harvest == null || _harvest.m_picked))
        {
            _harvest = null;
            _phase = Phase.Plan;
            return true;
        }

        switch (Navigator.Step(dt))
        {
            case NavResult.InReach:
            case NavResult.CloseEnough:
                _timer = _nextAfterWalk == Phase.Harvest ? 0.2f : PlantTime;
                _phase = _nextAfterWalk;
                break;

            case NavResult.Failed:
                if (_nextAfterWalk == Phase.Harvest && _harvest != null)
                {
                    _ = _failedPickables.Add(_harvest);
                    _harvest = null;
                }
                else if (_batchSpots.Count > 0)
                {
                    // Its neighbours are most likely cut off in the same way.
                    Vector3 failed = _batchSpots[0];
                    UnreachableSpots.Add(failed);
                    _ = _batchSpots.RemoveAll(s => Utils.DistanceXZ(s, failed) < 4f);
                }
                _phase = Phase.Plan;
                break;
        }
        return true;
    }

    private bool PlantNow(float dt)
    {
        Ai.StopMoving();
        _timer -= dt;
        if (_timer > 0f)
        {
            return true;
        }

        if (_batchSpots.Count == 0 || _batchType == null)
        {
            _phase = Phase.Plan;
            return true;
        }

        Vector3 spot = _batchSpots[0];
        _batchSpots.RemoveAt(0);
        PlantType type = _batchType;

        ItemDrop.ItemData seed = null;
        foreach (ItemDrop.ItemData candidate in Logistics.Carried.GetAllItems())
        {
            if (candidate.m_shared.m_name == type.RequiredShared && candidate.m_stack >= type.Amount)
            {
                seed = candidate;
                break;
            }
        }

        // The spot may have been taken meanwhile; plant only where it is still valid.
        int spaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid");
        if (seed != null && IsCultivatedSpot(spot, out _) && PlantCatalogue.BiomeAllows(type, spot) && !Physics.CheckSphere(spot + (Vector3.up * 0.15f), type.Plant.m_growRadius + 0.2f, spaceMask))
        {
            _ = Logistics.Carried.RemoveItem(seed, type.Amount);

            Quaternion rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            GameObject plant = Instantiate(type.Prefab, spot, rotation);
            _ = type.Piece.m_placeEffect.Create(spot, rotation, plant.transform);
            Animation.PlaySwing();

            DvergrWorkersPlugin.Log.LogInfo("Farmer planted " + type.Prefab.name + " in " + Heightmap.FindBiome(spot)
                + " (plant biomes " + type.Plant.m_biome + ", piece biomes " + type.Piece.m_onlyInBiome + ")");
            _planted++;
            _plantedSpots.Add(new Occupied { Position = spot, Radius = type.Plant.m_growRadius });
            _plantedByFamily[type.Family] = (_plantedByFamily.TryGetValue(type.Family, out int n) ? n : 0) + 1;
            SetStatus("Planting " + type.Prefab.name.Replace("sapling_", ""));
        }

        _phase = Phase.Plan;
        return true;
    }

    private bool HarvestNow(float dt)
    {
        Ai.StopMoving();
        _timer -= dt;
        if (_timer > 0f)
        {
            return true;
        }

        if (_harvest == null || _harvest.m_picked)
        {
            _harvest = null;
            _phase = Phase.Plan;
            return true;
        }

        _collectPos = _harvest.transform.position;
        _harvest.m_nview.ClaimOwnership();
        _ = _harvest.Interact(Humanoid, false, false);
        _harvested++;
        Animation.PlaySwing();

        _harvest = null;
        _collectTries = 0;
        _timer = HarvestWait;
        _phase = Phase.Collect;
        SetStatus("Harvesting");
        return true;
    }

    private bool Collect(float dt)
    {
        _timer -= dt;
        if (_timer > 0f)
        {
            return true;
        }
        _timer = CollectInterval;

        ItemDrop target = null;
        foreach (ItemDrop drop in ItemDrop.s_instances)
        {
            if (drop != null && drop.m_itemData != null && Vector3.Distance(drop.transform.position, _collectPos) < DropRadius)
            {
                target = drop;
                break;
            }
        }

        if (target == null || ++_collectTries > MaxCollectTries)
        {
            _phase = Phase.Plan;
            return true;
        }

        // Drops are owned by whoever last touched them; ask for ownership and retry for a moment.
        target.RequestOwn();
        string shared = target.m_itemData.m_shared.m_name;
        if (Humanoid.Pickup(target.gameObject, false, false))
        {
            _ = _ours.Add(shared);
            _collectTries = 0;
        }
        return true;
    }

    // ---------------------------------------------------------------- delivering

    /// <summary>Crops go to output chests, seeds back to the input chest for planting.</summary>
    private bool StartDeposit()
    {
        if (CarryingOther())
        {
            if (!Logistics.BeginDeposit(i => IsOurs(i) && !PlantCatalogue.IsSeedName(i.m_shared.m_name), ChestKind.Output))
            {
                return Abort(Logistics.Reason);
            }
            _phase = Phase.Deposit;
            SetStatus("Delivering crops to a chest");
            return true;
        }

        if (CarryingSeeds())
        {
            if (!Logistics.BeginDeposit(i => IsOurs(i) && PlantCatalogue.IsSeedName(i.m_shared.m_name), ChestKind.Input))
            {
                // Nowhere to put the seeds: keep them and use them next round.
                EndRound(finished: true);
                return false;
            }
            _phase = Phase.Deposit;
            SetStatus("Returning seeds to a chest");
            return true;
        }

        _phase = Phase.Plan;
        return true;
    }

    private bool Deposit(float dt)
    {
        return Logistics.Step(dt) switch
        {
            LogisticsResult.Moving => true,
            LogisticsResult.Failed => Abort(Logistics.Reason),
            _ => Logistics.Transferred == 0 ? Abort("Output chests are full") : StartDeposit(),
        };
    }
}
