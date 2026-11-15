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
        GameObject viewRoot;
        WorldCanvasHandInput menuInput;
        AccessibleOnboarding onboarding;
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
            viewRoot=root;
            root.transform.localPosition=new Vector3(0,0,1.05f);
            root.transform.localRotation=Quaternion.identity;
            root.transform.localScale=Vector3.one*.0007f;
            var canvas=root.GetComponentInChildren<Canvas>(true);
            canvas.renderMode=RenderMode.WorldSpace;
            canvas.worldCamera=rig.centerEyeAnchor.GetComponent<Camera>();
            view=root.GetComponentInChildren<ViewerFlow>(true);
            if(view==null) throw new InvalidOperationException("Story Bricks presentation prefab has no ViewerFlow.");
            menuInput=root.AddComponent<WorldCanvasHandInput>();
            menuInput.Configure(canvas,rig,core);
            onboarding=gameObject.AddComponent<AccessibleOnboarding>();
            onboarding.Configure(rig);
            onboarding.Ready+=OnOnboardingReady;
            onboarding.EnterRequested+=SelectCreateWorld;
            view.CreateWorldRequested+=SelectCreateWorld;
            core.SessionEnded+=OnSessionEnded;
            core.Flow.Changed+=OnFlowChanged;
            core.Flow.StoryNarrationCompleted+=OnLiveNarrationCompleted;
            BeginOnboarding(true);
        }

        public void SelectCreateWorld()
        {
            if(view==null || changingRoute) return;
            changingRoute=true;
            try
            {
                if(!core.BeginExperience("demo")) return;
                onboarding.Cancel();
                core.SetPresentationInputBlocked(false);
                viewRoot.SetActive(true);
                menuInput.enabled=true;
                experience=core.ExperienceSessionId;
                // Close the imported menu. Create World keeps the existing physical
                // capture, progress hint and Finish UI, without the prototype story screens.
                view.BeginRoute("demo");
                // This story-building introduction belongs to the newly selected experience,
                // after the separate opening and accessibility prompt have finished.
                bool started=core.Flow.BeginGuidedExperience();
                core.SessionStore?.SetStage(started ? "introduction" : "error");
                if(!started) Debug.LogError("[Experience] Could not start the guided introduction for the new experience.");
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
                case ConversationState.Ready:
                case ConversationState.Silent:
                    core.SessionStore?.SetStage("building"); break;
                case ConversationState.RequestingQuestion:
                    core.SessionStore?.SetStage("dialogue_generating"); break;
                case ConversationState.Asking:
                    core.SessionStore?.SetStage("dialogue_playing"); break;
                case ConversationState.PreparingAnswer:
                case ConversationState.Listening:
                    core.SessionStore?.SetStage("listening"); break;
                case ConversationState.ReceivingAnswer:
                    core.SessionStore?.SetStage("answer_saving"); break;
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
            BeginOnboarding(false);
        }
        void BeginOnboarding(bool playOpening)
        {
            core.ShowExperienceMenu();
            menuInput.enabled=false;
            viewRoot.SetActive(false);
            core.SetPresentationInputBlocked(true);
            onboarding.Begin(playOpening);
        }
        void OnOnboardingReady()
        {
            if(core==null || core.ExperienceActive) return;
            viewRoot.SetActive(true);
            view.ShowMenu();
            menuInput.enabled=true;
            core.SetPresentationInputBlocked(false);
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
            if(onboarding!=null)
            {
                onboarding.Ready-=OnOnboardingReady;
                onboarding.EnterRequested-=SelectCreateWorld;
                onboarding.Cancel();
            }
            if(view!=null) view.CreateWorldRequested-=SelectCreateWorld;
        }
    }
}
