using UnityEngine;

namespace DvergrWorkers.Finder;

/// <summary>
/// The finder's images, drawn pixel by pixel when first needed so the mod ships no asset bundle. Both are white
/// with a dark rim and get their colour from whatever draws them.
/// </summary>
internal static class FinderTextures
{
    /// <summary>A downward-pointing triangle: over the worker it points at it, at the screen edge it is turned to point the way.</summary>
    public static Sprite Arrow
    {
        get
        {
            // Compared with Unity's == null rather than ??, as Unity objects can be destroyed behind our back.
            if (field == null)
            {
                field = MakeArrow();
            }
            return field;
        }
    }

    /// <summary>A chevron pointing along +u, repeated along the ground trail so the trail reads as "this way".</summary>
    public static Texture2D Chevron
    {
        get
        {
            // Compared with Unity's == null rather than ??, as Unity objects can be destroyed behind our back.
            if (field == null)
            {
                field = MakeChevron();
            }
            return field;
        }
    }

    private static Sprite MakeArrow()
    {
        const int size = 64;
        Texture2D texture = NewTexture(size, size, TextureWrapMode.Clamp);
        Color[] pixels = new Color[size * size];

        // In [-1, 1] on both axes, y up: the flat top edge at y = 0.55, the tip at y = -0.75.
        const float top = 0.55f;
        const float tip = -0.75f;
        const float halfWidth = 0.8f;
        const float slope = halfWidth / (top - tip);
        float edgeScale = 1f / Mathf.Sqrt(1f + (slope * slope));

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = ((x + 0.5f) / size * 2f) - 1f;
                float v = ((y + 0.5f) / size * 2f) - 1f;

                // Distance inside the triangle (negative outside), the nearer of the top edge and the slanted sides.
                float toTop = top - v;
                float toSide = ((slope * (v - tip)) - Mathf.Abs(u)) * edgeScale;
                float inside = Mathf.Min(toTop, toSide);

                pixels[(y * size) + x] = Rimmed(inside, 0.07f);
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();
        Sprite sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }

    private static Texture2D MakeChevron()
    {
        const int width = 64;
        const int height = 64;
        Texture2D texture = NewTexture(width, height, TextureWrapMode.Repeat);
        Color[] pixels = new Color[width * height];

        // u runs along the trail (0..1 per tile), v across it (-1..1). The chevron's tip is at u = 0.7.
        const float tipU = 0.7f;
        const float depth = 0.35f;
        const float thickness = 0.13f;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float u = (x + 0.5f) / width;
                float v = ((y + 0.5f) / height * 2f) - 1f;
                float centre = tipU - (depth * Mathf.Abs(v));
                float inside = Mathf.Min(thickness - Mathf.Abs(u - centre), 0.85f - Mathf.Abs(v));
                pixels[(y * width) + x] = Rimmed(inside, 0.05f);
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();
        return texture;
    }

    /// <summary>White fill fading through a dark rim to transparent, for a shape whose signed inside distance is given.</summary>
    private static Color Rimmed(float inside, float rim)
    {
        float alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-rim, -rim * 0.3f, inside));
        float light = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, rim * 0.6f, inside));
        float shade = Mathf.Lerp(0.12f, 1f, light);
        return new Color(shade, shade, shade, alpha);
    }

    private static Texture2D NewTexture(int width, int height, TextureWrapMode wrap)
    {
        return new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
        {
            wrapMode = wrap,
            filterMode = FilterMode.Bilinear,

            // Kept for the whole session: the game unloads unreferenced assets on every scene change.
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
    }
}
