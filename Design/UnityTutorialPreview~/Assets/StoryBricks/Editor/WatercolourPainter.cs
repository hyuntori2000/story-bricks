using System.IO;
using UnityEditor;
using UnityEngine;

/// Paints the watercolour UI shapes (wash + pencil outline) as PNG sprites.
/// Sizes are in Figma px; textures are S times larger with an M px margin for the wobbly edge.
public static class WatercolourPainter
{
    public const int S = 3;
    public const int M = 6;
    public const string Dir = "Assets/StoryBricks/UI";

    static float Fbm(float x, float y, float seed)
    {
        float a = 0, amp = 0.5f, f = 1;
        for (int i = 0; i < 4; i++)
        {
            a += amp * Mathf.PerlinNoise(x * f + seed * 17.13f + i * 31.7f + 300f, y * f + seed * 9.71f + i * 12.3f + 300f);
            amp *= 0.5f; f *= 2f;
        }
        return a / 0.9375f;
    }

    static float Sd(float px, float py, float hw, float hh, float r)
    {
        float qx = Mathf.Abs(px) - hw + r, qy = Mathf.Abs(py) - hh + r;
        float ox = Mathf.Max(qx, 0), oy = Mathf.Max(qy, 0);
        return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0) - r;
    }

    static float SS(float a, float b, float x)
    {
        float t = Mathf.Clamp01((x - a) / (b - a));
        return t * t * (3 - 2 * t);
    }

    static float Hash(int x, int y, int seed)
    {
        float v = Mathf.Sin(x * 12.9898f + y * 78.233f + seed * 3.7f) * 43758.5453f;
        return v - Mathf.Floor(v);
    }

    public static Sprite Wash(string name, int w, int h, float r, Color c, Color dark, int seed, bool sliced)
    {
        r = Mathf.Min(r, h / 2f);
        int W = (w + 2 * M) * S, H = (h + 2 * M) * S;
        var px = new Color[W * H];
        Color pool = Color.Lerp(c, dark, 0.22f), rim = Color.Lerp(c, dark, 0.3f);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float fx = (x + 0.5f) / S - M - w / 2f, fy = (y + 0.5f) / S - M - h / 2f;
                float sd = Sd(fx, fy, w / 2f, h / 2f, r) + (Fbm(fx / 22f, fy / 22f, seed) - 0.5f) * 2f * 1.8f;
                float a = SS(0.6f, -0.6f, sd);
                float m = SS(0.42f, 0.62f, Fbm(fx / 70f, fy / 32f, seed + 5));
                Color col = Color.Lerp(c, pool, m * 0.55f);
                float dx = (fx + w * 0.2f) / (w * 0.38f), dy = (fy - h * 0.3f) / (h * 0.55f);
                float hl = SS(0f, 1f, Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy)));
                col = Color.Lerp(col, Color.white, 0.45f * hl);
                col = Color.Lerp(col, rim, SS(-6f, 0f, sd) * 0.35f);
                float g = (Hash(x, y, seed) - 0.5f) * 0.03f;
                col.r = Mathf.Clamp01(col.r + g); col.g = Mathf.Clamp01(col.g + g); col.b = Mathf.Clamp01(col.b + g);
                col.a = a;
                px[y * W + x] = col;
            }
        float b = sliced ? (M + Mathf.Min(r, h / 2f - 1f)) * S : 0;
        return Save(name, W, H, px, new Vector4(b, b, b, b));
    }

    public static Sprite Pencil(string name, int w, int h, float r, Color line, int seed)
    {
        r = Mathf.Min(r, h / 2f);
        int W = (w + 2 * M) * S, H = (h + 2 * M) * S;
        var px = new Color[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float fx = (x + 0.5f) / S - M - w / 2f, fy = (y + 0.5f) / S - M - h / 2f;
                float sd1 = Sd(fx, fy, w / 2f, h / 2f, r) + (Fbm(fx / 20f, fy / 20f, seed + 11) - 0.5f) * 2f * 1.4f;
                float a1 = SS(0.75f, 0.3f, Mathf.Abs(sd1)) * SS(0.34f, 0.42f, Fbm(fx / 45f, fy / 45f, seed + 21)) * 0.9f;
                float gx = fx - 0.8f, gy = fy + 1.4f;
                float sd2 = Sd(gx, gy, w / 2f, h / 2f, r) + (Fbm(gx / 20f, gy / 20f, seed + 31) - 0.5f) * 2f * 1.7f;
                float a2 = SS(0.6f, 0.25f, Mathf.Abs(sd2)) * SS(0.44f, 0.52f, Fbm(gx / 38f, gy / 38f, seed + 41)) * 0.45f;
                Color col = line; col.a = 1f - (1f - a1) * (1f - a2);
                px[y * W + x] = col;
            }
        return Save(name, W, H, px, Vector4.zero);
    }

    public static Sprite Dot(string name)
    {
        int W = 64;
        var px = new Color[W * W];
        for (int y = 0; y < W; y++)
            for (int x = 0; x < W; x++)
            {
                float fx = x + 0.5f - W / 2f, fy = y + 0.5f - W / 2f;
                float d = Mathf.Sqrt(fx * fx + fy * fy) + (Fbm(fx / 14f, fy / 14f, 7) - 0.5f) * 5f;
                px[y * W + x] = new Color(1, 1, 1, SS(27f, 22f, d));
            }
        return Save(name, W, W, px, Vector4.zero, 64);
    }

    /// A soft, slightly wobbly ring, white so it can be tinted. Used for sound ripples.
    public static Sprite Ring(string name)
    {
        int W = 192;
        var px = new Color[W * W];
        for (int y = 0; y < W; y++)
            for (int x = 0; x < W; x++)
            {
                float fx = x + 0.5f - W / 2f, fy = y + 0.5f - W / 2f;
                float d = Mathf.Sqrt(fx * fx + fy * fy) + (Fbm(fx / 30f, fy / 30f, 4) - 0.5f) * 6f;
                float a = SS(5.5f, 1.5f, Mathf.Abs(d - 84f)) * (0.55f + 0.45f * Fbm(fx / 18f, fy / 18f, 9));
                px[y * W + x] = new Color(1, 1, 1, a);
            }
        return Save(name, W, W, px, Vector4.zero, 192);
    }

    static Sprite Save(string name, int W, int H, Color[] px, Vector4 border, float ppu = 100f * S)
    {
        Directory.CreateDirectory(Dir);
        var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
        tex.SetPixels(px); tex.Apply();
        string path = Dir + "/" + name + ".png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var imp = (TextureImporter)AssetImporter.GetAtPath(path);
        imp.textureType = TextureImporterType.Sprite;
        imp.spriteImportMode = SpriteImportMode.Single;
        imp.spritePixelsPerUnit = ppu;
        imp.alphaIsTransparency = true;
        imp.mipmapEnabled = true;
        imp.filterMode = FilterMode.Trilinear;
        imp.wrapMode = TextureWrapMode.Clamp;
        imp.textureCompression = TextureImporterCompression.Uncompressed;
        imp.maxTextureSize = 2048;
        imp.spriteBorder = border;
        var st = new TextureImporterSettings();
        imp.ReadTextureSettings(st);
        st.spriteMeshType = SpriteMeshType.FullRect;
        imp.SetTextureSettings(st);
        imp.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }
}
