using System;
using System.IO;
using BrailleSoundDemo.CameraTest;
using BrailleSoundDemo.TableDepthTest;
using BrailleSoundDemo.PhysicalStory;
using Meta.XR;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BrailleSoundDemo.Editor
{
    /// <summary>The main conversation running on the already-tested physical capture rig.</summary>
    public static class BuildPhysicalStoryDemo
    {
        public const string ScenePath = "Assets/Scenes/PhysicalStoryDemo.unity";
        public const string ApkPath = "Builds/Quest/WordsToWorldsPhysical.apk";
        public const string PackageId = "org.braillelab.storydemo";

        [MenuItem("Word Story Demo/Physical Story/Create or Open")]
        public static void Setup()
        {
            // Use the diagnostic's tested rig, permissions, depth and hand history unchanged.
            BuildDemo.Setup();
            // These settings also generate the APK manifest. A saved scene must
            // not skip them when the editor's default config is controllers-only.
            BuildTableDepthTest.ConfigureCaptureFeatures();
            // This is a create-or-open operation. Never regenerate a saved,
            // designer-authored scene during a routine build.
            if(File.Exists(ScenePath))
            {
                EditorSceneManager.OpenScene(ScenePath);
                if(UnityEngine.Object.FindFirstObjectByType<PhysicalStoryCoordinator>()==null)
                    throw new InvalidOperationException("The saved physical scene is missing its coordinator. Restore it rather than overwriting the scene.");
                EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene(ScenePath,true)};
                return;
            }
            BuildTableDepthTest.Setup();
            var table = UnityEngine.Object.FindFirstObjectByType<TableDepthTestDemo>();
            var sender = UnityEngine.Object.FindFirstObjectByType<QuestFrameCapture>();
            var rig = UnityEngine.Object.FindFirstObjectByType<OVRCameraRig>();
            var camera = UnityEngine.Object.FindFirstObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
            if (table == null || sender == null || rig == null || camera == null)
                throw new InvalidOperationException("The tested table-capture scene did not supply its required components.");
            // This headset supports 1728 square; MRUK falls back on older devices.
            camera.RequestedResolution = new Vector2Int(1728, 1728);
            sender.IncludeUnityCrop = true;
            EnableTrackedHandVisuals(rig);
            sender.Configure(camera, "http://127.0.0.1:8769/frames");
            var coordinator = table.gameObject.AddComponent<PhysicalStoryCoordinator>();
            coordinator.Configure(table, sender, rig, "http://127.0.0.1:8769");
            table.gameObject.name = "Physical Story / table capture and conversation";
            EditorSceneManager.SaveScene(table.gameObject.scene, ScenePath);
            EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene(ScenePath,true)};
            AssetDatabase.SaveAssets();
            Debug.Log("PHYSICAL_STORY_SETUP_OK " + ScenePath);
        }

        public static void EnableTrackedHandVisuals(OVRCameraRig rig)
        {
            const string materialPath = "Assets/BrailleSoundDemo/Materials/TrackedHandWhite.mat";
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath));
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            var shader = Shader.Find("Unlit/Color");
            if (shader == null) throw new InvalidOperationException("Tracked-hand shader is missing.");
            if (material == null)
            {
                material = new Material(shader) { name = "Tracked Hand White" };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            material.shader = shader;
            material.color = new Color(.92f, .92f, .92f, 1);
            EditorUtility.SetDirty(material);
            var hands = rig.GetComponentsInChildren<OVRHand>(true);
            if (hands.Length != 2) throw new InvalidOperationException("Expected two tracked hands.");
            foreach (var hand in hands)
            {
                var mesh = hand.GetComponent<OVRMesh>();
                var renderer = hand.GetComponent<OVRMeshRenderer>();
                var skin = hand.GetComponent<SkinnedMeshRenderer>();
                if (mesh == null || renderer == null || skin == null)
                    throw new InvalidOperationException("Tracked hand is missing mesh components.");
                mesh.enabled = true;
                renderer.enabled = true;
                skin.sharedMaterial = material;
                skin.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                skin.receiveShadows = false;
                // The runtime enables the skin only when current tracking is valid.
                skin.enabled = false;
                hand.gameObject.AddComponent<TrackedHandVisual>();
            }
        }

        [MenuItem("Word Story Demo/Physical Story/Build Main Quest App")]
        public static void BuildQuest()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                throw new InvalidOperationException("Switch to Android before building the physical story app.");
            var config = OVRProjectConfig.CachedProjectConfig;
            if (config == null) throw new InvalidOperationException("Meta configuration is not ready.");
            string originalConfig = EditorJsonUtility.ToJson(config);
            string originalProduct = PlayerSettings.productName;
            string originalId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
            var originalHttp = PlayerSettings.insecureHttpOption;
            var originalScenes = EditorBuildSettings.scenes;
            try
            {
                Setup();
                HandProximityVerification.Run();
                Tests.DoublePinchCaptureVerification.Run();
                Tests.DoublePinchTimerVerification.Run();
                Tests.PinchWordBoxVerification.Run();
                Tests.TableSurfaceVerification.Run();
                Tests.HandRegionProjectionVerification.Run();
                Tests.PhysicalWordAnchorVerification.Run();
                Tests.CaptureValidityVerification.Run();
                Tests.SoundPaletteVerification.Run();
                Tests.TeamWorkspaceVerification.Run();
                Tests.ManualFinishVerification.Run();
                PlayerSettings.productName = "Words to Worlds";
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, PackageId);
                AssetDatabase.SaveAssets();
                Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                    scenes = new[] { ScenePath }, locationPathName = ApkPath,
                    target = BuildTarget.Android, options = BuildOptions.Development
                });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException("Physical story build failed: " + report.summary.result);
                Debug.Log("PHYSICAL_STORY_BUILD_OK " + Path.GetFullPath(ApkPath));
            }
            finally
            {
                PlayerSettings.productName = originalProduct;
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, originalId);
                PlayerSettings.insecureHttpOption = originalHttp;
                EditorBuildSettings.scenes = originalScenes;
                EditorJsonUtility.FromJsonOverwrite(originalConfig, config);
                OVRProjectConfig.CommitProjectConfig(config);
                AssetDatabase.SaveAssets();
            }
        }
    }
}
