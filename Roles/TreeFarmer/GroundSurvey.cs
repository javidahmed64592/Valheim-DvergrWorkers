using System.Collections.Generic;
using System.Diagnostics;

using UnityEngine;

namespace DvergrWorkers.Roles.TreeFarmer;

/// <summary>
/// <para>
/// The tree farmer's map of where trees could grow in its job area: every point of a grid across the area that
/// passes the checks that do not depend on the species (dry, flat, open sky, real dirt or not, away from buildings,
/// farmland and paving), with the biome and dirt there noted so each species can be matched to it cheaply.
/// </para>
/// <para>
/// Those checks are a dozen physics and terrain queries per point, and a large job area has thousands of points:
/// done in one go they froze the game for a moment every time the tree farmer looked for work. Instead the survey
/// is spread over as many frames as it takes, a couple of milliseconds at a time, and the finished map is reused
/// until it is a minute old. Trees and saplings are left out of it on purpose, since they come and go; they are
/// checked against when spots are chosen.
/// </para>
/// </summary>
internal sealed class GroundSurvey
{
    public const float GridStep = 2f;

    /// <summary>A finished survey is used for this long (seconds) before it is redone, picking up new buildings and paths.</summary>
    private const float MaxAge = 60f;

    /// <summary>Time spent surveying per frame, in milliseconds.</summary>
    private const double BudgetPerFrame = 2.0;

    private const float MinWaterClearance = 0.5f;
    private const float FarmlandClearance = 2.5f;

    /// <summary>The least room any sapling needs, so ground too cramped for every species is dropped straight away.</summary>
    private const float MinRoom = 0.8f;

    private static readonly int SpaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid");
    private static readonly int RoofMask = LayerMask.GetMask("Default", "static_solid", "piece");
    private static readonly int PieceMask = LayerMask.GetMask("piece", "piece_nonsolid");
    private static readonly int TerrainMask = LayerMask.GetMask("terrain");

    internal readonly struct Spot(Vector3 position, Heightmap.Biome biome, bool dirt)
    {
        public Vector3 Position { get; } = position;
        public Heightmap.Biome Biome { get; } = biome;

        /// <summary>Real dirt, where the game lets a player plant things that only grow on natural ground.</summary>
        public bool Dirt { get; } = dirt;
    }

    private readonly Stopwatch _watch = new();
    private List<Spot> _done = [];
    private List<Spot> _next = [];
    private float _completedAt = -1f;
    private Vector3 _doneHome;
    private float _doneRadius;

    /// <summary>Next grid point to look at, or -1 when no survey is running.</summary>
    private int _index = -1;
    private int _side;
    private Vector3 _home;
    private float _radius;
    private float _limit;
    private float _clearance;

    /// <summary>True once a survey has finished; <see cref="Spots"/> is then usable even while a newer one runs.</summary>
    public bool Ready => _completedAt >= 0f;

    public IReadOnlyList<Spot> Spots => _done;

    /// <summary>Starts a new survey if there is none for this area yet or the last one is out of date.</summary>
    /// <param name="limit">How far from <paramref name="home"/> a spot may be (the job radius plus its tolerance).</param>
    public void RefreshIfStale(Vector3 home, float radius, float limit, float clearance)
    {
        if (_index >= 0)
        {
            return;
        }

        bool stale = !Ready || Time.time - _completedAt > MaxAge || home != _doneHome
            || !Mathf.Approximately(radius, _doneRadius) || !Mathf.Approximately(clearance, _clearance);
        if (!stale)
        {
            return;
        }

        _next.Clear();
        _index = 0;
        _home = home;
        _radius = radius;
        _limit = limit;
        _clearance = clearance;
        _side = Mathf.FloorToInt(2f * radius / GridStep) + 1;
    }

    /// <summary>Surveys some more of the area, if a survey is running. Called every frame.</summary>
    public void Continue()
    {
        if (_index < 0 || ZoneSystem.instance == null)
        {
            return;
        }

        int total = _side * _side;
        _watch.Restart();
        while (_index < total && _watch.Elapsed.TotalMilliseconds < BudgetPerFrame)
        {
            Survey(_index++);
        }
        if (_index < total)
        {
            return;
        }

        (_done, _next) = (_next, _done);
        _next.Clear();
        _completedAt = Time.time;
        _doneHome = _home;
        _doneRadius = _radius;
        _index = -1;
    }

    private void Survey(int index)
    {
        float x = -_radius + (index % _side * GridStep);
        float z = -_radius + (index / _side * GridStep);

        // A little jitter so a forest does not come out as a perfect grid.
        Vector3 p = new(_home.x + x + Random.Range(-0.6f, 0.6f), 0f, _home.z + z + Random.Range(-0.6f, 0.6f));
        if (Utils.DistanceXZ(p, _home) > _limit)
        {
            return;
        }
        p.y = ZoneSystem.instance.GetGroundHeight(p);

        Heightmap heightmap = Heightmap.FindHeightmap(p);
        if (heightmap == null || p.y <= ZoneSystem.instance.m_waterLevel + MinWaterClearance
            || heightmap.GetPaintMask(p).b > 0.3f || NearFarmland(p)
            || Physics.CheckSphere(p, _clearance, PieceMask)
            || !FlatEnough(p)
            || Physics.CheckSphere(p + (Vector3.up * 0.2f), MinRoom, SpaceMask)
            || Physics.Raycast(p + (Vector3.up * 0.2f), Vector3.up, 100f, RoofMask))
        {
            return;
        }

        _next.Add(new Spot(p, Heightmap.FindBiome(p), HasDirt(heightmap, p)));
    }

    /// <summary>
    /// The checks that depend on how much room a species needs, made only for the few spots actually being picked:
    /// no paving that close, and nothing in the way of the sapling.
    /// </summary>
    public static bool StillClear(Vector3 p, float room)
    {
        return !NearPaved(p, room) && !Physics.CheckSphere(p + (Vector3.up * 0.2f), room + 0.3f, SpaceMask);
    }

    /// <summary>The same dirt rule the game applies when a player plants.</summary>
    private static bool HasDirt(Heightmap heightmap, Vector3 p)
    {
        float mask = heightmap.GetVegetationMask(p);
        return heightmap.GetBiome(p) == Heightmap.Biome.AshLands ? mask <= 0.1f : mask >= 0.25f;
    }

    private static bool FlatEnough(Vector3 p)
    {
        return Physics.Raycast(p + (Vector3.up * 2f), Vector3.down, out RaycastHit hit, 6f, TerrainMask) && hit.normal.y >= 0.8f;
    }

    /// <summary>Stone-paved ground (the hoe's "paved" paint) is for walking on, not for trees.</summary>
    private static bool NearPaved(Vector3 p, float room)
    {
        return Paved(p + (Vector3.right * room)) || Paved(p + (Vector3.left * room))
            || Paved(p + (Vector3.forward * room)) || Paved(p + (Vector3.back * room));
    }

    private static bool Paved(Vector3 p)
    {
        Heightmap heightmap = Heightmap.FindHeightmap(p);
        return heightmap != null && heightmap.GetPaintMask(p).b > 0.3f;
    }

    /// <summary>Tilled soil is the farmer's, not a forest's.</summary>
    private static bool NearFarmland(Vector3 p)
    {
        return Cultivated(p) || Cultivated(p + (Vector3.right * FarmlandClearance)) || Cultivated(p + (Vector3.left * FarmlandClearance))
            || Cultivated(p + (Vector3.forward * FarmlandClearance)) || Cultivated(p + (Vector3.back * FarmlandClearance));
    }

    private static bool Cultivated(Vector3 p)
    {
        Heightmap heightmap = Heightmap.FindHeightmap(p);
        return heightmap != null && heightmap.IsCultivated(p);
    }
}
