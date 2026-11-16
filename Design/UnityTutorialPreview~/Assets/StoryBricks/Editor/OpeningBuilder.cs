using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class OpeningBuilder
{
    [MenuItem("StoryBricks/Add Opening Animation")]
    public static void Add()
    {
        var log = new System.Text.StringBuilder();
        System.IO.Directory.CreateDirectory("Assets/StoryBricks/Materials");

        // Unlit transparent material the animation copies at runtime.
        const string matPath = "Assets/StoryBricks/Materials/OpeningUnlit.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            mat = new Material(sh);
            mat.SetFloat("_Surface", 1f); mat.SetFloat("_Blend", 0f); mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_Cull", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = 3000;
            AssetDatabase.CreateAsset(mat, matPath);
            log.Append("material created (" + sh.name + "); ");
        }

        // Inter Thin as a TextMesh Pro font.
        const string faPath = "Assets/StoryBricks/Fonts/Inter-Thin SDF.asset";
        var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(faPath);
        if (fa == null)
        {
            var f = AssetDatabase.LoadAssetAtPath<Font>("Assets/StoryBricks/Fonts/Inter-Thin.otf");
            if (f != null)
            {
                fa = TMP_FontAsset.CreateFontAsset(f, 96, 9, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024);
                fa.TryAddCharacters("Story Bricks");
                AssetDatabase.CreateAsset(fa, faPath);
                fa.material.name = "Inter-Thin SDF Material"; fa.atlasTexture.name = "Inter-Thin SDF Atlas";
                AssetDatabase.AddObjectToAsset(fa.material, fa);
                AssetDatabase.AddObjectToAsset(fa.atlasTexture, fa);
                EditorUtility.SetDirty(fa);
                log.Append("font asset created; ");
            }
            else log.Append("Inter-Thin.otf not imported; ");
        }

        var existing = Object.FindFirstObjectByType<OpeningAnimation>(FindObjectsInactive.Include);
        var go = existing != null ? existing.gameObject : new GameObject("Opening");
        var op = existing != null ? existing : go.AddComponent<OpeningAnimation>();
        op.baseMaterial = mat; op.font = fa;
        var panel = GameObject.Find("ParentUI");
        op.showWhenDone = panel;
        go.transform.position = new Vector3(0, 0, 0.8f);
        EditorUtility.SetDirty(op);
        AssetDatabase.SaveAssets();
        var scene = go.scene;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        System.IO.File.WriteAllText("Assets/StoryBricks/opening_log.txt", log + "font=" + (fa != null) + " panel=" + (panel != null) + " scene=" + scene.path);
        Debug.Log("StoryBricks: opening added. " + log);
    }
}
