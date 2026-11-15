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
        JudySequencePlayer judy;
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
            judy=gameObject.AddComponent<JudySequencePlayer>();
            judy.CueChanged+=OnCueChanged;
            judy.CaptionChanged+=OnJudyCaption;
            judy.StoryStarted+=OnJudyStoryStarted;
            judy.StoryCompleted+=OnJudyStoryCompleted;
            view.JudyRequested+=SelectJudy;
            view.CreateWorldRequested+=SelectCreateWorld;
            core.PreparedStoryCreator=CreatePreparedStory;
            core.PreparedStorySubmitted=()=>judy!=null && judy.StoryRequested;
            core.SessionEnded+=OnSessionEnded;
            core.SpatialReferenceReset+=OnSpatialReferenceReset;
            core.Flow.Changed+=OnFlowChanged;
            core.Flow.StoryOutputReady+=OnLiveStoryReady;
            core.Flow.StoryNarrationCompleted+=OnLiveNarrationCompleted;
            PresentationEvents.Published+=OnPresentation;
            core.ShowExperienceMenu();
            view.ShowMenu();
        }

        public void SelectJudy()=>Select("judy");
        public void SelectCreateWorld()=>Select("demo");
        void Select(string workspace)
        {
            if(view==null || changingRoute) return;
            changingRoute=true;
            try
            {
                judy.Cancel();
                if(!core.BeginExperience(workspace)) return;
                experience=core.ExperienceSessionId;
                view.BeginRoute(workspace);
                if(workspace=="judy") judy.Begin(experience);
                core.SessionStore?.SetStage("building");
                Debug.Log("[Experience] Selected "+workspace+" session="+experience);
            }
            finally { changingRoute=false; }
        }

        bool Active(string workspace)=>!changingRoute && core!=null && core.ExperienceActive &&
            core.WorkspaceId==workspace && core.ExperienceSessionId==experience;

        void OnPresentation(PresentationEventContext evt)
        {
            if(evt==null || !Active(evt.workspaceId) || evt.sessionId!=experience) return;
            if(evt.eventName=="word.accepted")
            {
                if(evt.word!=".") view.ShowWord(evt.word);
                if(evt.workspaceId=="judy")
                {
                    if(evt.word==".") CreatePreparedStory();
                    else
                    {
                        bool accepted=judy.AcceptWord(evt.word,evt.groupId,evt.anchor);
                        // Recognition remains unchanged; a period closes the authored sequence
                        // only after the accepted word's queued cue has finished.
                        if(accepted && evt.word.EndsWith(".",StringComparison.Ordinal)) CreatePreparedStory();
                    }
                }
            }
        }

        bool CreatePreparedStory()
        {
            if(!Active("judy") || core.SavedWordCount==0) return false;
            return judy.CreateStory();
        }

        void OnCueChanged(string cue,string status,string word)
        {
            if(!Active("judy")) return;
            core.SessionStore?.RecordCue(cue,status,word);
        }
        void OnJudyCaption(string text) { if(Active("judy")) view.ShowCaption(text); }
        void OnJudyStoryStarted()
        {
            if(!Active("judy")) return;
            var output=new StoryOutput { schemaVersion=3, sessionId=Guid.NewGuid().ToString("N"),
                workspaceId="judy",experienceSessionId=experience,storyText=judy.FinalStoryText };
            foreach(var word in core.Anchors)
            {
                if(!PhysicalWordInputPolicy.IsRecognizedWord(word.word)) continue;
                output.words.Add(new WordRecord { index=output.words.Count,groupId=word.groupId,word=word.word,
                    componentIds=word.componentIds,worldPosition=word.capturedWorldPosition,completedAt=word.captureTimestampUtc });
            }
            if(output.words.Count>0) output.words[output.words.Count-1].finishMarker=true;
            core.SessionStore?.RecordStory(output);
            core.SessionStore?.SetStage("story_playing");
            view.ShowStoryReady(output.storyText);
        }
        void OnJudyStoryCompleted()
        {
            if(!Active("judy")) return;
            core.SessionStore?.SetStage("next_stage");
            view.ShowNextStage("Your story is saved.");
        }

        void OnFlowChanged()
        {
            if(!Active("demo")) return;
            var flow=core.Flow;
            view.SetListening(flow.State==ConversationState.Listening && flow.IsRecording);
            switch(flow.State)
            {
                case ConversationState.RequestingQuestion:
                    view.SetBusy(true,"Creating your question..."); break;
                case ConversationState.Asking:
                    view.SetBusy(false); view.ShowCaption(flow.CurrentPrompt); break;
                case ConversationState.PreparingAnswer:
                    view.SetBusy(false); view.ShowCaption("Listen for the tone, then tell your story."); break;
                case ConversationState.ReceivingAnswer:
                    view.SetBusy(true,"Saving your answer..."); break;
                case ConversationState.RequestingStory:
                    core.SessionStore?.SetStage("story_generating");
                    view.SetBusy(true,"Creating your story..."); break;
                case ConversationState.Story:
                    core.SessionStore?.SetStage("story_playing");
                    view.SetBusy(false); view.ShowCaption(flow.CurrentPrompt); break;
                case ConversationState.Error:
                    core.SessionStore?.SetStage("error");
                    view.SetBusy(false); view.ShowCaption("Your progress is saved. Use Finish to try creating your story again."); break;
                case ConversationState.Silent:
                    view.SetBusy(false); view.ShowCaption("What comes next?"); break;
            }
        }
        void OnLiveStoryReady(StoryOutput output)
        {
            if(!Active("demo") || output.experienceSessionId!=experience) return;
            // Available for the future video generator. Never start a placeholder video here.
            view.ShowStoryReady(output.storyText);
        }
        void OnLiveNarrationCompleted(StoryOutput output)
        {
            if(!Active("demo") || output.experienceSessionId!=experience) return;
            core.SessionStore?.SetStage("next_stage");
            view.ShowNextStage("Your story is saved.");
        }
        void OnSpatialReferenceReset()
        {
            if(Active("judy")) judy.InvalidateSpatialAnchors();
        }
        public void RestartSelectedExperience()
        {
            if(core!=null && core.ExperienceActive) Select(core.WorkspaceId);
        }
        void OnSessionEnded()
        {
            judy?.Cancel(); experience=null;
            view?.ShowMenu();
        }
        void OnDestroy()
        {
            PresentationEvents.Published-=OnPresentation;
            if(core!=null)
            {
                core.SessionEnded-=OnSessionEnded;
                core.SpatialReferenceReset-=OnSpatialReferenceReset;
                core.PreparedStoryCreator=null; core.PreparedStorySubmitted=null;
                if(core.Flow!=null)
                {
                    core.Flow.Changed-=OnFlowChanged;
                    core.Flow.StoryOutputReady-=OnLiveStoryReady;
                    core.Flow.StoryNarrationCompleted-=OnLiveNarrationCompleted;
                }
            }
            judy?.Cancel();
        }
    }
}
