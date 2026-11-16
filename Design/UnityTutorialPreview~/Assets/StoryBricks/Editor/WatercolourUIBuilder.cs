using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using UnityEngine.Video;

/// Builds the watercolour viewer UI (palette A) under the existing ParentUI canvas.
public static class WatercolourUIBuilder
{
    const string FontDir = "Assets/StoryBricks/Fonts/";
    const int M = WatercolourPainter.M;
    static TMP_FontAsset reg, bold, ital;
    static int layer;

    static Color Hex(string h, float a = 1f) { Color c; ColorUtility.TryParseHtmlString("#" + h, out c); c.a = a; return c; }
    /// Overall size of the viewer UI. 1 = the original size.
    const float UiScale = 1.5f;

    static readonly Color Ink = Hex("5B4E45"), Soft = Hex("57524F", 0.9f), Line = Hex("B5856A");

    static TMP_FontAsset FontAsset(string style)
    {
        string path = FontDir + "AtkinsonHyperlegible-" + style + " SDF.asset";
        var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
        if (fa != null) return fa;
        var f = AssetDatabase.LoadAssetAtPath<Font>(FontDir + "AtkinsonHyperlegible-" + style + ".ttf");
        if (f == null) { Debug.LogError("StoryBricks: font not imported: " + style); return null; }
        fa = TMP_FontAsset.CreateFontAsset(f, 72, 8, GlyphRenderMode.SDFAA, 1024, 1024);
        fa.TryAddCharacters(" ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.,!?'\"-:;“”’·›…");
        AssetDatabase.CreateAsset(fa, path);
        fa.material.name = "Atkinson-" + style + " SDF Material"; fa.atlasTexture.name = "Atkinson-" + style + " SDF Atlas";
        AssetDatabase.AddObjectToAsset(fa.material, fa);
        AssetDatabase.AddObjectToAsset(fa.atlasTexture, fa);
        EditorUtility.SetDirty(fa);
        return fa;
    }

    static RectTransform Rect(string name, Transform parent, Vector2 size, Vector2 pos)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = layer;
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size; rt.anchoredPosition = pos;
        return rt;
    }

    static void StretchOut(RectTransform rt, float pad)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(-pad, -pad); rt.offsetMax = new Vector2(pad, pad);
    }

    /// Wash image that overhangs its parent by the painter margin so the wobbly edge is not clipped.
    static Image Wash(Transform parent, Sprite s, bool sliced, float alpha)
    {
        var rt = Rect("Wash", parent, Vector2.zero, Vector2.zero);
        StretchOut(rt, M);
        var im = rt.gameObject.AddComponent<Image>();
        im.sprite = s; im.type = sliced ? Image.Type.Sliced : Image.Type.Simple; im.raycastTarget = false;
        im.color = new Color(1, 1, 1, alpha);
        return im;
    }

    static TMP_Text Txt(string name, Transform parent, string s, TMP_FontAsset f, float size, Color col, TextAlignmentOptions al, Vector2 box, Vector2 pos)
    {
        var rt = Rect(name, parent, box, pos);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (f != null) t.font = f;
        t.text = s; t.fontSize = size; t.color = col; t.alignment = al;
        t.enableWordWrapping = true; t.raycastTarget = false;
        return t;
    }

    static CanvasGroup Group(RectTransform rt) { var g = rt.gameObject.AddComponent<CanvasGroup>(); g.blocksRaycasts = true; return g; }

    static Button Pill(string name, Transform parent, Vector2 size, Vector2 pos, Sprite wash, Sprite pencil)
    {
        var rt = Rect(name, parent, size, pos);
        var hit = rt.gameObject.AddComponent<Image>(); hit.color = new Color(1, 1, 1, 0);
        var b = rt.gameObject.AddComponent<Button>(); b.transition = Selectable.Transition.None; b.targetGraphic = hit;
        var w = Wash(rt, wash, false, 1f);
        var p = Wash(rt, pencil, false, 1f); p.name = "Pencil";
        var wb = rt.gameObject.AddComponent<WashButton>(); wb.wash = w; wb.pencil = p;
        return b;
    }

    [MenuItem("StoryBricks/Build Watercolour UI")]
    public static void Build()
    {
        var log = new System.Text.StringBuilder();
        AssetDatabase.Refresh();
        reg = FontAsset("Regular"); bold = FontAsset("Bold"); ital = FontAsset("Italic");
        log.Append("fonts=" + (reg != null) + (bold != null) + (ital != null) + "; ");

        // ---- Sprites (palette A · peach and sage) ----
        Color peach = Hex("F7D3B5");
        var sPanel = WatercolourPainter.Wash("wash_panel", 340, 196, 12, Hex("FBF4EA"), Line, 1, true);
        var sMenu = WatercolourPainter.Wash("wash_button_menu", 300, 58, 26, peach, Line, 2, false);
        var pMenu = WatercolourPainter.Pencil("pencil_button_menu", 300, 58, 26, Line, 2);
        var pMenu2 = WatercolourPainter.Pencil("pencil_button_menu_b", 300, 58, 26, Line, 9);
        var sPrim = WatercolourPainter.Wash("wash_button_primary", 144, 40, 20, peach, Line, 3, false);
        var pPrim = WatercolourPainter.Pencil("pencil_button_primary", 144, 40, 20, Line, 3);
        var sSec = WatercolourPainter.Wash("wash_button_secondary", 84, 40, 20, Color.white, Line, 4, false);
        var pSec = WatercolourPainter.Pencil("pencil_button_secondary", 84, 40, 20, Line, 4);
        var sTag = WatercolourPainter.Wash("wash_tag", 66, 28, 10, Hex("F4E2A8"), Hex("B09A5E"), 5, true);
        var sCap = WatercolourPainter.Wash("wash_caption", 208, 35, 10, Hex("D5E3E0"), Hex("8AA29D"), 6, true);
        var sHint = WatercolourPainter.Wash("wash_hint", 318, 52, 10, Hex("DDE6CF"), Hex("8E9C7C"), 7, true);
        var sSub = WatercolourPainter.Wash("wash_subtitle", 198, 35, 10, Hex("E8E2DC"), Hex("A39A90"), 8, true);
        var sReady = WatercolourPainter.Wash("wash_ready", 230, 44, 10, Hex("DDE6CF"), Hex("8E9C7C"), 10, false);
        var sDot = WatercolourPainter.Dot("dot");
        var sRestart = WatercolourPainter.Wash("wash_button_restart", 96, 40, 20, Color.white, Line, 12, false);
        var pRestart = WatercolourPainter.Pencil("pencil_button_restart", 96, 40, 20, Line, 12);
        var sVideo = WatercolourPainter.Wash("mask_video", 400, 225, 14, Hex("FBF8F1"), Hex("B0A698"), 13, false);
        log.Append("sprites ok; ");

        // ---- Find the panel ----
        GameObject panel = null;
        foreach (var g in SceneManager.GetActiveScene().GetRootGameObjects()) if (g.name == "ParentUI") panel = g;
        if (panel == null) { Debug.LogError("StoryBricks: no ParentUI object in the open scene."); return; }
        var canvas = panel.GetComponentInChildren<Canvas>(true);
        var ct = canvas.transform;
        layer = canvas.gameObject.layer;
        var old = ct.Find("ViewerUI"); if (old != null) Object.DestroyImmediate(old.gameObject);
        foreach (Transform ch in ct) if (ch.name == "Screens" || ch.name == "DebugBar") ch.gameObject.SetActive(false);
        var pc = panel.GetComponent<ParentUIController>(); if (pc != null) pc.enabled = false;
        foreach (var gr in canvas.GetComponentsInChildren<Graphic>(true)) if (gr.name.Contains("Backplate")) gr.enabled = false;

        var root = Rect("ViewerUI", ct, Vector2.zero, Vector2.zero);
        root.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one; root.offsetMin = root.offsetMax = Vector2.zero;
        root.localScale = Vector3.one * UiScale;   // everything in the viewer UI grows together
        var flow = root.gameObject.AddComponent<ViewerFlow>();
        flow.dotSprite = sDot; flow.ink = Ink;

        // ---- Word tags (top) ----
        var tagRow = Rect("Tags", root, new Vector2(600, 40), Vector2.zero);
        tagRow.anchorMin = tagRow.anchorMax = new Vector2(0.5f, 1f); tagRow.anchoredPosition = new Vector2(0, -44);
        var hl = tagRow.gameObject.AddComponent<HorizontalLayoutGroup>();
        hl.childAlignment = TextAnchor.MiddleCenter; hl.spacing = 16;
        hl.childControlWidth = false; hl.childControlHeight = false; hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
        flow.tags = new ViewerFlow.Tag[6];
        for (int i = 0; i < 6; i++)
        {
            var t = Rect("Tag " + (i + 1), tagRow, new Vector2(66, 28), Vector2.zero);
            var tag = new ViewerFlow.Tag { root = t, group = Group(t) };
            var hit = t.gameObject.AddComponent<Image>(); hit.color = new Color(1, 1, 1, 0);
            var tb = t.gameObject.AddComponent<Button>(); tb.transition = Selectable.Transition.None; tb.targetGraphic = hit;
            t.gameObject.AddComponent<WashButton>();
            UnityEventTools.AddIntPersistentListener(tb.onClick, flow.ToggleTag, i);
            tag.wash = Wash(t, sTag, true, 0.9f);
            tag.label = Txt("Label", t, "JUDY", bold, 13, Ink, TextAlignmentOptions.Center, Vector2.zero, Vector2.zero);
            StretchOut(tag.label.rectTransform, 0); tag.label.characterSpacing = 11; tag.label.enableWordWrapping = false;
            tag.braille = Rect("Braille", t, new Vector2(60, 16), Vector2.zero);
            flow.tags[i] = tag;
            t.gameObject.SetActive(false);
        }

        // ---- Story stage: painted cut-outs (centre) ----
        var sRing = WatercolourPainter.Ring("ring");
        var st = Rect("Stage", root, new Vector2(420, 240), new Vector2(0, 8));
        var stage = st.gameObject.AddComponent<StoryStage>();
        stage.group = Group(st); stage.group.blocksRaycasts = false; stage.group.interactable = false;
        flow.stage = stage;
        Color humBlue = Hex("8FA9C4");
        stage.hill = Art("Hill", st, "hill", 380, new Vector2(0, -78));
        var windRt = Rect("Wind", st, Vector2.zero, new Vector2(-5, 80)); stage.wind = windRt;
        stage.windOutline = Art("Outline", windRt, "wind_outline", 130, Vector2.zero);
        stage.windColour = Art("Colour", windRt, "wind_colour", 130, Vector2.zero); Filled(stage.windColour);
        var moonRt = Rect("Moon", st, Vector2.zero, new Vector2(125, 58)); stage.moon = moonRt;
        stage.rings = new Image[3];
        for (int i = 0; i < 3; i++)
        {
            var rg = Rect("Ring " + (i + 1), st, new Vector2(84, 84), new Vector2(125, 58));
            var rim = rg.gameObject.AddComponent<Image>(); rim.sprite = sRing; rim.raycastTarget = false; rim.color = new Color(humBlue.r, humBlue.g, humBlue.b, 0f);
            stage.rings[i] = rim;
        }
        stage.moonOutline = Art("Outline", moonRt, "moon_outline", 76, Vector2.zero);
        stage.moonDough = Art("Dough", moonRt, "moon_dough", 84, Vector2.zero); Filled(stage.moonDough);
        var strRt = Rect("String", st, new Vector2(10, 1.3f), Vector2.zero); strRt.pivot = new Vector2(0f, 0.5f);
        stage.stringLine = strRt.gameObject.AddComponent<Image>(); stage.stringLine.raycastTarget = false; stage.stringLine.color = new Color(Line.r, Line.g, Line.b, 0.9f);
        var dotRt = Rect("Ripple", st, new Vector2(8, 8), Vector2.zero);
        stage.rippleDot = dotRt.gameObject.AddComponent<Image>(); stage.rippleDot.sprite = sDot; stage.rippleDot.raycastTarget = false; stage.rippleDot.color = new Color(humBlue.r, humBlue.g, humBlue.b, 0f);
        var kiteRt = Rect("Kite", st, Vector2.zero, new Vector2(30, 58)); stage.kite = kiteRt;
        stage.tail = Art("Tail", kiteRt, "kite_tail", 6.8f, new Vector2(0, -26)); stage.tail.rectTransform.pivot = new Vector2(0.5f, 1f);
        stage.kiteOutline = Art("Outline", kiteRt, "kite_outline", 44, Vector2.zero);
        stage.kiteColour = Art("Colour", kiteRt, "kite_colour", 46, Vector2.zero); Filled(stage.kiteColour);
        stage.judyStand = Art("Judy standing", st, "judy_stand", 46.3f, new Vector2(-95, -5));
        stage.judyHold = Art("Judy holding", st, "judy_hold", 64.3f, new Vector2(-92, -5));
        stage.handPoint = new Vector2(-68, 24);
        stage.kiteBuild = new Vector2(30, 58); stage.kiteRest = new Vector2(-40, 46); stage.kiteHigh = new Vector2(38, 54); stage.kiteAtMoon = new Vector2(104, 46);
        EditorUtility.SetDirty(stage);

        // ---- Story video (centre) ----
        const string VideoPath = "Assets/StoryBricks/Video/story.mp4", RtPath = "Assets/StoryBricks/Video/StoryVideo.renderTexture";
        var clip = AssetDatabase.LoadAssetAtPath<VideoClip>(VideoPath);
        var rtAsset = AssetDatabase.LoadAssetAtPath<RenderTexture>(RtPath);
        if (rtAsset == null) { rtAsset = new RenderTexture(854, 480, 0); rtAsset.name = "StoryVideo"; AssetDatabase.CreateAsset(rtAsset, RtPath); }
        var vp = root.gameObject.AddComponent<VideoPlayer>();
        vp.playOnAwake = false; vp.source = VideoSource.VideoClip; vp.clip = clip; vp.isLooping = false;
        vp.renderMode = VideoRenderMode.RenderTexture; vp.targetTexture = rtAsset; vp.waitForFirstFrame = true;
        var vid = Rect("Video", root, new Vector2(400, 225), new Vector2(0, 8));
        flow.videoGroup = Group(vid); flow.video = vp;
        var frame = Wash(vid, sVideo, false, 1f); frame.name = "Frame";
        frame.gameObject.AddComponent<Mask>().showMaskGraphic = true;
        var screen = Rect("Screen", frame.transform, Vector2.zero, Vector2.zero);
        screen.anchorMin = Vector2.zero; screen.anchorMax = Vector2.one; screen.offsetMin = new Vector2(M, M); screen.offsetMax = new Vector2(-M, -M);
        var raw = screen.gameObject.AddComponent<RawImage>(); raw.texture = rtAsset; raw.raycastTarget = false;
        log.Append("video=" + (clip != null) + "; ");

        // ---- Start menu ----
        var menu = Rect("Menu", root, new Vector2(340, 264), Vector2.zero);
        flow.menu = Group(menu);
        Wash(menu, sPanel, true, 0.92f);
        Txt("Title", menu, "What would you like to do?", bold, 16, Ink, TextAlignmentOptions.Left, new Vector2(300, 22), new Vector2(0, 103));
        flow.newStoryButton = MenuButton(menu, "New story", "Make a new story", "Pinch twice to start", 49, sMenu, pMenu);
        flow.savedStoryButton = MenuButton(menu, "Saved story", "Play a saved story", "Watch and listen", -19, sMenu, pMenu2);
        flow.tutorialButton = MenuButton(menu, "Tutorial", "Try a practice word", "Learn how with the word FOX", -87, sMenu, pMenu);
        UnityEventTools.AddPersistentListener(flow.tutorialButton.onClick, flow.OnTutorial);
        UnityEventTools.AddPersistentListener(flow.newStoryButton.onClick, flow.OnNewStory);
        UnityEventTools.AddPersistentListener(flow.savedStoryButton.onClick, flow.OnSavedStory);

        // ---- Hint ----
        var hint = Rect("Hint", root, new Vector2(318, 52), Vector2.zero);
        flow.hint = Group(hint);
        Wash(hint, sHint, true, 0.9f);
        flow.hintText = Txt("Text", hint, "Judy is the storyteller. Watch her story grow above the bricks.", reg, 14, Soft, TextAlignmentOptions.Left, new Vector2(290, 40), Vector2.zero);

        // ---- Tutorial fox (pops up when the practice word FOX is finished) ----
        var foxRt = Rect("Fox", root, new Vector2(150, 195), new Vector2(0, 0));
        flow.fox = Group(foxRt);
        flow.foxSit = Art("Sit", foxRt, "fox_sit", 140, Vector2.zero);
        flow.foxYip = Art("Yip", foxRt, "fox_yip", 140, Vector2.zero);
        flow.foxYip.enabled = false;

        // ---- Tutorial bricks (F, O, X appear one by one) and the pinch-twice hand ----
        var bricksRt = Rect("Bricks", root, new Vector2(300, 110), new Vector2(0, 30));
        flow.bricks = Group(bricksRt); flow.bricks.blocksRaycasts = false;
        flow.brickImages = new[]
        {
            Art("F", bricksRt, "brick_f", 84, new Vector2(-92, 0)),
            Art("O", bricksRt, "brick_o", 84, new Vector2(0, 0)),
            Art("X", bricksRt, "brick_x", 84, new Vector2(92, 0))
        };
        var periodRt = Rect("Period", root, new Vector2(110, 110), new Vector2(0, 30));
        flow.period = Group(periodRt); flow.period.blocksRaycasts = false;
        Art("Brick", periodRt, "brick_period", 84, Vector2.zero);
        var pinchRt = Rect("Pinch", root, new Vector2(300, 80), new Vector2(0, -80));
        flow.pinch = Group(pinchRt); flow.pinch.blocksRaycasts = false;
        Wash(pinchRt, sHint, true, 0.9f);
        flow.pinchOpen = Art("Open", pinchRt, "hand_open", 52, new Vector2(-88, 0));
        flow.pinchClosed = Art("Closed", pinchRt, "hand_pinch", 52, new Vector2(-88, 0));
        flow.pinchClosed.enabled = false;
        flow.pinchText = Txt("Text", pinchRt, "Pinch twice to finish the word", reg, 14, Soft, TextAlignmentOptions.Left, new Vector2(210, 22), new Vector2(52, 0));

        // ---- Story ready (wash fill) ----
        var ready = Rect("Ready", root, new Vector2(230, 44), new Vector2(0, -150));
        flow.ready = Group(ready);
        flow.readyWash = Wash(ready, sReady, false, 0.9f);
        flow.readyWash.type = Image.Type.Filled; flow.readyWash.fillMethod = Image.FillMethod.Horizontal; flow.readyWash.fillOrigin = 0; flow.readyWash.fillAmount = 1f;
        Txt("Text", ready, "Judy’s story is ready.", bold, 15, Ink, TextAlignmentOptions.Center, new Vector2(220, 24), Vector2.zero);

        // ---- Caption, subtitle, listening pulse (bottom) ----
        Vector2 bottom = new Vector2(0, -150);
        var cap = Rect("Caption", root, new Vector2(208, 35), bottom);
        flow.caption = Group(cap); flow.captionBox = cap;
        Wash(cap, sCap, true, 0.9f);
        flow.captionText = Txt("Text", cap, "“It hums, like the fridge at night.”", ital, 14, Soft, TextAlignmentOptions.Center, Vector2.zero, Vector2.zero);
        StretchOut(flow.captionText.rectTransform, -12);

        var sub = Rect("Subtitle", root, new Vector2(198, 35), bottom);
        flow.subtitle = Group(sub); flow.subtitleBox = sub;
        Wash(sub, sSub, true, 0.92f);
        flow.subtitleText = Txt("Text", sub, "Once upon a time, there was a kid named Judy.", reg, 14, Soft, TextAlignmentOptions.Center, Vector2.zero, Vector2.zero);
        StretchOut(flow.subtitleText.rectTransform, -12);

        var pulse = Rect("Listening pulse", root, new Vector2(80, 20), bottom);
        flow.pulse = Group(pulse);
        flow.pulseDots = new RectTransform[4]; flow.pulseImages = new Image[4];
        for (int i = 0; i < 4; i++)
        {
            var d = Rect("Dot " + (i + 1), pulse, new Vector2(11, 11), new Vector2(-30 + i * 20, 0));
            var im = d.gameObject.AddComponent<Image>(); im.sprite = sDot; im.raycastTarget = false;
            var c = Ink; c.a = i == 0 ? 0.85f : 0.18f; im.color = c;
            flow.pulseDots[i] = d; flow.pulseImages[i] = im;
        }

        // ---- End ----
        var end = Rect("End", root, new Vector2(390, 150), Vector2.zero);
        flow.end = Group(end);
        Wash(end, sPanel, true, 0.92f);
        Txt("Title", end, "Judy’s story", bold, 18, Ink, TextAlignmentOptions.Center, new Vector2(300, 26), new Vector2(0, 44));
        flow.endWords = Txt("Words", end, "Judy · Moon · Wind · Kite", reg, 14, Soft, TextAlignmentOptions.Center, new Vector2(300, 20), new Vector2(0, 18));
        flow.watchAgainButton = Pill("Watch again", end, new Vector2(144, 40), new Vector2(-100, -36), sPrim, pPrim);
        var wl = Txt("Label", flow.watchAgainButton.transform, "Watch again", bold, 14, Ink, TextAlignmentOptions.Center, new Vector2(100, 20), new Vector2(-8, 0));
        Txt("Arrow", flow.watchAgainButton.transform, "›", reg, 18, Line, TextAlignmentOptions.Center, new Vector2(12, 24), new Vector2(48, 1));
        flow.doneButton = Pill("Done", end, new Vector2(84, 40), new Vector2(130, -36), sSec, pSec);
        Txt("Label", flow.doneButton.transform, "Done", reg, 14, Ink, TextAlignmentOptions.Center, new Vector2(70, 20), Vector2.zero);
        flow.restartButton = Pill("Restart", end, new Vector2(96, 40), new Vector2(30, -36), sRestart, pRestart);
        Txt("Label", flow.restartButton.transform, "Restart", reg, 14, Ink, TextAlignmentOptions.Center, new Vector2(80, 20), Vector2.zero);
        UnityEventTools.AddPersistentListener(flow.restartButton.onClick, flow.OnRestart);

        // ---- Finish (while a new story is being built) ----
        flow.finishButton = Pill("Finish", root, new Vector2(96, 40), new Vector2(0, -205), sRestart, pRestart);
        Txt("Label", flow.finishButton.transform, "Finish", reg, 14, Ink, TextAlignmentOptions.Center, new Vector2(80, 20), Vector2.zero);
        flow.finish = Group((RectTransform)flow.finishButton.transform);
        UnityEventTools.AddPersistentListener(flow.finishButton.onClick, flow.OnFinish);
        flow.endButtons = new[] { flow.watchAgainButton.gameObject.AddComponent<CanvasGroup>(), flow.restartButton.gameObject.AddComponent<CanvasGroup>(), flow.doneButton.gameObject.AddComponent<CanvasGroup>() };
        UnityEventTools.AddPersistentListener(flow.watchAgainButton.onClick, flow.OnWatchAgain);
        UnityEventTools.AddPersistentListener(flow.doneButton.onClick, flow.OnDone);

        // ---- Sound ----
        const string Sfx = "Assets/StoryBricks/Audio/SFX/";
        flow.clickClip = AssetDatabase.LoadAssetAtPath<AudioClip>(Sfx + "11_pinch_click.mp3");
        flow.bloopClip = AssetDatabase.LoadAssetAtPath<AudioClip>(Sfx + "03_rising_bloop.mp3");
        flow.sparkleClip = AssetDatabase.LoadAssetAtPath<AudioClip>(Sfx + "04_sparkle.mp3");
        flow.successClip = AssetDatabase.LoadAssetAtPath<AudioClip>(Sfx + "06_success_melody.mp3");
        flow.videoAudioClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/StoryBricks/Audio/story_video_audio.wav");
        log.Append("videoAudio=" + (flow.videoAudioClip != null) + "; ");
        int clips = 0;
        foreach (var c in new[] { flow.clickClip, flow.bloopClip, flow.sparkleClip, flow.successClip }) if (c != null) clips++;
        log.Append("clips=" + clips + "/4; ");

        // ---- Demo narration (from the Google Sheet) ----
        flow.demoWords = new[] { "JUDY", "MOON", "WIND", "KITE" };
        flow.narration = new[]
        {
            "Once upon a time, there was a kid named Judy.",
            "Judy had never touched the moon, so she imagined one. Soft and cold, like bread dough.",
            "And every night it hummed, like the fridge in the dark.",
            "“I want to feel it,” said Judy. So she got her kite.",
            "Whoosh! The wind pushed her hair and filled her ears.",
            "Up went the kite. The string pulled, like a dog on a leash.",
            "Jingle, jingle, went the bells on its tail.",
            "Higher, and higher. The jingling got smaller, and smaller, until… bump.",
            "The bells stopped. Then the string began to hum.",
            "Judy held on tight. Through the string she could feel it. Soft. Cold. Humming.",
            "“Hello, moon,” said Judy. And the string gave one little tug back.",
            "Judy wound the string in, slowly. Jingle, jingle, all the way home. The end."
        };
        // Line start times measured from the narration recording (pauses between sentences).
        flow.narrationStart = new[] { 0f, 3.67f, 10.84f, 14.54f, 18.77f, 22.95f, 27.10f, 29.89f, 36.59f, 40.74f, 47.99f, 53.14f };
        flow.narrationWordName = new[] { "JUDY", "MOON", "MOON", "KITE", "WIND", "KITE", "KITE", "KITE", "KITE", "MOON", "MOON", "KITE" };
        flow.storyEnd = 61.5f;
        flow.narrationClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/StoryBricks/Audio/Voice/narration_judy_moon.mp3");
        flow.systemVoice = Voices("Assets/StoryBricks/Audio/Voice/System", 26);
        flow.judyVoice = Voices("Assets/StoryBricks/Audio/Voice/Judy", 8);
        flow.foxYipClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/StoryBricks/Audio/SFX/08_fox_yip.mp3");
        flow.storySfxClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/StoryBricks/Audio/SFX/story_sfx_judy_moon.wav");
        log.Append("storySfx=" + (flow.storySfxClip != null) + "; ");
        log.Append("voices=" + System.Array.FindAll(flow.systemVoice, c => c != null).Length + "+" + System.Array.FindAll(flow.judyVoice, c => c != null).Length + " yip=" + (flow.foxYipClip != null) + "; ");
        log.Append("narration=" + (flow.narrationClip != null) + "; ");

        // Edit-mode default: only the menu is visible.
        foreach (var g in new[] { flow.hint, flow.ready, flow.caption, flow.subtitle, flow.pulse, flow.end, flow.videoGroup, stage.group, flow.fox, flow.bricks, flow.pinch, flow.period, flow.finish }) g.gameObject.SetActive(false);

        EditorUtility.SetDirty(flow);
        var scene = panel.scene;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        File.WriteAllText("Assets/StoryBricks/watercolour_log.txt", log + "built " + System.DateTime.Now + " scene=" + scene.path);
        Debug.Log("StoryBricks: watercolour UI built. " + log);
    }

    /// Numbered voice lines from a folder: "07_name.mp3" goes to element 6.
    static AudioClip[] Voices(string folder, int count)
    {
        var clips = new AudioClip[count];
        if (!Directory.Exists(folder)) return clips;
        foreach (var f in Directory.GetFiles(folder, "*.mp3"))
        {
            string n = Path.GetFileName(f);
            int i;
            if (n.Length > 2 && int.TryParse(n.Substring(0, 2), out i) && i >= 1 && i <= count)
                clips[i - 1] = AssetDatabase.LoadAssetAtPath<AudioClip>(f.Replace('\\', '/'));
        }
        return clips;
    }

    /// A painted cut-out from Assets/StoryBricks/Art, sized by width with its own proportions.
    static Image Art(string name, Transform parent, string file, float width, Vector2 pos)
    {
        string path = "Assets/StoryBricks/Art/" + file + ".png";
        var imp = AssetImporter.GetAtPath(path) as TextureImporter;
        if (imp != null && (imp.textureType != TextureImporterType.Sprite || !imp.mipmapEnabled))
        {
            imp.textureType = TextureImporterType.Sprite; imp.spriteImportMode = SpriteImportMode.Single;
            imp.alphaIsTransparency = true; imp.mipmapEnabled = true; imp.filterMode = FilterMode.Trilinear;
            imp.wrapMode = TextureWrapMode.Clamp; imp.maxTextureSize = 1024;
            var ts = new TextureImporterSettings(); imp.ReadTextureSettings(ts); ts.spriteMeshType = SpriteMeshType.FullRect; imp.SetTextureSettings(ts);
            imp.SaveAndReimport();
        }
        var sp = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (sp == null) Debug.LogError("StoryBricks: missing art " + path);
        float h = sp != null ? width * sp.rect.height / sp.rect.width : width;
        var rt = Rect(name, parent, new Vector2(width, h), pos);
        var im = rt.gameObject.AddComponent<Image>(); im.sprite = sp; im.raycastTarget = false; im.preserveAspect = false;
        return im;
    }

    static void Filled(Image im) { im.type = Image.Type.Filled; im.fillMethod = Image.FillMethod.Horizontal; im.fillOrigin = 0; im.fillAmount = 1f; }

    static Button MenuButton(Transform parent, string name, string title, string sub, float y, Sprite wash, Sprite pencil)
    {
        var b = Pill(name, parent, new Vector2(300, 58), new Vector2(0, y), wash, pencil);
        Txt("Title", b.transform, title, bold, 15, Ink, TextAlignmentOptions.Left, new Vector2(230, 20), new Vector2(-13, 9));
        Txt("Sub", b.transform, sub, reg, 12, Soft, TextAlignmentOptions.Left, new Vector2(230, 16), new Vector2(-13, -11));
        Txt("Arrow", b.transform, "›", reg, 18, Line, TextAlignmentOptions.Center, new Vector2(12, 24), new Vector2(124, 1));
        return b;
    }
}
