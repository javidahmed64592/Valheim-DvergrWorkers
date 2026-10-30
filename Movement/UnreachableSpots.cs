using System.Collections.Generic;

using UnityEngine;

namespace DvergrWorkers;

/// <summary>
/// Remembers places a worker gave up on reaching, for a few minutes, so that it plans around them instead of
/// walking up to the same wall again and again (for example a planting spot just outside a fence).
/// </summary>
internal static class UnreachableSpots
{
    private const float Cell = 2f;
    private const float Memory = 300f;

    /// <summary>
    /// The game only builds the navigation mesh around a place once somebody asks about it, and that takes a few
    /// seconds, so a first failed path query does not mean the place is unreachable. A spot is only written off
    /// when it fails again after this long.
    /// </summary>
    private const float Grace = 12f;

    private static readonly Dictionary<long, float> Until = [];
    private static readonly Dictionary<long, float> Suspected = [];

    private static long Key(Vector3 p)
    {
        long x = Mathf.RoundToInt(p.x / Cell);
        long z = Mathf.RoundToInt(p.z / Cell);
        return (x * 1000003L) + z;
    }

    public static void Add(Vector3 p)
    {
        Until[Key(p)] = Time.time + Memory;
    }

    /// <summary>True if a path check failed for this spot a moment ago and it is too soon to ask again.</summary>
    public static bool IsPending(Vector3 p)
    {
        return Suspected.TryGetValue(Key(p), out float since) && Time.time - since < Grace;
    }

    /// <summary>Records a failed path check: unreachable if it already failed earlier, otherwise worth another look.</summary>
    public static void Suspect(Vector3 p)
    {
        long key = Key(p);
        if (Suspected.TryGetValue(key, out float since) && Time.time - since >= Grace && Time.time - since < Memory)
        {
            Add(p);
            _ = Suspected.Remove(key);
            return;
        }
        Suspected[key] = Time.time;
    }

    public static bool IsKnown(Vector3 p)
    {
        return Until.TryGetValue(Key(p), out float until) && Time.time < until;
    }
}
