#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>Imports the supplied presentation only; the shared XR rig and project settings stay owned by the live app.</summary>
public static class StoryBricksPresentationImport
{
    const string Output = "Assets/DesignerWorkspace/Resources/StoryBricks";
    const string Content = "Assets/DesignerWorkspace/StoryBricks";
    const string Temporary = "Assets/DesignerWorkspace/Editor/StoryBricksImportTemporary";

    [MenuItem("Story Bricks/Import supplied presentation once")]
    public static void Import()
    {
        Directory.CreateDirectory(Output);
        EnsureOpeningPrefab();
        if (AssetDatabase.LoadAssetAtPath<GameObject>(Output + "/View.prefab") != null)
        {
            Debug.Log("[StoryBricksImport] Existing designer presentation preserved.");
            return;
        }
        UpdateSuppliedPresentation();
    }

    [MenuItem("Story Bricks/Update supplied presentation")]
    public static void UpdateSuppliedPresentation()
    {
        string source = Environment.GetEnvironmentVariable("STORY_BRICKS_UX_SOURCE");
        if (string.IsNullOrWhiteSpace(source)) source = "/Users/jihyunyu/Downloads/story-bricks-main-2";
        string contentSource = Path.Combine(source, "Assets/StoryBricks");
        string sourceScene = Path.Combine(contentSource, "Scenes/ParentUI.unity");
        if (!File.Exists(sourceScene))
            throw new FileNotFoundException("Set STORY_BRICKS_UX_SOURCE to the supplied Unity project before updating its presentation.", sourceScene);
        if (Directory.Exists(Temporary))
            throw new InvalidOperationException("An earlier import left temporary files. Review " + Temporary + " before importing again.");

        Directory.CreateDirectory(Output);
        ImportMissingContent(contentSource);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        EnsureOpeningPrefab();
        string oldViewGuid = AssetDatabase.AssetPathToGUID(Output + "/View.prefab");
        Directory.CreateDirectory(Temporary);
        // The old parent prefab is only needed to resolve scene-added ViewerUI. It is never saved into the result.
        CopyTemporary(sourceScene, Temporary + "/Source.unity");
        CopyTemporary(Path.Combine(contentSource, "Prefabs/ParentUI.prefab"), Temporary + "/ParentUI.prefab");
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Scene incoming = default;
        GameObject root = null;
        try
        {
            incoming = EditorSceneManager.OpenScene(Temporary + "/Source.unity", OpenSceneMode.Additive);
            var sourceView = incoming.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<ViewerFlow>(true)).FirstOrDefault();
            if (sourceView == null) throw new InvalidOperationException("The supplied scene does not contain the latest ViewerUI.");
            // ParentUI is an SDK prefab variant. Without that unrelated rig/panel package,
            // Unity substitutes a 100x100 rect: read its authored canvas overrides instead.
            Vector2 authoredSize = ReadAuthoredCanvasSize(contentSource);
            Vector3 authoredScale = sourceView.transform.localScale;
            if (authoredScale.x <= 0 || authoredScale.y <= 0)
                throw new InvalidOperationException("The supplied ViewerUI has an invalid scale.");
            Vector2 viewportSize = Vector2.Scale(authoredSize, new Vector2(authoredScale.x, authoredScale.y));
            root = new GameObject("StoryBricks View", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            root.GetComponent<RectTransform>().sizeDelta = viewportSize;
            root.transform.localScale = Vector3.one * .001f;
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 20;
            root.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 10;
            var child = UnityEngine.Object.Instantiate(sourceView.gameObject, root.transform, false);
            child.name = "ViewerUI";
            child.SetActive(true);
            var rect = child.GetComponent<RectTransform>();
            // Preserve the 640x420 logical layout and its authored 1.5 scale. The enclosing
            // canvas covers 960x630 so its hand-input bounds contain the entire presentation.
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = authoredSize;
            rect.anchoredPosition = Vector2.zero;
            rect.localRotation = Quaternion.identity;
            rect.localScale = authoredScale;
            var view = child.GetComponent<ViewerFlow>();

            // Keep the authored frame for actual generated films, but exclude the unused twelve-second clip/player.
            foreach (var player in child.GetComponentsInChildren<VideoPlayer>(true)) UnityEngine.Object.DestroyImmediate(player);
            view.liveVideoImage = view.videoGroup != null ? view.videoGroup.GetComponentInChildren<RawImage>(true) : null;
            if (view.liveVideoImage != null) { view.liveVideoImage.texture = null; view.liveVideoImage.raycastTarget = false; }

            // Remove every prototype callback before adding the small passive navigation surface.
            foreach (var button in child.GetComponentsInChildren<Button>(true)) button.onClick = new Button.ButtonClickedEvent();
            Bind(view.newStoryButton, view.OnNewStory);
            Bind(view.savedStoryButton, view.OnSavedStory);
            Bind(view.watchAgainButton, view.OnWatchAgain);
            Bind(view.doneButton, view.OnDone);
            Bind(view.restartButton, view.OnRestart);
            for (int i = 0; i < view.tags.Length; i++)
            {
                var button = view.tags[i].root.GetComponent<Button>();
                if (button != null) UnityEventTools.AddIntPersistentListener(button.onClick, view.ToggleTag, i);
            }
            if (view.tutorialButton != null) view.tutorialButton.gameObject.SetActive(false);
            if (view.finishButton != null) view.finishButton.gameObject.SetActive(false);
            foreach (var hidden in new[] { view.hint, view.caption, view.subtitle, view.ready, view.end, view.pulse,
                view.finish, view.fox, view.bricks, view.pinch, view.period, view.videoGroup })
                if (hidden != null) { hidden.alpha = 0; hidden.gameObject.SetActive(false); }
            foreach (var tag in view.tags) { tag.group.alpha = 0; tag.root.gameObject.SetActive(false); }
            if (view.stage != null) view.stage.ResetStage();
            view.menu.gameObject.SetActive(true);
            view.menu.alpha = 1;
            view.state = ViewerFlow.State.Menu;
            SetButtonCopy(view.newStoryButton, "Create Your World", "Build your own story");
            SetButtonCopy(view.savedStoryButton, "Judy's Story", "Watch Judy's world come alive");
            SetButtonCopy(view.restartButton, "Create World", "Make a story of your own");
            SetButtonCopy(view.doneButton, "Menu", "Choose your next world");

            // Reuse the current project's compatible font materials, even if source atlas subasset IDs differ.
            foreach (var text in child.GetComponentsInChildren<TMPro.TMP_Text>(true))
                if (text.font != null) text.fontSharedMaterial = text.font.material;
            Validate(root, view);
            PrefabUtility.SaveAsPrefabAsset(root, Output + "/View.prefab");
            AssetDatabase.SaveAssets();
            if (!string.IsNullOrEmpty(oldViewGuid) && AssetDatabase.AssetPathToGUID(Output + "/View.prefab") != oldViewGuid)
                throw new InvalidOperationException("The existing presentation prefab identity changed unexpectedly.");
            Debug.Log("[StoryBricksImport] Updated authored ViewerUI (" + authoredSize + " at scale " + authoredScale + ", viewport " + viewportSize + ") and 61.5-second Judy cutout story. Live route remains driven by shared functions; no source rig/settings or twelve-second clip imported.");
        }
        finally
        {
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            if (incoming.IsValid()) EditorSceneManager.CloseScene(incoming, true);
            AssetDatabase.DeleteAsset(Temporary);
        }
    }

    static Vector2 ReadAuthoredCanvasSize(string contentSource)
    {
        string prefab = File.ReadAllText(Path.Combine(contentSource, "Prefabs/ParentUI.prefab"));
        var x = Regex.Match(prefab, @"propertyPath: _size\.x\s+value: ([0-9.]+)");
        var y = Regex.Match(prefab, @"propertyPath: _size\.y\s+value: ([0-9.]+)");
        if (!x.Success || !y.Success)
            throw new InvalidOperationException("Cannot find the supplied ParentUI's authored canvas dimensions.");
        var size = new Vector2(float.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture), float.Parse(y.Groups[1].Value, CultureInfo.InvariantCulture));
        if (size.x < 400 || size.y < 300)
            throw new InvalidOperationException("The supplied canvas dimensions are too small for the authored layout: " + size);
        return size;
    }

    static void Bind(Button button, UnityAction action)
    {
        if (button == null) throw new InvalidOperationException("A supplied navigation button reference is missing.");
        button.interactable = true;
        UnityEventTools.AddPersistentListener(button.onClick, action);
    }

    static void SetButtonCopy(Button button, string title, string sub)
    {
        foreach (var text in button.GetComponentsInChildren<TMPro.TMP_Text>(true))
            if (text.name == "Title" || text.name == "Label")
            {
                text.text = title;
                text.enableAutoSizing = true;
                text.fontSizeMin = Mathf.Min(10f, text.fontSize);
                text.fontSizeMax = text.fontSize;
            }
            else if (text.name == "Sub") text.text = sub;
    }

    static void Validate(GameObject root, ViewerFlow view)
    {
        if (root.GetComponentsInChildren<Camera>(true).Length != 0 || root.GetComponentsInChildren<AudioListener>(true).Length != 0 ||
            root.GetComponentsInChildren<UnityEngine.EventSystems.EventSystem>(true).Length != 0 || root.GetComponentsInChildren<VideoPlayer>(true).Length != 0)
            throw new InvalidOperationException("Presentation extraction unexpectedly included scene services or the source video player.");
        foreach (var node in root.GetComponentsInChildren<Transform>(true))
            if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(node.gameObject) != 0)
                throw new InvalidOperationException("Missing presentation script on " + node.name);
        if (view.stage == null || view.narrationClip == null || view.storySfxClip == null || view.narration == null || view.narration.Length == 0 ||
            view.narrationStart == null || view.narrationStart.Length != view.narration.Length || view.storyEnd < 60f || view.liveVideoImage == null)
            throw new InvalidOperationException("The authored Judy story or generated-film frame is incomplete.");
        foreach (var button in root.GetComponentsInChildren<Button>(true))
            for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
                if (button.onClick.GetPersistentTarget(i) != view)
                    throw new InvalidOperationException("Presentation has a callback outside its passive view: " + button.name);
    }

    // Existing GUIDs always win. This preserves adapted scripts, shared fonts, and edits already made by designers.
    static void ImportMissingContent(string source)
    {
        var files = new List<string>();
        foreach (var folder in new[] { "Art", "UI", "Fonts" })
            files.AddRange(Directory.GetFiles(Path.Combine(source, folder), "*", SearchOption.AllDirectories).Where(path => !path.EndsWith(".meta", StringComparison.Ordinal)));
        foreach (var relative in new[] { "Scripts/StoryStage.cs", "Audio/Voice/narration_judy_moon.mp3", "Audio/SFX/story_sfx_judy_moon.wav",
            "Audio/SFX/11_pinch_click.mp3", "Audio/SFX/03_rising_bloop.mp3", "Audio/SFX/04_sparkle.mp3", "Audio/SFX/06_success_melody.mp3" })
            files.Add(Path.Combine(source, relative));
        foreach (string file in files)
        {
            string guid = GuidFromMeta(file + ".meta");
            if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid))) continue;
            string relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string destination = Path.Combine(Content, relative);
            if (File.Exists(destination)) throw new InvalidOperationException("An unrelated asset already occupies " + destination);
            EnsureContentFolders(source, Path.GetDirectoryName(relative));
            File.Copy(file, destination);
            File.Copy(file + ".meta", destination + ".meta");
        }
    }

    static void EnsureContentFolders(string source, string relative)
    {
        if (string.IsNullOrEmpty(relative)) return;
        EnsureContentFolders(source, Path.GetDirectoryName(relative));
        string folder = Path.Combine(Content, relative);
        Directory.CreateDirectory(folder);
        string sourceMeta = Path.Combine(source, relative) + ".meta";
        if (!File.Exists(folder + ".meta") && File.Exists(sourceMeta) && string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(GuidFromMeta(sourceMeta))))
            File.Copy(sourceMeta, folder + ".meta");
    }

    static string GuidFromMeta(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("A supplied asset is missing its Unity metadata.", path);
        var match = Regex.Match(File.ReadAllText(path), @"^guid: ([a-f0-9]{32})$", RegexOptions.Multiline);
        if (!match.Success) throw new InvalidOperationException("Invalid Unity metadata: " + path);
        return match.Groups[1].Value;
    }

    static void CopyTemporary(string source, string destination)
    {
        string guid = GuidFromMeta(source + ".meta");
        if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
            throw new InvalidOperationException("Source extraction asset already exists in this project: " + source);
        File.Copy(source, destination);
        File.Copy(source + ".meta", destination + ".meta");
    }

    static void EnsureOpeningPrefab()
    {
        const string path = Output + "/Opening.prefab";
        if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return;
        const string materialPath = Output + "/OpeningMaterial.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            // The shared Quest project uses the built-in renderer, not the incoming URP rig.
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) throw new InvalidOperationException("Opening transparency shader is missing.");
            material = new Material(shader) { name = "Story Bricks opening", renderQueue = 3000 };
            AssetDatabase.CreateAsset(material, materialPath);
        }
        var font = AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>(
            "Assets/DesignerWorkspace/StoryBricks/Fonts/Inter-Thin SDF.asset");
        if (font == null) throw new InvalidOperationException("The supplied opening font is missing.");
        var root = new GameObject("Story Bricks Opening");
        try
        {
            var opening = root.AddComponent<OpeningAnimation>();
            opening.look = OpeningAnimation.Look.Ink;
            opening.baseMaterial = material; opening.font = font;
            PrefabUtility.SaveAsPrefabAsset(root, path);
            AssetDatabase.SaveAssets();
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

}
#endif
