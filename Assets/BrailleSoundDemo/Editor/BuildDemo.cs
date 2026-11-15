using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace BrailleSoundDemo.Editor
{
    public static class BuildDemo
    {
        public const string ScenePath = "Assets/Scenes/BrailleStoryDemo.unity";

        [MenuItem("Word Story Demo/1. Create or Open Demo")]
        public static void Setup()
        {
            Directory.CreateDirectory("Assets/Scenes");
            if (!File.Exists(ScenePath))
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                new GameObject("Word Story Demo").AddComponent<DemoExperience>();
                EditorSceneManager.SaveScene(scene, ScenePath);
            }
            else EditorSceneManager.OpenScene(ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            PlayerSettings.companyName = "Braille Lab";
            PlayerSettings.productName = "Words to Worlds";
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.defaultScreenWidth = 1440;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
            PlayerSettings.iOS.microphoneUsageDescription = "Record your answer after each story question and send it to the local Python speech service.";
            PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentOnly;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "org.braillelab.storydemo");
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, "org.braillelab.storydemo");
            QualitySettings.antiAliasing = 4;
            PreserveRuntimeShaders();
            var audioManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset")[0]);
            var spatializer = audioManager.FindProperty("m_SpatializerPlugin");
            if (spatializer != null) spatializer.stringValue = "Meta XR Audio";
            audioManager.ApplyModifiedPropertiesWithoutUndo();
            ConfigureQuest();
            SetInputMode(1);
            AssetDatabase.SaveAssets();
            Debug.Log("BRAILLE_DEMO_SETUP_OK " + ScenePath);
        }

        // The demo creates its visual objects at runtime, so the empty bootstrap scene
        // alone cannot tell Unity's build stripper which built-in shaders it will need.
        static void PreserveRuntimeShaders()
        {
            var graphics = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            var shaders = graphics.FindProperty("m_AlwaysIncludedShaders");
            if (shaders == null) throw new InvalidOperationException("GraphicsSettings shader list not found.");
            // A Resources material retains Standard's needed variants without forcing
            // the enormous all-variants Standard shader into unity_builtin_extra.
            Directory.CreateDirectory("Assets/BrailleSoundDemo/Resources/DemoMaterials");
            AssetDatabase.Refresh();
            // Unity owns these transient DontSave shaders; serializing them into
            // resources or Always Included causes player builds to fail.
            foreach (var transientName in new[] { "GUI/Text Shader", "Hidden/Internal-Colored" })
            {
                var transient = Shader.Find(transientName);
                for (int i = shaders.arraySize - 1; i >= 0; i--)
                {
                    if (shaders.GetArrayElementAtIndex(i).objectReferenceValue != transient) continue;
                    shaders.GetArrayElementAtIndex(i).objectReferenceValue = null;
                    shaders.DeleteArrayElementAtIndex(i);
                }
                AssetDatabase.DeleteAsset("Assets/BrailleSoundDemo/Resources/DemoMaterials/" +
                    transientName.Replace('/', '_').Replace(' ', '_') + ".mat");
            }
            foreach (var name in new[] { "Standard", "Unlit/Color" })
            {
                var shader = Shader.Find(name);
                if (shader == null) throw new InvalidOperationException("Required built-in shader not found: " + name);
                var path = "Assets/BrailleSoundDemo/Resources/DemoMaterials/" + name.Replace('/', '_').Replace(' ', '_') + ".mat";
                if (AssetDatabase.LoadAssetAtPath<Material>(path) == null)
                    AssetDatabase.CreateAsset(new Material(shader) { name = name + " Runtime Reference" }, path);
                if (name == "Standard")
                {
                    for (int i = shaders.arraySize - 1; i >= 0; i--)
                    {
                        if (shaders.GetArrayElementAtIndex(i).objectReferenceValue != shader) continue;
                        shaders.GetArrayElementAtIndex(i).objectReferenceValue = null;
                        shaders.DeleteArrayElementAtIndex(i);
                    }
                    continue;
                }
                bool present = false;
                for (int i = 0; i < shaders.arraySize; i++)
                    if (shaders.GetArrayElementAtIndex(i).objectReferenceValue == shader) { present = true; break; }
                if (present) continue;
                shaders.InsertArrayElementAtIndex(shaders.arraySize);
                shaders.GetArrayElementAtIndex(shaders.arraySize - 1).objectReferenceValue = shader;
            }
            graphics.ApplyModifiedPropertiesWithoutUndo();
        }

        [MenuItem("Word Story Demo/2. Configure Quest OpenXR")]
        public static void ConfigureQuest()
        {
            ConfigureQuest(false);
        }

        static void ConfigureQuest(bool strict)
        {
            Directory.CreateDirectory("Assets/XR/Settings");
            AssetDatabase.Refresh();
            if (!EditorBuildSettings.TryGetConfigObject<XRGeneralSettingsPerBuildTarget>(
                    XRGeneralSettings.k_SettingsKey, out var all))
            {
                all = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(all, "Assets/XR/Settings/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, all, true);
            }
            if (!all.HasSettingsForBuildTarget(BuildTargetGroup.Android))
                all.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
            if (!all.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
                all.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            var android = all.SettingsForBuildTarget(BuildTargetGroup.Android);
            android.InitManagerOnStart = true;
            android.Manager.automaticLoading = true;
            android.Manager.automaticRunning = true;
            if (!XRPackageMetadataStore.AssignLoader(android.Manager,
                    "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Android))
                throw new InvalidOperationException("OpenXR loader could not be assigned to Android.");

            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
            EnableFeature("com.unity.openxr.feature.metaquest", strict);
            EnableFeature("com.unity.openxr.feature.input.oculustouch", strict);
            // Meta Core bundles its own OpenXR loader. Its enabled feature lets
            // the SDK build hook exclude Unity's duplicate loader library.
            EnableFeature(Meta.XR.MetaXRFeature.featureId, strict);
            var openxr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (openxr != null)
            {
                openxr.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
                EditorUtility.SetDirty(openxr);
            }
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            EditorUtility.SetDirty(all);
            EditorUtility.SetDirty(android);
            AssetDatabase.SaveAssets();
        }

        static void EnableFeature(string id, bool strict)
        {
            var feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(BuildTargetGroup.Android, id);
            if (feature == null)
            {
                var message = "Quest feature setup deferred: " + id +
                    ". Install Android Build Support, switch the active build target to Android, then run Configure Quest OpenXR again.";
                if (strict) throw new InvalidOperationException(message);
                Debug.LogWarning(message);
                return;
            }
            feature.enabled = true;
            EditorUtility.SetDirty(feature);
        }

        static void SetInputMode(int mode)
        {
            var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            var property = settings.FindProperty("activeInputHandler");
            if (property != null) { property.intValue = mode; settings.ApplyModifiedPropertiesWithoutUndo(); }
        }

        [MenuItem("Word Story Demo/3. Build Mac Demo")]
        public static void BuildMac()
        {
            Setup();
            Build(BuildTarget.StandaloneOSX, "Builds/Mac/Words to Worlds.app");
        }

        [MenuItem("Word Story Demo/4. Build Quest APK")]
        public static void BuildQuest()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
                throw new InvalidOperationException("Install Android Build Support, SDK/NDK and OpenJDK for Unity 6000.3.28f1 in Unity Hub before building Quest.");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                throw new InvalidOperationException("Switch the active Build Profile to Android first, wait for recompilation, then choose Build Quest APK again.");
            Setup();
            ConfigureQuest(true);
            SetInputMode(1);
            AssetDatabase.SaveAssets();
            // The live Python bridge is reached through adb reverse on loopback.
            // Scope this exception to this build and keep cleartext blocked elsewhere.
            var config = OVRProjectConfig.CachedProjectConfig;
            if (config == null) throw new InvalidOperationException("Meta project configuration is unavailable.");
            bool previousNsc = config.enableNSCConfig;
            string previousSecurityPath = config.securityXmlPath;
            try
            {
                config.enableNSCConfig = true;
                config.securityXmlPath = Path.GetFullPath("Assets/BrailleSoundDemo/Editor/CameraTestNetworkSecurity.xml");
                if (!File.Exists(config.securityXmlPath))
                    throw new InvalidOperationException("The loopback-only network security configuration is missing.");
                OVRProjectConfig.CommitProjectConfig(config);
                Build(BuildTarget.Android, "Builds/Quest/WordsToWorlds.apk");
            }
            finally
            {
                config.enableNSCConfig = previousNsc;
                config.securityXmlPath = previousSecurityPath;
                OVRProjectConfig.CommitProjectConfig(config);
            }
        }

        static void Build(BuildTarget target, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath }, locationPathName = path, target = target,
                options = BuildOptions.Development
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Build failed: " + report.summary.result);
            Debug.Log("BRAILLE_DEMO_BUILD_OK " + Path.GetFullPath(path));
        }
    }
}
