using System.Collections.Generic;
using System.Diagnostics;

using UnityEngine;

namespace DvergrWorkers.Core;

/// <summary>
/// <para>
/// A map of a worker's job area built from checks at every point of a grid across it: where a tree could grow,
/// where there is tilled soil, and so on. A large job area holds thousands of grid points, each needing physics and
/// terrain queries, which done in one go freeze the game for a moment. So a survey is spread over as many frames as
/// it takes, a couple of milliseconds at a time, and the finished map is reused until it is out of date.
/// </para>
/// <para>
/// The previous map stays usable while a newer one is being made. Only the checks that do not change from one
/// moment to the next belong in a survey; whatever comes and goes (plants, trees) is checked when spots are chosen.
/// </para>
/// </summary>
/// <typeparam name="TSpot">What the survey records about each usable spot.</typeparam>
internal abstract class AreaSurvey<TSpot>
{
    /// <summary>Time spent surveying per frame, in milliseconds.</summary>
    private const double BudgetPerFrame = 2.0;

    private readonly Stopwatch _watch = new();
    private List<TSpot> _done = [];
    private List<TSpot> _next = [];
    private float _completedAt = -1f;
    private Vector3 _doneHome;
    private float _doneRadius;
    private float _doneSetting;

    /// <summary>Next grid point to look at, or -1 when no survey is running.</summary>
    private int _index = -1;
    private int _side;
    private float _setting;

    /// <summary>The spacing (metres) between grid points.</summary>
    protected abstract float GridStep { get; }

    /// <summary>How long (seconds) a finished survey is used before it is redone, to pick up changes to the area.</summary>
    protected abstract float MaxAge { get; }

    protected Vector3 Home { get; private set; }
    protected float Radius { get; private set; }

    /// <summary>How far from <see cref="Home"/> a spot may be (the job radius plus its tolerance).</summary>
    protected float Limit { get; private set; }

    /// <summary>True once a survey has finished; <see cref="Spots"/> is then usable even while a newer one runs.</summary>
    public bool Ready => _completedAt >= 0f;

    public IReadOnlyList<TSpot> Spots => _done;

    /// <summary>
    /// Starts a new survey if there is none for this area yet or the last one is out of date.
    /// <paramref name="setting"/> is any config value the checks depend on; changing it redoes the survey.
    /// </summary>
    public void RefreshIfStale(Vector3 home, float radius, float limit, float setting = 0f)
    {
        if (_index >= 0)
        {
            return;
        }

        bool stale = !Ready || Time.time - _completedAt > MaxAge || home != _doneHome
            || !Mathf.Approximately(radius, _doneRadius) || !Mathf.Approximately(setting, _doneSetting);
        if (!stale)
        {
            return;
        }

        _next.Clear();
        _index = 0;
        Home = home;
        Radius = radius;
        Limit = limit;
        _setting = setting;
        _side = Mathf.FloorToInt(2f * radius / GridStep) + 1;
    }

    /// <summary>Surveys some more of the area, if a survey is running. Call every frame.</summary>
    public void Continue()
    {
        if (_index < 0 || ZoneSystem.instance == null)
        {
            return;
        }

        int total = _side * _side;
        _watch.Restart();
        while (_index < total && _watch.Elapsed.TotalMilliseconds < BudgetPerFrame)
        {
            int index = _index++;
            Vector3 point = new(Home.x - Radius + (index % _side * GridStep), Home.y, Home.z - Radius + (index / _side * GridStep));
            Survey(point, _next);
        }
        if (_index < total)
        {
            return;
        }

        (_done, _next) = (_next, _done);
        _next.Clear();
        _completedAt = Time.time;
        _doneHome = Home;
        _doneRadius = Radius;
        _doneSetting = _setting;
        _index = -1;
    }

    /// <summary>Checks one grid point (at the home's height) and adds whatever spots it yields.</summary>
    protected abstract void Survey(Vector3 gridPoint, List<TSpot> into);
}
