using System;
using System.IO;
using BrailleSoundDemo.CameraTest;
using Meta.XR;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;

namespace BrailleSoundDemo.Editor
{
    public static class BuildCameraTest
    {
        public const string ScenePath = "Assets/Scenes/ManualColorCameraTest.unity";
        public const string PackageId = "org.braillelab.cameratest";
        public const string ApkPath = "Builds/Quest/WordsToWorldsCameraTest.apk";
        const string Core = "Packages/com.meta.xr.sdk.core/";

        [MenuItem("Word Story Demo/Camera Test/Create or Open")]
        public static void Setup()
        {
            BuildDemo.ConfigureQuest();
            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
            var feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(BuildTargetGroup.Android, MetaXRFeature.featureId);
            if (feature == null) throw new InvalidOperationException("Meta XR OpenXR feature was not found after package import.");
            feature.enabled = true; EditorUtility.SetDirty(feature);
            var config = OVRProjectConfig.CachedProjectConfig;
            if (config == null) throw new InvalidOperationException("Meta editor initialization is incomplete. Let package import finish and run the build again.");
            config.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersOnly;
            config.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required;
            config.isPassthroughCameraAccessEnabled = true;
            config.enableNSCConfig = true;
            config.securityXmlPath = Path.GetFullPath("Assets/BrailleSoundDemo/Editor/CameraTestNetworkSecurity.xml");
            OVRProjectConfig.CommitProjectConfig(config);
            // Sensor metadata and the floor plane share this identity world origin.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var rigObject = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Core+"Prefabs/OVRCameraRig.prefab"));
            if (rigObject == null) throw new InvalidOperationException("OVRCameraRig prefab not found.");
            rigObject.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity); rigObject.transform.localScale=Vector3.one;
            var rig = rigObject.GetComponent<OVRCameraRig>();
            var manager = rigObject.GetComponent<OVRManager>();
            manager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
            manager.isInsightPassthroughEnabled = true;
            manager.wideMotionModeHandPosesEnabled = false;
            manager.controllerDrivenHandPosesType = OVRManager.ControllerDrivenHandPosesType.None;
            foreach(var eye in rigObject.GetComponentsInChildren<Camera>(true))
            { eye.clearFlags=CameraClearFlags.SolidColor; eye.backgroundColor=new Color(0,0,0,0); eye.allowHDR=false; eye.nearClipPlane=.05f; }
            // One permission owner: ManualColorCameraDemo requests HEADSET_CAMERA at runtime.
            var managerData = new SerializedObject(manager);
            foreach(var field in new[]{"requestPassthroughCameraAccessPermissionOnStartup","requestRecordAudioPermissionOnStartup","requestScenePermissionOnStartup"})
            { var p=managerData.FindProperty(field); if(p!=null) p.boolValue=false; }
            managerData.ApplyModifiedPropertiesWithoutUndo();
            rigObject.AddComponent<OVRPassthroughLayer>();
            var root=new GameObject("Manual Color Camera Test");
            var cameraObject=new GameObject("Left RGB camera access"); cameraObject.transform.SetParent(root.transform,false);
            var camera=cameraObject.AddComponent<PassthroughCameraAccess>(); camera.enabled=false;
            camera.CameraPosition=PassthroughCameraAccess.CameraPositionType.Left;
            camera.RequestedResolution=new Vector2Int(1280,960);
            camera.MaxFramerate=30;
            var sender=root.AddComponent<QuestFrameCapture>();
            var app=root.AddComponent<ManualColorCameraDemo>();
            app.rig=rig;
            app.cameraAccess=camera; app.capture=sender;
            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene,ScenePath);
            // USB adb reverse reaches the local Python receiver over HTTP.
            // Meta's separate network config above permits only the loopback host.
            // Restore both project settings after this test build.
            PlayerSettings.insecureHttpOption=InsecureHttpOption.AlwaysAllowed;
            PreserveShader("Unlit/Color");
            AssetDatabase.SaveAssets();
            Debug.Log("CAMERA_TEST_SETUP_OK "+ScenePath);
        }

        static void PreserveShader(string name)
        {
            var graphics=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            var shaders=graphics.FindProperty("m_AlwaysIncludedShaders"); var shader=Shader.Find(name);
            if(shader==null) throw new InvalidOperationException("Shader missing: "+name);
            for(int i=0;i<shaders.arraySize;i++) if(shaders.GetArrayElementAtIndex(i).objectReferenceValue==shader) return;
            shaders.InsertArrayElementAtIndex(shaders.arraySize); shaders.GetArrayElementAtIndex(shaders.arraySize-1).objectReferenceValue=shader;
            graphics.ApplyModifiedPropertiesWithoutUndo();
        }

        [MenuItem("Word Story Demo/Camera Test/Build Separate Quest APK")]
        public static void BuildQuest()
        {
            if(EditorUserBuildSettings.activeBuildTarget!=BuildTarget.Android) throw new InvalidOperationException("Switch to Android before building.");
            var originalProduct=PlayerSettings.productName;
            var originalId=PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
            var originalHttp=PlayerSettings.insecureHttpOption;
            var config=OVRProjectConfig.CachedProjectConfig;
            if(config==null) throw new InvalidOperationException("Wait for Meta editor initialization, then run again.");
            var originalConfig=EditorJsonUtility.ToJson(config);
            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
            var metaFeature=FeatureHelpers.GetFeatureWithIdForBuildTarget(BuildTargetGroup.Android,MetaXRFeature.featureId);
            bool originalFeatureEnabled=metaFeature!=null && metaFeature.enabled;
            try
            {
                Setup();
                Tests.CaptureProjectionVerification.Run();
                PlayerSettings.productName="Words to Worlds Color Test";
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android,PackageId);
                AssetDatabase.SaveAssets();
                Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
                var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes=new[]{ScenePath},locationPathName=ApkPath,target=BuildTarget.Android,options=BuildOptions.Development });
                if(report.summary.result!=BuildResult.Succeeded) throw new InvalidOperationException("Camera test build failed: "+report.summary.result);
                Debug.Log("CAMERA_TEST_BUILD_OK "+Path.GetFullPath(ApkPath));
            }
            finally
            {
                PlayerSettings.productName=originalProduct;
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android,originalId);
                PlayerSettings.insecureHttpOption=originalHttp;
                EditorJsonUtility.FromJsonOverwrite(originalConfig,config);
                OVRProjectConfig.CommitProjectConfig(config);
                if(metaFeature!=null) { metaFeature.enabled=originalFeatureEnabled; EditorUtility.SetDirty(metaFeature); }
                AssetDatabase.SaveAssets();
            }
        }
    }
}
