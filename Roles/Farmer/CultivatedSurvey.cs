using System.Collections.Generic;

using DvergrWorkers.Core;

using UnityEngine;

namespace DvergrWorkers.Roles.Farmer;

/// <summary>
/// The farmer's map of tilled soil in its job area under open sky, at half-metre spacing, with the biome at each
/// spot so a crop can be matched to it cheaply. Tilled ground is found on a one-metre grid first and only looked at
/// more closely where there is some. It is redone every so often to pick up soil the player has tilled since.
/// </summary>
internal sealed class CultivatedSurvey : AreaSurvey<CultivatedSurvey.Spot>
{
    private const float FineStep = 0.5f;

    private static readonly int RoofMask = LayerMask.GetMask("Default", "static_solid", "piece");

    private static readonly Vector2[] FineOffsets = [Vector2.zero, new(FineStep, 0f), new(0f, FineStep), new(FineStep, FineStep)];

    internal readonly struct Spot(Vector3 position, Heightmap.Biome biome)
    {
        public Vector3 Position { get; } = position;
        public Heightmap.Biome Biome { get; } = biome;
    }

    protected override float GridStep => 1f;

    /// <summary>Short, so soil the player has just tilled is planted before long.</summary>
    protected override float MaxAge => 20f;

    protected override void Survey(Vector3 gridPoint, List<Spot> into)
    {
        if (Utils.DistanceXZ(gridPoint, Home) > Limit || !IsCultivated(gridPoint))
        {
            return;
        }

        foreach (Vector2 offset in FineOffsets)
        {
            Vector3 p = new(gridPoint.x + offset.x, 0f, gridPoint.z + offset.y);
            p.y = ZoneSystem.instance.GetGroundHeight(p);
            if (IsCultivated(p) && !Physics.Raycast(p + (Vector3.up * 0.2f), Vector3.up, 100f, RoofMask))
            {
                into.Add(new Spot(p, Heightmap.FindBiome(p)));
            }
        }
    }

    internal static bool IsCultivated(Vector3 p)
    {
        Heightmap heightmap = Heightmap.FindHeightmap(p);
        return heightmap != null && heightmap.IsCultivated(p);
    }
}
