using System.Collections.Generic;
using UnityEngine;

namespace DvergrWorkers
{
    /// <summary>
    /// Remembers places a worker gave up on reaching, for a few minutes, so that it plans around them instead of
    /// walking up to the same wall again and again (for example a planting spot just outside a fence).
    /// </summary>
    internal static class UnreachableSpots
    {
        private const float Cell = 2f;
        private const float Memory = 300f;

        private static readonly Dictionary<long, float> Until = new Dictionary<long, float>();

        private static long Key(Vector3 p)
        {
            long x = Mathf.RoundToInt(p.x / Cell);
            long z = Mathf.RoundToInt(p.z / Cell);
            return x * 1000003L + z;
        }

        public static void Add(Vector3 p)
        {
            Until[Key(p)] = Time.time + Memory;
        }

        public static bool IsKnown(Vector3 p)
        {
            return Until.TryGetValue(Key(p), out float until) && Time.time < until;
        }
    }
}
