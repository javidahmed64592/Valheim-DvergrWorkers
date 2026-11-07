using System.Collections.Generic;
using System.Diagnostics;

using UnityEngine;

namespace DvergrWorkers.Movement;

/// <summary>
/// Every path query a worker makes goes through here so their cost can be measured. A query is synchronous and,
/// toward somewhere that cannot be reached, searches the whole connected navmesh, which in a large job area is the
/// costliest single thing a worker does. The running totals let a slow update say how much of it was pathfinding,
/// and let planning stop asking once it has spent its share.
/// </summary>
internal static class WorkerPaths
{
    private static readonly Stopwatch Watch = new();

    /// <summary>Path queries made this session.</summary>
    public static int Queries { get; private set; }

    /// <summary>Time spent on path queries this session, in milliseconds.</summary>
    public static double Milliseconds { get; private set; }

    public static bool GetPath(Vector3 from, Vector3 to, List<Vector3> path, Pathfinding.AgentType agent, bool requireFullPath)
    {
        Watch.Restart();
        bool found = Pathfinding.instance.GetPath(from, to, path, agent, requireFullPath);
        Milliseconds += Watch.Elapsed.TotalMilliseconds;
        Queries++;
        return found;
    }
}
