#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using BrailleSoundDemo.Presentation;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>Extracts presentation objects once. Never imports or replaces the project's XR rig/settings.</summary>
public static class StoryBricksPresentationImport
{
    const string Output = "Assets/DesignerWorkspace/Resources/StoryBricks";
    const string Temporary = "Assets/DesignerWorkspace/Editor/StoryBricksImportTemporary";

    [MenuItem("Story Bricks/Import supplied presentation once")]
    public static void Import()
    {
        Directory.CreateDirectory(Output);
        if (AssetDatabase.LoadAssetAtPath<GameObject>(Output + "/View.prefab") != null)
        {
            Debug.Log("[StoryBricksImport] Existing designer presentation preserved.");
            return;
        }
        string source = Environment.GetEnvironmentVariable("STORY_BRICKS_UX_SOURCE");
        if (string.IsNullOrWhiteSpace(source)) source = "/Users/jihyunyu/Downloads/story-bricks-main";
        string sourceScene = Path.Combine(source, "Assets/StoryBricks/Scenes/ParentUI.unity");
        if (!File.Exists(sourceScene)) throw new FileNotFoundException("Set STORY_BRICKS_UX_SOURCE to the supplied Unity project for the initial extraction.", sourceScene);
        Directory.CreateDirectory(Temporary);
        Copy(sourceScene, Temporary + "/Source.unity");
        Copy(Path.Combine(source, "Assets/StoryBricks/Prefabs/ParentUI.prefab"), Temporary + "/ParentUI.prefab");
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Scene incoming = default;
        GameObject root = null;
        try
        {
            incoming = EditorSceneManager.OpenScene(Temporary + "/Source.unity", OpenSceneMode.Additive);
            var sourceView = incoming.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<ViewerFlow>(true)).FirstOrDefault();
            if (sourceView == null) throw new InvalidOperationException("The supplied scene does not contain the latest ViewerUI.");
            root = new GameObject("StoryBricks View", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            root.GetComponent<RectTransform>().sizeDelta = new Vector2(720, 540);
            root.transform.localScale = Vector3.one * .001f;
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 20;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 10;
            var child = UnityEngine.Object.Instantiate(sourceView.gameObject, root.transform, false);
            child.name = "ViewerUI";
            child.SetActive(true);
            var rect = child.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var view = child.GetComponent<ViewerFlow>();
            // Rebind only navigation requests. Neither button is allowed to play/reset a story itself.
            view.newStoryButton.onClick = new Button.ButtonClickedEvent();
            view.savedStoryButton.onClick = new Button.ButtonClickedEvent();
            UnityEventTools.AddPersistentListener(view.newStoryButton.onClick, view.OnNewStory);
            UnityEventTools.AddPersistentListener(view.savedStoryButton.onClick, view.OnSavedStory);
            for (int i = 0; i < view.tags.Length; i++)
            {
                var button = view.tags[i].root.GetComponent<Button>();
                if (button == null) continue;
                button.onClick = new Button.ButtonClickedEvent();
                UnityEventTools.AddIntPersistentListener(button.onClick, view.ToggleTag, i);
            }
            foreach (var button in new[] { view.watchAgainButton, view.doneButton })
                if (button != null) { button.onClick = new Button.ButtonClickedEvent(); button.interactable = false; button.gameObject.SetActive(false); }
            foreach (var hidden in new[] { view.hint, view.caption, view.subtitle, view.ready, view.end, view.pulse })
                if (hidden != null) hidden.gameObject.SetActive(false);
            foreach (var tag in view.tags) tag.root.gameObject.SetActive(false);
            view.menu.gameObject.SetActive(true);
            view.menu.alpha = 1;
            view.state = ViewerFlow.State.Menu;
            // The extracted root contains only canvas/graphics/presentation; no source scene services.
            if (root.GetComponentsInChildren<Camera>(true).Length != 0 || root.GetComponentsInChildren<AudioListener>(true).Length != 0 ||
                root.GetComponentsInChildren<UnityEngine.EventSystems.EventSystem>(true).Length != 0)
                throw new InvalidOperationException("Presentation extraction unexpectedly included scene services.");
            CreateDefaultSequence(view);
            view.narration = Array.Empty<string>();
            view.narrationWord = Array.Empty<int>();
            PrefabUtility.SaveAsPrefabAsset(root, Output + "/View.prefab");
            AssetDatabase.SaveAssets();
            Debug.Log("[StoryBricksImport] Imported latest watercolour UI and four editable Judy cues. Source rigs/settings were excluded; no prepared voice/video was supplied.");
        }
        finally
        {
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            if (incoming.IsValid()) EditorSceneManager.CloseScene(incoming, true);
            AssetDatabase.DeleteAsset(Temporary);
        }
    }

    static void Copy(string source, string destination)
    {
        File.Copy(source, destination, true);
        if (File.Exists(source + ".meta")) File.Copy(source + ".meta", destination + ".meta", true);
    }

    static void CreateDefaultSequence(ViewerFlow view)
    {
        string path = Output + "/JudySequence.asset";
        if (AssetDatabase.LoadAssetAtPath<JudyCueSequence>(path) != null) return;
        var sequence = ScriptableObject.CreateInstance<JudyCueSequence>();
        string[] words = { "JUDY", "FOX", "RAIN", "TREE" };
        sequence.words = new JudyWordCue[words.Length];
        for (int i = 0; i < words.Length; i++)
        {
            int index = i;
            var captions = (view.narration ?? Array.Empty<string>()).Where((_, n) =>
                view.narrationWord != null && n < view.narrationWord.Length && view.narrationWord[n] == index);
            sequence.words[i] = new JudyWordCue { id = "judy-" + words[i].ToLowerInvariant(), expectedWord = words[i],
                steps = new[] { new JudyMediaStep { id = "word-presentation", caption = string.Join(" ", captions) } } };
        }
        sequence.finalStoryText = string.Join(" ", view.narration ?? Array.Empty<string>());
        sequence.finalStory = new[] { new JudyMediaStep { id = "prepared-story", caption = sequence.finalStoryText } };
        AssetDatabase.CreateAsset(sequence, path);
    }
}
#endif
