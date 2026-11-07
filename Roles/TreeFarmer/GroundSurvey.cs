using System.Collections.Generic;

using DvergrWorkers.Core;

using UnityEngine;

namespace DvergrWorkers.Roles.TreeFarmer;

/// <summary>
/// The tree farmer's map of where trees could grow in its job area: every grid point that passes the checks that do
/// not depend on the species (dry, flat, open sky, away from buildings, farmland and paving), with the biome and
/// dirt there noted so each species can be matched to it cheaply. The building clearance is the survey's setting.
/// </summary>
internal sealed class GroundSurvey : AreaSurvey<GroundSurvey.Spot>
{
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

    protected override float GridStep => 2f;

    /// <summary>Long enough to be cheap, short enough to notice new buildings and paths before long.</summary>
    protected override float MaxAge => 60f;

    private float _clearance;

    /// <summary>Starts a new survey if needed; trees are kept <paramref name="clearance"/> metres from any building piece.</summary>
    public void Refresh(Vector3 home, float radius, float limit, float clearance)
    {
        _clearance = clearance;
        RefreshIfStale(home, radius, limit, clearance);
    }

    protected override void Survey(Vector3 gridPoint, List<Spot> into)
    {
        // A little jitter so a forest does not come out as a perfect grid.
        Vector3 p = new(gridPoint.x + Random.Range(-0.6f, 0.6f), 0f, gridPoint.z + Random.Range(-0.6f, 0.6f));
        if (Utils.DistanceXZ(p, Home) > Limit)
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

        into.Add(new Spot(p, Heightmap.FindBiome(p), HasDirt(heightmap, p)));
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
