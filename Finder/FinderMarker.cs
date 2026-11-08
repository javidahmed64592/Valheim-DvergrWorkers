using DvergrWorkers.Core;

using TMPro;

using UnityEngine;
using UnityEngine.UI;

namespace DvergrWorkers.Finder;

/// <summary>
/// A green arrow and label on the HUD that sit over the worker wherever it is, walls or not. When the worker is
/// off screen (or behind the player) the marker clings to the edge of the screen and points the way to turn.
/// It hides along with the rest of the HUD.
/// </summary>
internal sealed class FinderMarker
{
    /// <summary>How far in from the screen's edge the marker stops, in HUD units.</summary>
    private const float EdgeMargin = 70f;

    /// <summary>How far above the top of the worker's head the arrow floats, in metres.</summary>
    private const float HeadClearance = 0.9f;

    private GameObject _root;
    private RectTransform _arrow;
    private Image _arrowImage;
    private RectTransform _label;
    private TextMeshProUGUI _text;
    private Canvas _canvas;

    public void Show(WorkerBrain worker, string name, float distance)
    {
        Camera camera = Utils.GetMainCamera();
        if (camera == null || !EnsureCreated())
        {
            Hide();
            return;
        }

        Character character = worker.GetComponent<Character>();
        Vector3 above = (character != null ? character.GetTopPoint() : worker.transform.position + (Vector3.up * 2f)) + (Vector3.up * HeadClearance);
        Vector3 screen = camera.WorldToScreenPointScaled(above);

        float width = Screen.width;
        float height = Screen.height;
        float margin = EdgeMargin * (_canvas != null ? _canvas.scaleFactor : 1f);
        bool behind = screen.z < 0f;
        bool onScreen = !behind && screen.x >= margin && screen.x <= width - margin && screen.y >= margin && screen.y <= height - margin;

        _root.SetActive(true);
        Vector2 position = screen;
        float bob = Mathf.Sin(Time.time * 3f) * 4f;

        if (onScreen)
        {
            _arrow.localEulerAngles = Vector3.zero;
            _arrow.localPosition = new Vector3(0f, bob, 0f);
            _label.localPosition = new Vector3(0f, 34f, 0f);
        }
        else
        {
            // Behind the camera the projection is mirrored through the centre of the screen.
            Vector2 centre = new(width * 0.5f, height * 0.5f);
            Vector2 direction = (Vector2)screen - centre;
            if (behind)
            {
                direction = -direction;
            }
            if (direction.sqrMagnitude < 1f)
            {
                direction = Vector2.down;
            }

            float scale = Mathf.Min(
                Mathf.Abs(direction.x) > 0.01f ? (centre.x - margin) / Mathf.Abs(direction.x) : float.MaxValue,
                Mathf.Abs(direction.y) > 0.01f ? (centre.y - margin) / Mathf.Abs(direction.y) : float.MaxValue);
            position = centre + (direction * scale);

            // The arrow image points down (-y); turn it to point along the direction, nudged outward as it bobs.
            Vector2 unit = direction.normalized;
            float angle = (Mathf.Atan2(unit.y, unit.x) * Mathf.Rad2Deg) + 90f;
            _arrow.localEulerAngles = new Vector3(0f, 0f, angle);
            _arrow.localPosition = unit * bob;
            _label.localPosition = -unit * 46f;
        }

        _root.transform.position = position;
        _text.text = "<color=#6bff80>" + Localization.instance.Localize(name) + "</color>\n" + Mathf.RoundToInt(distance) + " m";

        float pulse = Mathf.Lerp(0.75f, 1f, (Mathf.Sin(Time.time * 4f) + 1f) * 0.5f);
        _arrowImage.color = new Color(WorkerFinder.Green.r, WorkerFinder.Green.g, WorkerFinder.Green.b, pulse);
    }

    public void Hide()
    {
        if (_root != null)
        {
            _root.SetActive(false);
        }
    }

    /// <summary>The HUD is rebuilt every time a world is entered, taking the marker with it; this builds a new one when needed.</summary>
    private bool EnsureCreated()
    {
        if (_root != null)
        {
            return true;
        }

        // Under the same root as the game's creature name tags, which are placed with the same screen projection
        // and hidden along with the rest of the HUD.
        Hud hud = Hud.instance;
        EnemyHud enemyHud = EnemyHud.instance;
        if (hud == null || enemyHud == null || enemyHud.m_hudRoot == null)
        {
            return false;
        }

        _root = new GameObject("DW_FinderMarker", typeof(RectTransform));
        _root.transform.SetParent(enemyHud.m_hudRoot.transform, worldPositionStays: false);
        _canvas = _root.GetComponentInParent<Canvas>();

        GameObject arrow = new("Arrow", typeof(RectTransform), typeof(Image));
        _arrow = arrow.GetComponent<RectTransform>();
        _arrow.SetParent(_root.transform, worldPositionStays: false);
        _arrow.sizeDelta = new Vector2(34f, 34f);
        _arrowImage = arrow.GetComponent<Image>();
        _arrowImage.sprite = FinderTextures.Arrow;
        _arrowImage.raycastTarget = false;

        GameObject label = new("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        _label = label.GetComponent<RectTransform>();
        _label.SetParent(_root.transform, worldPositionStays: false);
        _label.sizeDelta = new Vector2(260f, 60f);
        _text = label.GetComponent<TextMeshProUGUI>();

        // The same font and outlined material as the crosshair's hover text.
        TextMeshProUGUI hover = hud.m_hoverName;
        if (hover != null)
        {
            _text.font = hover.font;
            _text.fontSharedMaterial = hover.fontSharedMaterial;
        }
        _text.fontSize = 18f;
        _text.alignment = TextAlignmentOptions.Center;
        _text.textWrappingMode = TextWrappingModes.NoWrap;
        _text.raycastTarget = false;
        _text.richText = true;

        _root.SetActive(false);
        return true;
    }
}
