using System;
using BrailleSoundDemo.PhysicalStory;
using UnityEngine;

namespace BrailleSoundDemo.Presentation
{
    /// <summary>Owns the selected experience, never the shared XR/capture rig.</summary>
    [DisallowMultipleComponent]
    public sealed class ExperienceRouter : MonoBehaviour
    {
        PhysicalStoryCoordinator core;
        ViewerFlow view;
        string experience;
        bool changingRoute;
        public bool HasView => view != null;

        public static ExperienceRouter Attach(PhysicalStoryCoordinator owner, OVRCameraRig rig)
        {
            var router=owner.GetComponent<ExperienceRouter>();
            if(router==null) router=owner.gameObject.AddComponent<ExperienceRouter>();
            router.Initialize(owner,rig);
            return router;
        }

        void Initialize(PhysicalStoryCoordinator owner,OVRCameraRig rig)
        {
            if(core!=null) return;
            core=owner;
            var prefab=Resources.Load<GameObject>("StoryBricks/View");
            if(prefab==null)
            {
                core.ShowExperienceMenu();
                Debug.LogError("[Experience] StoryBricks/View is missing. Run StoryBricksPresentationImport.Import before building.");
                return;
            }
            var root=Instantiate(prefab,rig.centerEyeAnchor,false);
            root.name="Story Bricks experience UI";
            root.transform.localPosition=new Vector3(0,0,1.05f);
            root.transform.localRotation=Quaternion.identity;
            root.transform.localScale=Vector3.one*.0007f;
            var canvas=root.GetComponentInChildren<Canvas>(true);
            canvas.renderMode=RenderMode.WorldSpace;
            canvas.worldCamera=rig.centerEyeAnchor.GetComponent<Camera>();
            view=root.GetComponentInChildren<ViewerFlow>(true);
            if(view==null) throw new InvalidOperationException("Story Bricks presentation prefab has no ViewerFlow.");
            var input=root.AddComponent<WorldCanvasHandInput>();
            input.Configure(canvas,rig,core);
            view.CreateWorldRequested+=SelectCreateWorld;
            core.SessionEnded+=OnSessionEnded;
            core.Flow.Changed+=OnFlowChanged;
            core.Flow.StoryNarrationCompleted+=OnLiveNarrationCompleted;
            core.ShowExperienceMenu();
            view.ShowMenu();
        }

        public void SelectCreateWorld()
        {
            if(view==null || changingRoute) return;
            changingRoute=true;
            try
            {
                if(!core.BeginExperience("demo")) return;
                experience=core.ExperienceSessionId;
                // Close the imported menu. Create World keeps the existing physical
                // capture, progress hint and Finish UI, without the prototype story screens.
                view.BeginRoute("demo");
                core.SessionStore?.SetStage("building");
                Debug.Log("[Experience] Selected demo session="+experience);
            }
            finally { changingRoute=false; }
        }

        bool Active(string workspace)=>!changingRoute && core!=null && core.ExperienceActive &&
            core.WorkspaceId==workspace && core.ExperienceSessionId==experience;

        void OnFlowChanged()
        {
            if(!Active("demo")) return;
            var flow=core.Flow;
            switch(flow.State)
            {
                case ConversationState.RequestingStory:
                    core.SessionStore?.SetStage("story_generating"); break;
                case ConversationState.Story:
                    core.SessionStore?.SetStage("story_playing"); break;
                case ConversationState.Error:
                    core.SessionStore?.SetStage("error"); break;
            }
        }
        void OnLiveNarrationCompleted(StoryOutput output)
        {
            if(!Active("demo") || output.experienceSessionId!=experience) return;
            core.SessionStore?.SetStage("next_stage");
        }
        public void RestartSelectedExperience()
        {
            if(Active("demo")) SelectCreateWorld();
        }
        void OnSessionEnded()
        {
            experience=null;
            view?.ShowMenu();
        }
        void OnDestroy()
        {
            if(core!=null)
            {
                core.SessionEnded-=OnSessionEnded;
                if(core.Flow!=null)
                {
                    core.Flow.Changed-=OnFlowChanged;
                    core.Flow.StoryNarrationCompleted-=OnLiveNarrationCompleted;
                }
            }
            if(view!=null) view.CreateWorldRequested-=SelectCreateWorld;
        }
    }
}
