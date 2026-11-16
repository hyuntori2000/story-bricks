using System;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

/// Creates an independent, headset-free scene using the original tutorial art and audio.
/// This only changes the generated preview scene, never ParentUI or its prefab.
public static class TutorialPreviewBuilder
{
    public const string ScenePath = "Assets/StoryBricks/Scenes/TutorialPreview.unity";

    [MenuItem("StoryBricks/Create Desktop Tutorial Preview")]
    public static void Build()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.OpenScene("Assets/StoryBricks/Scenes/ParentUI.unity");
        var flow = UnityEngine.Object.FindFirstObjectByType<ViewerFlow>(FindObjectsInactive.Include);
        var opening = UnityEngine.Object.FindFirstObjectByType<OpeningAnimation>(FindObjectsInactive.Include);
        if (flow == null || opening == null) throw new InvalidOperationException("The source scene must contain ViewerFlow and OpeningAnimation.");

        // Detach the finished viewer presentation from the original XR canvas hierarchy.
        var prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(flow.gameObject);
        if (prefabRoot != null) PrefabUtility.UnpackPrefabInstance(prefabRoot, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        var oldRoots = scene.GetRootGameObjects();
        var canvasGo = new GameObject("Desktop Tutorial", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 800);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        flow.transform.SetParent(canvasGo.transform, false);
        var rect = (RectTransform)flow.transform;
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
        rect.localScale = Vector3.one * 1.5f;
        opening.showWhenDone = canvasGo;
        opening.transform.SetParent(null, true);
        foreach (var root in oldRoots)
            if (root != opening.gameObject && root != flow.gameObject) UnityEngine.Object.DestroyImmediate(root);

        var cameraGo = new GameObject("Desktop Camera", typeof(Camera), typeof(AudioListener));
        cameraGo.tag = "MainCamera";
        cameraGo.transform.position = new Vector3(0, 1.6f, 0);
        var camera = cameraGo.GetComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.975f, 0.96f, 0.93f);
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 10f;
        camera.fieldOfView = 55;
        camera.stereoTargetEye = StereoTargetEyeMask.None;

        var eventGo = new GameObject("Desktop Input", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
        eventGo.AddComponent<InputSystemUIInputModule>().AssignDefaultActions();
#else
        eventGo.AddComponent<StandaloneInputModule>();
#endif
        flow.voiceStart = false;
        var serialized = new SerializedObject(flow);
        SetBool(serialized, "tutorialAfterOpening", true);
        SetBool(serialized, "autoPlayTutorial", true);
        serialized.ApplyModifiedPropertiesWithoutUndo();

        // A separate overlay keeps the controls visible during the opening as well.
        var helpGo = new GameObject("Desktop Controls", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        helpGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        helpGo.GetComponent<Canvas>().sortingOrder = 100;
        var helpScale = helpGo.GetComponent<CanvasScaler>();
        helpScale.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        helpScale.referenceResolution = new Vector2(1280, 800);
        helpScale.matchWidthOrHeight = 0.5f;
        var textGo = new GameObject("Controls", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGo.transform.SetParent(helpGo.transform, false);
        var helpRect = (RectTransform)textGo.transform;
        helpRect.anchorMin = new Vector2(0, 0); helpRect.anchorMax = new Vector2(1, 0);
        helpRect.pivot = new Vector2(0.5f, 0);
        helpRect.sizeDelta = new Vector2(-40, 54);
        helpRect.anchoredPosition = new Vector2(0, 8);
        var help = textGo.GetComponent<TextMeshProUGUI>();
        help.font = opening.font;
        help.fontSize = 15;
        help.color = new Color(0.35f, 0.31f, 0.27f);
        help.alignment = TextAlignmentOptions.Center;
        help.raycastTarget = false;
        help.text = "DESKTOP PREVIEW  ·  Click the Game view to use the keyboard\nP = double pinch (skip tutorial)   ·   T = replay from menu   ·   Space = next story beat";

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("Tutorial preview saved: " + ScenePath + ". Press Play; no headset required.");
    }

    static void SetBool(SerializedObject target, string name, bool value)
    {
        var property = target.FindProperty(name);
        if (property != null) property.boolValue = value;
    }
}
