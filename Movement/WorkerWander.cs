using System;
using System.Collections.Generic;

using UnityEngine;

namespace DvergrWorkers;

/// <summary>
/// Idle behaviour for a worker between rounds: stand still for a while, then stroll to a nearby spot and
/// repeat. It replaces the vanilla idle AI (which wanders to the rim of the radius, walks into corners,
/// and chases "friends" such as the player) so spots are chosen with care: reachable, in the open, close
/// to home more often than not, and clear of the player and of other workers.
/// </summary>
internal sealed class WorkerWander(Transform transform, MonsterAI ai, WorkerNavigator navigator, Func<Vector3, float> nearestOther)
{
    private const int Candidates = 12;
    private const float MinTravel = 3f;
    private const float StepAwayTravel = 4f;
    private const float ArriveTolerance = 0.8f;
    private const float WorkerSpace = 2.5f;
    private const float PlayerKeepOut = 3f;
    private const float PlayerBlockingWalk = 1.2f;
    private const float ClearanceDistance = 1.5f;
    private const int ClearanceRays = 8;
    private const int MaxBlockedRays = 1;
    private const float MaxHeightDifference = 4f;
    private const float StepAwayCooldown = 2f;
    private const float FacePlayerAfter = 1f;
    private const float FacePlayerRange = 6f;
    private const float ResumeGap = 0.5f;

    private enum Phase
    {
        Pause = 0,
        Walk = 1,
    }

    private readonly Transform _transform = transform;
    private readonly MonsterAI _ai = ai;
    private readonly WorkerNavigator _navigator = navigator;
    private readonly Func<Vector3, float> _nearestOther = nearestOther;
    private readonly int _groundMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "Default_small");
    private readonly int _obstacleMask = LayerMask.GetMask("Default", "static_solid", "piece", "Default_small");
    private readonly List<Vector3> _pathScratch = [];

    private Phase _phase = Phase.Pause;
    private float _pauseTimer = UnityEngine.Random.Range(1f, 4f);
    private float _stillTime;
    private float _nextStepAway;
    private float _lastUpdate = -1f;
    private Vector3 _destination;

    /// <summary>
    /// True while the worker is standing still and may turn to face a nearby player. It never does while
    /// walking, which is what made workers dither between the player and their path.
    /// </summary>
    public bool CanFacePlayer => _phase == Phase.Pause && _stillTime >= FacePlayerAfter && Time.time - _lastUpdate < ResumeGap;

    /// <summary>Advances idle behaviour by one frame. Always handles the frame, so the vanilla idle AI never runs.</summary>
    public bool Update(float dt, Vector3 home)
    {
        // Something else (a round, sleep, a fight) had control since the last frame: start over from standing.
        if (Time.time - _lastUpdate > ResumeGap)
        {
            StartPause(UnityEngine.Random.Range(1f, 3f));
        }
        _lastUpdate = Time.time;

        if (_phase == Phase.Walk)
        {
            Walk(dt);
        }
        else
        {
            Pause(dt, home);
        }
        return true;
    }

    private void StartPause(float seconds)
    {
        _ai.StopMoving();
        _phase = Phase.Pause;
        _pauseTimer = seconds;
        _stillTime = 0f;
    }

    private void StartPause()
    {
        StartPause(UnityEngine.Random.Range(WorkerConfig.WanderPauseMin.Value, Mathf.Max(WorkerConfig.WanderPauseMin.Value, WorkerConfig.WanderPauseMax.Value)));
    }

    private void Pause(float dt, Vector3 home)
    {
        _ai.StopMoving();
        _pauseTimer -= dt;
        _stillTime += dt;

        // Someone is standing too close: shuffle off instead of blocking them.
        Player crowding = Player.GetClosestPlayer(_transform.position, WorkerConfig.PersonalSpace.Value);
        if (crowding != null && Time.time >= _nextStepAway)
        {
            _nextStepAway = Time.time + StepAwayCooldown;
            if (TryStepAway(home, crowding.transform.position, out Vector3 away))
            {
                BeginWalk(away);
                return;
            }
        }

        if (_pauseTimer > 0f)
        {
            return;
        }

        if (TryPick(home, MinTravel, out Vector3 destination))
        {
            BeginWalk(destination);
        }
        else
        {
            StartPause(UnityEngine.Random.Range(2f, 4f));
        }
    }

    private void BeginWalk(Vector3 destination)
    {
        _destination = destination;
        _navigator.BeginPoint(destination, ArriveTolerance);
        _phase = Phase.Walk;
        _stillTime = 0f;
    }

    private void Walk(float dt)
    {
        // Never barge through the player, and give up on a spot the player has just moved onto.
        if (Player.GetClosestPlayer(_transform.position, PlayerBlockingWalk) != null
            || NearestPlayerDistance(_destination) < PlayerKeepOut * 0.5f)
        {
            StartPause(UnityEngine.Random.Range(1f, 2.5f));
            return;
        }

        if (_navigator.Step(dt) != NavResult.Moving)
        {
            StartPause();
        }
    }

    private static float NearestPlayerDistance(Vector3 point)
    {
        float nearest = float.MaxValue;
        foreach (Player player in Player.GetAllPlayers())
        {
            nearest = Mathf.Min(nearest, Vector3.Distance(player.transform.position, point));
        }
        return nearest;
    }

    /// <summary>
    /// Samples spots around home, favouring the ones near it, and picks the best one that is reachable,
    /// in the open and away from people.
    /// </summary>
    private bool TryPick(Vector3 home, float minTravel, out Vector3 best)
    {
        best = Vector3.zero;
        float radius = Mathf.Min(WorkerConfig.WanderRadius.Value, WorkerConfig.JobRadius.Value);
        float bestScore = float.MinValue;
        Vector3 here = _transform.position;

        for (int i = 0; i < Candidates; i++)
        {
            // Squaring the roll makes short distances from home much more likely than long ones.
            float roll = UnityEngine.Random.value;
            float distance = radius * roll * roll;
            float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            Vector3 flat = home + (new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance);

            float travel = Utils.DistanceXZ(flat, here);
            if (travel < minTravel)
            {
                continue;
            }

            if (!Physics.Raycast(new Vector3(flat.x, home.y + 2f, flat.z), Vector3.down, out RaycastHit hit, 10f, _groundMask))
            {
                continue;
            }
            Vector3 spot = hit.point;
            if (Mathf.Abs(spot.y - home.y) > MaxHeightDifference || Floating.GetLiquidLevel(spot) > spot.y || UnreachableSpots.IsKnown(spot))
            {
                continue;
            }

            float playerGap = NearestPlayerDistance(spot);
            float workerGap = _nearestOther(spot);
            if (playerGap < PlayerKeepOut || workerGap < WorkerSpace || !IsOpen(spot) || !IsWalkable(here, spot, travel))
            {
                continue;
            }

            float score = (2f * (1f - (distance / radius))) + (Mathf.Min(playerGap, 10f) / 10f) + (Mathf.Min(workerGap, 8f) / 8f)
                + UnityEngine.Random.Range(0f, 0.3f);
            if (score > bestScore)
            {
                bestScore = score;
                best = spot;
            }
        }
        return bestScore > float.MinValue;
    }

    /// <summary>
    /// Finds somewhere to go that is clearly further from a player standing too close. Unlike a normal stroll
    /// it starts from where the worker is, so it works in a crowded room where every spot near home is taken,
    /// and it skips the open-space and worker-spacing checks: getting out of the way matters more.
    /// </summary>
    private bool TryStepAway(Vector3 home, Vector3 from, out Vector3 best)
    {
        best = Vector3.zero;
        Vector3 here = _transform.position;
        Vector3 away = here - from;
        away.y = 0f;
        away = away.sqrMagnitude > 0.01f ? away.normalized : _transform.forward;

        float hereGap = Utils.DistanceXZ(here, from);
        float bestGap = hereGap + 1.5f;
        float limit = WorkerConfig.JobRadius.Value;

        for (int i = 0; i < Candidates; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, UnityEngine.Random.Range(-80f, 80f), 0f) * away;
            Vector3 flat = here + (dir * UnityEngine.Random.Range(StepAwayTravel, StepAwayTravel + 4f));
            if (Utils.DistanceXZ(flat, home) > limit
                || !Physics.Raycast(new Vector3(flat.x, here.y + 2f, flat.z), Vector3.down, out RaycastHit hit, 10f, _groundMask))
            {
                continue;
            }

            Vector3 spot = hit.point;
            float gap = NearestPlayerDistance(spot);
            if (gap <= bestGap || Mathf.Abs(spot.y - home.y) > MaxHeightDifference || Floating.GetLiquidLevel(spot) > spot.y
                || UnreachableSpots.IsKnown(spot)
                || Physics.CheckCapsule(spot + (Vector3.up * 0.5f), spot + (Vector3.up * 1.4f), 0.4f, _obstacleMask)
                || !IsWalkable(here, spot, Vector3.Distance(here, spot)))
            {
                continue;
            }
            bestGap = gap;
            best = spot;
        }
        return bestGap > hereGap + 1.5f;
    }

    /// <summary>True if there is room to stand and nothing hemming the spot in, which rules out corners and tight gaps.</summary>
    private bool IsOpen(Vector3 spot)
    {
        if (Physics.CheckCapsule(spot + (Vector3.up * 0.5f), spot + (Vector3.up * 1.4f), 0.4f, _obstacleMask))
        {
            return false;
        }

        Vector3 origin = spot + (Vector3.up * 0.8f);
        int blocked = 0;
        for (int i = 0; i < ClearanceRays; i++)
        {
            float angle = i * Mathf.PI * 2f / ClearanceRays;
            Vector3 dir = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            if (Physics.Raycast(origin, dir, ClearanceDistance, _obstacleMask) && ++blocked > MaxBlockedRays)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>True if the navmesh has a full path to the spot that is not a long way round.</summary>
    private bool IsWalkable(Vector3 from, Vector3 spot, float straight)
    {
        if (!Pathfinding.instance.GetPath(from, spot, _pathScratch, _ai.m_pathAgentType, requireFullPath: true) || _pathScratch.Count == 0)
        {
            return false;
        }

        float length = 0f;
        for (int i = 1; i < _pathScratch.Count; i++)
        {
            length += Vector3.Distance(_pathScratch[i - 1], _pathScratch[i]);
        }
        return length <= (2f * straight) + 3f && Utils.DistanceXZ(_pathScratch[_pathScratch.Count - 1], spot) < 1.5f;
    }
}
