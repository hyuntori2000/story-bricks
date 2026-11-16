using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// The Story Bricks opening: a grid of dots, the braille word "story" blooming dot by dot,
/// the rest fading, then the name. Plays once on start, then fades away and shows the menu.
/// Runs first so the menu panel is hidden before it can show for a frame.
[DefaultExecutionOrder(-100)]
public class OpeningAnimation : MonoBehaviour
{
    public enum Look { Water, Ink }
    public Look look = Look.Ink;
    public Material baseMaterial;
    public TMP_FontAsset font;
    public GameObject showWhenDone;
    public float distance = 0.8f;
    public float spacing = 0.055f;
    public float dotSize = 0.027f;
    [Tooltip("How long the finished mark stays before everything fades. The whole opening runs about 9 seconds.")]
    public float markStay = 1.4f;
    [Range(0f, 1f)] public float backdropOpacity = 0.45f;

    const int Cols = 8, Rows = 5;
    // Contracted braille "story": st (3,4), o (1,3,5), r (1,2,3,5), y (1,3,4,5,6).
    static readonly int[][] Cells = { new[] { 3, 4 }, new[] { 1, 3, 5 }, new[] { 1, 2, 3, 5 }, new[] { 1, 3, 4, 5, 6 } };
    static readonly float[] Scale = { 196f, 246.94f, 293.66f, 329.63f, 392f, 293.66f, 329.63f, 392f, 493.88f, 392f, 329.63f, 293.66f, 246.94f, 392f };

    Color cBack, cDot, cInk, cRing, cText;
    Transform grid;
    Material backMat;
    readonly List<Transform> dotT = new List<Transform>();
    readonly List<Material> dotM = new List<Material>();
    readonly List<Material> fxMats = new List<Material>();
    TextMeshPro word;
    AudioSource audioSrc;
    AudioClip[] notes; AudioClip chime;
    Mesh quad;

    void Awake()
    {
        if (showWhenDone != null) showWhenDone.SetActive(false);
    }

    IEnumerator Start()
    {
        if (look == Look.Water)
        {
            cBack = Hex(0xE6, 0xEF, 0xEF); cDot = Hex(0xB7, 0xCB, 0xCD); cInk = Hex(0x1D, 0x54, 0x66); cRing = Hex(0x2F, 0x71, 0x86); cText = Hex(0x17, 0x3F, 0x4D);
        }
        else
        {
            cBack = Hex(0xF4, 0xF4, 0xF4); cDot = Hex(0xC4, 0xC4, 0xC4); cInk = Hex(0x0B, 0x0B, 0x0B); cRing = Hex(0x0B, 0x0B, 0x0B); cText = Hex(0x11, 0x11, 0x11);
        }
        Build();
        BuildAudio();
        yield return new WaitForSeconds(0.4f);
        Place();
        yield return Play();
    }

    static Color Hex(int r, int g, int b) { return new Color(r / 255f, g / 255f, b / 255f, 1f); }

    void Place()
    {
        var cam = Camera.main;
        if (cam == null) return;
        var head = cam.transform;
        var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
        transform.position = head.position + fwd * distance + Vector3.down * 0.03f;
        transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
    }

    // ---------- Building ----------
    Material NewMat(Texture2D tex, Color c, float alpha, int queue)
    {
        var m = new Material(baseMaterial);
        m.mainTexture = tex;
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
        m.renderQueue = queue;
        SetCol(m, c, alpha);
        return m;
    }
    static void SetCol(Material m, Color c, float a)
    {
        c.a = a;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
    }
    static float Alpha(Material m) { return m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").a : m.color.a; }
    static Color Col(Material m) { return m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.color; }

    Transform NewQuad(string name, Transform parent, Vector3 pos, Vector2 size, Material m, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        go.AddComponent<MeshFilter>().sharedMesh = quad;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = m;
        r.sortingOrder = order;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return go.transform;
    }

    void Build()
    {
        quad = new Mesh();
        quad.vertices = new[] { new Vector3(-.5f, -.5f, 0), new Vector3(.5f, -.5f, 0), new Vector3(-.5f, .5f, 0), new Vector3(.5f, .5f, 0) };
        quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
        quad.RecalculateBounds();

        float w = (Cols - 1) * spacing, h = (Rows - 1) * spacing;
        var backSize = new Vector2(w + spacing * 2.6f, h + spacing * 3.6f);
        backMat = NewMat(RoundedRect(256, Mathf.RoundToInt(256 * backSize.y / backSize.x), 0.09f), cBack, 0f, 3000);
        NewQuad("Backdrop", transform, new Vector3(0, -spacing * 0.5f, 0.006f), backSize, backMat, 0);

        grid = new GameObject("Grid").transform;
        grid.SetParent(transform, false);
        var dotTex = Squircle(96);
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                var m = NewMat(dotTex, cDot, 0f, 3002);
                var pos = new Vector3(-w / 2 + c * spacing, h / 2 - r * spacing, 0f);
                dotT.Add(NewQuad("Dot " + r + "-" + c, grid, pos, new Vector2(dotSize, dotSize), m, 2));
                dotM.Add(m);
            }

        var go = new GameObject("Wordmark");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0, -h / 2 - spacing * 0.75f, 0f);
        word = go.AddComponent<TextMeshPro>();
        if (font != null) word.font = font;
        word.text = "Story Bricks";
        word.fontSize = 0.42f;
        word.characterSpacing = 4f;
        word.alignment = TextAlignmentOptions.Center;
        word.enableWordWrapping = false;
        word.rectTransform.sizeDelta = new Vector2(0.6f, 0.08f);
        word.color = new Color(cText.r, cText.g, cText.b, 0f);
        var wr = go.GetComponent<MeshRenderer>();
        if (wr != null) wr.sortingOrder = 5;
    }

    // ---------- Textures ----------
    static Texture2D NewTex(int w, int h) { var t = new Texture2D(w, h, TextureFormat.RGBA32, true); t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Trilinear; return t; }
    static Texture2D Squircle(int n)
    {
        var t = NewTex(n, n); var px = new Color32[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float u = Mathf.Abs((x + .5f) / n * 2 - 1), v = Mathf.Abs((y + .5f) / n * 2 - 1);
            float d = Mathf.Pow(Mathf.Pow(u, 2.4f) + Mathf.Pow(v, 2.4f), 1f / 2.4f);
            float a = Mathf.Clamp01((0.94f - d) * n * 0.22f);
            px[y * n + x] = new Color(1, 1, 1, a);
        }
        t.SetPixels32(px); t.Apply(true); return t;
    }
    static Texture2D Ring(int n, float thickness)
    {
        var t = NewTex(n, n); var px = new Color32[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float u = (x + .5f) / n * 2 - 1, v = (y + .5f) / n * 2 - 1;
            float d = Mathf.Sqrt(u * u + v * v);
            float a = Mathf.Clamp01(1f - Mathf.Abs(d - 0.9f) / thickness);
            px[y * n + x] = new Color(1, 1, 1, a * a);
        }
        t.SetPixels32(px); t.Apply(true); return t;
    }
    static Texture2D Blob(int n)
    {
        var t = NewTex(n, n); var px = new Color32[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float u = (x + .5f) / n * 2 - 1, v = (y + .5f) / n * 2 - 1;
            float d = Mathf.Sqrt(u * u + v * v);
            float a = Mathf.Clamp01(1f - d); a = a * a * (3 - 2 * a);
            px[y * n + x] = new Color(1, 1, 1, a);
        }
        t.SetPixels32(px); t.Apply(true); return t;
    }
    static Texture2D RoundedRect(int w, int h, float radiusFrac)
    {
        var t = NewTex(w, h); var px = new Color32[w * h]; float rad = radiusFrac * w;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float dx = Mathf.Max(Mathf.Max(rad - x - .5f, x + .5f - (w - rad)), 0f);
            float dy = Mathf.Max(Mathf.Max(rad - y - .5f, y + .5f - (h - rad)), 0f);
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            px[y * w + x] = new Color(1, 1, 1, Mathf.Clamp01(rad - d));
        }
        t.SetPixels32(px); t.Apply(true); return t;
    }

    // ---------- Sound ----------
    void BuildAudio()
    {
        audioSrc = gameObject.AddComponent<AudioSource>();
        audioSrc.playOnAwake = false; audioSrc.spatialBlend = 0f; audioSrc.volume = 0.9f;
        const int sr = 44100;
        notes = new AudioClip[Scale.Length];
        for (int i = 0; i < Scale.Length; i++)
        {
            var buf = new float[(int)(sr * 3.4f)];
            AddTone(buf, sr, 0f, Scale[i], 0.20f, 0.06f, 2.4f);
            AddTone(buf, sr, 0f, Scale[i] / 2f, 0.09f, 0.09f, 2.8f);
            if (look == Look.Water) AddDrop(buf, sr, Scale[i]);
            Echo(buf, sr);
            notes[i] = AudioClip.Create("note" + i, buf.Length, 1, sr, false); notes[i].SetData(buf, 0);
        }
        var cb = new float[(int)(sr * 6.5f)];
        float[] ch = { 196f, 293.66f, 392f, 493.88f };
        for (int i = 0; i < ch.Length; i++) AddTone(cb, sr, i * 0.28f, ch[i], 0.15f, 0.25f, 4.5f);
        Echo(cb, sr);
        chime = AudioClip.Create("chime", cb.Length, 1, sr, false); chime.SetData(cb, 0);
    }
    static void AddTone(float[] buf, int sr, float start, float freq, float peak, float attack, float tail)
    {
        int s0 = (int)(start * sr); float k = Mathf.Log(peak / 0.0001f) / tail;
        for (int i = s0; i < buf.Length; i++)
        {
            float t = (i - s0) / (float)sr;
            float env = t < attack ? peak * t / attack : peak * Mathf.Exp(-(t - attack) * k);
            if (t > attack && env < 0.00005f) break;
            buf[i] += env * Mathf.Sin(2 * Mathf.PI * freq * t);
        }
    }
    static void AddDrop(float[] buf, int sr, float freq)
    {
        float phase = 0f; int n = (int)(0.25f * sr);
        for (int i = 0; i < n && i < buf.Length; i++)
        {
            float t = i / (float)sr;
            float f = Mathf.Lerp(freq * 4.2f, freq * 2.6f, Mathf.Clamp01(t / 0.07f));
            phase += 2 * Mathf.PI * f / sr;
            float env = t < 0.008f ? t / 0.008f : Mathf.Exp(-(t - 0.008f) * 28f);
            buf[i] += 0.06f * env * Mathf.Sin(phase);
        }
    }
    static void Echo(float[] buf, int sr)
    {
        int d = (int)(0.34f * sr); var wet = new float[buf.Length];
        for (int i = d; i < buf.Length; i++) wet[i] = (buf[i - d] + wet[i - d]) * 0.38f;
        for (int i = 0; i < buf.Length; i++) buf[i] += wet[i] * 0.9f;
    }

    // ---------- Timeline ----------
    IEnumerator Tween(float dur, Action<float> step)
    {
        float t = 0f;
        while (t < dur) { t += Time.deltaTime; float k = Mathf.Clamp01(t / dur); step(k * k * (3 - 2 * k)); yield return null; }
        step(1f);
    }
    IEnumerator After(float delay, IEnumerator next) { yield return new WaitForSeconds(delay); yield return next; }

    IEnumerator Play()
    {
        var order = new List<int>(); var isInk = new HashSet<int>();
        for (int ci = 0; ci < Cells.Length; ci++)
            foreach (int d in Cells[ci])
            {
                int col = ci * 2 + (d > 3 ? 1 : 0), row = 1 + (d - 1) % 3;
                order.Add(row * Cols + col); isInk.Add(row * Cols + col);
            }

        // 1. Backdrop, then the grid column by column. (1.2 s)
        StartCoroutine(Tween(0.8f, k => SetCol(backMat, cBack, backdropOpacity * k)));
        for (int i = 0; i < dotM.Count; i++)
        {
            var m = dotM[i];
            StartCoroutine(After(0.1f + (i % Cols) * 0.06f + (i / Cols) * 0.03f, Tween(0.55f, k => SetCol(m, cDot, k))));
        }
        yield return new WaitForSeconds(1.2f);

        // 2. The braille blooms in reading order. (3.4 s)
        var ringTex = look == Look.Water ? Ring(256, 0.035f) : Blob(128);
        for (int n = 0; n < order.Count; n++)
        {
            int i = order[n];
            Bloom(dotT[i], dotM[i], ringTex);
            if (notes != null) audioSrc.PlayOneShot(notes[n % notes.Length], 0.7f);
            yield return new WaitForSeconds(0.24f);
        }
        yield return new WaitForSeconds(0.3f);

        // 3. The rest fades while the word settles and the name appears. (1.3 s, then the hold)
        for (int i = 0; i < dotM.Count; i++)
        {
            if (isInk.Contains(i)) continue;
            var m = dotM[i];
            StartCoroutine(Tween(1.0f, k => SetCol(m, cDot, 1f - k)));
        }
        var from = grid.localPosition; var to = from + Vector3.down * spacing * 0.3f;
        StartCoroutine(Tween(1.2f, k => grid.localPosition = Vector3.Lerp(from, to, k)));
        audioSrc.PlayOneShot(chime, 0.9f);
        yield return Tween(1.3f, k => word.color = new Color(cText.r, cText.g, cText.b, k));
        yield return new WaitForSeconds(markStay);

        // 4. The backdrop and the mark fade together, then the menu arrives. (1.1 s)
        yield return Tween(1.1f, k =>
        {
            SetCol(backMat, cBack, backdropOpacity * (1f - k));
            foreach (int i in order) SetCol(dotM[i], cInk, 1f - k);
            word.color = new Color(cText.r, cText.g, cText.b, 1f - k);
        });
        if (showWhenDone != null) showWhenDone.SetActive(true);
        gameObject.SetActive(false);
    }

    void Bloom(Transform dot, Material m, Texture2D fxTex)
    {
        StartCoroutine(Tween(look == Look.Water ? 1.4f : 1.1f, k => SetCol(m, Color.Lerp(cDot, cInk, k), 1f)));
        var baseScale = dot.localScale;
        StartCoroutine(Tween(1.2f, k =>
        {
            float s = k < 0.18f ? Mathf.Lerp(1f, 0.8f, k / 0.18f) : k < 0.55f ? Mathf.Lerp(0.8f, 1.14f, (k - 0.18f) / 0.37f) : Mathf.Lerp(1.14f, 1.04f, (k - 0.55f) / 0.45f);
            dot.localScale = baseScale * s;
        }));
        int rings = look == Look.Water ? 3 : 1;
        for (int r = 0; r < rings; r++)
        {
            var fm = NewMat(fxTex, cRing, 0f, 3001);
            fxMats.Add(fm);
            var t = NewQuad("Ripple", grid, dot.localPosition + new Vector3(0, 0, 0.002f), new Vector2(dotSize, dotSize), fm, 1);
            float peak = look == Look.Water ? 0.6f : 0.42f, grow = look == Look.Water ? 5.6f : 3.4f, dur = look == Look.Water ? 3.4f : 1.9f;
            StartCoroutine(After(r * 0.38f, Ripple(t, fm, peak, grow, dur)));
        }
    }
    IEnumerator Ripple(Transform t, Material m, float peak, float grow, float dur)
    {
        float e = 0f;
        while (e < dur)
        {
            e += Time.deltaTime; float k = Mathf.Clamp01(e / dur); float ease = 1f - Mathf.Pow(1f - k, 2.2f);
            float s = Mathf.Lerp(1.1f, grow, ease) * dotSize;
            t.localScale = new Vector3(s, s, 1f);
            float a = k < 0.1f ? peak * k / 0.1f : peak * (1f - (k - 0.1f) / 0.9f);
            SetCol(m, cRing, a);
            yield return null;
        }
        Destroy(t.gameObject);
    }
}
