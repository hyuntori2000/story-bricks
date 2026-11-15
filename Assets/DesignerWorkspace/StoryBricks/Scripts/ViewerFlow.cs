using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// Passive watercolour presentation. Only the experience router advances story state.
/// Visual transitions never start capture, invent words, finish a session or play a story.
public class ViewerFlow : MonoBehaviour
{
    [System.Serializable]
    public class Tag
    {
        public RectTransform root;
        public CanvasGroup group;
        public Image wash;
        public TMP_Text label;
        public RectTransform braille;
        [System.NonSerialized] public bool revealing;
        [System.NonSerialized] public bool inBraille;
    }

    public enum State { Menu, Hint, Building, Ready, Playing, End }

    [Header("Blocks")]
    public CanvasGroup menu;
    public CanvasGroup hint, caption, subtitle, ready, end, pulse;
    public CanvasGroup[] endButtons;
    public Image readyWash;
    public RectTransform captionBox, subtitleBox;
    public TMP_Text captionText, subtitleText, endWords;
    public RectTransform[] pulseDots;
    public Image[] pulseImages;
    public Tag[] tags;
    public Sprite dotSprite;
    public Button newStoryButton, savedStoryButton, watchAgainButton, doneButton;

    [Header("Look")]
    public Color ink = new Color(0.357f, 0.306f, 0.271f);
    [Range(0f, 1f)] public float tagNormal = 0.9f;
    [Range(0f, 1f)] public float tagDim = 0.45f;

    [Header("Timing (seconds)")]
    public float bloom = 0.4f;
    public float dry = 0.3f;
    public float brailleLetter = 0.12f;
    public float brailleHold = 0.6f;
    public float brailleCrossfade = 0.4f;

    // Read once by the importer to seed the designer-owned Judy asset; cleared in the runtime prefab.
    [HideInInspector] public string[] narration;
    [HideInInspector] public int[] narrationWord;

    public State state;
    readonly Dictionary<CanvasGroup, Coroutine> running = new Dictionary<CanvasGroup, Coroutine>();
    readonly List<string> words = new List<string>();
    public event Action JudyRequested, CreateWorldRequested;
    public string WorkspaceId { get; private set; } = "";
    public bool IsMenuVisible => state == State.Menu;
    bool busy;
    bool wantListening, pressing;

    static readonly string[] Braille =
    {
        "1", "12", "14", "145", "15", "124", "1245", "125", "24", "245", "13", "123", "134",
        "1345", "135", "1234", "12345", "1235", "234", "2345", "136", "1236", "2456", "1346", "13456", "1356"
    };

    void Update() { AnimatePulse(); }

    // No OnEnable reset: showing a view never changes the active session.
    public void ShowMenu()
    {
        ResetAll();
        WorkspaceId = "";
        state = State.Menu;
        SetButtonCopy(newStoryButton, "Create World", "Build your own story");
        SetButtonCopy(savedStoryButton, "Judy", "Bring the prepared story to life");
        Bloom(menu);
    }

    public void OnNewStory() { if (state == State.Menu && !pressing) StartCoroutine(Choose(newStoryButton, CreateWorldRequested)); }
    public void OnSavedStory() { if (state == State.Menu && !pressing) StartCoroutine(Choose(savedStoryButton, JudyRequested)); }
    IEnumerator Choose(Button button, Action selected)
    {
        pressing = true;
        button?.GetComponent<WashButton>()?.Flash();
        yield return Wait(.18f);
        pressing = false;
        selected?.Invoke();
    }

    public void BeginRoute(string workspaceId)
    {
        ResetAll();
        WorkspaceId = workspaceId == "judy" ? "judy" : "demo";
        state = State.Building;
    }

    public void SetListening(bool on) { wantListening = on; RefreshPulse(); }
    public void SetBusy(bool on) { busy = on; RefreshPulse(); }
    public void SetBusy(bool on, string message) { SetBusy(on); if (!string.IsNullOrWhiteSpace(message)) ShowCaption(message); }
    public void ShowWord(string word) { ShowWord(word, true); }
    public void ShowWord(string word, bool brailleFirst)
    {
        if (string.IsNullOrWhiteSpace(word) || tags == null || tags.Length == 0) return;
        int index = words.Count;
        if (index >= tags.Length) AddTag();
        words.Add(word);
        // Keep a readable window, while retaining every accepted word for display state.
        for (int i = 0; i < index - 5; i++) tags[i].root.gameObject.SetActive(false);
        StartCoroutine(WordCo(tags[index], word.ToUpperInvariant(), brailleFirst));
    }

    public void ShowCaption(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { Dry(caption); return; }
        Fit(captionBox, captionText, text);
        Bloom(caption);
    }

    public void ShowStoryReady(string storyText = "")
    {
        state = State.Ready;
        SetListening(false); SetBusy(false);
        if (!string.IsNullOrWhiteSpace(storyText)) ShowCaption(storyText);
    }

    public void ShowNextStage(string message = "Animation will be available here.")
    {
        state = State.Ready;
        SetListening(false); SetBusy(false);
        Dry(caption); Dry(subtitle);
        var label = ready.GetComponentInChildren<TMP_Text>(true);
        if (label != null) label.text = message;
        readyWash.fillAmount = 1f;
        Bloom(ready);
        // Intentionally no video, timer, end screen, or session reset.
    }

    static void SetButtonCopy(Button button, string title, string sub)
    {
        if (button == null) return;
        foreach (var text in button.GetComponentsInChildren<TMP_Text>(true))
            if (text.name == "Title") text.text = title;
            else if (text.name == "Sub") text.text = sub;
    }

    void AddTag()
    {
        var original = tags[0];
        var copy = Instantiate(original.root, original.root.parent, false);
        copy.name = "Word " + (tags.Length + 1);
        var tag = new Tag { root = copy, group = copy.GetComponent<CanvasGroup>(),
            wash = Mapped(original.root, copy, original.wash),
            label = Mapped(original.root, copy, original.label),
            braille = Mapped(original.root, copy, original.braille) };
        for (int i = tag.braille.childCount - 1; i >= 0; i--) Destroy(tag.braille.GetChild(i).gameObject);
        tag.group.alpha = 0;
        copy.gameObject.SetActive(false);
        int index = tags.Length;
        var button = copy.GetComponent<Button>();
        if (button != null)
        {
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => ToggleTag(index));
        }
        Array.Resize(ref tags, index + 1);
        tags[index] = tag;
    }

    static T Mapped<T>(Transform original, Transform copy, T component) where T : Component
    {
        var path = new List<int>();
        for (var node = component.transform; node != original; node = node.parent) path.Add(node.GetSiblingIndex());
        for (int i = path.Count - 1; i >= 0; i--) copy = copy.GetChild(path[i]);
        return copy.GetComponent<T>();
    }

    IEnumerator WordCo(Tag t, string word, bool brailleFirst)
    {
        t.label.text = word;
        float available = Mathf.Max(240f, ((RectTransform)t.root.parent).rect.width);
        float w = Mathf.Min(available, Mathf.Max(66f, t.label.GetPreferredValues(word).x + 28f));
        t.label.enableAutoSizing = true;
        t.label.fontSizeMin = 12;
        t.label.fontSizeMax = Mathf.Max(12, t.label.fontSize);
        t.root.sizeDelta = new Vector2(w, t.root.sizeDelta.y);
        FitWordWindow(available);
        SetAlpha(t.wash, tagNormal);
        t.label.alpha = brailleFirst ? 0f : 1f;
        Bloom(t.group);
        if (!brailleFirst) yield break;
        t.revealing = true;

        var perLetter = BuildDots(t, word);
        var dots = new List<Image>();
        foreach (var l in perLetter) dots.AddRange(l);
        yield return Wait(bloom * 0.5f);
        foreach (var list in perLetter)
        {
            foreach (var im in list) StartCoroutine(DotCo(im));
            yield return Wait(brailleLetter);
        }
        yield return Wait(0.25f + brailleHold);
        for (float e = 0; e < brailleCrossfade; e += Time.unscaledDeltaTime)
        {
            float k = e / brailleCrossfade;
            foreach (var im in dots) if (im != null) SetAlpha(im, 0.85f * (1f - k));
            t.label.alpha = k;
            yield return null;
        }
        t.label.alpha = 1f;
        foreach (var im in dots) if (im != null) Destroy(im.gameObject);
        t.revealing = false;
    }

    void FitWordWindow(float available)
    {
        var row = tags[0].root.parent.GetComponent<HorizontalLayoutGroup>();
        float spacing = row != null ? row.spacing : 12f, used = 0;
        int first = words.Count - 1;
        for (int i = words.Count - 1; i >= 0 && i >= words.Count - 6; i--)
        {
            float next = tags[i].root.sizeDelta.x + (i == words.Count - 1 ? 0 : spacing);
            if (i < words.Count - 1 && used + next > available) break;
            used += next; first = i;
        }
        for (int i = 0; i < words.Count; i++) tags[i].root.gameObject.SetActive(i >= first);
    }

    /// Creates the braille dots for a word inside a tag, invisible, grouped per letter.
    List<List<Image>> BuildDots(Tag t, string word)
    {
        var perLetter = new List<List<Image>>();
        int n = word.Length;
        float total = n * 15f - 7f;
        for (int c = 0; c < n; c++)
        {
            var list = new List<Image>();
            int idx = word[c] - 'A';
            if (idx >= 0 && idx < 26)
                foreach (char d in Braille[idx])
                {
                    int k = d - '0';
                    var go = new GameObject("dot", typeof(RectTransform), typeof(Image));
                    go.layer = gameObject.layer;
                    var rt = go.GetComponent<RectTransform>();
                    rt.SetParent(t.braille, false);
                    rt.sizeDelta = new Vector2(3.6f, 3.6f);
                    rt.anchoredPosition = new Vector2(c * 15f + (k <= 3 ? 0f : 5f) + 1.5f - total / 2f, 5f - ((k - 1) % 3) * 5f);
                    var im = go.GetComponent<Image>();
                    im.sprite = dotSprite; im.raycastTarget = false;
                    var col = ink; col.a = 0f; im.color = col;
                    list.Add(im);
                }
            perLetter.Add(list);
        }
        return perLetter;
    }

    /// Tapping a word tag switches it between print letters and braille.
    public void ToggleTag(int i)
    {
        if (i < 0 || i >= words.Count || i >= tags.Length) return;
        var t = tags[i];
        if (t.revealing || !t.group.gameObject.activeSelf) return;
        StartCoroutine(ToggleCo(t));
    }

    IEnumerator ToggleCo(Tag t)
    {
        t.revealing = true;
        if (!t.inBraille)
        {
            var perLetter = BuildDots(t, t.label.text);
            float a0 = t.label.alpha;
            for (float e = 0; e < 0.2f; e += Time.unscaledDeltaTime) { t.label.alpha = Mathf.Lerp(a0, 0f, e / 0.2f); yield return null; }
            t.label.alpha = 0f;
            foreach (var list in perLetter)
            {
                foreach (var im in list) StartCoroutine(DotCo(im));
                yield return Wait(brailleLetter);
            }
            yield return Wait(0.25f);
            t.inBraille = true;
        }
        else
        {
            var dots = t.braille.GetComponentsInChildren<Image>();
            for (float e = 0; e < brailleCrossfade; e += Time.unscaledDeltaTime)
            {
                float k = e / brailleCrossfade;
                foreach (var im in dots) if (im != null) SetAlpha(im, 0.85f * (1f - k));
                t.label.alpha = k;
                yield return null;
            }
            t.label.alpha = 1f;
            foreach (var im in dots) if (im != null) Destroy(im.gameObject);
            t.inBraille = false;
        }
        t.revealing = false;
    }

    IEnumerator DotCo(Image im)
    {
        const float d = 0.25f;
        for (float e = 0; e < d; e += Time.unscaledDeltaTime)
        {
            if (im == null) yield break;
            float k = 1f - Mathf.Pow(1f - e / d, 3f);
            SetAlpha(im, 0.85f * k);
            im.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.4f, 1f, k);
            yield return null;
        }
        if (im != null) { SetAlpha(im, 0.85f); im.rectTransform.localScale = Vector3.one; }
    }

    // ---------- Motion helpers ----------

    void Bloom(CanvasGroup g, float dur = -1f) { Run(g, BloomCo(g, dur < 0 ? bloom : dur)); }
    void Dry(CanvasGroup g) { if (g.gameObject.activeSelf) Run(g, DryCo(g, dry)); }

    void Run(CanvasGroup g, IEnumerator co)
    {
        Coroutine c;
        if (running.TryGetValue(g, out c) && c != null) StopCoroutine(c);
        running[g] = StartCoroutine(co);
    }

    IEnumerator BloomCo(CanvasGroup g, float dur)
    {
        g.interactable = true; g.blocksRaycasts = true;
        if (!g.gameObject.activeSelf) { g.alpha = 0f; g.gameObject.SetActive(true); }
        float a0 = g.alpha;
        var t = g.transform;
        for (float e = 0; e < dur; e += Time.unscaledDeltaTime)
        {
            float k = 1f - Mathf.Pow(1f - e / dur, 3f);
            g.alpha = Mathf.Lerp(a0, 1f, k);
            t.localScale = Vector3.one * Mathf.Lerp(0.92f, 1f, k);
            yield return null;
        }
        g.alpha = 1f; t.localScale = Vector3.one;
    }

    IEnumerator DryCo(CanvasGroup g, float dur)
    {
        float a0 = g.alpha;
        for (float e = 0; e < dur; e += Time.unscaledDeltaTime)
        {
            float k = e / dur;
            g.alpha = Mathf.Lerp(a0, 0f, k * k);
            yield return null;
        }
        g.alpha = 0f; g.gameObject.SetActive(false);
    }

    IEnumerator FadeText(TMP_Text t, float to, float dur)
    {
        float a0 = t.alpha;
        for (float e = 0; e < dur; e += Time.unscaledDeltaTime) { t.alpha = Mathf.Lerp(a0, to, e / dur); yield return null; }
        t.alpha = to;
    }

    void RefreshPulse()
    {
        bool show = wantListening || busy;
        if (show && !pulse.gameObject.activeSelf) Bloom(pulse);
        else if (!show && pulse.gameObject.activeSelf) Dry(pulse);
    }

    void AnimatePulse()
    {
        if (pulse == null || !pulse.gameObject.activeSelf) return;
        const float gap = 0.15f, len = 0.4f;
        float period = gap * pulseDots.Length + len + 0.35f;
        float t = Time.unscaledTime % period;
        for (int i = 0; i < pulseDots.Length; i++)
        {
            float local = Mathf.Clamp01((t - i * gap) / len);
            float v = Mathf.Sin(local * Mathf.PI);
            pulseDots[i].localScale = Vector3.one * (0.75f + 0.25f * v);
            SetAlpha(pulseImages[i], 0.18f + 0.67f * v);
        }
    }

    void Fit(RectTransform box, TMP_Text text, string s)
    {
        text.text = s;
        Vector2 p = text.GetPreferredValues(s, 500f, 0f);
        box.sizeDelta = new Vector2(Mathf.Min(p.x, 500f) + 30f, p.y + 18f);
    }

    static void SetAlpha(Graphic g, float a) { var c = g.color; c.a = a; g.color = c; }

    static WaitForSecondsRealtime Wait(float s) { return new WaitForSecondsRealtime(s); }

    void ResetAll()
    {
        StopAllCoroutines();
        running.Clear();
        words.Clear();
        wantListening = false; busy = false; pressing = false;
        foreach (var g in new[] { menu, hint, caption, subtitle, ready, end, pulse })
            if (g != null) { g.alpha = 0f; g.transform.localScale = Vector3.one; g.gameObject.SetActive(false); }
        if (tags != null)
            foreach (var t in tags)
            {
                if (t == null || t.group == null) continue;
                t.revealing = false; t.inBraille = false;
                for (int i = t.braille.childCount - 1; i >= 0; i--) Destroy(t.braille.GetChild(i).gameObject);
                t.group.alpha = 0f; t.group.gameObject.SetActive(false);
            }
        if (subtitleText != null) subtitleText.alpha = 1f;
    }
}
