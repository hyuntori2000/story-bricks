using System;
using System.Collections.Generic;
using System.IO;
using BrailleSoundDemo.CameraTest;
using BrailleSoundDemo.TableDepthTest;
using Meta.XR;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;

namespace BrailleSoundDemo.Editor
{
    /// <summary>Builds the independent on-device table-depth diagnostic.</summary>
    public static class BuildTableDepthTest
    {
        public const string ScenePath = "Assets/Scenes/TableDepthTest.unity";
        public const string PackageId = "org.braillelab.tabledepthtest";
        public const string ApkPath = "Builds/Quest/StoryBricksTableDepthTest.apk";
        const string Core = "Packages/com.meta.xr.sdk.core/";
        static readonly string[] FeatureIds = {
            "com.unity.openxr.feature.metaquest",
            "com.unity.openxr.feature.input.oculustouch",
            MetaXRFeature.featureId
        };

        /// <summary>Apply the required Android features without replacing the current scene.</summary>
        public static void ConfigureCaptureFeatures(bool handsOnly = false)
        {
            ValidateQuestProject();
            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
            foreach (var id in FeatureIds)
            {
                var feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(BuildTargetGroup.Android, id);
                if (feature == null) throw new InvalidOperationException("Required OpenXR feature not found: " + id);
                feature.enabled = true;
                EditorUtility.SetDirty(feature);
            }

            var config = OVRProjectConfig.CachedProjectConfig;
            if (config == null) throw new InvalidOperationException("Meta editor initialization is incomplete. Finish package import and retry.");
            config.handTrackingSupport = handsOnly ? OVRProjectConfig.HandTrackingSupport.HandsOnly
                : OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
            config.handTrackingFrequency = OVRProjectConfig.HandTrackingFrequency.HIGH;
            config.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required;
            config.sceneSupport = OVRProjectConfig.FeatureSupport.Required;
            config.isPassthroughCameraAccessEnabled = true;
            config.enableNSCConfig = true;
            config.securityXmlPath = Path.GetFullPath("Assets/BrailleSoundDemo/Editor/CameraTestNetworkSecurity.xml");
            OVRProjectConfig.CommitProjectConfig(config);
        }

        [MenuItem("Word Story Demo/Table Depth Test/Create or Open")]
        public static void Setup()
        {
            ConfigureCaptureFeatures();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Core + "Prefabs/OVRCameraRig.prefab");
            if (rigPrefab == null) throw new InvalidOperationException("OVRCameraRig prefab is missing.");
            var rigObject = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab);
            rigObject.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            rigObject.transform.localScale = Vector3.one;
            var rig = rigObject.GetComponent<OVRCameraRig>();
            var manager = rigObject.GetComponent<OVRManager>();
            manager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
            manager.isInsightPassthroughEnabled = true;
            manager.wideMotionModeHandPosesEnabled = false;
            manager.controllerDrivenHandPosesType = OVRManager.ControllerDrivenHandPosesType.None;
            foreach (var eye in rigObject.GetComponentsInChildren<Camera>(true))
            {
                eye.clearFlags = CameraClearFlags.SolidColor;
                eye.backgroundColor = new Color(0, 0, 0, 0);
                eye.allowHDR = false;
                eye.nearClipPlane = .05f;
            }
            // The coordinator owns both USE_SCENE and HEADSET_CAMERA prompts.
            // No microphone or other manager-owned permission requests are used.
            var managerData = new SerializedObject(manager);
            foreach (var field in new[] { "requestPassthroughCameraAccessPermissionOnStartup",
                "requestRecordAudioPermissionOnStartup", "requestScenePermissionOnStartup",
                "requestBodyTrackingPermissionOnStartup", "requestFaceTrackingPermissionOnStartup",
                "requestEyeTrackingPermissionOnStartup" })
            {
                var property = managerData.FindProperty(field);
                if (property != null) property.boolValue = false;
            }
            managerData.ApplyModifiedPropertiesWithoutUndo();
            rigObject.AddComponent<OVRPassthroughLayer>();

            var left = AddHand(rig.leftHandAnchor, false, out var leftSkeleton);
            var right = AddHand(rig.rightHandAnchor, true, out var rightSkeleton);
            var root = new GameObject("Table Depth Diagnostic");
            var app = root.AddComponent<TableDepthTestDemo>();
            // SDK209 uses native OpenXR environment raycasts. Do not also add an
            // EnvironmentDepthManager. The app creates the raycaster only after
            // XR initialization and permission, avoiding an early cached false
            // EnvironmentRaycastManager.IsSupported result in Awake().
            app.Configure(rig, null, left, leftSkeleton, right, rightSkeleton);
            var cameraObject = new GameObject("Left RGB camera access");
            cameraObject.transform.SetParent(root.transform, false);
            var camera = cameraObject.AddComponent<PassthroughCameraAccess>();
            camera.enabled = false;
            camera.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
            camera.RequestedResolution = new Vector2Int(1280, 960);
            camera.MaxFramerate = 30;
            var sender = root.AddComponent<QuestFrameCapture>();
            sender.Configure(camera, "http://127.0.0.1:8768/frames");
            app.ConfigureCapture(camera, sender);

            // ADB reverse exposes only the Mac receiver at device loopback.
            // The separate Android network config allows cleartext to 127.0.0.1
            // only; build completion restores this Unity setting and Meta config.
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            PreserveShader("Unlit/Color");
            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("TABLE_DEPTH_TEST_SETUP_OK " + ScenePath);
        }

        static OVRHand AddHand(Transform anchor, bool right, out OVRSkeleton skeleton)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Core + "Prefabs/OVRHandPrefab.prefab");
            if (prefab == null) throw new InvalidOperationException("OVRHandPrefab is missing.");
            var handObject = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            handObject.name = right ? "Tracked Right Hand" : "Tracked Left Hand";
            handObject.transform.SetParent(anchor, false);
            handObject.transform.localPosition = Vector3.zero;
            handObject.transform.localRotation = Quaternion.identity;
            handObject.transform.localScale = Vector3.one;
            var hand = handObject.GetComponent<OVRHand>();
            skeleton = handObject.GetComponent<OVRSkeleton>();
            if (hand == null || skeleton == null) throw new InvalidOperationException("Hand prefab lacks data providers.");
            var handData = new SerializedObject(hand);
            handData.FindProperty("HandType").intValue = (int)(right ? OVRHand.Hand.HandRight : OVRHand.Hand.HandLeft);
            handData.ApplyModifiedPropertiesWithoutUndo();
            // Uses the project's actual OVR/OpenXR skeleton version and also
            // updates the mesh side. No global skeleton setting is changed.
            hand.OnValidate();
            var skeletonData = new SerializedObject(skeleton);
            skeletonData.FindProperty("_updateRootPose").boolValue = false;
            skeletonData.FindProperty("_updateRootScale").boolValue = true;
            skeletonData.FindProperty("_applyBoneTranslations").boolValue = true;
            skeletonData.FindProperty("_enablePhysicsCapsules").boolValue = false;
            skeletonData.ApplyModifiedPropertiesWithoutUndo();
            var expected = OVRRuntimeSettings.Instance.HandSkeletonVersion == OVRHandSkeletonVersion.OpenXR
                ? (right ? OVRSkeleton.SkeletonType.XRHandRight : OVRSkeleton.SkeletonType.XRHandLeft)
                : (right ? OVRSkeleton.SkeletonType.HandRight : OVRSkeleton.SkeletonType.HandLeft);
            if (skeleton.GetSkeletonType() != expected)
                throw new InvalidOperationException("Hand skeleton setup did not match runtime settings. Wait for Meta editor initialization.");
            // Passthrough shows the user's real hands. Keep only data providers
            // active so a virtual hand cannot conceal the table measurement.
            var meshRenderer = handObject.GetComponent<OVRMeshRenderer>();
            if (meshRenderer != null) meshRenderer.enabled = false;
            var mesh = handObject.GetComponent<OVRMesh>();
            if (mesh != null) mesh.enabled = false;
            foreach (var renderer in handObject.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
            return hand;
        }

        static void ValidateQuestProject()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                throw new InvalidOperationException("Switch to Android before building this Quest diagnostic.");
            if (!EditorBuildSettings.TryGetConfigObject<XRGeneralSettingsPerBuildTarget>(XRGeneralSettings.k_SettingsKey, out var all))
                throw new InvalidOperationException("Run the existing Configure Quest OpenXR menu once before this test build.");
            var settings = all.SettingsForBuildTarget(BuildTargetGroup.Android);
            bool openXR = false;
            if (settings != null && settings.Manager != null)
                foreach (var loader in settings.Manager.activeLoaders)
                    if (loader != null && loader.GetType().FullName == "UnityEngine.XR.OpenXR.OpenXRLoader") openXR = true;
            if (!openXR || !settings.InitManagerOnStart || !settings.Manager.automaticLoading || !settings.Manager.automaticRunning)
                throw new InvalidOperationException("This test requires the existing Quest OpenXR loader with automatic startup enabled.");
            if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) != ScriptingImplementation.IL2CPP ||
                (PlayerSettings.Android.targetArchitectures & AndroidArchitecture.ARM64) == 0)
                throw new InvalidOperationException("This test requires IL2CPP and ARM64 Android settings.");
            var graphics = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            if (graphics.Length == 0 || graphics[0] != GraphicsDeviceType.Vulkan)
                throw new InvalidOperationException("This test requires the existing Vulkan Quest graphics configuration.");
        }

        static SerializedObject GraphicsSettingsObject() =>
            new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);

        static void PreserveShader(string name)
        {
            var graphics = GraphicsSettingsObject();
            var shaders = graphics.FindProperty("m_AlwaysIncludedShaders");
            var shader = Shader.Find(name);
            if (shader == null) throw new InvalidOperationException("Shader missing: " + name);
            for (int i = 0; i < shaders.arraySize; i++)
                if (shaders.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;
            shaders.InsertArrayElementAtIndex(shaders.arraySize);
            shaders.GetArrayElementAtIndex(shaders.arraySize - 1).objectReferenceValue = shader;
            graphics.ApplyModifiedPropertiesWithoutUndo();
        }

        [MenuItem("Word Story Demo/Table Depth Test/Build Separate Quest APK")]
        public static void BuildQuest()
        {
            ValidateQuestProject();
            var config = OVRProjectConfig.CachedProjectConfig;
            if (config == null) throw new InvalidOperationException("Wait for Meta editor initialization and retry.");
            string originalConfig = EditorJsonUtility.ToJson(config);
            string originalProduct = PlayerSettings.productName;
            string originalId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
            var originalHttp = PlayerSettings.insecureHttpOption;
            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
            var features = new Dictionary<UnityEngine.XR.OpenXR.Features.OpenXRFeature, bool>();
            foreach (var id in FeatureIds)
            {
                var feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(BuildTargetGroup.Android, id);
                if (feature != null) features[feature] = feature.enabled;
            }
            var graphics = GraphicsSettingsObject();
            var shaderProperty = graphics.FindProperty("m_AlwaysIncludedShaders");
            var originalShaders = new UnityEngine.Object[shaderProperty.arraySize];
            for (int i = 0; i < originalShaders.Length; i++) originalShaders[i] = shaderProperty.GetArrayElementAtIndex(i).objectReferenceValue;
            try
            {
                Setup();
                Tests.TableSurfaceVerification.Run();
                Tests.HandRegionProjectionVerification.Run();
                HandProximityVerification.Run();
                PlayerSettings.productName = "Story Bricks Table Depth Test";
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, PackageId);
                AssetDatabase.SaveAssets();
                Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                    scenes = new[] { ScenePath }, locationPathName = ApkPath,
                    target = BuildTarget.Android, options = BuildOptions.Development
                });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException("Table depth test build failed: " + report.summary.result);
                Debug.Log("TABLE_DEPTH_TEST_BUILD_OK " + Path.GetFullPath(ApkPath));
            }
            finally
            {
                PlayerSettings.productName = originalProduct;
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, originalId);
                PlayerSettings.insecureHttpOption = originalHttp;
                EditorJsonUtility.FromJsonOverwrite(originalConfig, config);
                OVRProjectConfig.CommitProjectConfig(config);
                foreach (var pair in features) { pair.Key.enabled = pair.Value; EditorUtility.SetDirty(pair.Key); }
                graphics.Update();
                shaderProperty = graphics.FindProperty("m_AlwaysIncludedShaders");
                shaderProperty.arraySize = originalShaders.Length;
                for (int i = 0; i < originalShaders.Length; i++) shaderProperty.GetArrayElementAtIndex(i).objectReferenceValue = originalShaders[i];
                graphics.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
            }
        }
    }
}
