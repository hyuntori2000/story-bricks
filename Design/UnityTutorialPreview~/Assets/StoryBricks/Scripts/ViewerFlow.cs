using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// Viewer-side UI for Story Bricks: v2 flow with the five motions from the Figma motion spec
/// (bloom in, dry out, wash fill, listening pulse, braille first).
/// The story backend calls the public methods; Advance() steps through the JUDY · MOON · WIND · KITE demo.
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
    public Button finishButton;
    public CanvasGroup finish;
    public Button newStoryButton, savedStoryButton, tutorialButton, watchAgainButton, doneButton, restartButton;

    [Header("Story video")]
    public StoryStage stage;
    [Tooltip("Off: the painted cut-outs act out the story. On: play the video clip instead.")]
    public bool useVideo;
    public VideoPlayer video;
    public CanvasGroup videoGroup;
    public AudioClip videoAudioClip;
    [Range(0f, 1f)] public float videoVolume = 1f;

    [Header("Sound")]
    public AudioClip clickClip;
    public AudioClip bloopClip, sparkleClip, successClip;
    [Range(0f, 1f)] public float sfxVolume = 0.8f;
    AudioSource sfx, videoAudio, narrationSrc, voiceSrc, judySrc, storySfxSrc;

    [Header("Story sound effects")]
    [Tooltip("One track with the wind, kite, bells, bump and hum, laid out on the narration's timeline.")]
    public AudioClip storySfxClip;
    [Range(0f, 1f)] public float storySfxVolume = 0.9f;

    [Header("Voice start (menu)")]
    [Tooltip("After the menu question, anything said out loud starts a new story.")]
    public bool voiceStart = false;
    [Tooltip("How loud the voice must be (0-1). Raised automatically in a noisy room.")]
    public float voiceThreshold = 0.03f;
    [Tooltip("How long the voice must last, in seconds.")]
    public float voiceHold = 0.35f;
    AudioClip micClip;
    bool micOn;
    float micOpenAt, micFloor, micLoud;
    readonly float[] micBuf = new float[1024];

    [Header("Voices")]
    [Tooltip("Story Bricks' lines, in order: element 0 is line 01.")]
    public AudioClip[] systemVoice;
    [Tooltip("Judy's recorded lines, in order. Turn 'Play Judy Voice' off when a person plays Judy live.")]
    public AudioClip[] judyVoice;
    public bool playJudyVoice = true;
    public AudioClip foxYipClip;

    [Header("Tutorial")]
    [Tooltip("Automatically show the tutorial when the opening reveals this UI. Once per scene session.")]
    public bool tutorialAfterOpening = true;
    [Tooltip("Play the complete tutorial without requiring blocks or a headset.")]
    public bool autoPlayTutorial = true;
    public bool IsTutorialActive { get; private set; }
    bool tutorialShown;
    public CanvasGroup fox;
    public Image foxSit, foxYip;
    public TMP_Text hintText;
    [Tooltip("The F, O and X bricks, shown one by one.")]
    public CanvasGroup bricks;
    public Image[] brickImages;
    [Tooltip("Pause after each brick's line before the next brick appears.")]
    public float brickPause = 1.2f;
    public CanvasGroup pinch;
    public Image pinchOpen, pinchClosed;
    public TMP_Text pinchText;
    [Tooltip("The period brick: it ends a story.")]
    public CanvasGroup period;
    [Tooltip("Judy's recorded answers play on their own once Story Bricks has finished asking.")]
    public bool autoAnswers = true;
    public float answerGap = 1f;
    float idleSince;
    Coroutine pinchCo;
    string storyHint;
    bool madeNew, menuAnswering, welcomed;
    Coroutine voiceCo;
    readonly List<bool> pinchStep = new List<bool>();
    int storyStart, stepEnd;


    [Header("Look")]
    public Color ink = new Color(0.357f, 0.306f, 0.271f);
    [Range(0f, 1f)] public float tagNormal = 0.9f;
    [Range(0f, 1f)] public float tagDim = 0.45f;

    [Header("Timing (seconds)")]
    public float bloom = 0.4f;
    public float dry = 0.3f;
    public float washFill = 0.6f;
    public float hintHold = 4f;
    public float captionHold = 2f;
    public float readyHold = 2.2f;
    public float endHold = 2f;
    public float brailleLetter = 0.12f;
    public float brailleHold = 0.6f;
    public float brailleCrossfade = 0.4f;

    [Header("Demo content")]
    public string[] demoWords = { "JUDY", "MOON", "WIND", "KITE" };
    [TextArea] public string[] narration;
    public int[] narrationWord;
    public float[] narrationStart;
    public string[] narrationWordName;
    public AudioClip narrationClip;
    public float storyEnd = 67f;

    public State state;
    readonly Dictionary<CanvasGroup, Coroutine> running = new Dictionary<CanvasGroup, Coroutine>();
    readonly List<string> words = new List<string>();
    List<System.Action> steps;
    int step;
    bool wantListening, centreBusy, skipLine, pressing;
    Coroutine flow, highlightCo;

    static readonly string[] Braille =
    {
        "1", "12", "14", "145", "15", "124", "1245", "125", "24", "245", "13", "123", "134",
        "1345", "135", "1234", "12345", "1235", "234", "2345", "136", "1236", "2456", "1346", "13456", "1356"
    };

    void Awake()
    {
        sfx = NewSource(false); sfx.volume = 1f;
        videoAudio = NewSource(false); videoAudio.volume = 1f;
        narrationSrc = NewSource(false); narrationSrc.volume = 1f;
        // The video's soundtrack is played from its own clip, started together with the picture.
        if (video != null) { video.playOnAwake = false; video.audioOutputMode = VideoAudioOutputMode.None; }
        voiceSrc = NewSource(false); voiceSrc.volume = 1f;
        storySfxSrc = NewSource(false);
        if (voiceStart) RequestMic();
        judySrc = NewSource(false); judySrc.volume = 1f;
        if (hintText != null) storyHint = hintText.text;
        if (bricks != null) bricksHome = bricks.transform.localPosition;

        // The kids' flow, one beat per trigger. pinch = the child's double pinch ends a word;
        // the other beats are the operator's (brick placed, Judy has answered).
        steps = new List<System.Action>();
        // --- Tutorial: the bricks appear on their own (TutorialCo); the only trigger is the double pinch ---
        Add(true,  () => Restart(FoxWordCo()));      // double pinch: the word FOX is read
        Add(true,  () => Restart(FoxCo()));          // double pinch: the period ends the practice story
        storyStart = steps.Count;
        // --- Judy's story (Make a new story) ---
        Add(true,  () => Word("JUDY", StoryStage.Step.Judy, 10));
        Add(true,  () => Word("MOON", StoryStage.Step.MoonOutline, 11));
        Add(false, () => Answer(3, "“Bread dough. Soft, and cold.”", StoryStage.Step.MoonDough, 12));
        Add(false, () => Answer(4, "“It hums, like the fridge at night.”", StoryStage.Step.Hum, 13));
        Add(true,  () => Word("WIND", StoryStage.Step.WindOutline, 14));
        Add(false, () => Answer(5, "“It pushes my hair and goes whoosh in my ears.”", StoryStage.Step.WindColour, 15));
        Add(true,  () => Word("KITE", StoryStage.Step.KiteOutline, 16));
        Add(false, () => Answer(6, "“It pulls, like a dog on a leash.”", StoryStage.Step.KiteColour, 17));
        Add(false, () => Answer(7, "“It has bells on its tail. Jingle, jingle.”", StoryStage.Step.Bells, 24));
        // The period brick plus a double pinch finishes the story.
        Add(true,  () => { Cue(clickClip); float r = Say(19, 0.4f); StartCoroutine(After(0.4f + r * 0.5f, StoryReady)); });
    }

    void Add(bool pinch, System.Action a) { steps.Add(a); pinchStep.Add(pinch); }

    /// A finished word: click, the tag (braille first), its picture, then Story Bricks reads it and asks.
    void Word(string word, StoryStage.Step picture, int line)
    {
        Cue(clickClip);
        ShowWord(word);
        Paint(picture, 1.6f);
        Cue(sparkleClip, 1.0f);
        Say(line, 1.5f);
    }

    /// Judy answers: her line, the caption, the picture changes, then the next question.
    void Answer(int judyLine, string captionLine, StoryStage.Step picture, int nextLine)
    {
        float l = JudySay(judyLine);
        ShowCaption(captionLine);
        Paint(picture, 0.8f);
        Say(nextLine, l + 0.7f);
    }

    // ---------- Voices ----------

    static float Len(AudioClip[] clips, int n) { return clips != null && n >= 1 && n <= clips.Length && clips[n - 1] != null ? clips[n - 1].length : 0f; }

    /// Story Bricks speaks line n (1-based) after a delay. A new line replaces one still waiting or playing. Returns its length.
    public float Say(int n, float delay = 0f)
    {
        if (voiceCo != null) StopCoroutine(voiceCo);
        voiceCo = StartCoroutine(SayCo(n, delay));
        return Len(systemVoice, n);
    }

    IEnumerator SayCo(int n, float delay)
    {
        if (delay > 0f) yield return Wait(delay);
        if (voiceSrc != null && Len(systemVoice, n) > 0f) { voiceSrc.Stop(); voiceSrc.clip = systemVoice[n - 1]; voiceSrc.Play(); }
        voiceCo = null;
    }

    /// Judy's recorded line n (1-based). Returns its length, or 0 when a person plays Judy live.
    public float JudySay(int n)
    {
        if (!playJudyVoice || judySrc == null || Len(judyVoice, n) <= 0f) return 0f;
        if (voiceSrc != null) voiceSrc.Stop();
        judySrc.Stop(); judySrc.clip = judyVoice[n - 1]; judySrc.Play();
        return judyVoice[n - 1].length;
    }

    IEnumerator After(float delay, System.Action then) { yield return Wait(delay); then(); }

    // ---------- Voice start ----------

    /// Asks for the microphone once (Quest shows its own prompt).
    public static void RequestMic()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
#endif
    }

    static bool MicAllowed()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone);
#else
        return true;
#endif
    }

    /// On the menu, once Story Bricks has finished asking, a spoken answer starts a new story.
    /// It hears that someone spoke, not which words: "play a saved story" still needs its button.
    void ListenForVoice()
    {
        bool want = voiceStart && state == State.Menu && !pressing && !menuAnswering && voiceCo == null
                    && voiceSrc != null && !voiceSrc.isPlaying && menu != null && menu.gameObject.activeSelf && menu.alpha > 0.9f;
        if (want && !micOn) StartMic();
        else if (!want && micOn) StopMic();
        if (!micOn || micClip == null) return;

        int pos = Microphone.GetPosition(null);
        if (pos < micBuf.Length) return;
        micClip.GetData(micBuf, pos - micBuf.Length);
        float sum = 0f;
        for (int i = 0; i < micBuf.Length; i++) sum += micBuf[i] * micBuf[i];
        float rms = Mathf.Sqrt(sum / micBuf.Length);
        float t = Time.unscaledTime - micOpenAt;
        if (t < 0.6f) { micFloor = Mathf.Max(micFloor, rms); return; }      // learn the room's noise first
        float threshold = Mathf.Max(voiceThreshold, micFloor * 2.5f);
        if (rms > threshold) micLoud += Time.unscaledDeltaTime;
        else micLoud = Mathf.Max(0f, micLoud - Time.unscaledDeltaTime * 0.5f);
        if (micLoud >= voiceHold) { StopMic(); OnNewStory(); }
    }

    void StartMic()
    {
        if (!MicAllowed() || Microphone.devices.Length == 0) return;
        micClip = Microphone.Start(null, true, 10, 16000);
        micOn = micClip != null;
        micOpenAt = Time.unscaledTime; micFloor = 0f; micLoud = 0f;
        if (micOn) SetListening(true);
    }

    void StopMic()
    {
        if (!micOn) return;
        micOn = false;
        Microphone.End(null);
        micClip = null;
        SetListening(false);
    }

    // ---------- Tutorial ----------

    /// Short instruction in the hint box (the text version of what Story Bricks is saying).
    void Prompt(string text)
    {
        if (hintText != null) hintText.text = text;
        if (!hint.gameObject.activeSelf || hint.alpha < 0.5f) Bloom(hint);
    }

    /// Menu option 3: practise with the word FOX, then come back to the menu.
    public void OnTutorial() { if (state == State.Menu && !pressing) StartCoroutine(PressThen(tutorialButton, () => { Dry(menu); ShowTutorial(); })); }

    /// Menu option 1: straight into Judy's story.
    void StartStory()
    {
        madeNew = true;
        step = storyStart; stepEnd = steps.Count;
        Say(9, 0.3f);                             // "Who is your story about?"
        ShowHint();
    }

    public void ShowTutorial()
    {
        ResetAll();
        tutorialShown = true;
        IsTutorialActive = true;
        step = 0; stepEnd = storyStart;
        Restart(autoPlayTutorial ? TutorialPlaybackCo() : TutorialCo());
    }

    // Keep one owner coroutine so finishing or skipping cancels the entire presentation.
    IEnumerator TutorialPlaybackCo()
    {
        yield return TutorialCo();
        yield return WaitForTutorialVoice();
        yield return Wait(2.2f); // Show the two-pinch gesture without waiting for hardware.
        yield return FoxWordCo();
        yield return WaitForTutorialVoice();
        yield return Wait(2.2f);
        yield return FoxCo();
    }

    IEnumerator WaitForTutorialVoice()
    {
        while (voiceCo != null || (voiceSrc != null && voiceSrc.isPlaying)) yield return null;
    }

    public void SkipTutorial()
    {
        if (!IsTutorialActive) return;
        ResetAll();
        ShowMenu();
    }

    /// Hardware and editor simulation share this route; consume a skip exactly once.
    public void HandleDoublePinch()
    {
        if (IsTutorialActive) { SkipTutorial(); return; }
        if (state == State.Building && step < stepEnd && pinchStep[step]) Advance();
        else if (state == State.Menu && !menuAnswering && !pressing && menu.alpha > 0.9f)
        {
            if (voiceSrc != null) voiceSrc.Stop();
            OnNewStory();
        }
    }

    /// Practice word: the F, O and X bricks appear one by one with their spoken lines, then the pinch-twice animation.
    IEnumerator TutorialCo()
    {
        state = State.Hint;                       // nothing to trigger until the pinch is asked for
        Prompt("Practice: build the word FOX");
        Cue(bloopClip, 0.2f);
        float l = Say(2, 0.5f);                   // "...Can you find the bricks to make the word fox?"
        yield return Wait(0.5f + l + 0.3f);
        Dry(hint);
        yield return Wait(dry);
        if (bricks != null)
        {
            foreach (var im in brickImages) SetAlpha(im, 0f);
            bricks.transform.localPosition = bricksHome;
            Bloom(bricks, 0.05f);
            for (int i = 0; i < brickImages.Length; i++)
            {
                l = Say(3 + i);                   // "Find the letter F." / "Now find O." / "Now find X."
                Cue(bloopClip);
                StartCoroutine(BrickIn(brickImages[i]));
                yield return Wait(Mathf.Max(l, 0.8f) + brickPause);
            }
        }
        l = Say(6);                               // "You made a word! ... pinch your fingers two times."
        yield return Wait(l * 0.45f);
        ShowPinch("Pinch twice to finish the word");
        state = autoPlayTutorial ? State.Hint : State.Building;
        if (!autoPlayTutorial) flow = null;
    }

    Vector3 bricksHome;

    void ShowPinch(string text)
    {
        if (pinch == null) return;
        if (pinchText != null) pinchText.text = text;
        Bloom(pinch);
        if (pinchCo != null) StopCoroutine(pinchCo);
        pinchCo = StartCoroutine(PinchCo());
    }

    void HidePinch()
    {
        if (pinchCo != null) { StopCoroutine(pinchCo); pinchCo = null; }
        if (pinch != null) Dry(pinch);
    }

    IEnumerator BrickIn(Image im)
    {
        var rt = im.rectTransform;
        Vector2 home = rt.anchoredPosition;
        for (float e = 0f; e < bloom; e += Time.unscaledDeltaTime)
        {
            float k = 1f - Mathf.Pow(1f - e / bloom, 3f);
            SetAlpha(im, k);
            rt.anchoredPosition = home + new Vector2(0f, 14f * (1f - k));
            rt.localScale = Vector3.one * Mathf.Lerp(0.9f, 1f, k);
            yield return null;
        }
        SetAlpha(im, 1f); rt.anchoredPosition = home; rt.localScale = Vector3.one;
    }

    /// The hand pinches twice, rests, and repeats until the word is finished.
    IEnumerator PinchCo()
    {
        while (true)
        {
            SetPinch(false);
            yield return Wait(0.7f);
            for (int i = 0; i < 2; i++)
            {
                SetPinch(true);
                yield return Wait(0.22f);
                SetPinch(false);
                yield return Wait(0.2f);
            }
            yield return Wait(0.9f);
        }
    }

    void SetPinch(bool closed)
    {
        if (pinchOpen != null) pinchOpen.enabled = !closed;
        if (pinchClosed != null) pinchClosed.enabled = closed;
        if (pinch != null) pinch.transform.localScale = Vector3.one * (closed ? 0.95f : 1f);
    }

    /// The practice word is finished: FOX appears, the fox pops up and yips, then the real story starts.
    IEnumerator FoxWordCo()
    {
        state = State.Hint;                       // ignore triggers until the story starts
        Cue(clickClip);
        if (pinchCo != null) { StopCoroutine(pinchCo); pinchCo = null; }
        if (pinch != null) Dry(pinch);
        if (bricks != null) Dry(bricks);
        Dry(hint);
        yield return Wait(dry);
        ShowWord("FOX");
        float l = Say(7, 0.9f);                   // "F, O, X. Fox!"
        yield return Wait(0.9f + l + 0.3f);
        // The period brick: how a finished story is marked.
        l = Say(22);                              // "...find the period brick. Put it at the end, then pinch two times."
        Cue(bloopClip);
        if (period != null) Bloom(period);
        yield return Wait(l * 0.6f);
        ShowPinch("Pinch twice to finish the story");
        state = autoPlayTutorial ? State.Hint : State.Building;
        if (!autoPlayTutorial) flow = null;
    }

    /// The period is down and read: the practice story is finished, and the fox arrives.
    IEnumerator FoxCo()
    {
        state = State.Hint;
        Cue(clickClip);
        HidePinch();
        if (period != null) Dry(period);
        Cue(sparkleClip, 0.3f);
        float l = Say(23, 0.3f);                  // "That's the period. It tells me your story is finished."
        yield return Wait(0.3f + l + 0.2f);
        if (fox != null)
        {
            SetFox(false);
            Bloom(fox);
            yield return Wait(bloom);
            for (int i = 0; i < 2; i++)
            {
                Cue(foxYipClip);
                SetFox(true);
                float y = foxYipClip != null ? Mathf.Clamp(foxYipClip.length, 0.35f, 0.9f) : 0.5f;
                for (float e = 0f; e < y; e += Time.unscaledDeltaTime)
                {
                    fox.transform.localScale = Vector3.one * (1f + 0.06f * Mathf.Sin(e / y * Mathf.PI));
                    yield return null;
                }
                fox.transform.localScale = Vector3.one;
                SetFox(false);
                yield return Wait(0.45f);
            }
        }
        l = Say(8);                               // "That was practice. Now let's make your own story."
        yield return Wait(l + 0.3f);
        if (fox != null) Dry(fox);
        ClearTags();
        yield return Wait(dry + 0.2f);
        if (hintText != null) hintText.text = storyHint;
        ResetAll();
        ShowMenu();                               // completion and skip use the same clean menu state
    }

    void SetFox(bool yipping)
    {
        if (foxSit != null) foxSit.enabled = !yipping;
        if (foxYip != null) foxYip.enabled = yipping;
    }

    void ClearTags()
    {
        foreach (var t in tags)
        {
            if (t == null || t.group == null || !t.group.gameObject.activeSelf) continue;
            Dry(t.group);
        }
        words.Clear();
    }

    /// Two index-finger pinches close together, on either hand, read straight from hand tracking.
    /// A pinch held for a while is a grab (picking up a brick) and does not count.
    bool DoublePinch()
    {
#if !UNITY_ANDROID || UNITY_EDITOR
        return false; // Desktop preview uses P and never initializes headset native APIs.
#else
        float now = Time.unscaledTime;
        bool fired = false;
        for (int h = 0; h < 2; h++)
        {
            bool down = IndexPinch(h == 0 ? OVRPlugin.Hand.HandLeft : OVRPlugin.Hand.HandRight);
            if (down && !pinchWas[h])
            {
                float gap = now - pinchLast[h];
                if (gap > 0.06f && gap < doublePinchGap) { fired = true; pinchLast[h] = -10f; }
                else pinchLast[h] = now;
                pinchStart[h] = now;
            }
            else if (!down && pinchWas[h] && now - pinchStart[h] > pinchMaxHold) pinchLast[h] = -10f;
            pinchWas[h] = down;
        }
        return fired;
#endif
    }

    [Header("Double pinch")]
    [Tooltip("Longest time between the two pinches.")]
    public float doublePinchGap = 0.9f;
    [Tooltip("A pinch held longer than this is treated as picking something up.")]
    public float pinchMaxHold = 0.6f;
    readonly bool[] pinchWas = new bool[2];
    readonly float[] pinchLast = { -10f, -10f }, pinchStart = { 0f, 0f };
    OVRPlugin.HandState handState = new OVRPlugin.HandState();

    bool IndexPinch(OVRPlugin.Hand hand)
    {
        if (!OVRPlugin.GetHandState(OVRPlugin.Step.Render, hand, ref handState)) return false;
        if ((handState.Status & OVRPlugin.HandStatus.HandTracked) == 0) return false;
        return (handState.Pinches & OVRPlugin.HandFingerPinch.Index) != 0;
    }

    void Paint(StoryStage.Step step, float delay) { if (stage != null) stage.Reveal(step, delay); }

    AudioSource NewSource(bool loop)
    {
        var a = gameObject.AddComponent<AudioSource>();
        a.playOnAwake = false; a.loop = loop; a.spatialBlend = 0f; a.volume = 0f;
        return a;
    }

    /// Plays a one-shot sound, optionally after a delay.
    public void Cue(AudioClip clip, float delay = 0f)
    {
        if (clip == null || sfx == null) return;
        if (delay <= 0f) sfx.PlayOneShot(clip, sfxVolume);
        else StartCoroutine(CueCo(clip, delay));
    }

    IEnumerator CueCo(AudioClip clip, float delay)
    {
        yield return Wait(delay);
        sfx.PlayOneShot(clip, sfxVolume);
    }

    /// Plays the story video in the centre. It keeps its last frame when it ends.
    void StartVideo()
    {
        if (video == null || videoGroup == null) return;
        var rt = video.targetTexture;
        if (rt != null)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(0.98f, 0.97f, 0.94f, 1f));
            RenderTexture.active = prev;
        }
        video.Stop();
        videoAudio.Stop();
        Bloom(videoGroup);
        if (videoCo != null) StopCoroutine(videoCo);
        videoCo = StartCoroutine(VideoCo());
    }

    Coroutine videoCo;

    IEnumerator VideoCo()
    {
        video.Prepare();
        for (float t = 0f; !video.isPrepared && t < 3f; t += Time.unscaledDeltaTime) yield return null;
        video.Play();
        if (videoAudioClip == null) yield break;
        float len = videoAudioClip.length;
        videoAudio.clip = videoAudioClip;
        videoAudio.volume = videoVolume;
        // Start the soundtrack once the picture is clearly moving, then keep the two together.
        for (float t = 0f; !(video.isPlaying && video.time > 0.12 && video.time < 1.0) && t < 5f; t += Time.unscaledDeltaTime) yield return null;
        videoAudio.time = Mathf.Clamp((float)video.time, 0f, len - 0.05f);
        videoAudio.Play();
        for (float e = 0f; e < len + 1f; e += 0.3f)
        {
            yield return Wait(0.3f);
            if (!video.isPlaying) continue;
            float vt = (float)video.time;
            if (vt >= len - 0.15f) continue;
            if (!videoAudio.isPlaying) { videoAudio.time = vt; videoAudio.Play(); }
            else if (Mathf.Abs(videoAudio.time - vt) > 0.12f) videoAudio.time = vt;
        }
    }

    /// Restarts the whole experience from the opening animation.
    public void OnRestart()
    {
        if (state != State.End || pressing) return;
        StartCoroutine(PressThen(restartButton, () => SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex)));
    }

    void OnEnable()
    {
        ResetAll();
        if (tutorialAfterOpening && !tutorialShown) ShowTutorial();
        else ShowMenu();
    }

    void OnDisable() { ResetAll(); }

    void Update()
    {
        bool adv = false;
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null && (kb.spaceKey.wasPressedThisFrame || kb.rightArrowKey.wasPressedThisFrame)) adv = true;
#endif
        bool pinchKey = false;
#if ENABLE_INPUT_SYSTEM
        if (kb != null && kb.pKey.wasPressedThisFrame) pinchKey = true;      // stands in for the double pinch
#endif
        // Operator: A on a Touch controller. A single hand pinch must not advance anything.
#if UNITY_ANDROID && !UNITY_EDITOR
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.Touch)) adv = true;
#endif
        // Operator shortcut for the tutorial from the menu: T on the keyboard, B on a Touch controller.
        bool tut = false;
#if UNITY_ANDROID && !UNITY_EDITOR
        tut = OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.Touch);
#endif
#if ENABLE_INPUT_SYSTEM
        if (kb != null && kb.tKey.wasPressedThisFrame) tut = true;
#endif
        if (tut && state == State.Menu) { if (voiceSrc != null) voiceSrc.Stop(); OnTutorial(); }
        bool pinched = DoublePinch() || pinchKey;
        if (IsTutorialActive)
        {
            if (pinched) HandleDoublePinch();
            // Auto playback owns its timeline; operator shortcuts must not race it.
            else if (adv && !autoPlayTutorial) Advance();
            return;
        }
        if (adv) Advance();
        ListenForVoice();
        // Judy's recorded answers follow on their own, a moment after Story Bricks stops talking.
        if (autoAnswers && playJudyVoice && state == State.Building && step < stepEnd && !pinchStep[step])
        {
            bool busy = voiceCo != null || (voiceSrc != null && voiceSrc.isPlaying) || (judySrc != null && judySrc.isPlaying);
            if (busy) idleSince = Time.unscaledTime;
            else if (Time.unscaledTime - idleSince > answerGap) { idleSince = Time.unscaledTime; Advance(); }
        }
        else idleSince = Time.unscaledTime;
        if (!adv && pinched) HandleDoublePinch();
        AnimatePulse();
    }

    // ---------- Public API ----------

    /// Steps through the scripted demo. Space / right arrow in the editor, A button on the controller.
    public void Advance()
    {
        if (IsTutorialActive && autoPlayTutorial) return;
        switch (state)
        {
            case State.Menu: if (!menuAnswering && !pressing) StartCoroutine(MenuAnswerCo()); break;
            case State.Building: if (step < stepEnd) steps[step++](); break;
            case State.Playing: skipLine = true; break;
        }
    }

    public void ShowMenu()
    {
        state = State.Menu;
        Bloom(menu);
        // The full welcome the first time; afterwards only the question.
        // "...Do you want to create a new story? Pinch two times to start."
        float w = Say(welcomed ? 26 : 25);
        // Counts as heard only if it played through (the panel can be hidden again during the opening).
        if (!welcomed) StartCoroutine(After(w * 0.8f, () => welcomed = true));
    }

    /// Judy answers the welcome by voice, then the button presses itself.
    IEnumerator MenuAnswerCo()
    {
        menuAnswering = true;
        // Judy answers only after Story Bricks has finished asking.
        while (voiceCo != null || (voiceSrc != null && voiceSrc.isPlaying)) yield return null;
        yield return Wait(0.35f);
        float l = JudySay(1);
        yield return Wait(l + 0.2f);
        menuAnswering = false;
        OnNewStory();
    }

    public void OnNewStory() { if (state == State.Menu && !pressing) StartCoroutine(PressThen(newStoryButton, () => { Dry(menu); StartStory(); })); }

    public void OnSavedStory()
    {
        if (state != State.Menu || pressing) return;
        // Words are revealed one by one, braille first, as the narration reaches them.
        StartCoroutine(PressThen(savedStoryButton, () => { Dry(menu); if (voiceSrc != null) voiceSrc.Stop(); StoryReady(); }));
    }

    public void ShowHint()
    {
        state = State.Hint;
        Restart(HintCo());
    }

    public void SetListening(bool on) { wantListening = on; RefreshPulse(); }

    public void ShowWord(string word) { ShowWord(word, true); }

    public void ShowWord(string word, bool brailleFirst)
    {
        if (words.Count >= tags.Length) return;
        var t = tags[words.Count];
        words.Add(word);
        StartCoroutine(WordCo(t, word.ToUpperInvariant(), brailleFirst));
    }

    public void ShowCaption(string text) { StartCoroutine(CaptionCo(text)); }

    /// [Finish]: ends the story being built, the same as the period brick plus a double pinch.
    public void OnFinish()
    {
        if (state != State.Building || !madeNew || pressing) return;
        StartCoroutine(PressThen(finishButton, () =>
        {
            step = stepEnd;                       // nothing more to build
            float r = Say(19, 0.3f);              // "Your story is ready. Here it comes!"
            StartCoroutine(After(0.3f + r * 0.5f, StoryReady));
        }));
    }

    public void StoryReady()
    {
        if (finish != null) Dry(finish);
        state = State.Ready;
        Restart(ReadyCo());
    }

    public void PlayStory()
    {
        state = State.Playing;
        Restart(PlayCo());
    }

    public void ShowEnd()
    {
        state = State.End;
        Restart(EndCo());
    }

    public void OnWatchAgain() { if (state == State.End && !pressing) StartCoroutine(PressThen(watchAgainButton, () => { Dry(end); PlayStory(); })); }

    public void OnDone() { if (state == State.End && !pressing) StartCoroutine(PressThen(doneButton, () => { ResetAll(); ShowMenu(); })); }

    // ---------- Sequences ----------

    void Restart(IEnumerator co)
    {
        if (flow != null) StopCoroutine(flow);
        flow = StartCoroutine(co);
    }

    IEnumerator HintCo()
    {
        yield return Wait(dry);
        Bloom(hint);
        yield return Wait(bloom + hintHold);
        Dry(hint);
        yield return Wait(dry);
        state = State.Building;
        if (finish != null && madeNew) Bloom(finish);
        Paint(StoryStage.Step.Hill, 0f);
        SetListening(true);
    }

    IEnumerator WordCo(Tag t, string word, bool brailleFirst)
    {
        t.label.text = word;
        float w = Mathf.Max(66f, t.label.GetPreferredValues(word).x + 28f);
        t.root.sizeDelta = new Vector2(w, t.root.sizeDelta.y);
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
        Cue(clickClip);
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

    IEnumerator CaptionCo(string text)
    {
        centreBusy = true; RefreshPulse();
        if (caption.gameObject.activeSelf) { Dry(caption); yield return Wait(dry); }
        Fit(captionBox, captionText, text);
        Bloom(caption);
        yield return Wait(bloom + captionHold);
        Dry(caption);
        yield return Wait(dry);
        centreBusy = false; RefreshPulse();
    }

    IEnumerator ReadyCo()
    {
        SetListening(true);                 // the story is being written
        yield return Wait(1.6f);
        SetListening(false);
        yield return Wait(dry);
        readyWash.fillAmount = 0f;
        Bloom(ready, 0.2f);
        for (float e = 0; e < washFill; e += Time.unscaledDeltaTime)
        {
            readyWash.fillAmount = Mathf.SmoothStep(0f, 1f, e / washFill);
            yield return null;
        }
        readyWash.fillAmount = 1f;
        yield return Wait(readyHold);
        Dry(ready);
        yield return Wait(dry);
        PlayStory();
        flow = null;
    }

    /// Story playback: the video in the centre with narration subtitles underneath, on one timeline.
    /// Word tags come in (or light up) when the narration reaches them.
    IEnumerator PlayCo()
    {
        SetListening(false);
        if (subtitle.gameObject.activeSelf) Dry(subtitle);
        yield return Wait(dry);
        bool puppets = stage != null && !useVideo;
        storyT0 = Time.unscaledTime;
        if (narrationClip != null) { narrationSrc.clip = narrationClip; narrationSrc.time = 0f; narrationSrc.Play(); }
        if (storySfxClip != null) { storySfxSrc.clip = storySfxClip; storySfxSrc.volume = storySfxVolume; storySfxSrc.time = 0f; storySfxSrc.Play(); }
        if (puppets) stage.BeginStory(StoryClock); else StartVideo();
        skipLine = false;
        for (int i = 0; i < narration.Length; i++)
        {
            float start = narrationStart != null && i < narrationStart.Length ? narrationStart[i] : i * 5f;
            while (StoryClock() < start && !skipLine) yield return null;
            if (skipLine)
            {
                storyT0 = Time.unscaledTime - start;
                if (narrationSrc.isPlaying && narrationClip != null) narrationSrc.time = Mathf.Min(start, narrationClip.length - 0.1f);
                if (storySfxSrc.isPlaying && storySfxClip != null) storySfxSrc.time = Mathf.Min(start, storySfxClip.length - 0.1f);
                skipLine = false;
            }

            // Which word this line is about. A word not on the board yet arrives now, braille first.
            string wn = narrationWordName != null && i < narrationWordName.Length ? narrationWordName[i] : null;
            int w = -1;
            bool revealed = false;
            if (!string.IsNullOrEmpty(wn))
            {
                w = words.IndexOf(wn);
                if (w < 0 && words.Count < tags.Length) { ShowWord(wn); w = words.Count - 1; revealed = true; }
            }
            Highlight(w);
            playingWord = w;
            if (revealed) StartCoroutine(HighlightAfter(w, 2.4f));

            if (!subtitle.gameObject.activeSelf) { Fit(subtitleBox, subtitleText, narration[i]); subtitleText.alpha = 1f; Bloom(subtitle); }
            else
            {
                yield return FadeText(subtitleText, 0f, 0.15f);
                Fit(subtitleBox, subtitleText, narration[i]);
                yield return FadeText(subtitleText, 1f, 0.15f);
            }
        }
        while (StoryClock() < storyEnd && !skipLine) yield return null;
        if (skipLine && video != null) { video.Pause(); videoAudio.Stop(); }
        if (skipLine) { narrationSrc.Stop(); storySfxSrc.Stop(); }
        skipLine = false;
        playingWord = -1;
        Dry(subtitle);
        Highlight(-1);
        yield return Wait(endHold);
        ShowEnd();
    }

    float storyT0;

    /// Seconds into the story. Follows the video when it covers the whole story, otherwise real time.
    float StoryClock()
    {
        if (narrationSrc != null && narrationSrc.isPlaying) return narrationSrc.time;
        if (video != null && video.isPlaying && video.clip != null && video.clip.length > storyEnd - 5f) return (float)video.time;
        return Time.unscaledTime - storyT0;
    }

    int playingWord = -1;

    IEnumerator HighlightAfter(int index, float delay)
    {
        yield return Wait(delay);
        if (state == State.Playing && playingWord == index) Highlight(index);
    }

    IEnumerator EndCo()
    {
        var parts = new List<string>();
        foreach (var w in words) parts.Add(w.Length > 1 ? char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant() : w);
        endWords.text = string.Join(" · ", parts);
        foreach (var b in endButtons) { b.alpha = 0f; }
        if (videoGroup != null && videoGroup.gameObject.activeSelf) { Dry(videoGroup); yield return Wait(dry); }
        if (stage != null && stage.group.gameObject.activeSelf) { stage.Hide(); yield return Wait(0.3f); }
        Bloom(end);
        Cue(successClip);
        if (madeNew) Say(20, 1.2f);               // "The end. What a story, Judy! I saved it..."
        yield return Wait(bloom);
        foreach (var b in endButtons) { Bloom(b); yield return Wait(0.1f); }
        flow = null;
    }

    IEnumerator PressThen(Button b, System.Action then)
    {
        pressing = true;
        Cue(clickClip);
        var wb = b != null ? b.GetComponent<WashButton>() : null;
        if (wb != null) wb.Flash();
        yield return Wait(0.18f);
        then();
        pressing = false;
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

    void Highlight(int index)
    {
        if (highlightCo != null) StopCoroutine(highlightCo);
        highlightCo = StartCoroutine(HighlightCo(index));
    }

    IEnumerator HighlightCo(int index)
    {
        int n = Mathf.Min(words.Count, tags.Length);
        var from = new float[n]; var fromL = new float[n];
        for (int i = 0; i < n; i++) { from[i] = tags[i].wash.color.a; fromL[i] = tags[i].label.alpha; }
        for (float e = 0; e < 0.3f; e += Time.unscaledDeltaTime)
        {
            float k = e / 0.3f;
            for (int i = 0; i < n; i++)
            {
                if (tags[i].revealing) continue;
                SetAlpha(tags[i].wash, Mathf.Lerp(from[i], index < 0 ? tagNormal : i == index ? 1f : tagDim, k));
                if (!tags[i].inBraille) tags[i].label.alpha = Mathf.Lerp(fromL[i], index < 0 || i == index ? 1f : 0.6f, k);
            }
            yield return null;
        }
        for (int i = 0; i < n; i++)
        {
            if (tags[i].revealing) continue;
            SetAlpha(tags[i].wash, index < 0 ? tagNormal : i == index ? 1f : tagDim);
            if (!tags[i].inBraille) tags[i].label.alpha = index < 0 || i == index ? 1f : 0.6f;
        }
    }

    void RefreshPulse()
    {
        bool show = wantListening && !centreBusy;
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
        IsTutorialActive = false;
        for (int h = 0; h < pinchWas.Length; h++)
        {
            pinchWas[h] = false; pinchLast[h] = -10f; pinchStart[h] = 0f;
        }
        running.Clear();
        if (sfx != null) sfx.Stop();
        if (video != null) video.Stop();
        if (videoAudio != null) videoAudio.Stop();
        if (narrationSrc != null) narrationSrc.Stop();
        if (voiceSrc != null) voiceSrc.Stop();
        if (judySrc != null) judySrc.Stop();
        if (storySfxSrc != null) storySfxSrc.Stop();
        StopMic();
        voiceCo = null; pinchCo = null; madeNew = false; menuAnswering = false;
        if (hintText != null && storyHint != null) hintText.text = storyHint;
        if (stage != null) stage.ResetStage();
        flow = null; highlightCo = null;
        words.Clear();
        step = 0; wantListening = false; centreBusy = false; skipLine = false; pressing = false;
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
    }
}
