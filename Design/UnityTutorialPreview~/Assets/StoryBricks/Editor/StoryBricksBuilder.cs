using System;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public static class StoryBricksBuilder
{
    const string UISet = "Packages/com.meta.xr.sdk.interaction/Runtime/Sample/Objects/UISet/Prefabs/";
    const string Backplate = UISet + "Backplate/EmptyUIBackplateWithCanvas.prefab";
    const string Primary = UISet + "Button/UnityUIButtonBased/PrimaryButton_IconAndLabel_UnityUIButton.prefab";
    const string Secondary = UISet + "Button/UnityUIButtonBased/SecondaryButton_IconAndLabel_UnityUIButton.prefab";
    const string Rig = "Packages/com.meta.xr.sdk.interaction.ovr/Runtime/Prefabs/OVRComprehensiveInteractionRig.prefab";
    const string CamRig = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
    const float W = 640, H = 420;
    static TMP_FontAsset font;

    static Type FindType(string name)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
            .FirstOrDefault(t => t.FullName == name || t.Name == name);
    }

    static GameObject Spawn(string path, Transform parent = null)
    {
        var p = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (p == null) { Debug.LogError("StoryBricks: missing prefab " + path); return null; }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(p);
        if (parent != null) go.transform.SetParent(parent, false);
        return go;
    }

    [MenuItem("StoryBricks/Build Parent UI Scene")]
    public static void Build()
    {
        var log = new System.Text.StringBuilder();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>(); light.type = LightType.Directional;
        lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);

        // ---- Rig with passthrough ----
        var rig = Spawn(Rig);
        Component mgr = null;
        var mgrType = FindType("OVRManager");
        if (rig != null && mgrType != null) mgr = rig.GetComponentInChildren(mgrType, true);
        if (mgr == null)
        {
            var cam = Spawn(CamRig);
            if (cam != null) { if (rig != null) rig.transform.SetParent(cam.transform, false); mgr = cam.GetComponentInChildren(mgrType, true); rig = cam; }
        }
        log.Append("rig=" + (rig != null) + " ovrManager=" + (mgr != null) + "; ");
        try
        {
            if (mgr != null)
            {
                var f = mgrType.GetField("isInsightPassthroughEnabled");
                if (f != null) f.SetValue(mgr, true);
                else { var p = mgrType.GetProperty("isInsightPassthroughEnabled"); if (p != null) p.SetValue(mgr, true, null); }
                var layerType = FindType("OVRPassthroughLayer");
                if (layerType != null && mgr.gameObject.GetComponent(layerType) == null)
                {
                    var layer = mgr.gameObject.AddComponent(layerType);
                    var ot = layerType.GetField("overlayType");
                    if (ot != null) ot.SetValue(layer, Enum.Parse(ot.FieldType, "Underlay"));
                }
                foreach (var c in mgr.GetComponentsInChildren<Camera>(true))
                { c.clearFlags = CameraClearFlags.SolidColor; c.backgroundColor = new Color(0, 0, 0, 0); }
                log.Append("passthrough on; ");
            }
        }
        catch (Exception e) { log.Append("passthrough err " + e.Message + "; "); }

        // ---- EventSystem for UI Set (poke + ray) ----
        var es = new GameObject("EventSystem", typeof(EventSystem));
        var pcm = FindType("Oculus.Interaction.PointableCanvasModule");
        if (pcm != null) es.AddComponent(pcm); else log.Append("no PointableCanvasModule; ");

        // ---- Panel ----
        var panel = Spawn(Backplate);
        if (panel == null) return;
        panel.name = "ParentUI";
        panel.transform.position = new Vector3(0, 1.15f, 0.6f);
        panel.transform.localScale = Vector3.one * 1.6f;
        var canvas = panel.GetComponentInChildren<Canvas>(true);
        var canvasRt = canvas.GetComponent<RectTransform>();
        canvasRt.sizeDelta = new Vector2(W, H);
        foreach (var rt in canvas.GetComponentsInChildren<RectTransform>(true))
            if (rt.name == "UIBackplate") rt.sizeDelta = new Vector2(W, H);

        var probe = AssetDatabase.LoadAssetAtPath<GameObject>(Primary);
        if (probe != null) { foreach (var t in probe.GetComponentsInChildren<TMP_Text>(true)) if (t.font != null) { font = t.font; break; } }
        if (font == null) { var g = AssetDatabase.FindAssets("t:TMP_FontAsset"); if (g.Length > 0) font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(g[0])); }
        log.Append("font=" + (font != null ? font.name : "null") + "; ");

        var ctrl = panel.AddComponent<ParentUIController>();
        var screensRoot = NewRect("Screens", canvas.transform); screensRoot.gameObject.AddComponent<LayoutElement>().ignoreLayout = true; Stretch(screensRoot, 0);

        // ---- 11 screens ----
        Screen(ctrl, screensRoot, "01 Camera permission", new[] { "session_start" }, null,
            "Allow the headset camera", "Story Bricks reads the printed letters on the bricks. Camera images aren't saved.",
            new[] { "Not now", "Allow" }, new UnityAction[] { ctrl.Next, ctrl.Next });
        Screen(ctrl, screensRoot, "02 Find the baseplate", new[] { "find_plate" }, "Finding the baseplate...",
            "Look at the bricks on the table", null, null, null);
        Screen(ctrl, screensRoot, "03 Child is building", new[] { "building" }, "Judy is building",
            null, "No rush. She'll say \"done\" when a word is ready.", null, null);
        Screen(ctrl, screensRoot, "04 Look at the bricks", new[] { "need_look" }, "Waiting for you",
            "Look down at the bricks", null, null, null);
        Screen(ctrl, screensRoot, "05 Word read", new[] { "word_read" }, "Word read",
            "JUDY", null, null, null, 96);
        Screen(ctrl, screensRoot, "06 Brick check", new[] { "brick_check" }, "Checking one brick",
            "One brick may be upside down", "Judy is checking it by touch.", null, null);
        Screen(ctrl, screensRoot, "07 Question + listening", new[] { "ask", "listen_start" }, "Listening to Judy",
            "\"What does your fox sound like?\"", null, null, null);
        Screen(ctrl, screensRoot, "08 Beat appears", new[] { "beat_add", "thinking" }, "Adding to the story",
            "\"It goes yip yip when it runs.\"", "Judy's words", null, null);
        Screen(ctrl, screensRoot, "09 Parent prompt", new[] { "parent_prompt" }, "Your turn: ask Judy",
            "Where does the fox go when it rains?", null,
            new[] { "Skip", "Listen" }, new UnityAction[] { ctrl.GoToBuilding, ctrl.Next });
        Screen(ctrl, screensRoot, "10 Story playing", new[] { "story_start", "beat_start" }, "Story - beat 3 of 5",
            "\"Tap tap tap, like tiny drums.\"", "Judy's words", null, null);
        Screen(ctrl, screensRoot, "11 Story end", new[] { "story_end" }, null,
            "Judy's story", "Read the words together: JUDY, FOX, RAIN, TREE",
            new[] { "Finish", "Play again" }, new UnityAction[] { ctrl.Restart, ctrl.GoToStory });

        // ---- Debug bar (always visible) ----
        var bar = NewRect("DebugBar", canvas.transform);
        bar.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        bar.anchorMin = new Vector2(0, 0); bar.anchorMax = new Vector2(1, 0); bar.pivot = new Vector2(0.5f, 0);
        bar.sizeDelta = new Vector2(-32, 56); bar.anchoredPosition = new Vector2(0, 12);
        var hl = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
        hl.childAlignment = TextAnchor.MiddleCenter; hl.spacing = 16; hl.childControlWidth = false; hl.childControlHeight = false; hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
        Btn(Secondary, bar, "< Prev", ctrl.Prev);
        Btn(Secondary, bar, "Next >", ctrl.Next);

        ctrl.ShowIndex(0);

        // ---- Save ----
        System.IO.Directory.CreateDirectory("Assets/StoryBricks/Prefabs");
        System.IO.Directory.CreateDirectory("Assets/StoryBricks/Scenes");
        PrefabUtility.SaveAsPrefabAssetAndConnect(panel, "Assets/StoryBricks/Prefabs/ParentUI.prefab", InteractionMode.AutomatedAction);
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, "Assets/StoryBricks/Scenes/ParentUI.unity");
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene("Assets/StoryBricks/Scenes/ParentUI.unity", true) };

        // ---- Player settings for Quest ----
        try
        {
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.storybricks.parentui");
            PlayerSettings.productName = "Story Bricks";
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)32;
        }
        catch (Exception e) { log.Append("player err " + e.Message + "; "); }
        try
        {
            var setup = FindType("OVRProjectSetup");
            if (setup != null) setup.GetMethod("FixAllAsync").Invoke(null, new object[] { BuildTargetGroup.Android });
        }
        catch (Exception e) { log.Append("fix err " + e.Message + "; "); }

        AssetDatabase.SaveAssets();
        System.IO.File.WriteAllText("Assets/StoryBricks/build_log.txt", log + "screens=" + ctrl.screens.Count + " done " + DateTime.Now);
        Debug.Log("StoryBricks: Parent UI built. " + log);
    }

    static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        go.layer = parent.gameObject.layer;
        return go.GetComponent<RectTransform>();
    }

    static void Stretch(RectTransform rt, float pad)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(pad, pad); rt.offsetMax = new Vector2(-pad, -pad);
    }

    static TMP_Text Text(Transform parent, string name, string s, float size, FontStyles style, Color color, float height)
    {
        var rt = NewRect(name, parent);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) t.font = font;
        t.text = s; t.fontSize = size; t.fontStyle = style; t.color = color;
        t.alignment = TextAlignmentOptions.Center; t.enableWordWrapping = true; t.raycastTarget = false;
        var le = rt.gameObject.AddComponent<LayoutElement>(); le.preferredHeight = height; le.preferredWidth = W - 96;
        return t;
    }

    static GameObject Btn(string prefab, Transform parent, string label, UnityAction action)
    {
        var go = Spawn(prefab, parent);
        if (go == null) return null;
        go.name = "Button - " + label;
        foreach (var t in go.GetComponentsInChildren<TMP_Text>(true))
        {
            if (t.name == "Label") t.text = label;
            else if (t.name == "Subtitle") t.gameObject.SetActive(false);
        }
        foreach (var tr in go.GetComponentsInChildren<Transform>(true))
            if (tr.name == "Icon") tr.gameObject.SetActive(false);
        var rt = go.GetComponent<RectTransform>(); rt.sizeDelta = new Vector2(170, 56);
        var le = go.GetComponent<LayoutElement>(); if (le == null) le = go.AddComponent<LayoutElement>();
        le.preferredWidth = 170; le.preferredHeight = 56;
        var b = go.GetComponentInChildren<Button>(true);
        if (b != null && action != null) UnityEventTools.AddPersistentListener(b.onClick, action);
        return go;
    }

    static void Screen(ParentUIController ctrl, RectTransform root, string id, string[] events, string chip,
        string title, string body, string[] buttons, UnityAction[] actions, float titleSize = 40)
    {
        var rt = NewRect(id, root);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(32, 84); rt.offsetMax = new Vector2(-32, -28);
        var vl = rt.gameObject.AddComponent<VerticalLayoutGroup>();
        vl.childAlignment = TextAnchor.MiddleCenter; vl.spacing = 18;
        vl.childControlWidth = true; vl.childControlHeight = true; vl.childForceExpandWidth = false; vl.childForceExpandHeight = false;

        if (!string.IsNullOrEmpty(chip)) Text(rt, "Status", chip, 22, FontStyles.Bold, new Color(0.45f, 0.72f, 1f), 30);
        if (!string.IsNullOrEmpty(title)) Text(rt, "Title", title, titleSize, FontStyles.Bold, Color.white, titleSize > 60 ? 120 : 100);
        if (!string.IsNullOrEmpty(body)) Text(rt, "Body", body, 22, FontStyles.Normal, new Color(0.82f, 0.82f, 0.85f), 64);
        if (buttons != null)
        {
            var row = NewRect("Buttons", rt);
            var hl = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.childAlignment = TextAnchor.MiddleCenter; hl.spacing = 20;
            hl.childControlWidth = false; hl.childControlHeight = false; hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            var le = row.gameObject.AddComponent<LayoutElement>(); le.preferredHeight = 60; le.preferredWidth = W - 96;
            for (int i = 0; i < buttons.Length; i++)
                Btn(i == buttons.Length - 1 ? Primary : Secondary, row, buttons[i], actions != null && i < actions.Length ? actions[i] : null);
        }
        ctrl.screens.Add(new ParentUIController.Screen { id = id, events = events, root = rt.gameObject });
    }
}
