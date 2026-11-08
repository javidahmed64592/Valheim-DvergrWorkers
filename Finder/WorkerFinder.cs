using DvergrWorkers.Beds;
using DvergrWorkers.Core;

using UnityEngine;

namespace DvergrWorkers.Finder;

/// <summary>
/// <para>
/// Helps the local player find a worker: pressing E on a worker's bed starts showing the way to it, until the
/// player reaches the worker, presses E on the bed again, or it times out. While it runs:
/// </para>
/// <list type="bullet">
/// <item>a marker on the HUD sits over the worker, through walls, and clings to the screen's edge pointing
/// the way when the worker is off screen (<see cref="FinderMarker"/>);</item>
/// <item>a trail of arrows on the ground leads from the player to the worker along the path a person would
/// walk (<see cref="FinderTrail"/>);</item>
/// <item>the worker glows green.</item>
/// </list>
/// <para>
/// Everything here is local to this player's game: nothing is sent over the network or saved.
/// </para>
/// </summary>
internal static class WorkerFinder
{
    internal static readonly Color Green = new(0.42f, 1f, 0.5f);

    /// <summary>How long a worker may be missing from the loaded world before the search gives up.</summary>
    private const float LostAfter = 5f;

    private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

    private static string _bedId = "";
    private static string _workerName = "";
    private static float _startedAt;
    private static float _missingSince = -1f;
    private static float _glowRefresh;
    private static GameObject _glowing;

    private static readonly FinderMarker Marker = new();
    private static readonly FinderTrail Trail = new();

    public static bool IsFinding(string bedId)
    {
        return _bedId.Length > 0 && _bedId == bedId;
    }

    public static void Toggle(WorkerBed bed, Humanoid user)
    {
        string bedId = bed.BedId;
        if (IsFinding(bedId))
        {
            Stop();
            return;
        }

        WorkerBrain worker = WorkerBrain.FindLoaded(bedId);
        if (worker == null)
        {
            user.Message(MessageHud.MessageType.Center, Text.Show(Text.Of("$dw_msg_not_around", bed.Role.WorkerName)));
            return;
        }
        if (Vector3.Distance(user.transform.position, worker.transform.position) <= WorkerConfig.FinderArriveDistance.Value)
        {
            user.Message(MessageHud.MessageType.Center, Text.Show(Text.Of("$dw_msg_right_here", bed.Role.WorkerName)));
            return;
        }

        Stop();
        _bedId = bedId;
        _workerName = bed.Role.WorkerName;
        _startedAt = Time.time;
        _missingSince = -1f;
        user.Message(MessageHud.MessageType.Center, Text.Show(Text.Of("$dw_msg_follow_arrows", _workerName)));
    }

    public static void Stop()
    {
        _bedId = "";
        Marker.Hide();
        Trail.Hide();
        StopGlow();
    }

    /// <summary>Runs after every frame's movement and camera update, so the marker sits exactly on the worker.</summary>
    public static void LateUpdate(float dt)
    {
        if (_bedId.Length == 0)
        {
            return;
        }

        Player player = Player.m_localPlayer;
        if (player == null || player.IsDead())
        {
            Stop();
            return;
        }

        WorkerBrain worker = WorkerBrain.FindLoaded(_bedId);
        if (worker == null)
        {
            Marker.Hide();
            Trail.Hide();
            StopGlow();
            if (_missingSince < 0f)
            {
                _missingSince = Time.time;
            }
            else if (Time.time - _missingSince > LostAfter)
            {
                End(player, Text.Of("$dw_msg_lost_track", _workerName));
            }
            return;
        }
        _missingSince = -1f;

        float distance = Vector3.Distance(player.transform.position, worker.transform.position);
        if (distance <= WorkerConfig.FinderArriveDistance.Value)
        {
            End(player, Text.Of("$dw_msg_found", _workerName));
            return;
        }

        float timeout = WorkerConfig.FinderTimeout.Value;
        if (timeout > 0f && Time.time - _startedAt > timeout)
        {
            End(player, Text.Of("$dw_msg_stopped_showing", _workerName));
            return;
        }

        Marker.Show(worker, _workerName, distance);

        if (WorkerConfig.FinderTrail.Value)
        {
            Trail.Show(player.transform.position, worker.transform.position, dt);
        }
        else
        {
            Trail.Hide();
        }

        if (WorkerConfig.FinderGlow.Value)
        {
            Glow(worker.gameObject, dt);
        }
        else
        {
            StopGlow();
        }
    }

    private static void End(Player player, string message)
    {
        Stop();
        player.Message(MessageHud.MessageType.Center, Text.Show(message));
    }

    /// <summary>
    /// A slow green pulse, through the same per-object material overrides the game uses to tint pieces red when
    /// they cannot be built. Creature shaders take an emission colour (it is what makes starred creatures glow).
    /// </summary>
    private static void Glow(GameObject worker, float dt)
    {
        if (_glowing != worker)
        {
            StopGlow();
            _glowing = worker;
            _glowRefresh = 0f;
        }

        // The worker's tool appears in its hand later, so the list of renderers is refreshed now and then.
        _glowRefresh -= dt;
        bool refresh = _glowRefresh <= 0f;
        if (refresh)
        {
            _glowRefresh = 1f;
        }

        float pulse = Mathf.Lerp(0.25f, 0.6f, (Mathf.Sin(Time.time * 4f) + 1f) * 0.5f);
        MaterialMan.instance.SetValue(worker, EmissionColor, Green * pulse, refresh);
    }

    private static void StopGlow()
    {
        if (_glowing != null && MaterialMan.instance != null)
        {
            MaterialMan.instance.ResetValue(_glowing, EmissionColor);
        }
        _glowing = null;
    }
}
