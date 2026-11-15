using System;
using System.IO;
using UnityEditor.SceneManagement;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace BrailleSoundDemo.Editor
{
    public static class BuildExperienceDemo
    {
        [MenuItem("Word Story Demo/Build Judy and Create World")]
        public static void BuildQuest()
        {
            StoryBricksPresentationImport.Import();
            var opening=Resources.Load<GameObject>("StoryBricks/Opening")?.GetComponent<OpeningAnimation>();
            var onboardingVoice=Resources.Load<AudioClip>("StoryAudio/onboarding_ready");
            if(opening==null || opening.baseMaterial==null || opening.font==null ||
                onboardingVoice==null || onboardingVoice.length<=0)
                throw new InvalidOperationException("Accessible onboarding needs its authored opening, font and narrator clip.");
            if(opening.GetComponentsInChildren<Camera>(true).Length!=0 ||
                opening.GetComponentsInChildren<AudioListener>(true).Length!=0 ||
                opening.GetComponentsInChildren<OVRCameraRig>(true).Length!=0)
                throw new InvalidOperationException("The opening must use the shared XR camera and audio listener.");
            if(TMP_Settings.instance==null) throw new InvalidOperationException("Imported UX requires Resources/TMP Settings.asset.");
            var view=Resources.Load<GameObject>("StoryBricks/View");
            if(view==null || view.GetComponentInChildren<ViewerFlow>(true)==null)
                throw new InvalidOperationException("Judy/Create World presentation assets are missing.");
            if(view.GetComponentsInChildren<Camera>(true).Length!=0 || view.GetComponentsInChildren<AudioListener>(true).Length!=0 ||
                view.GetComponentsInChildren<OVRCameraRig>(true).Length!=0)
                throw new InvalidOperationException("The imported view must not own an XR rig, camera, or listener.");
            ValidateIntegratedView(view);
            SaveMenuPreview(view);
            Tests.GuidedDialogueVerification.Run();
            BuildPhysicalStoryDemo.BuildQuest();
        }

        public static void Preview()
        {
            SaveMenuPreview(Resources.Load<GameObject>("StoryBricks/View"));
        }

        static void ValidateIntegratedView(GameObject prefab)
        {
            var view=prefab.GetComponentInChildren<ViewerFlow>(true);
            if(view.stage==null || view.narrationClip==null || view.storySfxClip==null ||
                Mathf.Abs(view.storyEnd-61.5f)>.05f || view.narrationClip.length<59f)
                throw new InvalidOperationException("Judy needs the supplied 61.5-second cutout stage, narration and sound track.");
            if(view.newStoryButton==null || view.savedStoryButton==null || view.tutorialButton==null ||
                view.watchAgainButton==null || view.doneButton==null || view.restartButton==null)
                throw new InvalidOperationException("The integrated view is missing a navigation button.");
            foreach(var action in new[]{"home","create_story"})
                if(Resources.Load<Texture2D>("StoryBricks/Buttons/"+action)==null)
                    throw new InvalidOperationException("The painted story action is missing: "+action);
            if(prefab.GetComponentsInChildren<UnityEngine.Video.VideoPlayer>(true).Length!=0)
                throw new InvalidOperationException("Judy must use its authored Unity components, not the unused 12-second clip.");
            foreach(var component in prefab.GetComponentsInChildren<Transform>(true))
                if(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(component.gameObject)>0)
                    throw new InvalidOperationException("Missing presentation script on "+component.name);
            Debug.Log("INTEGRATED_UX_ASSETS_OK Judy=61.5s cutouts; CreateWorld=live; shared rig preserved.");
        }

        static void SaveMenuPreview(GameObject prefab)
        {
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            RenderTexture target=null;
            Texture2D image=null;
            try
            {
                var root=(GameObject)PrefabUtility.InstantiatePrefab(prefab,scene);
                root.transform.position=Vector3.zero;
                root.transform.rotation=Quaternion.identity;
                root.transform.localScale=Vector3.one*.001f;
                var view=root.GetComponentInChildren<ViewerFlow>(true);
                foreach(var group in new[]{view.hint,view.caption,view.subtitle,view.ready,view.end,view.pulse,
                    view.finish,view.fox,view.bricks,view.pinch,view.period})
                    if(group!=null) group.gameObject.SetActive(false);
                if(view.stage!=null) view.stage.gameObject.SetActive(false);
                if(view.tutorialButton!=null) view.tutorialButton.gameObject.SetActive(true);
                foreach(var tag in view.tags) tag.root.gameObject.SetActive(false);
                view.menu.gameObject.SetActive(true); view.menu.alpha=1;
                foreach(var text in view.newStoryButton.GetComponentsInChildren<TMP_Text>(true))
                    if(text.name=="Title") text.text="Create Your World"; else if(text.name=="Sub") text.text="Build your own story";
                foreach(var text in view.savedStoryButton.GetComponentsInChildren<TMP_Text>(true))
                    if(text.name=="Title") text.text="Judy's Story"; else if(text.name=="Sub") text.text="Watch Judy's world come to life";
                foreach(var text in view.tutorialButton.GetComponentsInChildren<TMP_Text>(true))
                    if(text.name=="Title" || text.name=="Label") text.text="See previous story"; else if(text.name=="Sub") text.text="Watch your saved animations";
                view.savedStoryButton.interactable=true;
                var cameraObject=new GameObject("Preview camera");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject,scene);
                var camera=cameraObject.AddComponent<Camera>();
                camera.scene=scene;
                camera.transform.position=new Vector3(0,0,-1);
                camera.orthographic=true;
                var viewport=root.GetComponent<RectTransform>().rect.size;
                camera.orthographicSize=Mathf.Max(viewport.y,viewport.x*.75f)*.001f*.55f;
                camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(.87f,.86f,.83f);
                target=new RenderTexture(1200,900,24); target.Create(); camera.targetTexture=target;
                var canvas=root.GetComponent<Canvas>(); canvas.worldCamera=camera;
                Canvas.ForceUpdateCanvases();
                foreach(var text in root.GetComponentsInChildren<TMP_Text>(false))
                {
                    text.ForceMeshUpdate(true,true);
                    text.Rebuild(UnityEngine.UI.CanvasUpdate.PreRender);
                    if(!string.IsNullOrWhiteSpace(text.text) && (text.textInfo==null || text.textInfo.characterCount==0 || text.mesh==null))
                        throw new InvalidOperationException("The imported menu text did not produce a renderable font mesh: "+text.name);
                    Debug.Log("[View preview] text="+text.text+" chars="+(text.textInfo!=null?text.textInfo.characterCount:-1)+" vertices="+(text.mesh!=null?text.mesh.vertexCount:-1)+
                        " rect="+text.rectTransform.rect+" font="+(text.font!=null?text.font.name:"missing")+
                        " shader="+(text.fontSharedMaterial!=null?text.fontSharedMaterial.shader.name:"missing")+
                        " culled="+text.canvasRenderer.cull+" alpha="+text.canvasRenderer.GetAlpha());
                }
                Canvas.ForceUpdateCanvases();
                camera.Render();
                var previous=RenderTexture.active;
                try
                {
                    RenderTexture.active=target;
                    image=new Texture2D(1200,900,TextureFormat.RGB24,false);
                    image.ReadPixels(new Rect(0,0,1200,900),0,0); image.Apply();
                    Directory.CreateDirectory("runtime/experience-preview");
                    File.WriteAllBytes("runtime/experience-preview/menu.png",image.EncodeToPNG());
                }
                finally { RenderTexture.active=previous; }
            }
            finally
            {
                if(image!=null) UnityEngine.Object.DestroyImmediate(image);
                if(target!=null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            }
        }
    }
}
