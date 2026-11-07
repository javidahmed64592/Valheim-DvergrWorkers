using System;
using System.Collections.Generic;

using DvergrWorkers.Core;
using DvergrWorkers.Logistics;
using DvergrWorkers.Movement;
using DvergrWorkers.Roles.Farmer;

using UnityEngine;

namespace DvergrWorkers.Roles.TreeFarmer;

/// <summary>
/// Tree farmer role: plants tree saplings (from tree seeds, cones and acorns in the input chest) on suitable
/// ground inside the job radius, holding a cultivator. It plants evenly across species, only where the species
/// grows, gives every sapling plenty of room, keeps clear of buildings and farmland, and stops once the area
/// holds as many trees as the limit allows. Felled trees drop seeds, which the woodcutter delivers, so the
/// two roles keep a forest going between them.
/// </summary>
public class TreeFarmerWorker : WorkerBrain
{
    private enum Phase { Plan = 0, Fetch = 1, Walk = 2, Plant = 3, Return = 4, Gather = 5 }

    private const float PlantTime = 0.6f;

    /// <summary>
    /// Most path checks made while choosing spots in one planning pass (across every plant type it tries), so a pass
    /// cannot stall the game.
    /// </summary>
    private const int MaxReachChecks = 30;

    private int _reachChecksLeft;
    private const float TreeSpacingMargin = 1f;
    private const float DefaultTreeRoom = 2f;
    private const float GatherInterval = 0.3f;
    private const int MaxGatherTries = 12;

    private Phase _phase = Phase.Plan;
    private PlantType _batchType;
    private readonly List<Vector3> _batchSpots = [];
    private float _timer;
    private int _planted;
    private int _plantedLastRound;
    private bool _noSupply;
    private bool _noSpots;
    private int _pendingChecks;
    private bool _full;
    private readonly HashSet<PlantType> _failedTypes = [];
    private readonly HashSet<PlantType> _loggedBiome = [];
    private readonly Dictionary<string, int> _plantedByFamily = [];
    private readonly List<Occupied> _plantedSpots = [];
    private readonly GroundSurvey _survey = new();
    private readonly OccupiedGrid _occupied = new();

    /// <summary>Trees and saplings near the job area, found once per frame however many times planning asks.</summary>
    private readonly List<TreeBase> _nearbyTrees = [];
    private readonly List<Plant> _nearbyPlants = [];
    private int _sceneScanFrame = -1;

    /// <summary>A tree seed lying near where a tree could grow, being walked to so it can be picked up.</summary>
    private ItemDrop _looseSeed;
    private PlantType _looseType;

    /// <summary>
    /// Seeds picked up off the ground this round and where they lay. They are planted before anything else, nearest
    /// that spot first, and whatever is left over goes back to the input chest.
    /// </summary>
    private readonly List<(PlantType Type, Vector3 Origin)> _loosePlanted = [];
    private int _gatherTries;
    private bool _walkingToSeed;
    private readonly HashSet<ItemDrop> _looseFailed = [];

    internal override WorkerRole Role => WorkerRoles.TreeFarmer;

    protected override string IdleStatus()
    {
        return _plantedLastRound > 0
            ? "Idle (planted " + _plantedLastRound + " trees last round)"
            : "Idle";
    }

    protected override void OnRoundEnded(bool finished)
    {
        _batchSpots.Clear();
        _plantedLastRound = _planted;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The tree seeds, cones and acorns it plants.</summary>
    internal override bool WantsItem(string sharedName)
    {
        return PlantCatalogue.TreeSeedNames.Contains(sharedName);
    }

    private int Supply(string sharedName)
    {
        return Logistics.Carried.CountItems(sharedName) + Logistics.AvailableTo(sharedName);
    }

    private bool CarryingSeeds()
    {
        HashSet<string> names = PlantCatalogue.TreeSeedNames;
        foreach (ItemDrop.ItemData item in Logistics.Carried.GetAllItems())
        {
            if (names.Contains(item.m_shared.m_name))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Trees and saplings already in the area, per species, so planting can even them out.</summary>
    private Dictionary<string, int> CountFamilies(out int total)
    {
        Dictionary<string, int> counts = [];
        foreach (PlantType type in PlantCatalogue.Trees)
        {
            counts[type.Family] = 0;
        }

        total = 0;
        ScanScene();
        foreach (TreeBase tree in _nearbyTrees)
        {
            if (InJobArea(tree.transform.position))
            {
                total++;
                PlantType type = PlantCatalogue.Of(tree.gameObject);
                if (type != null && type.IsTree)
                {
                    counts[type.Family]++;
                }
            }
        }

        foreach (Plant plant in _nearbyPlants)
        {
            if (InJobArea(plant.transform.position))
            {
                PlantType type = PlantCatalogue.Of(plant.gameObject);
                if (type != null && type.IsTree)
                {
                    total++;
                    counts[type.Family]++;
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

    private static List<string> OrderedFamilies(Dictionary<string, int> counts)
    {
        List<string> families = [.. counts.Keys];
        families.Sort((a, b) => counts[a].CompareTo(counts[b]));
        return families;
    }

    /// <summary>The species of this family that has seeds to plant, if any.</summary>
    private PlantType ChooseType(string family)
    {
        foreach (PlantType type in PlantCatalogue.Trees)
        {
            if (type.Family == family && !_failedTypes.Contains(type) && Supply(type.RequiredShared) >= type.Amount)
            {
                return type;
            }
        }
        return null;
    }

    /// <summary>
    /// Ground where this tree can be planted: its biome, real dirt (not paved), flat enough, above water, away from
    /// buildings and farmland, with open sky and room all round, and well clear of other trees. Most of that comes
    /// from the <see cref="GroundSurvey"/>; only what depends on the species is checked here.
    /// </summary>
    /// <param name="nearestTo">Spots closest to this point are chosen first; the worker's own position by default.</param>
    private List<Vector3> FindSpots(PlantType type, int max, Vector3? nearestTo = null)
    {
        int wrongBiome = 0;
        float room = Room(type);
        OccupiedGrid occupied = OccupiedSpots();

        List<Vector3> spots = [];
        foreach (GroundSurvey.Spot spot in _survey.Spots)
        {
            if (Fits(type, spot, occupied, room, ref wrongBiome))
            {
                spots.Add(spot.Position);
            }
        }

        if (spots.Count == 0 && wrongBiome > 0 && _loggedBiome.Add(type))
        {
            DvergrWorkersPlugin.Log.LogInfo("Tree farmer: " + type.Prefab.name + " cannot be planted here, wrong biome ("
                + Heightmap.FindBiome(Home) + " is not in plant biomes " + type.Plant.m_biome + ")");
        }

        Vector3 here = nearestTo ?? transform.position;
        spots.Sort((a, b) => Vector3.Distance(a, here).CompareTo(Vector3.Distance(b, here)));

        // Chosen spots must not crowd each other either, since they are planted one after another.
        List<Vector3> chosen = [];
        foreach (Vector3 spot in spots)
        {
            bool crowded = false;
            foreach (Vector3 other in chosen)
            {
                if (Utils.DistanceXZ(other, spot) < (room * 2f) + TreeSpacingMargin)
                {
                    crowded = true;
                    break;
                }
            }
            if (crowded || UnreachableSpots.IsKnown(spot) || !GroundSurvey.StillClear(spot, room))
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
            if (_reachChecksLeft <= 0)
            {
                // Out of checks for this pass: there may well be good spots left, so look again soon.
                _pendingChecks++;
                break;
            }
            _reachChecksLeft--;
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

    private static float Room(PlantType type)
    {
        return Mathf.Max(type.Plant.m_growRadius, 0.5f);
    }

    /// <summary>
    /// Finds the trees and saplings near the job area. The game-wide search behind it is slow with a lot of the
    /// world loaded, and planning asks several times in a row, so it is done at most once per frame.
    /// </summary>
    private void ScanScene()
    {
        if (_sceneScanFrame == Time.frameCount)
        {
            return;
        }
        _sceneScanFrame = Time.frameCount;

        float range = WorkerConfig.JobRadius.Value + 10f;
        _nearbyTrees.Clear();
        foreach (TreeBase tree in FindObjectsByType<TreeBase>(FindObjectsSortMode.None))
        {
            if (tree != null && tree.gameObject.activeInHierarchy && Utils.DistanceXZ(tree.transform.position, Home) <= range)
            {
                _nearbyTrees.Add(tree);
            }
        }
        _nearbyPlants.Clear();
        foreach (Plant plant in FindObjectsByType<Plant>(FindObjectsSortMode.None))
        {
            if (plant != null && Utils.DistanceXZ(plant.transform.position, Home) <= range)
            {
                _nearbyPlants.Add(plant);
            }
        }
    }

    /// <summary>Plants, saplings and trees near the job area, with the room each needs to grow.</summary>
    private OccupiedGrid OccupiedSpots()
    {
        ScanScene();
        _occupied.Clear();
        foreach (Occupied planted in _plantedSpots)
        {
            _occupied.Add(planted);
        }
        foreach (Plant plant in _nearbyPlants)
        {
            if (plant != null)
            {
                _occupied.Add(new Occupied { Position = plant.transform.position, Radius = plant.m_growRadius });
            }
        }
        foreach (TreeBase tree in _nearbyTrees)
        {
            if (tree != null)
            {
                PlantType existing = PlantCatalogue.Of(tree.gameObject);
                float r = existing != null && existing.IsTree ? existing.Plant.m_growRadius : DefaultTreeRoom;
                _occupied.Add(new Occupied { Position = tree.transform.position, Radius = r });
            }
        }
        return _occupied;
    }

    /// <summary>The cheap, species-specific checks on a surveyed spot: biome, dirt, and room clear of other trees.</summary>
    private static bool Fits(PlantType type, GroundSurvey.Spot spot, OccupiedGrid occupied, float room, ref int wrongBiome)
    {
        if (!PlantCatalogue.BiomeAllows(type, spot.Biome))
        {
            wrongBiome++;
            return false;
        }
        return (!type.Piece.m_vegetationGroundOnly || spot.Dirt) && !occupied.Crowds(spot.Position, room, TreeSpacingMargin);
    }

    /// <summary>
    /// Finds the nearest tree seed lying loose in the job area within <see cref="WorkerConfig.TreeLooseSeedRadius"/>
    /// of somewhere the tree farmer would plant that tree itself. A stack dropped on a path or by a building still
    /// counts as long as there is good ground close by.
    /// </summary>
    private bool NearestLooseSeed(out ItemDrop seed, out PlantType type)
    {
        seed = null;
        type = null;
        if (!WorkerConfig.TreeFarmerPlantsLooseSeeds.Value)
        {
            return false;
        }

        HashSet<string> names = PlantCatalogue.TreeSeedNames;
        List<ItemDrop> candidates = [];
        foreach (ItemDrop drop in GroundItems.Loose(i => names.Contains(i.m_shared.m_name)))
        {
            // A seed the worker already failed to reach would otherwise start a round that ends at once, over and over.
            Vector3 at = drop.transform.position;
            if (!_looseFailed.Contains(drop) && InJobArea(at) && !UnreachableSpots.IsKnown(at) && !UnreachableSpots.IsPending(at))
            {
                candidates.Add(drop);
            }
        }
        if (candidates.Count == 0)
        {
            return false;
        }

        Vector3 here = transform.position;
        candidates.Sort((a, b) => Vector3.Distance(a.transform.position, here).CompareTo(Vector3.Distance(b.transform.position, here)));
        OccupiedGrid occupied = OccupiedSpots();
        foreach (ItemDrop drop in candidates)
        {
            foreach (PlantType candidate in PlantCatalogue.Trees)
            {
                if (candidate.RequiredShared == drop.m_itemData.m_shared.m_name && !_failedTypes.Contains(candidate)
                    && HasSpotNear(candidate, drop.transform.position, occupied))
                {
                    seed = drop;
                    type = candidate;
                    return true;
                }
            }

            // No good ground anywhere near it (deep inside the base, on farmland...): leave it be this round.
            _ = _looseFailed.Add(drop);
        }
        return false;
    }

    /// <summary>Whether this tree could be planted somewhere within <see cref="WorkerConfig.TreeLooseSeedRadius"/> of a point.</summary>
    private bool HasSpotNear(PlantType type, Vector3 centre, OccupiedGrid occupied)
    {
        float radius = WorkerConfig.TreeLooseSeedRadius.Value;
        float room = Room(type);
        int wrongBiome = 0;
        foreach (GroundSurvey.Spot spot in _survey.Spots)
        {
            if (Utils.DistanceXZ(spot.Position, centre) <= radius && !UnreachableSpots.IsKnown(spot.Position)
                && Fits(type, spot, occupied, room, ref wrongBiome) && GroundSurvey.StillClear(spot.Position, room))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Keeps the ground survey going a little each frame; see <see cref="GroundSurvey"/>.</summary>
    protected override void BackgroundWork(float dt)
    {
        _survey.Continue();
    }

    // ---------------------------------------------------------------- rounds

    protected override bool BeginRound()
    {
        _planted = 0;
        _noSupply = false;
        _noSpots = false;
        _pendingChecks = 0;
        _full = false;
        _failedTypes.Clear();
        _loggedBiome.Clear();
        _plantedByFamily.Clear();
        _plantedSpots.Clear();
        _batchSpots.Clear();
        _batchType = null;
        _looseSeed = null;
        _walkingToSeed = false;
        _looseFailed.Clear();
        _loosePlanted.Clear();

        if (!System.Linq.Enumerable.Any(PlantCatalogue.Trees))
        {
            IdleNote = "No tree saplings found in the cultivator";
            return false;
        }

        // The map of plantable ground is built in the background; the first time round, wait for it.
        _survey.RefreshIfStale(Home, WorkerConfig.JobRadius.Value, WorkerConfig.JobRadius.Value + TaskRadiusTolerance,
            WorkerConfig.TreeBuildingClearance.Value);
        if (!_survey.Ready)
        {
            IdleNote = "Looking over the ground";
            RetrySoon(1f);
            return false;
        }

        if (!AnythingToPlant())
        {
            _plantedLastRound = 0;
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
        DvergrWorkersPlugin.Log.LogInfo("Tree farmer round started");
        return true;
    }

    /// <summary>True if there is room for more trees, seeds to plant, and somewhere to plant them.</summary>
    private bool AnythingToPlant()
    {
        _reachChecksLeft = MaxReachChecks;
        Dictionary<string, int> counts = CountFamilies(out int total);
        if (total >= WorkerConfig.MaxTrees.Value)
        {
            _full = true;
            return CarryingSeeds();
        }

        if (NearestLooseSeed(out _, out _))
        {
            return true;
        }

        foreach (string family in OrderedFamilies(counts))
        {
            PlantType type = ChooseType(family);
            if (type == null)
            {
                _noSupply = true;
                continue;
            }
            if (FindSpots(type, 1).Count > 0)
            {
                return true;
            }
            _noSpots = true;
        }
        return CarryingSeeds();
    }

    private string NothingToDoNote()
    {
        return (_full, _noSupply, _noSpots) switch
        {
            (true, _, _) => "The forest is full",
            (_, true, false) => "Waiting for tree seeds in a chest",
            (_, false, true) => "No suitable ground to plant trees",
            _ => null,
        };
    }

    protected override bool RunTask(float dt)
    {
        return _phase switch
        {
            Phase.Plan => Plan(),
            Phase.Fetch => Fetch(dt),
            Phase.Walk => Walk(dt),
            Phase.Plant => PlantNow(dt),
            Phase.Return => Return(dt),
            Phase.Gather => Gather(dt),
            _ => false,
        };
    }

    private bool Plan()
    {
        // Carry on with a batch that is already fetched.
        if (_batchSpots.Count > 0 && _batchType != null
            && Logistics.Carried.CountItems(_batchType.RequiredShared) >= _batchType.Amount)
        {
            return BeginWalk(_batchSpots[0]);
        }
        _batchSpots.Clear();

        // Seeds already lying where a tree could grow are planted right there before anything else.
        if (StartLooseSeed())
        {
            return true;
        }

        if (StartBatch())
        {
            return true;
        }

        if (CarryingSeeds())
        {
            return StartReturn();
        }
        if (_planted == 0)
        {
            IdleNote = NothingToDoNote();
        }
        EndRound(finished: true);
        return false;
    }

    private bool StartLooseSeed()
    {
        _ = CountFamilies(out int total);
        if (total >= WorkerConfig.MaxTrees.Value)
        {
            return false;
        }

        while (NearestLooseSeed(out ItemDrop seed, out PlantType type))
        {
            Vector3 at = seed.transform.position;
            if (Navigator.Begin("loose seed", _ => at, rememberFailure: true))
            {
                _looseSeed = seed;
                _looseType = type;
                _walkingToSeed = true;
                _phase = Phase.Walk;
                SetStatus("Picking up fallen " + type.RequiredShared.TrimStart('$').Replace("item_", ""));
                return true;
            }
            _ = _looseFailed.Add(seed);
        }
        return false;
    }

    /// <summary>Picks up the whole stack of loose seeds; <see cref="StartBatch"/> then plants them first.</summary>
    private bool Gather(float dt)
    {
        Ai.StopMoving();
        _timer -= dt;
        if (_timer > 0f)
        {
            return true;
        }
        _timer = GatherInterval;

        if (_looseSeed == null || ++_gatherTries > MaxGatherTries)
        {
            // Gone (someone else picked it up) or never got hold of it.
            if (_looseSeed != null)
            {
                _ = _looseFailed.Add(_looseSeed);
            }
            _looseSeed = null;
            _phase = Phase.Plan;
            return true;
        }

        Vector3 origin = _looseSeed.transform.position;
        if (GroundItems.Take(Humanoid, _looseSeed) > 0)
        {
            _looseSeed = null;
            _loosePlanted.Add((_looseType, origin));
            Animation.PlayPickup();
            _phase = Phase.Plan;
        }
        return true;
    }

    private bool StartBatch()
    {
        _reachChecksLeft = MaxReachChecks;
        Dictionary<string, int> counts = CountFamilies(out int total);
        int room = WorkerConfig.MaxTrees.Value - total;
        if (room <= 0)
        {
            _full = true;
            return false;
        }

        // Seeds picked up off the ground go in first, as many as there is room for, nearest where they lay first.
        foreach ((PlantType Type, Vector3 Origin) loose in new List<(PlantType, Vector3)>(_loosePlanted))
        {
            PlantType type = loose.Type;
            int seeds = Logistics.Carried.CountItems(type.RequiredShared) / type.Amount;
            List<Vector3> spots = seeds > 0 && !_failedTypes.Contains(type)
                ? FindSpots(type, Math.Min(seeds, room), loose.Origin)
                : [];
            if (spots.Count == 0)
            {
                // All planted, or nowhere left to plant them: any spares go back to the input chest.
                _ = _loosePlanted.Remove(loose);
                continue;
            }

            _batchType = type;
            _batchSpots.Clear();
            _batchSpots.AddRange(spots);
            _phase = Phase.Plan;
            return true;
        }

        foreach (string family in OrderedFamilies(counts))
        {
            PlantType type = ChooseType(family);
            if (type == null)
            {
                _noSupply = true;
                continue;
            }

            List<Vector3> spots = FindSpots(type, Math.Min(WorkerConfig.TreesPerTrip.Value, room));
            if (spots.Count == 0)
            {
                _noSpots = true;
                continue;
            }

            int count = Math.Min(spots.Count, Supply(type.RequiredShared) / type.Amount);
            if (count <= 0)
            {
                continue;
            }

            _batchType = type;
            _batchSpots.Clear();
            _batchSpots.AddRange(spots.GetRange(0, count));

            int missing = (count * type.Amount) - Logistics.Carried.CountItems(type.RequiredShared);
            if (missing > 0)
            {
                string shared = type.RequiredShared;
                if (Logistics.BeginFetchAny(i => i.m_shared.m_name == shared, missing))
                {
                    _phase = Phase.Fetch;
                    SetStatus("Fetching seeds to plant");
                    return true;
                }
                DvergrWorkersPlugin.Log.LogInfo("Tree farmer fetch failed: " + Logistics.Reason);
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
                DvergrWorkersPlugin.Log.LogInfo("Tree farmer fetch failed: " + Logistics.Reason);
                if (_batchType != null)
                {
                    _ = _failedTypes.Add(_batchType);
                }
                break;
        }

        _phase = Phase.Plan;
        return true;
    }

    // ---------------------------------------------------------------- walking and planting

    private bool BeginWalk(Vector3 spot)
    {
        if (!Navigator.Begin("planting spot", _ => spot, rememberFailure: true))
        {
            DvergrWorkersPlugin.Log.LogInfo("No reachable spot next to a planting spot, skipping");
            _batchSpots.RemoveAt(0);
            _phase = Phase.Plan;
            return true;
        }

        _phase = Phase.Walk;
        SetStatus("Walking to plant a " + _batchType.Prefab.name.Replace("sapling_", ""));
        return true;
    }

    private bool Walk(float dt)
    {
        bool gathering = _walkingToSeed;
        if (gathering && _looseSeed == null)
        {
            // Someone else picked it up meanwhile.
            _walkingToSeed = false;
            _phase = Phase.Plan;
            return true;
        }

        switch (Navigator.Step(dt))
        {
            case NavResult.InReach:
            case NavResult.CloseEnough:
                _timer = gathering ? 0.2f : PlantTime;
                _gatherTries = 0;
                _walkingToSeed = false;
                _phase = gathering ? Phase.Gather : Phase.Plant;
                break;

            case NavResult.Failed:
                if (gathering)
                {
                    _ = _looseFailed.Add(_looseSeed);
                    _looseSeed = null;
                    _walkingToSeed = false;
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

        // The spot may have been taken meanwhile; plant only where it is still free.
        int spaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid");
        float room = Mathf.Max(type.Plant.m_growRadius, 0.5f);
        if (seed != null && PlantCatalogue.BiomeAllows(type, spot) && !Physics.CheckSphere(spot + (Vector3.up * 0.2f), room + 0.3f, spaceMask))
        {
            _ = Logistics.Carried.RemoveItem(seed, type.Amount);

            Quaternion rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            GameObject sapling = Instantiate(type.Prefab, spot, rotation);
            _ = type.Piece.m_placeEffect.Create(spot, rotation, sapling.transform);
            Animation.PlaySwing();

            DvergrWorkersPlugin.Log.LogInfo("Tree farmer planted " + type.Prefab.name + " in " + Heightmap.FindBiome(spot));
            _planted++;
            _plantedSpots.Add(new Occupied { Position = spot, Radius = room });
            _plantedByFamily[type.Family] = (_plantedByFamily.TryGetValue(type.Family, out int n) ? n : 0) + 1;
            SetStatus("Planting a " + type.Prefab.name.Replace("sapling_", ""));
        }

        _phase = Phase.Plan;
        return true;
    }

    // ---------------------------------------------------------------- spare seeds

    private bool StartReturn()
    {
        HashSet<string> names = PlantCatalogue.TreeSeedNames;
        if (!Logistics.BeginDeposit(i => names.Contains(i.m_shared.m_name), ChestKind.Input))
        {
            // Nowhere to put them: keep them and use them next round.
            EndRound(finished: true);
            return false;
        }

        _phase = Phase.Return;
        SetStatus("Returning spare seeds to a chest");
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

        if (Logistics.Transferred > 0 && CarryingSeeds())
        {
            return StartReturn();
        }
        EndRound(finished: true);
        return false;
    }
}
