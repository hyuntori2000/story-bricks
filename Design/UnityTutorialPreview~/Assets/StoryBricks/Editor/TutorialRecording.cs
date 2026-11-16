using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Run with the normal (non-batch) Editor so Game view renders and audio is available.
[InitializeOnLoad]
public static class TutorialRecording
{
    const string Key = "StoryBricks.RecordTutorial";
    static TutorialRecording()
    {
        EditorApplication.playModeStateChanged += OnPlayMode;
    }

    [MenuItem("StoryBricks/Record Tutorial Test")]
    public static void Run()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode before starting a recording.");
        EditorSceneManager.OpenScene(TutorialPreviewBuilder.ScenePath);
        var gameView = EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor"));
        SetCaptureSize(gameView, 1280, 800);
        gameView.titleContent = new GUIContent("Game");
        gameView.Show();
        gameView.maximized = true;
        gameView.Focus();
        SessionState.SetBool(Key, true);
        EditorApplication.isPlaying = true;
    }

    static void SetCaptureSize(EditorWindow gameView, int width, int height)
    {
        // Unity exposes fixed Game-view resolutions only through editor internals.
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var assembly = typeof(Editor).Assembly;
        var sizesType = assembly.GetType("UnityEditor.GameViewSizes", true);
        var singletonType = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
        var sizes = singletonType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).GetValue(null);
        var groupType = assembly.GetType("UnityEditor.GameViewSizeGroupType", true);
        var group = sizesType.GetMethod("GetGroup", flags).Invoke(sizes, new[] { Enum.Parse(groupType, "Standalone") });
        var sizeType = assembly.GetType("UnityEditor.GameViewSize", true);
        var kindType = assembly.GetType("UnityEditor.GameViewSizeType", true);
        var fixedSize = Enum.Parse(kindType, "FixedResolution");
        string label = "Story Bricks Recording 1280x800";
        var size = Activator.CreateInstance(sizeType, flags, null, new object[] { fixedSize, width, height, label }, null);
        group.GetType().GetMethod("AddCustomSize", flags).Invoke(group, new[] { size });
        int count = (int)group.GetType().GetMethod("GetTotalCount", flags).Invoke(group, null);
        gameView.GetType().GetProperty("selectedSizeIndex", flags).SetValue(gameView, count - 1);
        gameView.Repaint();
    }

    static void OnPlayMode(PlayModeStateChange change)
    {
        if (change != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Key, false)) return;
        SessionState.SetBool(Key, false);
        var cam = Camera.main;
        if (cam == null) throw new InvalidOperationException("No desktop preview camera.");
        cam.gameObject.AddComponent<TutorialRecordingCapture>();
    }
}
