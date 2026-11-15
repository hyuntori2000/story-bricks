using System;
using System.IO;
using UnityEditor.SceneManagement;
using TMPro;
using UnityEditor;
using UnityEngine;
using BrailleSoundDemo.Presentation;

namespace BrailleSoundDemo.Editor
{
    public static class BuildExperienceDemo
    {
        [MenuItem("Word Story Demo/Build Judy and Create World")]
        public static void BuildQuest()
        {
            StoryBricksPresentationImport.Import();
            if(TMP_Settings.instance==null) throw new InvalidOperationException("Imported UX requires Resources/TMP Settings.asset.");
            var view=Resources.Load<GameObject>("StoryBricks/View");
            var sequence=Resources.Load<JudyCueSequence>("StoryBricks/JudySequence");
            if(view==null || view.GetComponentInChildren<ViewerFlow>(true)==null || sequence==null)
                throw new InvalidOperationException("Judy/Create World presentation assets are missing.");
            if(view.GetComponentsInChildren<Camera>(true).Length!=0 || view.GetComponentsInChildren<AudioListener>(true).Length!=0 ||
                view.GetComponentsInChildren<OVRCameraRig>(true).Length!=0)
                throw new InvalidOperationException("The imported view must not own an XR rig, camera, or listener.");
            SaveMenuPreview(view);
            BuildPhysicalStoryDemo.BuildQuest();
        }

        public static void Preview()
        {
            SaveMenuPreview(Resources.Load<GameObject>("StoryBricks/View"));
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
                foreach(var group in new[]{view.hint,view.caption,view.subtitle,view.ready,view.end,view.pulse})
                    if(group!=null) group.gameObject.SetActive(false);
                foreach(var tag in view.tags) tag.root.gameObject.SetActive(false);
                view.menu.gameObject.SetActive(true); view.menu.alpha=1;
                foreach(var text in view.newStoryButton.GetComponentsInChildren<TMP_Text>(true))
                    if(text.name=="Title") text.text="Create World"; else if(text.name=="Sub") text.text="Build your own story";
                foreach(var text in view.savedStoryButton.GetComponentsInChildren<TMP_Text>(true))
                    if(text.name=="Title") text.text="Judy"; else if(text.name=="Sub") text.text="Bring the prepared story to life";
                var cameraObject=new GameObject("Preview camera");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject,scene);
                var camera=cameraObject.AddComponent<Camera>();
                camera.scene=scene;
                camera.transform.position=new Vector3(0,0,-1);
                camera.orthographic=true; camera.orthographicSize=.32f;
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
