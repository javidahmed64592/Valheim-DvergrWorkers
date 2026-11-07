using System.Collections.Generic;

using DvergrWorkers.Roles.Farmer;

using UnityEngine;

namespace DvergrWorkers.Roles.TreeFarmer;

/// <summary>
/// Trees, saplings and plants near the job area, bucketed by where they stand, so checking whether a spot is too
/// close to any of them only looks at its neighbours instead of every tree in a large area.
/// </summary>
internal sealed class OccupiedGrid
{
    private const float Cell = 4f;

    private readonly Dictionary<long, List<Occupied>> _cells = [];
    private float _maxRadius;

    private static long Key(int x, int z)
    {
        return (x * 1000003L) + z;
    }

    public void Clear()
    {
        foreach (List<Occupied> cell in _cells.Values)
        {
            cell.Clear();
        }
        _maxRadius = 0f;
    }

    public void Add(Occupied occupied)
    {
        long key = Key(Mathf.FloorToInt(occupied.Position.x / Cell), Mathf.FloorToInt(occupied.Position.z / Cell));
        if (!_cells.TryGetValue(key, out List<Occupied> cell))
        {
            cell = [];
            _cells[key] = cell;
        }
        cell.Add(occupied);
        _maxRadius = Mathf.Max(_maxRadius, occupied.Radius);
    }

    /// <summary>True if something already growing is too close to give a plant needing <paramref name="room"/> space at the point.</summary>
    public bool Crowds(Vector3 p, float room, float margin)
    {
        int span = Mathf.CeilToInt((Mathf.Max(room, _maxRadius) + margin) / Cell);
        int cx = Mathf.FloorToInt(p.x / Cell);
        int cz = Mathf.FloorToInt(p.z / Cell);
        for (int x = cx - span; x <= cx + span; x++)
        {
            for (int z = cz - span; z <= cz + span; z++)
            {
                if (!_cells.TryGetValue(Key(x, z), out List<Occupied> cell))
                {
                    continue;
                }
                foreach (Occupied other in cell)
                {
                    if (Utils.DistanceXZ(other.Position, p) < Mathf.Max(room, other.Radius) + margin)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }
}
