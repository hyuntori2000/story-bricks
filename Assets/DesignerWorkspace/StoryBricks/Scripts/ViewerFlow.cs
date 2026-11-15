using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// Watercolour presentation shared by two isolated routes. Live state comes from the
/// experience router; only the Judy route plays the supplied, authored cut-out story.
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
    public Button newStoryButton, savedStoryButton, tutorialButton, watchAgainButton, doneButton, restartButton;
    // Retain the supplied scene's presentation references, without its prototype input flow.
    public Button finishButton;
    public CanvasGroup finish, fox, bricks, pinch, period;
    public Image foxSit, foxYip, pinchOpen, pinchClosed;
    public Image[] brickImages;
    public TMP_Text hintText, pinchText;

    [Header("Authored Judy story")]
    public StoryStage stage;
    public string[] demoWords;
    [TextArea] public string[] narration;
    public int[] narrationWord;
    public float[] narrationStart;
    public string[] narrationWordName;
    public AudioClip narrationClip, storySfxClip;
    [Range(0f, 1f)] public float storySfxVolume = 0.9f;
    public float storyEnd = 61.5f;
    public float endHold = 2f;

    [Header("Shared video frame")]
    public CanvasGroup videoGroup;
    public RawImage liveVideoImage;

    [Header("Presentation sound")]
    public AudioClip clickClip, bloopClip, sparkleClip, successClip;
    [Range(0f, 1f)] public float sfxVolume = 0.8f;

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

    public State state;
    readonly Dictionary<CanvasGroup, Coroutine> running = new Dictionary<CanvasGroup, Coroutine>();
    readonly List<string> words = new List<string>();
    public event Action CreateWorldRequested;
    public event Action JudyRequested;
    public event Action BackToMenuRequested;
    public event Action ReplayRequested;
    public event Action PreviousStoriesRequested;
    JudyStoryPlayer judyPlayer;
    AudioSource interfaceAudio;
    Coroutine highlight;
    int highlightedWord = -1;
    public string WorkspaceId { get; private set; } = "";
    public bool IsMenuVisible => state == State.Menu;
    bool busy, presentationPaused;
    bool wantListening, pressing, pulseRequested;

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
        SetButtonCopy(newStoryButton, "Create Your World", "Build your own story");
        SetButtonCopy(savedStoryButton, "Judy's Story", "Watch her world come to life");
        if (savedStoryButton != null) savedStoryButton.interactable = true;
        if (tutorialButton != null)
        {
            tutorialButton.gameObject.SetActive(true);
            tutorialButton.interactable = true;
            SetButtonCopy(tutorialButton, "See previous story", "Watch your saved animations");
        }
        Bloom(menu);
    }

    public void OnNewStory()
    {
        if ((state == State.Menu || state == State.End) && !pressing)
            StartCoroutine(Choose(newStoryButton, CreateWorldRequested));
    }
    public void OnSavedStory()
    {
        if (state == State.Menu && !pressing) StartCoroutine(Choose(savedStoryButton, JudyRequested));
    }
    public void OnPreviousStories()
    {
        if (state == State.Menu && !pressing) StartCoroutine(Choose(tutorialButton, PreviousStoriesRequested));
    }
    public void OnWatchAgain()
    {
        if (state == State.End && !pressing) StartCoroutine(Choose(watchAgainButton, ReplayRequested));
    }
    public void OnDone()
    {
        if (state == State.End && !pressing) StartCoroutine(Choose(doneButton, BackToMenuRequested));
    }
    public void OnRestart()
    {
        if (state == State.End && !pressing) StartCoroutine(Choose(restartButton, CreateWorldRequested));
    }
    IEnumerator Choose(Button button, Action selected)
    {
        pressing = true;
        Cue(clickClip);
        button?.GetComponent<WashButton>()?.Flash();
        yield return Wait(.18f);
        pressing = false;
        selected?.Invoke();
    }

    public void BeginRoute(string workspaceId)
    {
        if (workspaceId != "demo" && workspaceId != "judy" && workspaceId != "library")
            throw new ArgumentException("Unknown presentation route.", nameof(workspaceId));
        ResetAll();
        WorkspaceId = workspaceId;
        state = State.Building;
    }

    public void PlayJudy()
    {
        if (WorkspaceId != "judy") return;
        ResetAll();
        state = State.Playing;
        if (judyPlayer == null) judyPlayer = GetComponent<JudyStoryPlayer>();
        if (judyPlayer == null) judyPlayer = gameObject.AddComponent<JudyStoryPlayer>();
        judyPlayer.Play(this, presentationPaused);
    }

    public void StopJudy()
    {
        if (judyPlayer != null) judyPlayer.Stop();
        else if (stage != null) stage.ResetStage();
    }
    public void SetPaused(bool paused)
    {
        presentationPaused = paused;
        if (judyPlayer != null) judyPlayer.SetPaused(paused);
    }

    public void ShowLiveVideo(bool visible)
    {
        if (WorkspaceId != "demo" && WorkspaceId != "library") return;
        Dry(ready); Dry(caption); Dry(subtitle); Dry(end);
        SetBusy(false); SetListening(false);
        if (visible) { state = State.Playing; Bloom(videoGroup); }
        else Dry(videoGroup);
    }

    public void ShowLiveEnd(string title)
    {
        if (WorkspaceId != "demo" && WorkspaceId != "library") return;
        ShowEndCard(string.IsNullOrWhiteSpace(title) ? "Your story" : title);
    }

    internal void ShowJudyLine(int index)
    {
        if (WorkspaceId != "judy" || state != State.Playing || narration == null || index >= narration.Length) return;
        string name = narrationWordName != null && index < narrationWordName.Length ? narrationWordName[index] : null;
        int word = -1;
        if (!string.IsNullOrEmpty(name))
        {
            word = words.IndexOf(name);
            if (word < 0) { ShowWord(name); word = words.Count - 1; }
        }
        highlightedWord = word;
        Highlight(word);
        StartCoroutine(HighlightAfter(word));
        StartCoroutine(SubtitleCo(narration[index]));
    }

    internal void FinishJudy()
    {
        if (WorkspaceId != "judy") return;
        ShowEndCard("Judy's story");
        ShowCaption("Curious how Judy made this story? Create your own world.");
        Cue(successClip);
    }

    IEnumerator SubtitleCo(string text)
    {
        if (subtitle == null || subtitleText == null) yield break;
        if (!subtitle.gameObject.activeSelf)
        {
            Fit(subtitleBox, subtitleText, text); subtitleText.alpha = 1f; Bloom(subtitle);
        }
        else
        {
            yield return FadeText(subtitleText, 0f, 0.15f);
            Fit(subtitleBox, subtitleText, text);
            yield return FadeText(subtitleText, 1f, 0.15f);
        }
    }

    void ShowEndCard(string title)
    {
        state = State.End;
        Dry(subtitle); Dry(ready); Dry(videoGroup); Dry(caption);
        SetListening(false); SetBusy(false);
        if (stage != null) stage.Hide();
        Highlight(-1);
        if (endWords != null) endWords.text = string.Join(" · ", words);
        if (end != null)
            foreach (var label in end.GetComponentsInChildren<TMP_Text>(true))
                if (label.name == "Title") label.text = title;
        SetButtonCopy(watchAgainButton, "Watch again", "");
        SetButtonCopy(restartButton, "Create World", "");
        SetButtonCopy(doneButton, "Menu", "");
        foreach (var button in new[] { watchAgainButton, restartButton, doneButton })
            if (button != null) { button.gameObject.SetActive(true); button.interactable = true; }
        StartCoroutine(EndCo());
    }

    IEnumerator EndCo()
    {
        if (endButtons != null)
            foreach (var group in endButtons) if (group != null) { group.alpha = 0; group.interactable = false; }
        Bloom(end);
        yield return Wait(bloom);
        if (endButtons != null)
            foreach (var group in endButtons) { Bloom(group); yield return Wait(0.1f); }
    }

    void Cue(AudioClip clip)
    {
        if (clip == null) return;
        if (interfaceAudio == null)
        {
            interfaceAudio = gameObject.AddComponent<AudioSource>();
            interfaceAudio.playOnAwake = false; interfaceAudio.spatialBlend = 0f;
        }
        interfaceAudio.PlayOneShot(clip, sfxVolume);
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
        var label = ready != null ? ready.GetComponentInChildren<TMP_Text>(true) : null;
        if (label != null) label.text = message;
        if (readyWash != null) readyWash.fillAmount = 1f;
        if (ready != null && !ready.gameObject.activeSelf) Bloom(ready);
        // Polling updates the existing label; only entering this stage blooms the panel.
    }

    static void SetButtonCopy(Button button, string title, string sub)
    {
        if (button == null) return;
        foreach (var text in button.GetComponentsInChildren<TMP_Text>(true))
        {
            if (text.name == "Title" || text.name == "Label")
            {
                text.text = title;
                text.enableAutoSizing = true;
                text.fontSizeMin = 10f;
                text.fontSizeMax = Mathf.Max(14f, text.fontSize);
            }
            else if (text.name == "Sub") text.text = sub;
        }
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

    void Bloom(CanvasGroup g, float dur = -1f) { if (g != null) Run(g, BloomCo(g, dur < 0 ? bloom : dur)); }
    void Dry(CanvasGroup g) { if (g != null && g.gameObject.activeSelf) Run(g, DryCo(g, dry)); }

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
        g.interactable = false; g.blocksRaycasts = false;
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
        if (pulse == null) return;
        bool show = wantListening || busy;
        if (show == pulseRequested) return;
        pulseRequested = show;
        // Opposite state updates can arrive in one frame; cancel the previous fade.
        if (show) Bloom(pulse); else Dry(pulse);
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
        if (text == null || box == null) return;
        text.text = s;
        Vector2 p = text.GetPreferredValues(s, 500f, 0f);
        box.sizeDelta = new Vector2(Mathf.Min(p.x, 500f) + 30f, p.y + 18f);
    }

    static void SetAlpha(Graphic g, float a) { var c = g.color; c.a = a; g.color = c; }

    static WaitForSecondsRealtime Wait(float s) { return new WaitForSecondsRealtime(s); }

    void ResetAll()
    {
        StopJudy();
        StopAllCoroutines();
        running.Clear();
        highlight = null; highlightedWord = -1;
        if (interfaceAudio != null) interfaceAudio.Stop();
        if (liveVideoImage != null) liveVideoImage.texture = null;
        words.Clear();
        wantListening = false; busy = false; pressing = false; pulseRequested = false;
        foreach (var g in new[] { menu, hint, caption, subtitle, ready, end, pulse, videoGroup, fox, bricks, pinch, period, finish })
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
        if (tutorialButton != null) tutorialButton.gameObject.SetActive(false);
        if (finishButton != null) finishButton.gameObject.SetActive(false);
    }

    IEnumerator HighlightAfter(int index)
    {
        yield return Wait(2.4f);
        if (state == State.Playing && highlightedWord == index) Highlight(index);
    }

    void Highlight(int index)
    {
        if (highlight != null) StopCoroutine(highlight);
        highlight = StartCoroutine(HighlightCo(index));
    }

    IEnumerator HighlightCo(int index)
    {
        int count = tags == null ? 0 : Mathf.Min(words.Count, tags.Length);
        var washes = new float[count]; var labels = new float[count];
        for (int i = 0; i < count; i++) { washes[i] = tags[i].wash.color.a; labels[i] = tags[i].label.alpha; }
        for (float elapsed = 0; elapsed < 0.3f; elapsed += Time.unscaledDeltaTime)
        {
            float progress = elapsed / 0.3f;
            for (int i = 0; i < count; i++)
            {
                if (tags[i].revealing) continue;
                SetAlpha(tags[i].wash, Mathf.Lerp(washes[i], index < 0 ? tagNormal : i == index ? 1f : tagDim, progress));
                if (!tags[i].inBraille) tags[i].label.alpha = Mathf.Lerp(labels[i], index < 0 || i == index ? 1f : 0.6f, progress);
            }
            yield return null;
        }
        for (int i = 0; i < count; i++)
        {
            if (tags[i].revealing) continue;
            SetAlpha(tags[i].wash, index < 0 ? tagNormal : i == index ? 1f : tagDim);
            if (!tags[i].inBraille) tags[i].label.alpha = index < 0 || i == index ? 1f : 0.6f;
        }
    }

    void OnDisable() { StopJudy(); }
}
