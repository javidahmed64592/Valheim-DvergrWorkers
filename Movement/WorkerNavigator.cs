using System;
using System.Collections.Generic;
using System.Globalization;

using DvergrWorkers.Core;

using UnityEngine;

namespace DvergrWorkers.Movement;

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

    /// <summary>Seconds allowed per metre of path on top of a fixed allowance, so a long walk is not cut short.</summary>
    private const float SecondsPerMetre = 0.75f;
    private const float WalkAllowance = 10f;

    /// <summary>
    /// Vanilla MoveTo reports "arrived" when it has no path, which is normal for a few seconds while the game builds
    /// the navmesh around somewhere new. Wait this long for a path before steering straight for the spot instead.
    /// </summary>
    private const float NoPathWait = 6f;
    private const float ArriveSlack = 1f;

    private readonly Transform _transform = ai.transform;
    private readonly Character _character = character;
    private readonly MonsterAI _ai = ai;
    private readonly WorkerRole _role = role;
    private readonly int _groundMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "Default_small");
    private readonly int _obstacleMask = LayerMask.GetMask("Default", "static_solid", "piece", "Default_small");

    /// <summary>Walls and other solid things that block reaching through them (not bushes or saplings).</summary>
    private readonly int _sightMask = LayerMask.GetMask("Default", "static_solid", "piece");

    private const float SightMargin = 0.3f;
    private readonly List<Vector3> _pathScratch = [];

    private Func<Vector3, Vector3> _closestPoint;
    private string _targetName;
    private bool _rememberFailure;
    private float _reach;
    private Vector3 _standPoint;
    private float _walkTimer;
    private float _walkLimit = WalkTimeout;
    private float _noPathTimer;
    private float _pathLength;
    private float _stuckTimer;
    private Vector3 _stuckSamplePos;
    private int _stuckStrikes;
    private int _escapes;
    private float _escapeUntil;
    private Vector3 _escapeDir;

    /// <summary>A first point to steer straight to before following the path, to get off a navmesh island.</summary>
    private bool _hasVia;
    private Vector3 _via;
    private float _viaUntil;

    /// <summary>What the last stand point search found for <see cref="_via"/>, used by <see cref="Begin"/>.</summary>
    private bool _foundVia;

    /// <summary>A last stretch to steer straight along, into a spot the navmesh has no path to.</summary>
    private bool _hasApproach;
    private Vector3 _approachPoint;
    private bool _foundApproach;
    private Vector3 _foundLanding;
    private Vector3 _foundViaPoint;

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
    /// <param name="reach">How close is close enough; the role's reach if not given.</param>
    public bool Begin(string targetName, Func<Vector3, Vector3> closestPoint, bool rememberFailure = false, float reach = 0f)
    {
        // A target this worker already gave up on is not worth walking to again for a while. Finding no standing
        // spot is remembered too: that search is the expensive part, and repeating it every round for a piece
        // nobody can reach (a high roof, a fire behind a wall) is what stalls the game.
        Vector3 spot = rememberFailure ? closestPoint(_transform.position) : Vector3.zero;
        if (rememberFailure && (UnreachableSpots.IsKnown(spot) || UnreachableSpots.IsPending(spot)))
        {
            return false;
        }

        // The search reads the new target, but a failed one must leave a walk already under way as it was.
        Func<Vector3, Vector3> previousPoint = _closestPoint;
        float previousReach = _reach;
        _closestPoint = closestPoint;
        _reach = reach > 0f ? reach : _role.Reach.Value;
        if (!FindStandPoint(out Vector3 standPoint, strict: false))
        {
            _closestPoint = previousPoint;
            _reach = previousReach;
            if (rememberFailure)
            {
                UnreachableSpots.Suspect(spot);
            }
            return false;
        }

        _standPoint = standPoint;
        _targetName = targetName;
        _rememberFailure = rememberFailure;
        ResetWalk();
        _hasVia = _foundVia;
        _via = _foundViaPoint;
        if (_hasVia)
        {
            DvergrWorkersPlugin.Log.LogInfo("No path out from " + _transform.position + ", stepping to " + _via
                + " first to reach " + targetName);
        }
        _viaUntil = Time.time + ViaTimeout;
        _hasApproach = _foundApproach;
        if (_hasApproach)
        {
            _approachPoint = standPoint;
            _standPoint = _foundLanding;
            DvergrWorkersPlugin.Log.LogInfo("No path into the spot by " + targetName + ", walking to " + _foundLanding
                + " and stepping in from there");
        }
        _walkLimit = Mathf.Max(WalkTimeout, WalkAllowance + (_pathLength * SecondsPerMetre));
        return true;
    }

    /// <summary>
    /// Arms the navigator to walk to an exact, already validated ground point (such as an idle wander
    /// destination) and stop within <paramref name="tolerance"/> metres of it.
    /// </summary>
    public void BeginPoint(Vector3 point, float tolerance)
    {
        Vector3 raised = point + (Vector3.up * EyeHeight);
        _closestPoint = _ => raised;
        _targetName = "wander point";
        _rememberFailure = true;
        _reach = tolerance;
        _standPoint = point;
        ResetWalk();
        _walkLimit = Mathf.Max(WalkTimeout, WalkAllowance + (Vector3.Distance(_transform.position, point) * 1.5f * SecondsPerMetre));
    }

    private void ResetWalk()
    {
        _walkTimer = 0f;
        _noPathTimer = 0f;
        _stuckTimer = 0f;
        _stuckStrikes = 0;
        _escapes = 0;
        _escapeUntil = 0f;
        _hasVia = false;
        _hasApproach = false;
        _stuckSamplePos = _transform.position;
    }

    public NavResult Step(float dt)
    {
        if (InReach(_transform.position))
        {
            _ai.StopMoving();
            return NavResult.InReach;
        }

        _walkTimer += dt;

        // Off a navmesh island first: straight to the launch point, then the path from there takes over.
        if (_hasVia)
        {
            if (Utils.DistanceXZ(_transform.position, _via) > ViaArrive && Time.time < _viaUntil)
            {
                _ = SteerTowards(dt, _via);
                return NavResult.Moving;
            }
            _hasVia = false;
            _stuckSamplePos = _transform.position;
            _stuckTimer = 0f;
        }

        // At the landing point: the navmesh goes no further, so steer straight for the real standing spot.
        if (_hasApproach && Utils.DistanceXZ(_transform.position, _standPoint) <= ArriveSlack)
        {
            _hasApproach = false;
            _standPoint = _approachPoint;
            _stuckStrikes = Mathf.Max(_stuckStrikes, 1);
            _stuckSamplePos = _transform.position;
            _stuckTimer = 0f;
        }

        // Mid-escape: walk straight along the clearest direction for a moment to get free of whatever is wedging us.
        if (Time.time < _escapeUntil)
        {
            _ai.MoveTowards(_escapeDir, false);
            if (_walkTimer <= _walkLimit)
            {
                return NavResult.Moving;
            }
        }

        // Path following first; once stuck, fall back to steering with obstacle avoidance.
        bool waitingForPath = false;
        bool arrived = _stuckStrikes >= 1 ? SteerDirect(dt) : _ai.MoveTo(dt, _standPoint, 0.6f, false);
        if (arrived && Utils.DistanceXZ(_transform.position, _standPoint) > ArriveSlack)
        {
            // Not really there: MoveTo found no path. Give the navmesh a moment, then head straight for the spot.
            arrived = false;
            _noPathTimer += dt;
            if (_noPathTimer < NoPathWait)
            {
                waitingForPath = true;
            }
            else
            {
                arrived = SteerDirect(dt);
            }
        }

        _stuckTimer = waitingForPath ? 0f : _stuckTimer + dt;
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

        if (!arrived && _walkTimer <= _walkLimit && _stuckStrikes < StuckStrikesToGiveUp)
        {
            return NavResult.Moving;
        }

        _ai.StopMoving();
        if (Gap(_transform.position) <= _reach * ToleranceFactor)
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

    /// <summary>Steers straight for the standing spot around obstacles, but never into water.</summary>
    private bool SteerDirect(float dt)
    {
        return SteerTowards(dt, _standPoint);
    }

    /// <summary>Steers straight for <paramref name="goal"/> around obstacles, but never into water.</summary>
    private bool SteerTowards(float dt, Vector3 goal)
    {
        Vector3 dir = goal - _transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 1f && IsWaterAhead(_transform.position + (dir.normalized * 1.5f)))
        {
            _ai.StopMoving();
            return false;
        }
        return _ai.MoveAndAvoid(dt, goal, 0.6f, false);
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
            if (IsWaterAhead(_transform.position + (dir * 2f)))
            {
                // Better to stay wedged than to wade into water.
                continue;
            }
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

    /// <summary>True if a standing point (the top of the ground or a floor) is below a water surface.</summary>
    public static bool IsUnderwater(Vector3 point)
    {
        return Floating.GetLiquidLevel(point) > point.y;
    }

    /// <summary>True if whatever a worker would stand on at this spot (ground, or a floor over water) is under water.</summary>
    private bool IsWaterAhead(Vector3 flat)
    {
        return Physics.Raycast(flat + (Vector3.up * 2f), Vector3.down, out RaycastHit hit, 6f, _groundMask) && IsUnderwater(hit.point);
    }

    /// <summary>
    /// Finds the nearest dry ground around a worker that has ended up in water (knocked back, spawned on a
    /// flooded spot...), so it can climb straight out instead of waiting for a path that the navmesh will not give.
    /// </summary>
    public bool FindShore(float maxDistance, out Vector3 shore)
    {
        Vector3 here = _transform.position;
        for (float radius = 2f; radius <= maxDistance; radius += 2f)
        {
            int angles = Mathf.Max(8, Mathf.RoundToInt(radius * 2f));
            for (int a = 0; a < angles; a++)
            {
                float rad = a * Mathf.PI * 2f / angles;
                Vector3 flat = here + (new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad)) * radius);
                if (Physics.Raycast(new Vector3(flat.x, here.y + 10f, flat.z), Vector3.down, out RaycastHit hit, 30f, _groundMask)
                    && !IsUnderwater(hit.point))
                {
                    shore = hit.point;
                    return true;
                }
            }
        }
        shore = Vector3.zero;
        return false;
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
        return Gap(feet) <= _reach;
    }

    /// <summary>
    /// Whether nothing solid stands between a worker at <paramref name="feet"/> and the target. Reach is measured in
    /// a straight line, so without this a fire on the far side of a wall counts as in reach from the near side, and
    /// the worker heads for the wall instead of walking round through the door.
    /// </summary>
    private bool CanSee(Vector3 feet)
    {
        Vector3 chest = Chest(feet);
        Vector3 toTarget = _closestPoint(chest) - chest;
        float distance = toTarget.magnitude;

        // Stop just short of the target's surface so the target itself does not count as being in the way.
        return distance <= SightMargin + 0.1f
            || !Physics.Raycast(chest, toTarget / distance, distance - SightMargin, _sightMask, QueryTriggerInteraction.Ignore);
    }

    /// <summary>
    /// Whether there is a standing spot within reach of the target that the worker can walk to along a full
    /// path from where it is now. Used to drop planting spots that sit behind a wall before walking to them.
    /// </summary>
    public bool CanReach(Func<Vector3, Vector3> closestPoint)
    {
        Func<Vector3, Vector3> previous = _closestPoint;
        float previousReach = _reach;
        _closestPoint = closestPoint;
        _reach = _role.Reach.Value;
        try
        {
            return FindStandPoint(out _, strict: true);
        }
        finally
        {
            _closestPoint = previous;
            _reach = previousReach;
        }
    }

    /// <summary>
    /// Path queries are synchronous and, for a spot that cannot be reached, search the whole connected navmesh.
    /// These caps keep one search for a standing spot cheap however many ring points there are.
    /// </summary>
    private const int MaxPathQueries = 10;

    /// <summary>
    /// Candidates are tried nearest the worker first, so when this many in a row cannot be walked to, the target is
    /// almost certainly cut off (behind a wall, up on a roof) and asking about the rest would only cost time.
    /// </summary>
    private const int MaxMissesInARow = 3;

    /// <summary>
    /// How far (metres) a path may end from the spot it was asked for and still count as getting there; the navmesh
    /// moves a goal on top of furniture or against a wall a little way, while a path that is cut off stops far short.
    /// </summary>
    private const float PartialPathSlack = 2f;

    private readonly List<Vector3> _candidates = [];

    /// <summary>
    /// Samples ground positions in rings around the target and asks for a path to each, nearest the worker first.
    /// The best is the shortest walk to a spot within reach; if no path gets within reach, whichever path ends
    /// closest to the target. One query per candidate answers both, so a target nobody can reach costs a few
    /// queries instead of a dozen.
    /// </summary>
    private bool FindStandPoint(out Vector3 best, bool strict)
    {
        best = Vector3.zero;
        _pathLength = 0f;
        _foundVia = false;
        _foundApproach = false;
        Vector3 here = _transform.position;
        if (InReach(here) || (!strict && Gap(here) <= _reach * ToleranceFactor))
        {
            best = here;
            return true;
        }

        // Ground spots in rings around the target. Casting down from just above the target finds floors under roofs, not the roof.
        Vector3 target = _closestPoint(here);
        _candidates.Clear();
        for (float radius = 0f; radius <= _reach; radius += StandRingStep)
        {
            int angles = radius <= 0f ? 1 : StandAngles;
            for (int a = 0; a < angles; a++)
            {
                float rad = a * Mathf.PI * 2f / angles;
                Vector3 candidate = target + (new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad)) * radius);
                if (Physics.Raycast(candidate + Vector3.up, Vector3.down, out RaycastHit hit, 20f, _groundMask)
                    && !IsUnderwater(hit.point))
                {
                    _candidates.Add(hit.point);
                }
            }
        }

        // The spots nearest the worker are the likeliest to have a short path, so they are asked about first.
        _candidates.Sort((a, b) => (a - here).sqrMagnitude.CompareTo((b - here).sqrMagnitude));

        float bestLength = float.MaxValue;
        bool found = false;
        float bestGap = float.MaxValue;
        Vector3 nearest = Vector3.zero;
        float nearestLength = 0f;
        int queries = 0;
        int misses = 0;
        float farthestEnd = 0f;
        foreach (Vector3 candidate in _candidates)
        {
            if (queries++ >= MaxPathQueries || misses >= MaxMissesInARow)
            {
                break;
            }

            // A partial path is asked for so that, if it falls short, it still says how close one can get.
            if (!WorkerPaths.GetPath(here, candidate, _pathScratch, _ai.m_pathAgentType, requireFullPath: false)
                || _pathScratch.Count == 0)
            {
                misses++;
                continue;
            }

            // The path may stop short, or the navmesh may have snapped the goal elsewhere: judge from where we will really stand.
            Vector3 end = _pathScratch[_pathScratch.Count - 1];
            farthestEnd = Mathf.Max(farthestEnd, Utils.DistanceXZ(end, here));
            if (!InReach(end) || !CanSee(end)
                || Physics.CheckCapsule(end + (Vector3.up * 0.5f), end + (Vector3.up * 1.4f), 0.4f, _obstacleMask))
            {
                float gap = Gap(end);
                if (gap < bestGap)
                {
                    bestGap = gap;
                    nearest = end;
                    nearestLength = PathLength(_pathScratch);
                }

                // Only a path that stops well short of the spot says the target may be cut off. Reaching a spot that
                // is just out of reach (the outer ring, which is nearest the worker and so tried first) says nothing.
                if (Utils.DistanceXZ(end, candidate) > PartialPathSlack)
                {
                    misses++;
                }
                continue;
            }
            misses = 0;

            if (strict)
            {
                // Only asked whether there is a way at all.
                best = end;
                return true;
            }

            float length = PathLength(_pathScratch);
            if (length < bestLength)
            {
                bestLength = length;
                _pathLength = length;
                best = end;
                found = true;
            }

            // A path that is close to a straight line cannot be beaten by much, so stop looking.
            if (length <= (Vector3.Distance(here, end) * 1.3f) + 2f)
            {
                return true;
            }
        }
        if (found)
        {
            return true;
        }

        // Every path ended right next to the worker: it is standing on a scrap of navmesh cut off from the rest
        // (between beds, behind an open door), which it can step off although the navmesh says it cannot.
        if (farthestEnd < IslandRadius && TryLeaveIsland(here, out best))
        {
            return true;
        }

        // Or the other way round: the target is in such a scrap (a bed in a corner) and paths stop just outside it.
        // Only tried when a path did get close, so a target nobody can reach (a roof) costs no more queries.
        if (bestGap <= _reach + IslandRadius && TryLandNear(here, out best))
        {
            return true;
        }
        if (strict)
        {
            return false;
        }

        // No path gets within reach: settle for whichever ends closest to the target, if that is any closer than here.
        if (bestGap < Gap(here) - 0.5f)
        {
            best = nearest;
            _pathLength = nearestLength;
            return true;
        }
        return false;
    }

    /// <summary>How far (metres) every path may end from the worker for it to count as cut off.</summary>
    private const float IslandRadius = 3f;

    private const int MaxIslandQueries = 6;
    private const int MaxLandingQueries = 4;
    private const float ViaArrive = 0.7f;
    private const float ViaTimeout = 4f;

    private static readonly float[] LaunchRadii = [3.5f, 2f];
    private readonly List<Vector3> _launches = [];

    /// <summary>
    /// Looks for a point a couple of metres away that the worker can walk to in a straight line and from which a
    /// path does lead to within reach of the target. The navmesh leaves out gaps narrower than a worker plus a margin
    /// and steps higher than a stair (a bed, say), so a worker can stand somewhere it has no path out of. On success
    /// the launch point is noted for <see cref="Begin"/>, which steers there before following the path.
    /// </summary>
    private bool TryLeaveIsland(Vector3 here, out Vector3 best)
    {
        best = Vector3.zero;

        // Head for a spot the worker could stand on and see the target from, nearest the worker first.
        if (!GoodCandidate(out Vector3 goal))
        {
            return false;
        }

        StraightLineNeighbours(here);

        int queries = 0;
        foreach (Vector3 launch in _launches)
        {
            if (queries++ >= MaxIslandQueries)
            {
                break;
            }
            if (!WorkerPaths.GetPath(launch, goal, _pathScratch, _ai.m_pathAgentType, requireFullPath: false)
                || _pathScratch.Count == 0)
            {
                continue;
            }
            Vector3 end = _pathScratch[_pathScratch.Count - 1];
            if (InReach(end) && CanSee(end)
                && !Physics.CheckCapsule(end + (Vector3.up * 0.5f), end + (Vector3.up * 1.4f), 0.4f, _obstacleMask))
            {
                best = end;
                _pathLength = Vector3.Distance(here, launch) + PathLength(_pathScratch);
                _foundVia = true;
                _foundViaPoint = launch;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The first standing spot nearest the worker that is within reach of the target and can see it, or false.
    /// </summary>
    private bool GoodCandidate(out Vector3 goal)
    {
        foreach (Vector3 candidate in _candidates)
        {
            if (InReach(candidate) && CanSee(candidate))
            {
                goal = candidate;
                return true;
            }
        }
        goal = Vector3.zero;
        return false;
    }

    /// <summary>
    /// Adds the points around <paramref name="centre"/> that can be walked to from it in a straight line, with room
    /// to stand and out of water, to <see cref="_launches"/>.
    /// </summary>
    private void StraightLineNeighbours(Vector3 centre)
    {
        Vector3 waist = Vector3.up * 0.8f;
        _launches.Clear();
        foreach (float radius in LaunchRadii)
        {
            for (int a = 0; a < StandAngles; a++)
            {
                float rad = a * Mathf.PI * 2f / StandAngles;
                Vector3 point = centre + (new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad)) * radius);
                if (Physics.Raycast(point + (Vector3.up * 1.5f), Vector3.down, out RaycastHit hit, 4f, _groundMask)
                    && !IsUnderwater(hit.point)
                    && !Physics.Linecast(centre + waist, hit.point + waist, _obstacleMask, QueryTriggerInteraction.Ignore)
                    && !Physics.CheckCapsule(hit.point + (Vector3.up * 0.5f), hit.point + (Vector3.up * 1.4f), 0.4f, _obstacleMask))
                {
                    _launches.Add(hit.point);
                }
            }
        }
    }

    /// <summary>
    /// Looks for a landing point a couple of metres from a standing spot by the target that a path does reach and
    /// from which the spot is a straight walk. On success the landing is noted for <see cref="Begin"/>, which walks
    /// there along the path and then steers straight in.
    /// </summary>
    private bool TryLandNear(Vector3 here, out Vector3 best)
    {
        best = Vector3.zero;
        if (!GoodCandidate(out Vector3 goal))
        {
            return false;
        }

        StraightLineNeighbours(goal);
        _launches.Sort((a, b) => (a - here).sqrMagnitude.CompareTo((b - here).sqrMagnitude));
        int queries = 0;
        foreach (Vector3 landing in _launches)
        {
            if (queries++ >= MaxLandingQueries)
            {
                break;
            }
            if (!WorkerPaths.GetPath(here, landing, _pathScratch, _ai.m_pathAgentType, requireFullPath: false)
                || _pathScratch.Count == 0)
            {
                continue;
            }
            Vector3 end = _pathScratch[_pathScratch.Count - 1];
            if (Utils.DistanceXZ(end, landing) <= 1f)
            {
                best = goal;
                _pathLength = PathLength(_pathScratch) + Vector3.Distance(end, goal);
                _foundApproach = true;
                _foundLanding = end;
                return true;
            }
        }
        return false;
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
