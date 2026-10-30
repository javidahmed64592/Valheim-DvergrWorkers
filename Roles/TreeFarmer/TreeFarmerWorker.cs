using System;
using System.Collections.Generic;

using UnityEngine;

namespace DvergrWorkers;

/// <summary>
/// Tree farmer role: plants tree saplings (from tree seeds, cones and acorns in the input chest) on suitable
/// ground inside the job radius, holding a cultivator. It plants evenly across species, only where the species
/// grows, gives every sapling plenty of room, keeps clear of buildings and farmland, and stops once the area
/// holds as many trees as the limit allows. Felled trees drop seeds, which the woodcutter delivers, so the
/// two roles keep a forest going between them.
/// </summary>
public class TreeFarmerWorker : WorkerBrain
{
    private enum Phase { Plan = 0, Fetch = 1, Walk = 2, Plant = 3, Return = 4 }

    private const float PlantTime = 0.6f;

    /// <summary>Most path checks made while choosing spots, so one planning pass cannot stall the game.</summary>
    private const int MaxReachChecks = 30;
    private const float GridStep = 2f;
    private const float TreeSpacingMargin = 1f;
    private const float DefaultTreeRoom = 2f;
    private const float MinWaterClearance = 0.5f;
    private const float FarmlandClearance = 2.5f;

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
        foreach (TreeBase tree in FindObjectsByType<TreeBase>(FindObjectsSortMode.None))
        {
            if (tree != null && tree.gameObject.activeInHierarchy && InJobArea(tree.transform.position))
            {
                total++;
                PlantType type = PlantCatalogue.Of(tree.gameObject);
                if (type != null && type.IsTree)
                {
                    counts[type.Family]++;
                }
            }
        }

        foreach (Plant plant in FindObjectsByType<Plant>(FindObjectsSortMode.None))
        {
            if (plant != null && InJobArea(plant.transform.position))
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
    /// Ground where this tree can be planted: its biome, real dirt, flat enough, above water, away from
    /// buildings and farmland, with open sky and room all round, and well clear of other trees.
    /// </summary>
    private List<Vector3> FindSpots(PlantType type, int max)
    {
        int spaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid");
        int roofMask = LayerMask.GetMask("Default", "static_solid", "piece");
        int pieceMask = LayerMask.GetMask("piece", "piece_nonsolid");
        int terrainMask = LayerMask.GetMask("terrain");
        float radius = WorkerConfig.JobRadius.Value;
        float room = Mathf.Max(type.Plant.m_growRadius, 0.5f);
        float clearance = WorkerConfig.TreeBuildingClearance.Value;
        int wrongBiome = 0;

        List<Occupied> occupied = [.. _plantedSpots];
        foreach (Plant plant in FindObjectsByType<Plant>(FindObjectsSortMode.None))
        {
            if (plant != null && Utils.DistanceXZ(plant.transform.position, Home) <= radius + 10f)
            {
                occupied.Add(new Occupied { Position = plant.transform.position, Radius = plant.m_growRadius });
            }
        }
        foreach (TreeBase tree in FindObjectsByType<TreeBase>(FindObjectsSortMode.None))
        {
            if (tree != null && tree.gameObject.activeInHierarchy && Utils.DistanceXZ(tree.transform.position, Home) <= radius + 10f)
            {
                PlantType existing = PlantCatalogue.Of(tree.gameObject);
                float r = existing != null && existing.IsTree ? existing.Plant.m_growRadius : DefaultTreeRoom;
                occupied.Add(new Occupied { Position = tree.transform.position, Radius = r });
            }
        }

        List<Vector3> spots = [];
        for (float x = -radius; x <= radius; x += GridStep)
        {
            for (float z = -radius; z <= radius; z += GridStep)
            {
                // A little jitter so a forest does not come out as a perfect grid.
                Vector3 p = new(Home.x + x + UnityEngine.Random.Range(-0.6f, 0.6f), 0f,
                    Home.z + z + UnityEngine.Random.Range(-0.6f, 0.6f));
                if (!InJobArea(p))
                {
                    continue;
                }
                p.y = ZoneSystem.instance.GetGroundHeight(p);

                Heightmap heightmap = Heightmap.FindHeightmap(p);
                if (heightmap == null || p.y <= ZoneSystem.instance.m_waterLevel + MinWaterClearance)
                {
                    continue;
                }
                if (!PlantCatalogue.BiomeAllows(type, p))
                {
                    wrongBiome++;
                    continue;
                }
                if (type.Piece.m_vegetationGroundOnly && !HasDirt(heightmap, p))
                {
                    continue;
                }
                if (NearFarmland(p) || Physics.CheckSphere(p, clearance, pieceMask))
                {
                    continue;
                }
                if (!FlatEnough(p, terrainMask)
                    || Physics.CheckSphere(p + (Vector3.up * 0.2f), room + 0.3f, spaceMask)
                    || Physics.Raycast(p + (Vector3.up * 0.2f), Vector3.up, 100f, roofMask))
                {
                    continue;
                }

                bool crowded = false;
                foreach (Occupied other in occupied)
                {
                    crowded |= Utils.DistanceXZ(other.Position, p) < Mathf.Max(room, other.Radius) + TreeSpacingMargin;
                }
                if (!crowded)
                {
                    spots.Add(p);
                }
            }
        }

        if (spots.Count == 0 && wrongBiome > 0 && _loggedBiome.Add(type))
        {
            DvergrWorkersPlugin.Log.LogInfo("Tree farmer: " + type.Prefab.name + " cannot be planted here, wrong biome ("
                + Heightmap.FindBiome(Home) + " is not in plant biomes " + type.Plant.m_biome + ")");
        }

        Vector3 here = transform.position;
        spots.Sort((a, b) => Vector3.Distance(a, here).CompareTo(Vector3.Distance(b, here)));

        // Chosen spots must not crowd each other either, since they are planted one after another.
        List<Vector3> chosen = [];
        int checks = 0;
        foreach (Vector3 spot in spots)
        {
            bool crowded = false;
            foreach (Vector3 other in chosen)
            {
                crowded |= Utils.DistanceXZ(other, spot) < (room * 2f) + TreeSpacingMargin;
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

    /// <summary>The same dirt rule the game applies when a player plants.</summary>
    private static bool HasDirt(Heightmap heightmap, Vector3 p)
    {
        float mask = heightmap.GetVegetationMask(p);
        return heightmap.GetBiome(p) == Heightmap.Biome.AshLands ? mask <= 0.1f : mask >= 0.25f;
    }

    private static bool FlatEnough(Vector3 p, int terrainMask)
    {
        return Physics.Raycast(p + (Vector3.up * 2f), Vector3.down, out RaycastHit hit, 6f, terrainMask) && hit.normal.y >= 0.8f;
    }

    /// <summary>Tilled soil is the farmer's, not a forest's.</summary>
    private static bool NearFarmland(Vector3 p)
    {
        Vector3[] offsets =
        [
            Vector3.zero, Vector3.right * FarmlandClearance, Vector3.left * FarmlandClearance,
            Vector3.forward * FarmlandClearance, Vector3.back * FarmlandClearance,
        ];
        foreach (Vector3 offset in offsets)
        {
            Heightmap heightmap = Heightmap.FindHeightmap(p + offset);
            if (heightmap != null && heightmap.IsCultivated(p + offset))
            {
                return true;
            }
        }
        return false;
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

        if (!System.Linq.Enumerable.Any(PlantCatalogue.Trees))
        {
            IdleNote = "No tree saplings found in the cultivator";
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
        Dictionary<string, int> counts = CountFamilies(out int total);
        if (total >= WorkerConfig.MaxTrees.Value)
        {
            _full = true;
            return CarryingSeeds();
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

    private bool StartBatch()
    {
        Dictionary<string, int> counts = CountFamilies(out int total);
        int room = WorkerConfig.MaxTrees.Value - total;
        if (room <= 0)
        {
            _full = true;
            return false;
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
        switch (Navigator.Step(dt))
        {
            case NavResult.InReach:
            case NavResult.CloseEnough:
                _timer = PlantTime;
                _phase = Phase.Plant;
                break;

            case NavResult.Failed:
                if (_batchSpots.Count > 0)
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
