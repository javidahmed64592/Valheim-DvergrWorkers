using System.Collections.Generic;

using UnityEngine;

namespace DvergrWorkers.Finder;

/// <summary>
/// A line of green chevrons on the ground from the player to the worker, following the path the game's own
/// pathfinding would take (around walls, through doors, up stairs) and flowing toward the worker. The path is
/// worked out again twice a second as both of them move. Where no path can be found yet (the game builds its
/// navigation data on demand, which takes a moment), the last path found is kept.
/// </summary>
internal sealed class FinderTrail
{
    private const float RepathInterval = 0.5f;

    /// <summary>Length of one chevron along the trail, in metres.</summary>
    private const float ChevronSpacing = 0.9f;

    /// <summary>How fast the chevrons flow toward the worker, in metres per second.</summary>
    private const float FlowSpeed = 1.6f;

    private const float Width = 0.45f;

    /// <summary>The trail floats this far above the ground so it is not lost in grass or swallowed by the floor.</summary>
    private const float Lift = 0.15f;

    /// <summary>Straight stretches of the path are split into pieces about this long, each laid on the ground below.</summary>
    private const float SegmentLength = 0.75f;

    /// <summary>The trail starts this far ahead of the player, so it is not hidden under their feet.</summary>
    private const float StartGap = 1f;

    private static readonly int GroundMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "Default_small");

    private readonly List<Vector3> _path = [];
    private readonly List<Vector3> _points = [];
    private GameObject _root;
    private LineRenderer _line;
    private Material _material;
    private float _repathTimer;
    private float _flow;

    public void Show(Vector3 from, Vector3 to, float dt)
    {
        if (!EnsureCreated())
        {
            return;
        }

        _repathTimer -= dt;
        if (_repathTimer <= 0f)
        {
            _repathTimer = RepathInterval;
            Repath(from, to);
        }

        if (_points.Count < 2)
        {
            _root.SetActive(false);
            return;
        }

        _root.SetActive(true);
        _flow = Mathf.Repeat(_flow - (dt * FlowSpeed / ChevronSpacing), 1f);
        _material.mainTextureOffset = new Vector2(_flow, 0f);
    }

    public void Hide()
    {
        _path.Clear();
        _points.Clear();
        _repathTimer = 0f;
        if (_root != null)
        {
            _root.SetActive(false);
        }
    }

    private void Repath(Vector3 from, Vector3 to)
    {
        Pathfinding pathfinding = Pathfinding.instance;
        if (pathfinding == null)
        {
            return;
        }

        // A player, unlike a worker, will happily wade, so this uses the plain humanoid navmesh.
        if (!pathfinding.GetPath(from, to, _path, Pathfinding.AgentType.Humanoid) || _path.Count < 2)
        {
            return;
        }

        // The path ends where the navmesh does; finish it at the worker's feet.
        _path[_path.Count - 1] = to;
        LayOnGround();
        _line.positionCount = _points.Count;
        _line.SetPositions(_points.ToArray());
    }

    /// <summary>
    /// The path's corners are joined by straight lines that would cut through bumps in the ground, so each stretch
    /// is split into short pieces dropped onto the ground beneath. The first metre is left out.
    /// </summary>
    private void LayOnGround()
    {
        _points.Clear();
        float skipped = 0f;
        for (int i = 1; i < _path.Count; i++)
        {
            Vector3 a = _path[i - 1];
            Vector3 b = _path[i];
            float length = Vector3.Distance(a, b);
            int pieces = Mathf.Max(1, Mathf.CeilToInt(length / SegmentLength));
            for (int p = i == 1 ? 0 : 1; p <= pieces; p++)
            {
                float along = (float)p / pieces;
                if (skipped + (along * length) < StartGap)
                {
                    continue;
                }
                _points.Add(Ground(Vector3.Lerp(a, b, along)));
            }
            skipped += length;
        }
    }

    private static Vector3 Ground(Vector3 point)
    {
        Vector3 from = point + (Vector3.up * 1f);
        return Physics.Raycast(from, Vector3.down, out RaycastHit hit, 2.5f, GroundMask)
            ? hit.point + (Vector3.up * Lift)
            : point + (Vector3.up * Lift);
    }

    /// <summary>The trail object goes when the world is left; this builds a new one when needed.</summary>
    private bool EnsureCreated()
    {
        if (_root != null)
        {
            return true;
        }

        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
        {
            return false;
        }

        if (_material == null)
        {
            _material = new Material(shader)
            {
                mainTexture = FinderTextures.Chevron,
                mainTextureScale = new Vector2(1f / ChevronSpacing, 1f),
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
        }

        _root = new GameObject("DW_FinderTrail");
        _line = _root.AddComponent<LineRenderer>();
        _line.sharedMaterial = _material;
        _line.textureMode = LineTextureMode.Tile;
        _line.alignment = LineAlignment.View;
        _line.widthMultiplier = Width;
        _line.numCornerVertices = 2;
        _line.numCapVertices = 2;
        _line.useWorldSpace = true;
        _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _line.receiveShadows = false;

        // Fades in at the player's end and stays bright up to the worker.
        _line.colorGradient = new Gradient
        {
            colorKeys = [new GradientColorKey(WorkerFinder.Green, 0f), new GradientColorKey(WorkerFinder.Green, 1f)],
            alphaKeys = [new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.9f, 0.08f), new GradientAlphaKey(0.9f, 1f)],
        };

        _root.SetActive(false);
        return true;
    }
}
