using System;
using System.Collections.Generic;
using System.Globalization;

using UnityEngine;

namespace DvergrWorkers;

internal enum NavResult
{
    /// <summary>Still walking towards the target.</summary>
    Moving = 0,
    /// <summary>Within reach of the target.</summary>
    InReach = 1,
    /// <summary>Stopped short (blocked or timed out) but close enough to work from here.</summary>
    CloseEnough = 2,
    /// <summary>Gave up: the target cannot be reached.</summary>
    Failed = 3,
}

/// <summary>
/// Walks a worker to within reach of a target, using the game's navmesh pathfinding. A target is anything
/// that can say which of its points is closest to a given position (a piece, a tree, a chest...).
/// </summary>
internal sealed class WorkerNavigator(MonsterAI ai, Character character, WorkerRole role)
{
    private const float WalkTimeout = 25f;
    private const int MaxEscapes = 3;
    private const float EscapeDuration = 1.2f;
    private const float StuckSampleInterval = 1.5f;
    private const float StuckDistance = 0.4f;
    private const int StuckStrikesToGiveUp = 3;
    private const float StandRingStep = 1.5f;
    private const int StandAngles = 8;
    private const float EyeHeight = 1.5f;
    private const float ToleranceFactor = 1.25f;

    private readonly Transform _transform = ai.transform;
    private readonly Character _character = character;
    private readonly MonsterAI _ai = ai;
    private readonly WorkerRole _role = role;
    private readonly int _groundMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "Default_small");
    private readonly int _obstacleMask = LayerMask.GetMask("Default", "static_solid", "piece", "Default_small");
    private readonly List<Vector3> _pathScratch = [];

    private Func<Vector3, Vector3> _closestPoint;
    private string _targetName;
    private bool _rememberFailure;
    private Vector3 _standPoint;
    private float _walkTimer;
    private float _stuckTimer;
    private Vector3 _stuckSamplePos;
    private int _stuckStrikes;
    private int _escapes;
    private float _escapeUntil;
    private Vector3 _escapeDir;

    /// <summary>Closest point on the solid colliders of an object (bounds for concave meshes, which can't be queried).</summary>
    public static Vector3 ClosestPointOnColliders(GameObject go, Vector3 from)
    {
        Vector3 best = go.transform.position;
        float bestDist = float.MaxValue;
        foreach (Collider col in go.GetComponentsInChildren<Collider>())
        {
            if (col.isTrigger || !col.enabled)
            {
                continue;
            }
            Vector3 p = col is MeshCollider mesh && !mesh.convex
                ? col.bounds.ClosestPoint(from)
                : col.ClosestPoint(from);
            float d = (p - from).sqrMagnitude;
            if (d < bestDist)
            {
                bestDist = d;
                best = p;
            }
        }
        return best;
    }

    /// <summary>
    /// Picks where to stand for the target. Returns false if no spot can be reached. On success the
    /// navigator is armed and <see cref="Step"/> should be called each frame.
    /// </summary>
    public bool Begin(string targetName, Func<Vector3, Vector3> closestPoint, bool rememberFailure = false)
    {
        _closestPoint = closestPoint;
        _targetName = targetName;
        _rememberFailure = rememberFailure;

        // A target this worker already gave up on is not worth walking to again for a while.
        if (rememberFailure && UnreachableSpots.IsKnown(closestPoint(_transform.position)))
        {
            return false;
        }
        if (!FindStandPoint(out _standPoint, strict: false))
        {
            return false;
        }

        _walkTimer = 0f;
        _stuckTimer = 0f;
        _stuckStrikes = 0;
        _escapes = 0;
        _escapeUntil = 0f;
        _stuckSamplePos = _transform.position;
        return true;
    }

    public NavResult Step(float dt)
    {
        if (InReach(_transform.position))
        {
            _ai.StopMoving();
            return NavResult.InReach;
        }

        _walkTimer += dt;

        // Mid-escape: walk straight along the clearest direction for a moment to get free of whatever is wedging us.
        if (Time.time < _escapeUntil)
        {
            _ai.MoveTowards(_escapeDir, false);
            if (_walkTimer <= WalkTimeout)
            {
                return NavResult.Moving;
            }
        }

        // Path following first; once stuck, fall back to steering with obstacle avoidance.
        bool arrived = _stuckStrikes >= 1
            ? _ai.MoveAndAvoid(dt, _standPoint, 0.6f, false)
            : _ai.MoveTo(dt, _standPoint, 0.6f, false);

        _stuckTimer += dt;
        if (_stuckTimer >= StuckSampleInterval)
        {
            // Movement is locked while an attack animation plays, so that is waiting, not being stuck.
            bool moved = Vector3.Distance(_transform.position, _stuckSamplePos) >= StuckDistance || _character.InAttack();
            _stuckStrikes = moved ? 0 : _stuckStrikes + 1;
            _stuckSamplePos = _transform.position;
            _stuckTimer = 0f;

            if (_stuckStrikes >= 2 && _escapes < MaxEscapes)
            {
                StartEscape();
                _stuckStrikes = 1;
                return NavResult.Moving;
            }
        }

        if (!arrived && _walkTimer <= WalkTimeout && _stuckStrikes < StuckStrikesToGiveUp)
        {
            return NavResult.Moving;
        }

        _ai.StopMoving();
        if (Gap(_transform.position) <= _role.Reach.Value * ToleranceFactor)
        {
            DvergrWorkersPlugin.Log.LogInfo("Stopped short of " + _targetName + " but close enough");
            return NavResult.CloseEnough;
        }

        if (_rememberFailure)
        {
            UnreachableSpots.Add(_closestPoint(_transform.position));
        }

        DvergrWorkersPlugin.Log.LogInfo("Could not reach " + _targetName + " (arrived=" + arrived
            + ", stuck=" + _stuckStrikes + ") at " + _transform.position + " -> stand " + _standPoint
            + " (" + Vector3.Distance(_transform.position, _standPoint).ToString("F1", CultureInfo.InvariantCulture) + "m), pathNodes="
            + _ai.m_path.Count + ", onGround=" + _character.IsOnGround() + ", inAttack=" + _character.InAttack());
        return NavResult.Failed;
    }

    /// <summary>Picks the direction with the most free space (leaning towards the goal) and heads that way briefly.</summary>
    private void StartEscape()
    {
        _escapes++;
        Vector3 origin = _transform.position + (Vector3.up * 0.8f);
        Vector3 toGoal = _standPoint - _transform.position;
        toGoal.y = 0f;
        toGoal = toGoal.sqrMagnitude > 0.01f ? toGoal.normalized : _transform.forward;

        float bestScore = float.MinValue;
        Vector3 bestDir = toGoal;
        for (int i = 0; i < 8; i++)
        {
            float angle = i * Mathf.PI * 0.25f;
            Vector3 dir = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            float free = Physics.Raycast(origin, dir, out RaycastHit hit, 2f, _obstacleMask) ? hit.distance : 2f;
            float score = free + (0.5f * Vector3.Dot(dir, toGoal));
            if (score > bestScore)
            {
                bestScore = score;
                bestDir = dir;
            }
        }

        _escapeDir = bestDir;
        _escapeUntil = Time.time + EscapeDuration;
        _stuckSamplePos = _transform.position;
        _stuckTimer = 0f;

        List<string> touching = [];
        foreach (Collider col in Physics.OverlapSphere(_transform.position + (Vector3.up * 0.9f), 0.9f, _obstacleMask))
        {
            string name = col.transform.root.name;
            if (!touching.Contains(name) && touching.Count < 6)
            {
                touching.Add(name);
            }
        }
        DvergrWorkersPlugin.Log.LogInfo("Stuck heading for " + _targetName + " at " + _transform.position
            + ", escaping (touching: " + string.Join(", ", touching) + ")");
    }

    private static Vector3 Chest(Vector3 feet)
    {
        return feet + (Vector3.up * EyeHeight);
    }

    /// <summary>Distance from a worker standing at <paramref name="feet"/> to the target.</summary>
    private float Gap(Vector3 feet)
    {
        Vector3 chest = Chest(feet);
        return Vector3.Distance(chest, _closestPoint(chest));
    }

    private bool InReach(Vector3 feet)
    {
        return Gap(feet) <= _role.Reach.Value;
    }

    /// <summary>
    /// Samples ground positions in rings around the target, keeps those that are within reach and
    /// reachable by a full navmesh path, and returns the one with the shortest walk. If none qualifies,
    /// falls back to whichever partial-path end gets closest to the target.
    /// </summary>
    /// <summary>
    /// Whether there is a standing spot within reach of the target that the worker can walk to along a full
    /// path from where it is now. Used to drop planting spots that sit behind a wall before walking to them.
    /// </summary>
    public bool CanReach(Func<Vector3, Vector3> closestPoint)
    {
        Func<Vector3, Vector3> previous = _closestPoint;
        _closestPoint = closestPoint;
        try
        {
            return FindStandPoint(out _, strict: true);
        }
        finally
        {
            _closestPoint = previous;
        }
    }

    private bool FindStandPoint(out Vector3 best, bool strict)
    {
        best = Vector3.zero;
        if (InReach(_transform.position))
        {
            best = _transform.position;
            return true;
        }

        Vector3 target = _closestPoint(_transform.position);
        float reach = _role.Reach.Value;
        float bestLength = float.MaxValue;
        float bestGap = float.MaxValue;
        bool found = false;

        for (int pass = 0; pass < (strict ? 1 : 2) && !found; pass++)
        {
            for (float radius = 0f; radius <= reach; radius += StandRingStep)
            {
                int angles = radius <= 0f ? 1 : StandAngles;
                for (int a = 0; a < angles; a++)
                {
                    float rad = a * Mathf.PI * 2f / angles;
                    Vector3 candidate = target + (new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad)) * radius);

                    // Cast down from just above the target so floors under roofs are found, not the roof.
                    if (!Physics.Raycast(candidate + Vector3.up, Vector3.down, out RaycastHit hit, 20f, _groundMask))
                    {
                        continue;
                    }

                    if (!Pathfinding.instance.GetPath(_transform.position, hit.point, _pathScratch, _ai.m_pathAgentType, requireFullPath: pass == 0)
                        || _pathScratch.Count == 0)
                    {
                        continue;
                    }

                    // The navmesh may have snapped the goal elsewhere; judge from where we will really stand.
                    Vector3 end = _pathScratch[_pathScratch.Count - 1];
                    if (pass == 0)
                    {
                        if (!InReach(end) || Physics.CheckCapsule(end + (Vector3.up * 0.5f), end + (Vector3.up * 1.4f), 0.4f, _obstacleMask))
                        {
                            continue;
                        }
                        float length = PathLength(_pathScratch);
                        if (length < bestLength)
                        {
                            bestLength = length;
                            best = end;
                            found = true;
                        }
                    }
                    else
                    {
                        float gap = Gap(end);
                        if (gap < bestGap)
                        {
                            bestGap = gap;
                            best = end;
                            found = true;
                        }
                    }
                }
            }
        }
        return found;
    }

    private static float PathLength(List<Vector3> path)
    {
        float total = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            total += Vector3.Distance(path[i - 1], path[i]);
        }
        return total;
    }
}