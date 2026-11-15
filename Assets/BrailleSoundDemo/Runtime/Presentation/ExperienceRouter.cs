using System;
using System.Collections.Generic;
using BrailleSoundDemo.PhysicalStory;
using UnityEngine;

namespace BrailleSoundDemo.Presentation
{
    /// <summary>Connects the supplied UX to either authored Judy playback or the real Create World session.</summary>
    [DisallowMultipleComponent]
    public sealed class ExperienceRouter : MonoBehaviour
    {
        PhysicalStoryCoordinator core;
        ViewerFlow view;
        GameObject viewRoot;
        WorldCanvasHandInput menuInput;
        AccessibleOnboarding onboarding;
        StoryVideoPipeline videos;
        StoryLibraryBrowser library;
        Transform head;
        readonly HashSet<string> displayedWords=new HashSet<string>(StringComparer.Ordinal);
        string experience,route="",lastCaption="",lastVideoStage="idle";
        bool changingRoute,applicationPaused,applicationFocused=true;
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
            head=rig.centerEyeAnchor;
            var prefab=Resources.Load<GameObject>("StoryBricks/View");
            if(prefab==null)
            {
                core.ShowExperienceMenu();
                Debug.LogError("[Experience] StoryBricks/View is missing. Run StoryBricksPresentationImport.Import before building.");
                return;
            }
            viewRoot=Instantiate(prefab,rig.centerEyeAnchor,false);
            viewRoot.name="Story Bricks experience UI";
            viewRoot.transform.localPosition=new Vector3(0,0,1.05f);
            viewRoot.transform.localRotation=Quaternion.identity;
            viewRoot.transform.localScale=Vector3.one*(.0007f*1.2f); // UX size trial; session action buttons have a separate root.
            var canvas=viewRoot.GetComponentInChildren<Canvas>(true);
            canvas.renderMode=RenderMode.WorldSpace;
            canvas.worldCamera=rig.centerEyeAnchor.GetComponent<Camera>();
            view=viewRoot.GetComponentInChildren<ViewerFlow>(true);
            if(view==null) throw new InvalidOperationException("Story Bricks presentation prefab has no ViewerFlow.");
            menuInput=viewRoot.AddComponent<WorldCanvasHandInput>();
            menuInput.Configure(canvas,rig,core);
            onboarding=gameObject.AddComponent<AccessibleOnboarding>();
            onboarding.Configure(rig);
            onboarding.RouteGestureAllowed=()=>route=="" && !core.PresentationInputBlocked;
            onboarding.Ready+=OnOnboardingReady;
            onboarding.EnterRequested+=SelectCreateWorld;
            view.CreateWorldRequested+=SelectCreateWorld;
            view.JudyRequested+=SelectJudy;
            view.BackToMenuRequested+=ReturnToMenu;
            view.ReplayRequested+=Replay;
            view.PreviousStoriesRequested+=SelectPreviousStories;
            library=viewRoot.AddComponent<StoryLibraryBrowser>();
            library.Configure(view);
            library.Selected+=SelectSavedStory;
            library.BackRequested+=ReturnToMenu;
            core.SessionEnded+=OnSessionEnded;
            core.StatusChanged+=OnCaptureStatusChanged;
            core.UsePresentationStatus(true);
            core.Flow.Changed+=OnFlowChanged;
            core.Flow.StoryNarrationCompleted+=OnLiveNarrationCompleted;
            PresentationEvents.Published+=OnPresentationEvent;
            videos=core.GetComponent<StoryVideoPipeline>();
            if(videos!=null)
            {
                videos.SetPresentationTarget(view.liveVideoImage);
                videos.PresentationChanged+=OnVideoPresentationChanged;
            }
            applicationFocused=Application.isFocused;
            BeginOnboarding(true);
        }

        public void SelectCreateWorld()
        {
            if(view==null || changingRoute) return;
            changingRoute=true;
            try
            {
                if(!core.BeginExperience("demo")) return;
                library.Close();
                onboarding.Cancel();
                view.StopJudy();
                core.SetPresentationInputBlocked(false);
                viewRoot.SetActive(true);
                menuInput.enabled=true;
                route="demo";experience=core.ExperienceSessionId;
                displayedWords.Clear();lastCaption="";lastVideoStage="idle";
                view.BeginRoute("demo");
                bool started=core.Flow.BeginGuidedExperience();
                core.SessionStore?.SetStage(started ? "introduction" : "error");
                if(!started) Debug.LogError("[Experience] Could not start the guided introduction for the new experience.");
                Debug.Log("[Experience] Selected demo session="+experience);
            }
            finally { changingRoute=false; }
            OnFlowChanged();
        }

        public void SelectJudy()
        {
            if(view==null || changingRoute) return;
            changingRoute=true;
            try
            {
                // A Judy viewing has no recognition/conversation session or API requests.
                // Ending a live session first also invalidates every queued live callback.
                if(core.ExperienceActive) core.EndSessionToStart();
                onboarding.Cancel();library.Close();view.StopJudy();core.ShowExperienceMenu();
                experience=null;route="judy";displayedWords.Clear();lastCaption="";lastVideoStage="idle";
                viewRoot.SetActive(true);menuInput.enabled=true;
                view.BeginRoute("judy");
                view.SetPaused(applicationPaused || !applicationFocused);
                view.PlayJudy();
                Debug.Log("[Experience] Selected authored Judy animation.");
            }
            finally { changingRoute=false; }
        }

        public void ReturnToMenu()
        {
            if(view==null || changingRoute) return;
            view.StopJudy();
            if(core.ExperienceActive) core.EndSessionToStart();
            else BeginOnboarding(false);
        }

        public void SelectPreviousStories()
        {
            if(view==null || changingRoute) return;
            changingRoute=true;
            try
            {
                if(core.ExperienceActive) core.EndSessionToStart();
                onboarding.Cancel();view.StopJudy();core.ShowExperienceMenu();
                experience=null;route="library";displayedWords.Clear();lastCaption="";lastVideoStage="idle";
                viewRoot.SetActive(true);menuInput.enabled=true;view.BeginRoute("library");
                library.Open();
                Debug.Log("[Experience] Browsing saved Mac stories; capture and conversation remain inactive.");
            }
            finally { changingRoute=false; }
        }

        void SelectSavedStory(StoryLibraryItem item)
        {
            if(changingRoute || route!="library" || core.ExperienceActive || item==null) return;
            if(videos==null || !videos.BeginSaved(item,head,()=>route=="library" && !core.ExperienceActive))
                library.ShowPlaybackError("This saved animation could not open. Return to the menu and try again.");
        }

        public void Replay()
        {
            if(changingRoute || view==null) return;
            if(route=="judy" && !core.ExperienceActive) view.PlayJudy();
            else if((Active("demo") || (route=="library" && !core.ExperienceActive)) && videos!=null && !videos.Replay())
                ShowCaption("Your animation is not ready to replay yet.");
        }

        bool Active(string workspace)=>!changingRoute && route==workspace && core!=null && core.ExperienceActive &&
            core.WorkspaceId==workspace && core.ExperienceSessionId==experience;

        void OnPresentationEvent(PresentationEventContext context)
        {
            if(!Active("demo") || context.preview || context.workspaceId!="demo" || context.sessionId!=experience) return;
            if(context.eventName=="word.accepted" && !string.IsNullOrWhiteSpace(context.word) &&
                !string.IsNullOrEmpty(context.groupId) && displayedWords.Add(context.groupId))
                view.ShowWord(context.word);
        }

        void ShowCaption(string message)
        {
            message=message ?? "";
            if(lastCaption==message) return;
            lastCaption=message;view.ShowCaption(message);
        }

        void OnCaptureStatusChanged(string message)
        {
            if(!Active("demo")) return;
            // Questions and listening instructions retain priority while another photo is queued.
            if(core.Flow.State==ConversationState.Ready || core.Flow.State==ConversationState.Silent ||
                core.Flow.State==ConversationState.Error)
            {
                ShowCaption(message);
                view.SetBusy(core.WaitingForRecognition);
            }
        }

        void OnFlowChanged()
        {
            if(!Active("demo")) return;
            var flow=core.Flow;
            // Animation status is owned by the actual video job, never by a UI timer.
            if(flow.State==ConversationState.Finished && videos!=null && videos.Stage!="idle") return;
            bool listening=flow.State==ConversationState.PreparingAnswer || flow.State==ConversationState.Listening;
            bool busy=flow.State==ConversationState.RequestingQuestion || flow.State==ConversationState.ReceivingAnswer ||
                flow.State==ConversationState.RequestingStory || core.WaitingForRecognition;
            view.SetListening(listening);view.SetBusy(busy);
            bool showPrompt=flow.State==ConversationState.Asking || flow.State==ConversationState.PreparingAnswer ||
                flow.State==ConversationState.Listening || flow.State==ConversationState.PromptingNextWord;
            ShowCaption(showPrompt && !string.IsNullOrWhiteSpace(flow.CurrentPrompt) ? flow.CurrentPrompt : flow.Status);
            switch(flow.State)
            {
                case ConversationState.Ready:
                case ConversationState.Silent:
                    core.SessionStore?.SetStage("building"); break;
                case ConversationState.RequestingQuestion:
                    core.SessionStore?.SetStage("dialogue_generating"); break;
                case ConversationState.PlayingWord:
                case ConversationState.Asking:
                case ConversationState.PromptingNextWord:
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

        void OnVideoPresentationChanged(string stage,string message)
        {
            if(!Active("demo") && !(route=="library" && !core.ExperienceActive)) return;
            bool stageChanged=lastVideoStage!=stage;
            lastVideoStage=stage;
            view.SetListening(false);
            switch(stage)
            {
                case "animation_playing":
                    view.SetBusy(false);view.ShowLiveVideo(true);lastCaption="";break;
                case "animation_complete":
                    view.SetBusy(false);view.ShowLiveEnd("Share your story in video!");lastCaption="";break;
                case "animation_failed":
                    view.SetBusy(false);view.ShowLiveVideo(false);
                    if(route=="library") library.ShowPlaybackError("This saved animation could not play. Check the Mac connection, then return to the menu and try again.");
                    else ShowCaption(message);
                    break;
                default:
                    if(stageChanged) view.ShowLiveVideo(false);
                    view.ShowNextStage(message);view.SetBusy(true);lastCaption="";break;
            }
        }

        void OnLiveNarrationCompleted(StoryOutput output)
        {
            if(!Active("demo") || output==null || output.experienceSessionId!=experience) return;
            if(!core.BeginStoryVideo(output))
            {
                core.SessionStore?.SetStage("animation_unavailable");
                view.SetBusy(false);ShowCaption("Your story is saved. Animation is currently unavailable.");
            }
        }
        public void RestartSelectedExperience()
        {
            if(route=="judy") view.PlayJudy();
            else if(Active("demo")) SelectCreateWorld();
        }
        void OnSessionEnded()
        {
            experience=null;
            if(!changingRoute)
            {
                BeginOnboarding(false);
                // Home shows the landing choices immediately; the existing spoken
                // invitation can finish in parallel and still enable gesture entry.
                OnOnboardingReady();
            }
        }
        void BeginOnboarding(bool playOpening)
        {
            route="";experience=null;displayedWords.Clear();lastCaption="";lastVideoStage="idle";
            library?.Close();view.StopJudy();core.ShowExperienceMenu();
            menuInput.enabled=false;viewRoot.SetActive(false);
            core.SetPresentationInputBlocked(true);
            onboarding.Begin(playOpening);
        }
        void OnOnboardingReady()
        {
            if(core==null || core.ExperienceActive || route!="") return;
            viewRoot.SetActive(true);view.ShowMenu();menuInput.enabled=true;
            core.SetPresentationInputBlocked(false);
        }
        void OnApplicationPause(bool paused)
        {
            applicationPaused=paused;view?.SetPaused(applicationPaused || !applicationFocused);
        }
        void OnApplicationFocus(bool focused)
        {
            applicationFocused=focused;view?.SetPaused(applicationPaused || !applicationFocused);
        }
        void OnDestroy()
        {
            PresentationEvents.Published-=OnPresentationEvent;
            if(library!=null) { library.Selected-=SelectSavedStory;library.BackRequested-=ReturnToMenu;library.Close(); }
            if(videos!=null) { videos.PresentationChanged-=OnVideoPresentationChanged;videos.SetPresentationTarget(null); }
            if(core!=null)
            {
                core.SessionEnded-=OnSessionEnded;core.StatusChanged-=OnCaptureStatusChanged;
                core.UsePresentationStatus(false);
                if(core.Flow!=null)
                {
                    core.Flow.Changed-=OnFlowChanged;
                    core.Flow.StoryNarrationCompleted-=OnLiveNarrationCompleted;
                }
            }
            if(onboarding!=null)
            {
                onboarding.Ready-=OnOnboardingReady;onboarding.EnterRequested-=SelectCreateWorld;onboarding.Cancel();
            }
            if(view!=null)
            {
                view.CreateWorldRequested-=SelectCreateWorld;view.JudyRequested-=SelectJudy;
                view.BackToMenuRequested-=ReturnToMenu;view.ReplayRequested-=Replay;view.StopJudy();
                view.PreviousStoriesRequested-=SelectPreviousStories;
            }
        }
    }
}
