using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// The picture above the bricks, built from painted cut-outs.
/// While Judy builds, each word adds a plain outline and each answer turns it into her version.
/// During playback the same cut-outs act out the story in step with the subtitles.
public class StoryStage : MonoBehaviour
{
    public enum Step { Hill, Judy, MoonOutline, MoonDough, Hum, WindOutline, WindColour, KiteOutline, KiteColour, Bells }

    public CanvasGroup group;
    public Image hill, judyStand, judyHold, moonOutline, moonDough, windOutline, windColour, kiteOutline, kiteColour, tail, stringLine, rippleDot;
    public RectTransform moon, wind, kite;
    public Image[] rings;
    public Vector2 handPoint, kiteBuild, kiteRest, kiteHigh, kiteAtMoon;
    public float bloom = 0.4f, fill = 0.6f;

    // Keep the authored local motion still while the narrator/XR session is paused.
    public bool Paused { get; set; }
    float MotionDelta => Paused ? 0f : Time.unscaledDeltaTime;
    float motionTime;

    bool hum, ringsAtHand;
    float bellAmp, gust = 1f, kiteScale = 1f, judyBob;
    Vector2 kitePos, windBase, judyStandBase, judyHoldBase, moonBase;
    bool ready;

    void Awake() { Init(); }

    void Init()
    {
        if (ready) return;
        windBase = wind.anchoredPosition;
        judyStandBase = judyStand.rectTransform.anchoredPosition;
        judyHoldBase = judyHold.rectTransform.anchoredPosition;
        moonBase = moon.anchoredPosition;
        ready = true;
    }

    // ---------- Building: the picture grows ----------

    public void ResetStage()
    {
        Init();
        StopAllCoroutines();
        foreach (var im in new[] { hill, judyStand, judyHold, moonOutline, moonDough, windOutline, windColour, kiteOutline, kiteColour, tail, stringLine, rippleDot }) A(im, 0f);
        foreach (var r in rings) A(r, 0f);
        moonDough.fillAmount = windColour.fillAmount = kiteColour.fillAmount = 1f;
        hum = false; ringsAtHand = false; bellAmp = 0f; gust = 1f; kiteScale = 1f; judyBob = 0f;
        kitePos = kiteBuild;
        moon.localScale = Vector3.one;
        group.alpha = 0f;
        group.gameObject.SetActive(false);
    }

    public void Show()
    {
        if (group.gameObject.activeSelf && group.alpha > 0.99f) return;
        group.gameObject.SetActive(true);
        StartCoroutine(FadeGroup(1f, bloom));
    }

    public void Hide() { if (group.gameObject.activeSelf) StartCoroutine(FadeGroup(0f, 0.3f)); }

    public void Reveal(Step s, float delay = 0f)
    {
        Show();
        StartCoroutine(RevealCo(s, delay));
    }

    IEnumerator RevealCo(Step s, float delay)
    {
        if (delay > 0f) yield return WaitMotion(delay);
        switch (s)
        {
            case Step.Hill: StartCoroutine(Fade(hill, 1f, bloom)); break;
            case Step.Judy: StartCoroutine(BloomIn(judyStand)); break;
            case Step.MoonOutline: StartCoroutine(BloomIn(moonOutline)); break;
            case Step.MoonDough: StartCoroutine(WashIn(moonDough, moonOutline)); break;
            case Step.Hum: hum = true; break;
            case Step.WindOutline: StartCoroutine(BloomIn(windOutline)); break;
            case Step.WindColour: StartCoroutine(WashIn(windColour, windOutline)); break;
            case Step.KiteOutline:
                StartCoroutine(BloomIn(kiteOutline));
                StartCoroutine(Fade(judyStand, 0f, 0.3f));
                StartCoroutine(Fade(judyHold, 1f, 0.3f));
                StartCoroutine(Fade(stringLine, 0.9f, 0.6f));
                break;
            case Step.KiteColour: StartCoroutine(WashIn(kiteColour, kiteOutline)); StartCoroutine(Tug()); break;
            case Step.Bells: StartCoroutine(BloomIn(tail)); StartCoroutine(Jingle(12f, 4f, 2.5f)); break;
        }
    }

    // ---------- Playback: the cut-outs act out the story ----------

    /// Starts the story. `clock` returns seconds into the narration, so every move follows the voice.
    public void BeginStory(System.Func<float> clock)
    {
        Init();
        StopAllCoroutines();
        group.gameObject.SetActive(true);
        StartCoroutine(FadeGroup(1f, bloom));
        hum = false; ringsAtHand = false; bellAmp = 0f; gust = 1f; kiteScale = 1f; judyBob = 0f;
        kitePos = kiteRest;
        moon.localScale = Vector3.one;
        foreach (var im in new[] { judyHold, moonOutline, moonDough, windOutline, windColour, kiteOutline, kiteColour, tail, stringLine, rippleDot })
            StartCoroutine(Fade(im, 0f, 0.3f));
        moonDough.fillAmount = windColour.fillAmount = kiteColour.fillAmount = 1f;
        StartCoroutine(Fade(hill, 1f, bloom));
        StartCoroutine(Fade(judyStand, 1f, bloom));
        StartCoroutine(StoryCo(clock));
    }

    /// Cue times are seconds into the narration recording (narration_judy_moon).
    IEnumerator StoryCo(System.Func<float> clock)
    {
        yield return Until(clock, 5.6f);    // "...so she imagined one"
        StartCoroutine(BloomIn(moonDough));
        yield return Until(clock, 12.0f);   // "it hummed"
        hum = true;
        yield return Until(clock, 17.0f);   // "So she got her kite"
        StartCoroutine(Fade(judyStand, 0f, 0.3f)); StartCoroutine(Fade(judyHold, 1f, 0.3f));
        StartCoroutine(BloomIn(kiteColour)); StartCoroutine(BloomIn(tail));
        StartCoroutine(Fade(stringLine, 0.9f, 0.6f));
        yield return Until(clock, 18.8f);   // "Whoosh!"
        StartCoroutine(BloomIn(windColour)); gust = 2.4f;
        yield return Until(clock, 22.95f);  // "Up went the kite"
        StartCoroutine(MoveKite(kiteHigh, 0.85f, 3.4f));
        yield return Until(clock, 24.4f);   // "The string pulled"
        StartCoroutine(Tug());
        yield return Until(clock, 27.1f);   // "Jingle, jingle"
        StartCoroutine(Jingle(15f, 9f, 2.6f));
        yield return Until(clock, 29.9f);   // "Higher, and higher... until... bump" (bump lands at 35.8)
        gust = 1.2f;
        StartCoroutine(ClimbToMoon(5.85f));
        yield return Until(clock, 36.6f);   // "The bells stopped"
        bellAmp = 0f; gust = 0.5f;
        yield return Until(clock, 38.1f);   // "Then the string began to hum"
        StartCoroutine(Ripples(1));
        yield return Until(clock, 40.7f);   // "Judy held on tight"
        StartCoroutine(Bob(2.5f, 0.5f));
        yield return Until(clock, 42.4f);   // "Through the string she could feel it"
        StartCoroutine(Ripples(1));
        yield return Until(clock, 44.8f);   // "Soft."
        ringsAtHand = true; StartCoroutine(Bob(2f, 0.4f));
        yield return Until(clock, 45.9f);   // "Cold."
        StartCoroutine(Bob(2f, 0.4f));
        yield return Until(clock, 47.0f);   // "Humming."
        StartCoroutine(Bob(2.5f, 0.5f));
        yield return Until(clock, 51.5f);   // "one little tug back"
        StartCoroutine(Tug());
        yield return Until(clock, 53.1f);   // "Judy wound the string in, slowly"
        ringsAtHand = false; gust = 1f;
        StartCoroutine(MoveKite(kiteRest, 1f, 5.2f));
        yield return Until(clock, 56.1f);   // "Jingle, jingle, all the way home"
        StartCoroutine(Jingle(13f, 3f, 3f));
    }

    IEnumerator WaitMotion(float seconds)
    {
        for (float elapsed = 0; elapsed < seconds; elapsed += MotionDelta) yield return null;
    }

    static IEnumerator Until(System.Func<float> clock, float t) { while (clock() < t) yield return null; }

    IEnumerator ClimbToMoon(float dur)
    {
        StartCoroutine(Jingle(9f, 2f, dur));
        yield return MoveKite(kiteAtMoon, 0.5f, dur);
        bellAmp = 0f;
        for (float e = 0; e < 0.5f; e += MotionDelta)             // bump: the dough dents and springs back
        {
            float k = Mathf.Sin(e / 0.5f * Mathf.PI);
            moon.localScale = new Vector3(1f + 0.07f * k, 1f - 0.09f * k, 1f);
            yield return null;
        }
        moon.localScale = Vector3.one;
    }

    IEnumerator Ripples(int count)
    {
        for (int n = 0; n < count; n++)
        {
            for (float e = 0; e < 1.6f; e += MotionDelta)
            {
                float u = Mathf.SmoothStep(0f, 1f, e / 1.6f);
                rippleDot.rectTransform.anchoredPosition = Vector2.Lerp(kite.anchoredPosition, Hand(), u);
                A(rippleDot, Mathf.Sin(u * Mathf.PI) * 0.85f);
                yield return null;
            }
            A(rippleDot, 0f);
            StartCoroutine(Bob(2.5f, 0.35f));
            yield return WaitMotion(0.25f);
        }
    }

    IEnumerator Tug()
    {
        Vector2 p0 = kitePos;
        for (float e = 0; e < 0.7f; e += MotionDelta)
        {
            float k = Mathf.Sin(e / 0.7f * Mathf.PI);
            kitePos = p0 + new Vector2(2f, 5f) * k;
            judyBob = 3f * k;
            yield return null;
        }
        kitePos = p0; judyBob = 0f;
    }

    IEnumerator Bob(float amount, float dur)
    {
        for (float e = 0; e < dur; e += MotionDelta) { judyBob = amount * Mathf.Sin(e / dur * Mathf.PI); yield return null; }
        judyBob = 0f;
    }

    IEnumerator Jingle(float from, float to, float dur)
    {
        for (float e = 0; e < dur; e += MotionDelta) { bellAmp = Mathf.Lerp(from, to, e / dur); yield return null; }
        bellAmp = to;
    }

    IEnumerator MoveKite(Vector2 to, float scaleTo, float dur)
    {
        Vector2 p0 = kitePos; float s0 = kiteScale;
        for (float e = 0; e < dur; e += MotionDelta)
        {
            float k = Mathf.SmoothStep(0f, 1f, e / dur);
            kitePos = Vector2.Lerp(p0, to, k); kiteScale = Mathf.Lerp(s0, scaleTo, k);
            yield return null;
        }
        kitePos = to; kiteScale = scaleTo;
    }

    // ---------- Motion ----------

    Vector2 Hand() { return handPoint + new Vector2(0f, judyBob); }

    void Update()
    {
        if (Paused || !ready || !group.gameObject.activeSelf) return;
        motionTime += Time.unscaledDeltaTime;
        float t = motionTime;
        wind.anchoredPosition = windBase + new Vector2(Mathf.Sin(t * 0.9f) * 6f * gust, Mathf.Sin(t * 1.3f) * 2f);
        judyStand.rectTransform.anchoredPosition = judyStandBase + new Vector2(0f, judyBob);
        judyHold.rectTransform.anchoredPosition = judyHoldBase + new Vector2(0f, judyBob);

        kite.anchoredPosition = kitePos + new Vector2(Mathf.Sin(t * 1.7f) * 2f, Mathf.Sin(t * 2.3f) * 2.5f) * kiteScale;
        kite.localScale = Vector3.one * kiteScale;
        kite.localRotation = Quaternion.Euler(0f, 0f, -12f + Mathf.Sin(t * 1.1f) * 4f);
        tail.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 12f + Mathf.Sin(t * 7f) * bellAmp + Mathf.Sin(t * 1.5f) * 4f);

        Vector2 a = Hand(), b = kite.anchoredPosition, d = b - a;
        var sr = stringLine.rectTransform;
        sr.anchoredPosition = a;
        sr.sizeDelta = new Vector2(d.magnitude, 1.3f);
        sr.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);

        Vector2 c = ringsAtHand ? Hand() : moonBase;
        float baseSize = ringsAtHand ? 26f : 84f;
        for (int k = 0; k < rings.Length; k++)
        {
            float ph = (t * 0.45f + k / (float)rings.Length) % 1f;
            var rr = rings[k].rectTransform;
            rr.anchoredPosition = c;
            rr.sizeDelta = Vector2.one * baseSize * Mathf.Lerp(0.95f, 1.75f, ph);
            float target = hum ? (1f - ph) * 0.55f : 0f;
            A(rings[k], Mathf.MoveTowards(rings[k].color.a, target, Time.unscaledDeltaTime * 2f));
        }
    }

    // ---------- Helpers ----------

    static void A(Graphic g, float a) { var c = g.color; c.a = a; g.color = c; }

    IEnumerator Fade(Image im, float to, float dur)
    {
        float a0 = im.color.a;
        for (float e = 0; e < dur; e += MotionDelta) { A(im, Mathf.Lerp(a0, to, e / dur)); yield return null; }
        A(im, to);
    }

    IEnumerator FadeGroup(float to, float dur)
    {
        float a0 = group.alpha;
        for (float e = 0; e < dur; e += MotionDelta) { group.alpha = Mathf.Lerp(a0, to, e / dur); yield return null; }
        group.alpha = to;
        if (to <= 0f) group.gameObject.SetActive(false);
    }

    IEnumerator BloomIn(Image im)
    {
        var rt = im.rectTransform;
        im.fillAmount = 1f;
        for (float e = 0; e < bloom; e += MotionDelta)
        {
            float k = 1f - Mathf.Pow(1f - e / bloom, 3f);
            A(im, k); rt.localScale = Vector3.one * Mathf.Lerp(0.9f, 1f, k);
            yield return null;
        }
        A(im, 1f); rt.localScale = Vector3.one;
    }

    /// Wash fill: Judy's version sweeps in over the plain outline.
    IEnumerator WashIn(Image colour, Image outline)
    {
        colour.fillAmount = 0f; A(colour, 1f);
        float a0 = outline.color.a;
        for (float e = 0; e < fill; e += MotionDelta)
        {
            float k = Mathf.SmoothStep(0f, 1f, e / fill);
            colour.fillAmount = k;
            A(outline, a0 * (1f - Mathf.Clamp01((k - 0.4f) / 0.6f)));
            yield return null;
        }
        colour.fillAmount = 1f; A(outline, 0f);
    }
}
