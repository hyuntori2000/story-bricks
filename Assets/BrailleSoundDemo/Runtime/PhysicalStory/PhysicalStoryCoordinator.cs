using System;
using System.Collections;
using System.Collections.Generic;
using BrailleSoundDemo.CameraTest;
using BrailleSoundDemo.TableDepthTest;
using BrailleSoundDemo.Presentation;
using UnityEngine;
using UnityEngine.Networking;

namespace BrailleSoundDemo.PhysicalStory
{
    [Serializable]
    public sealed class PhysicalWordAnchor
    {
        public int index;
        public string groupId, word, frameId, captureTimestampUtc;
        public Vector3 capturedWorldPosition;
        public Quaternion rotation;
        public Vector2 size;
        public string[] componentIds;
        public bool spatialValid = true;
        [NonSerialized] public Transform anchor;
    }

    /// <summary>
    /// Reuses the existing table/hand capture loop and live conversation without
    /// creating another camera, listener, or virtual table. A small status hint
    /// reports progress and when the current word needs recapturing.
    /// Python reads printed labels on the frozen crop. Accepted word anchors are
    /// independent of the optional, queued physical-word conversation.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class PhysicalStoryCoordinator : MonoBehaviour
    {
        [SerializeField] TableDepthTestDemo table;
        [SerializeField] QuestFrameCapture sender;
        [SerializeField] OVRCameraRig rig;
        [SerializeField] string recognitionBaseUrl = "http://127.0.0.1:8769";
        [SerializeField] float recognitionWaitSeconds = 10;
        [SerializeField] string workspaceId = "demo";
        [SerializeField] bool liveConversation = true;
        [SerializeField] WordMediaPresenter mediaPresenterPrefab;
        [SerializeField] Transform presentationStage;
        readonly List<PhysicalWordAnchor> anchors = new List<PhysicalWordAnchor>();
        readonly Dictionary<string,PhysicalWordAnchor> anchorsById = new Dictionary<string,PhysicalWordAnchor>();
        readonly List<PhysicalWordAnchor> conversationQueue = new List<PhysicalWordAnchor>();
        bool initialized, awaitingFrame, awaitingRecognition, resetting;
        bool startingConversationWord, waitingForConversationRecapture, finishMenuOpen;
        bool experienceActive = true, presentationInputBlocked;
        int generation, captureRevision, captureWordIndex, nextCaptureIndex;
        string captureStorySession, observedStorySession, lastFlowMessage;
        string workspaceCaptureSession, lastAcceptedGroupId, pendingPresentationGroupId;
        Coroutine poll;
        UnityWebRequest activePoll;
        PinchWordBox pendingPinchBox;
        WordImagePipeline wordImages;
        StoryVideoPipeline storyVideos;
        HandApproachSample pendingPinchSample;
        string pendingPinchSession;
        int pendingPinchWordIndex, pendingPinchRevision;
        AudioSource inputFeedback;
        AudioClip recognitionRetry, differentWordRetry;
        TextMesh statusHint;
        Func<bool> ownedCaptureGate;
        Func<bool> ownedCapturedContextGate;
        Func<bool> ownedCaptureFeedbackGate;

        public ExperienceSessionStore SessionStore { get; private set; }
        public string ExperienceSessionId => workspaceCaptureSession;
        public bool ExperienceActive => experienceActive;
        public bool PresentationInputBlocked => presentationInputBlocked;
        public bool FinishDialogOpen => GetComponent<SessionFinishMenu>()?.DialogOpen == true;
        public event Action SessionEnded;
        public event Action<string> StatusChanged;
        bool presentationOwnsStatus;
        public ConversationFlow Flow { get; private set; }
        public StoryImagePipeline Images { get; private set; }
        public IReadOnlyList<PhysicalWordAnchor> Anchors => anchors;
        public bool WaitingForRecognition => awaitingFrame || awaitingRecognition;
        public string Status { get; private set; } = "Waiting for physical story initialization";
        public string WorkspaceId => workspaceId;
        public bool LiveConversationEnabled => liveConversation;
        public int QueuedWordCount => conversationQueue.Count;
        public int SavedWordCount
        {
            get
            {
                var groups = new HashSet<string>(StringComparer.Ordinal);
                if(Flow?.Record!=null) foreach(var word in Flow.Record.words) groups.Add(word.groupId);
                foreach(var word in liveConversation ? conversationQueue : anchors)
                    if(word.anchor!=null && PhysicalWordInputPolicy.IsRecognizedWord(word.word)) groups.Add(word.groupId);
                return groups.Count;
            }
        }

        public void ConfigureWorkspace(string id,bool enableLiveConversation,WordMediaPresenter presenterPrefab=null)
        {
            if(id!="demo") throw new ArgumentException("Capture is available only in Create World.",nameof(id));
            bool changed=workspaceId!=id || liveConversation!=enableLiveConversation ||
                (presenterPrefab!=null && mediaPresenterPrefab!=presenterPrefab);
            workspaceId=id; liveConversation=enableLiveConversation;
            if(presenterPrefab!=null) mediaPresenterPrefab=presenterPrefab;
            ApplyPhysicalPresentation();
            if(initialized && changed) ResetPhysicalSession("Workspace changed");
        }

        public bool BeginExperience(string id)
        {
            if(!initialized || id!="demo") return false;
            experienceActive=true;
            workspaceId=id; liveConversation=id=="demo";
            ApplyPhysicalPresentation();
            ResetPhysicalSession("Experience started");
            SetStatus("Double pinch above the blocks. Your photo follows after 3 seconds.");
            return true;
        }

        public void ShowExperienceMenu()
        {
            experienceActive=false;
            wordImages?.CancelAll();
            storyVideos?.CancelAll();
            CancelRecognition(); table?.CancelPinchTarget(); sender?.CancelPending("Choose an experience first");
            if(statusHint!=null) statusHint.gameObject.SetActive(false);
        }

        public void UsePresentationStatus(bool enabled)
        {
            presentationOwnsStatus=enabled;
            if(enabled && statusHint!=null) statusHint.gameObject.SetActive(false);
        }

        public void SetPresentationInputBlocked(bool blocked)
        {
            if(blocked && !presentationInputBlocked) table?.CancelPinchTarget();
            presentationInputBlocked=blocked;
        }

        public void Configure(TableDepthTestDemo tableDemo, QuestFrameCapture captureSender,
            OVRCameraRig cameraRig, string receiverBaseUrl = "http://127.0.0.1:8769")
        {
            table=tableDemo; sender=captureSender; rig=cameraRig;
            recognitionBaseUrl=(receiverBaseUrl ?? "").TrimEnd('/');
            ApplyPhysicalPresentation();
        }

        void Awake() { ApplyPhysicalPresentation(); }

        void ApplyPhysicalPresentation()
        {
            if(table == null) return;
            table.ShowDiagnostics=false;
            table.DiagnosticSpeechEnabled=false;
            table.RequestMicrophonePermission=liveConversation;
            table.FreezeCaptureAfterReadback=true;
            table.ResilientPhysicalCapture=true;
            table.DoublePinchCapture=true;
        }

        void Start()
        {
            if(table == null || sender == null || rig == null ||
                !Uri.TryCreate(recognitionBaseUrl,UriKind.Absolute,out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            { Debug.LogError("[PhysicalStory] Missing rig/capture configuration or invalid recognition URL."); enabled=false; return; }
            ApplyPhysicalPresentation();
            Flow=GetComponent<ConversationFlow>();
            if(Flow == null) Flow=gameObject.AddComponent<ConversationFlow>();
            workspaceCaptureSession=Guid.NewGuid().ToString("N");
            SessionStore=GetComponent<ExperienceSessionStore>();
            if(SessionStore==null) SessionStore=gameObject.AddComponent<ExperienceSessionStore>();
            Flow.SetExperienceContext(workspaceId,workspaceCaptureSession);
            sender.SetExperienceContext(workspaceId,workspaceCaptureSession);
            Flow.ConfigureLive(liveConversation);
            Flow.ConfigurePhysicalWords(true);
            Flow.UseNeutralCueForUnknownPhysicalWords=false;
            Flow.Palette.LiveEffectsEnabled=liveConversation;
            Flow.Palette.SessionId=()=>Flow?.Record?.sessionId;
            Flow.Palette.ResolveWorldPosition=ResolveSavedWordPosition;
            Flow.Palette.StoryAmbienceEnabled=false;
            Flow.ResolveWordAudioSource=ResolveWordAudioSource;
            Flow.WordSoundPrepared+=OnWordSoundPrepared;
            Flow.WordRecaptureRequested+=OnWordRecaptureRequested;
            Flow.QuestionStarted+=OnQuestionStarted;
            Flow.AnswerSaved+=OnAnswerSaved;
            Flow.SetSpatialReady(table.HeadTrackingValid);
            Images=GetComponent<StoryImagePipeline>();
            if(Images == null) Images=gameObject.AddComponent<StoryImagePipeline>();
            Images.EnableImagePreparation=false;
            wordImages=GetComponent<WordImagePipeline>();
            if(wordImages==null) wordImages=gameObject.AddComponent<WordImagePipeline>();
            storyVideos=GetComponent<StoryVideoPipeline>();
            if(storyVideos==null) storyVideos=gameObject.AddComponent<StoryVideoPipeline>();
            storyVideos.VideoReady+=OnStoryVideoReady;
            inputFeedback=gameObject.AddComponent<AudioSource>();
            inputFeedback.playOnAwake=false; inputFeedback.spatialBlend=0; inputFeedback.volume=.8f;
            recognitionRetry=Resources.Load<AudioClip>("StoryAudio/recognition_retry");
            differentWordRetry=Resources.Load<AudioClip>("StoryAudio/word_retry");
            if(recognitionRetry == null || differentWordRetry == null)
            { Debug.LogError("[PhysicalStory] Required prerecorded input prompts are missing."); enabled=false; return; }
            observedStorySession=Flow.Record.sessionId;
            Images.ResetSession(observedStorySession);
            Flow.Changed+=OnFlowChanged;
            Flow.StoryOutputReady+=OnStoryOutputReady;
            ownedCaptureGate=CanCapture;
            table.CapturePermitted=ownedCaptureGate;
            ownedCapturedContextGate=CapturedContextStillValid;
            table.CapturedContextStillValid=ownedCapturedContextGate;
            ownedCaptureFeedbackGate=()=>!liveConversation || Flow==null ||
                (!Flow.IsRecording && !Flow.IsAnswerCapturePending && !Flow.IsAudioPlaying);
            table.CaptureFeedbackPermitted=ownedCaptureFeedbackGate;
            table.CaptureStarted+=OnCaptureStarted;
            table.PinchTargetSelected+=OnPinchTargetSelected;
            table.ReferenceReset+=OnReferenceReset;
            sender.Completed+=OnCaptureCompleted;
            initialized=true;
            BindStagePresenter();
            SessionFinishMenu.Attach(this,rig);
            ExperienceRouter.Attach(this,rig);
            SetStatus("Ready. Double pinch above printed labels; one photo follows after 3 seconds.");
            Debug.Log("[PhysicalStory] Recognition="+recognitionBaseUrl+" | Live speech="+LiveStoryProvider.BaseUrl+
                " | Workspace="+workspaceId+" liveConversation="+liveConversation+"; words queue until a recognized period ends the story.");
        }

        void Update()
        {
            if(!initialized) return;
            // Fresh depth is required by the capture loop, but existing world-fixed
            // audio only needs valid head/origin tracking. Looking away is permitted.
            Flow.SetSpatialReady(table.HeadTrackingValid);
            storyVideos?.SetSpatialReady(table.HeadTrackingValid);
            if(!finishMenuOpen && liveConversation && OVRInput.GetDown(OVRInput.RawButton.A) && Flow.CanAnswer) Flow.AnswerAction();
            TryStartQueuedConversation();
        }

        bool CanCapture()
        {
            if(!initialized || !isActiveAndEnabled || !experienceActive || presentationInputBlocked || finishMenuOpen ||
                (storyVideos!=null && storyVideos.ReservesDoublePinch) || resetting || awaitingFrame || awaitingRecognition || table == null ||
                !table.RequiredPermissionsGranted || !table.HeadTrackingValid) return false;
            return true;
        }

        void OnPinchTargetSelected(HandApproachSample sample,string hand,Quaternion targetRotation)
        {
            if(!CanCapture()) return;
            Vector3 center=sample.fingerPosition;center.y=sample.surfaceHeight;
            if(pendingPinchBox==null)
            {
                var root=new GameObject("Pending pinch word box");
                root.transform.SetParent(transform,false);
                pendingPinchBox=root.AddComponent<PinchWordBox>();
            }
            pendingPinchBox.Configure(center,targetRotation);
            pendingPinchSample=sample;
            pendingPinchSession=workspaceCaptureSession;
            pendingPinchWordIndex=nextCaptureIndex++;
            pendingPinchRevision=table.ReferenceRevision;
            pendingPresentationGroupId="pending-"+pendingPinchWordIndex;
            WordMediaPresenter.Attach(mediaPresenterPrefab,pendingPinchBox.transform,workspaceCaptureSession,
                pendingPresentationGroupId,pendingPinchWordIndex,"",workspaceId,!liveConversation);
            PublishPresentation("box.created",pendingPresentationGroupId,pendingPinchWordIndex,"",pendingPinchBox.transform);
            Debug.Log("[PhysicalStory] Pinch box created immediately hand="+hand+" target="+center.ToString("F3")+
                " sampleTime="+sample.time.ToString("F6")+" revision="+pendingPinchRevision);
        }

        bool MatchingPinchBox(QuestFrameCapture.CaptureMetadata metadata,int index)
        {
            if(pendingPinchBox==null || metadata?.handApproach==null || metadata.board==null ||
                pendingPinchSession!=workspaceCaptureSession || pendingPinchWordIndex!=index ||
                pendingPinchRevision!=metadata.frameRevision || pendingPinchRevision!=table.ReferenceRevision) return false;
            return Math.Abs(metadata.handApproach.sampleMonotonicSeconds-pendingPinchSample.time)<.00001 &&
                metadata.handApproach.sampleFrame==pendingPinchSample.frame &&
                Vector3.Distance(metadata.board.position,pendingPinchBox.CenterWorld)<.001f &&
                Quaternion.Angle(metadata.board.rotation,pendingPinchBox.RotationWorld)<.1f;
        }

        void OnCaptureStarted()
        {
            if(!CanCapture()) { sender.CancelPending("Another capture is active or camera tracking is unavailable."); return; }
            ++generation;
            awaitingFrame=true;
            captureStorySession=workspaceCaptureSession;
            captureRevision=table.ReferenceRevision;
            captureWordIndex=pendingPinchBox!=null && pendingPinchSession==workspaceCaptureSession
                ? pendingPinchWordIndex : nextCaptureIndex++;
            SetStatus("Capturing one frame for word "+(captureWordIndex+1)+".");
        }

        bool CapturedContextStillValid()
        {
            return initialized && isActiveAndEnabled && awaitingFrame &&
                captureStorySession==workspaceCaptureSession && captureRevision==table.ReferenceRevision;
        }

        void OnCaptureCompleted(bool accepted,string message)
        {
            if(!initialized || !awaitingFrame) return;
            awaitingFrame=false;
            if(!accepted) { SetStatus("Photo not accepted: "+message); return; }
            var original=sender.LastMetadata;
            if(original!=null) SessionStore?.RecordCapture(original);
            if(original == null || original.frameRevision!=captureRevision ||
                table.ReferenceRevision!=captureRevision || workspaceCaptureSession!=captureStorySession)
            { SetStatus("Discarded a photo from an obsolete tracking reference or workspace."); return; }
            // Preserve the camera/plane values belonging to this exact JPEG.
            var metadata=JsonUtility.FromJson<QuestFrameCapture.CaptureMetadata>(JsonUtility.ToJson(original));
            if(MatchingPinchBox(metadata,captureWordIndex)) pendingPinchBox.MarkCaptured();
            awaitingRecognition=true;
            SetStatus("Photo received. Reading the word automatically.");
            poll=StartCoroutine(PollRecognition(metadata,generation,captureStorySession,captureWordIndex));
        }

        IEnumerator PollRecognition(QuestFrameCapture.CaptureMetadata metadata,int token,string storySession,int wordIndex)
        {
            float deadline=Time.realtimeSinceStartup+Mathf.Max(5,recognitionWaitSeconds);
            int lastAnalysis=-1;
            string url=recognitionBaseUrl+"/recognition/result?sessionId="+Uri.EscapeDataString(metadata.sessionId)+
                "&frameId="+Uri.EscapeDataString(metadata.frameId)+
                "&workspaceId="+Uri.EscapeDataString(metadata.workspaceId)+
                "&experienceSessionId="+Uri.EscapeDataString(metadata.experienceSessionId);
            while(PollValid(token,storySession,metadata.frameRevision,wordIndex))
            {
                if(Time.realtimeSinceStartup>deadline)
                { RecognitionNeedsRetry("Recognition timed out. Double pinch again; one photo follows after 3 seconds."); yield break; }
                string body=null;
                long code=0;
                bool success=false;
                using(var request=UnityWebRequest.Get(url))
                {
                    activePoll=request; request.timeout=8; request.redirectLimit=0;
                    yield return request.SendWebRequest();
                    if(activePoll==request) activePoll=null;
                    if(!PollValid(token,storySession,metadata.frameRevision,wordIndex)) yield break;
                    code=request.responseCode;
                    success=request.result==UnityWebRequest.Result.Success;
                    if(success) body=request.downloadHandler?.text;
                }
                if(code==409)
                { FinishWaiting("The receiver's current photo changed. Capture again before recognizing another word."); yield break; }
                if(success && !string.IsNullOrEmpty(body) && body.Length<131072)
                {
                    PhysicalColorResult result=null;
                    try { result=JsonUtility.FromJson<PhysicalColorResult>(body); }
                    catch(ArgumentException) { }
                    if(result == null || result.sessionId!=metadata.sessionId || result.frameId!=metadata.frameId ||
                        result.workspaceId!=metadata.workspaceId || result.experienceSessionId!=metadata.experienceSessionId)
                    { FinishWaiting("Rejected a recognition response with a mismatched photo identity."); yield break; }
                    if(result.ready && result.status=="recognized")
                    {
                        if(PhysicalWordAnchorProjection.TryCreate(metadata,result,table.ReferenceRevision,out var geometry,out var reason))
                        {
                            if(PollValid(token,storySession,metadata.frameRevision,wordIndex))
                                AcceptWord(metadata,geometry,wordIndex);
                            yield break;
                        }
                        if(result.automaticBlockDetection)
                        { RecognitionNeedsRetry("The detected center could not be placed: "+reason); yield break; }
                        if(result.analysisRevision!=lastAnalysis) SetStatus("Recognition geometry is invalid: "+reason);
                    }
                    else if(result.ready && result.automaticBlockDetection &&
                        (result.status=="retry_needed" || result.status=="needs_review"))
                    { RecognitionNeedsRetry("The word is uncertain. Adjust the blocks and capture again.",true); yield break; }
                    else if(result.analysisRevision!=lastAnalysis && result.ready)
                        SetStatus("At least one printed label is uncertain. Center the word and capture again.");
                    lastAnalysis=result.analysisRevision;
                }
                else if(!success) SetStatus("Waiting for the Mac recognition receiver; the current photo remains pending.");
                yield return new WaitForSecondsRealtime(.1f);
            }
            FinishWaiting("Recognition canceled because the workspace or tracking reference changed.");
        }

        bool PollValid(int token,string storySession,int referenceRevision,int wordIndex)
        {
            return initialized && isActiveAndEnabled && token==generation && awaitingRecognition &&
                table.HeadTrackingValid &&
                table.ReferenceRevision==referenceRevision && workspaceCaptureSession==storySession &&
                captureWordIndex==wordIndex;
        }

        void AcceptWord(QuestFrameCapture.CaptureMetadata metadata,PhysicalWordGeometry geometry,int index)
        {
            if(anchorsById.ContainsKey(geometry.GroupId))
            { FinishWaiting("This captured word was already accepted."); return; }
            bool pinchCapture=metadata.trigger!=null && (metadata.trigger.gesture=="double_pinch_raise_hands" || metadata.trigger.gesture=="double_pinch_timer");
            if(pinchCapture && !MatchingPinchBox(metadata,index))
            { RecognitionNeedsRetry("The pinch target no longer matches this photo. Double pinch again."); return; }
            var anchor=pinchCapture ? PromotePinchBox(metadata,geometry,index) : CreateWordBox(metadata,geometry,index);
            var sound=anchor.anchor.gameObject.AddComponent<WordSoundAttachment>();
            sound.Configure(anchor.groupId,anchor.word);
            anchor.componentIds=(string[])geometry.ComponentIds.Clone();
            anchors.Add(anchor); anchorsById.Add(anchor.groupId,anchor);
            BeginWordImage(anchor,sound);
            lastAcceptedGroupId=anchor.groupId;
            SessionStore?.RecordWord(anchor.groupId,anchor.word,anchor.index,anchor.frameId);
            WordMediaPresenter.Attach(mediaPresenterPrefab,anchor.anchor,workspaceCaptureSession,
                anchor.groupId,anchor.index,anchor.word,workspaceId,!liveConversation);
            PublishPresentation("word.accepted",anchor.groupId,anchor.index,anchor.word,anchor.anchor);
            if(liveConversation)
            {
                bool duplicatePeriod = PhysicalWordInputPolicy.IsStoryFinishMarker(anchor.word) &&
                    (Flow.PeriodQuestionPending || Flow.StoryFinishRequested || conversationQueue.Exists(queued => PhysicalWordInputPolicy.EndsStory(queued.word)));
                if(!duplicatePeriod)
                {
                    if(waitingForConversationRecapture) conversationQueue.Insert(0,anchor);
                    else conversationQueue.Add(anchor);
                }
                waitingForConversationRecapture=false;
                RefreshFinishPending();
            }
            FinishWaiting(PhysicalWordInputPolicy.IsStoryFinishMarker(geometry.Word) && liveConversation
                ? "Period received. One final question follows the earlier captured words."
                : "Created "+geometry.Word+(liveConversation?". Queued for conversation; use Create Story whenever you are ready.":". Prepared media is ready; no automatic conversation."));
            Debug.Log("[PhysicalStory] Word="+geometry.Word+" | frame="+metadata.frameId+
                " | captured table-plane anchor="+anchor.capturedWorldPosition.ToString("F3")+
                " | component IDs identify selected regions in this photo, not continuously tracked physical bricks.");
        }

        PhysicalWordAnchor PromotePinchBox(QuestFrameCapture.CaptureMetadata metadata,PhysicalWordGeometry geometry,int index)
        {
            var box=pendingPinchBox;
            pendingPinchBox=null;
            box.MarkAccepted();
            box.gameObject.name="Recognized pinch word anchor "+(index+1)+" "+geometry.Word;
            return new PhysicalWordAnchor {index=index,groupId=geometry.GroupId,word=geometry.Word,
                frameId=metadata.frameId,captureTimestampUtc=metadata.captureTimestampUtc,
                capturedWorldPosition=box.VisualPosition,rotation=box.transform.rotation,
                size=new Vector2(PinchWordBox.AcceptedWidthMeters,PinchWordBox.AcceptedDepthMeters),anchor=box.transform};
        }

        PhysicalWordAnchor CreateWordBox(QuestFrameCapture.CaptureMetadata metadata,PhysicalWordGeometry geometry,int index)
        {
            Vector3 normal=geometry.Rotation*Vector3.up;
            Vector3 position=geometry.CenterWorld+normal*.01f;
            var root=new GameObject("Recognized word anchor "+(index+1)+" "+geometry.Word);
            root.transform.SetParent(transform,false);
            var box=root.AddComponent<PinchWordBox>();
            box.ConfigureAtPose(new Pose(geometry.CenterWorld,geometry.Rotation));
            box.MarkAccepted();
            return new PhysicalWordAnchor { index=index,groupId=geometry.GroupId,word=geometry.Word,
                frameId=metadata.frameId,captureTimestampUtc=metadata.captureTimestampUtc,
                capturedWorldPosition=position,rotation=geometry.Rotation,
                size=new Vector2(PinchWordBox.AcceptedWidthMeters,PinchWordBox.AcceptedDepthMeters),anchor=root.transform };
        }

        void TryStartQueuedConversation()
        {
            RefreshFinishPending();
            if(!initialized || !experienceActive || finishMenuOpen || resetting || startingConversationWord || !liveConversation || waitingForConversationRecapture ||
                Flow==null || Flow.Record==null || conversationQueue.Count==0 ||
                (inputFeedback!=null && inputFeedback.isPlaying)) return;
            int round=Flow.ExpectedWordIndex;
            if(round>=Flow.StoryWordLimit || !Flow.CanComplete(round)) return;
            for(int i=0;i<conversationQueue.Count;i++)
            {
                var word=conversationQueue[i];
                if(word.anchor==null || !anchorsById.ContainsKey(word.groupId))
                { conversationQueue.RemoveAt(i--);RefreshFinishPending();continue; }
                if(PhysicalWordInputPolicy.IsStoryFinishMarker(word.word))
                {
                    if(Flow.Record.words.Count==0)
                    {
                        conversationQueue.RemoveAt(i);
                        RefreshFinishPending();
                        SetStatus("Build at least one word before capturing the period.");
                    }
                    else if(Flow.RequestStoryFinish())
                    {
                        conversationQueue.RemoveAt(i);
                        RefreshFinishPending();
                        SetStatus("Period accepted. Answer one final question, then your story and animation will begin.");
                    }
                    return;
                }
                // Guided dialogue accepts each distinct capture, including the
                // same spelling when it answers a different build prompt.
                if(!Flow.GuidedDialogueEnabled && PhysicalWordInputPolicy.IsRepeatedWord(word.word,Flow.Record.words)) continue;
                startingConversationWord=true;
                try
                {
                    if(Flow.BeginWord(round,word.groupId,word.word,PhysicalWordInputPolicy.EndsStory(word.word),
                        word.anchor.position,word.componentIds))
                    {
                        conversationQueue.Remove(word);
                        RefreshFinishPending();
                        Debug.Log("[ConversationQueue] Started round="+(round+1)+
                            " captureIndex="+word.index+" pending="+conversationQueue.Count);
                    }
                }
                finally { startingConversationWord=false; }
                return;
            }
        }

        // A captured delimiter can arrive while the child is answering. Tell the
        // dialogue to skip a new build invitation after that answer, without
        // bypassing the build-word gate or consuming earlier queued words.
        void RefreshFinishPending()
        {
            if(!initialized || resetting || !experienceActive || !liveConversation || Flow==null) return;
            bool pending=false;
            foreach(var word in conversationQueue)
                if(word.anchor!=null && anchorsById.ContainsKey(word.groupId) && PhysicalWordInputPolicy.EndsStory(word.word))
                { pending=true; break; }
            Flow.SetFinishPending(pending);
        }

        /// <summary>Start another story without removing captured word anchors or queued words.</summary>
        public bool StartNextConversation()
        {
            if(!initialized || !liveConversation || Flow==null ||
                (Flow.State!=ConversationState.Finished && Flow.State!=ConversationState.Error)) return false;
            waitingForConversationRecapture=false;
            bool guided=Flow.GuidedDialogueEnabled;
            Flow.ResetSession();
            if(guided && !Flow.BeginGuidedExperience(false)) return false;
            observedStorySession=Flow.Record.sessionId;
            Images?.ResetSession(observedStorySession);
            RefreshFinishPending();
            SetStatus("New conversation ready. Saved word anchors and queued words are kept.");
            return true;
        }

        void OnQuestionStarted(WordRecord word) => EmitPresentationEvent("question.started",word?.groupId);
        void OnAnswerSaved(ConversationTurn answer) => EmitPresentationEvent("answer.saved",answer?.groupId);

        /// <summary>Shared word media and external completion events use the same presentation interface.</summary>
        public void EmitPresentationEvent(string stage,string groupId=null,string cueId=null)
        {
            if(!initialized || string.IsNullOrEmpty(stage)) return;
            if(string.IsNullOrEmpty(groupId))
                groupId=liveConversation && !string.IsNullOrEmpty(Flow?.ActiveGroupId) ? Flow.ActiveGroupId : lastAcceptedGroupId;
            if(!string.IsNullOrEmpty(groupId) && anchorsById.TryGetValue(groupId,out var word))
                PublishPresentation(stage,word.groupId,word.index,word.word,word.anchor,cueId);
            else PublishPresentation(stage,"",-1,"",presentationStage,cueId);
        }

        void BindStagePresenter()
        {
            if(string.IsNullOrEmpty(workspaceCaptureSession)) return;
            if(presentationStage==null)
            {
                var stage=new GameObject("Workspace presentation stage");
                stage.transform.SetParent(transform,false);
                if(rig!=null && rig.centerEyeAnchor!=null)
                {
                    stage.transform.position=rig.centerEyeAnchor.position+rig.centerEyeAnchor.forward*1.2f;
                    stage.transform.rotation=rig.centerEyeAnchor.rotation;
                }
                presentationStage=stage.transform;
            }
            WordMediaPresenter.Attach(mediaPresenterPrefab,presentationStage,workspaceCaptureSession,
                "",-1,"",workspaceId,!liveConversation,"stage");
        }

        void PublishPresentation(string stage,string groupId,int index,string word,Transform anchor,string cueId=null)
        {
            PresentationEvents.Publish(new PresentationEventContext {
                eventName=stage,sessionId=workspaceCaptureSession,groupId=groupId,wordIndex=index,word=word,
                anchor=anchor,workspaceId=workspaceId,allowVoice=!liveConversation,preview=false,cueId=cueId,
                storyText=Flow?.LastStoryOutput?.storyText ?? "",
                audioReady=liveConversation ? (Func<bool>)(()=>Flow!=null && !Flow.IsRecording &&
                    !Flow.IsAnswerCapturePending && !Flow.IsAudioPlaying && !Flow.CanAnswer &&
                    (Flow.State==ConversationState.Ready || Flow.State==ConversationState.Silent ||
                     Flow.State==ConversationState.Finished)) : null
            });
        }

        void PlayInputFeedback(AudioClip cue)
        {
            // A new capture can fail while an earlier question/answer is active.
            // Keep its visible status, but do not speak over that conversation.
            if(inputFeedback==null || cue==null || (liveConversation && Flow!=null &&
                (Flow.IsAudioPlaying || Flow.IsRecording || Flow.IsAnswerCapturePending || Flow.CanAnswer))) return;
            inputFeedback.PlayOneShot(cue);
        }

        AudioSource ResolveWordAudioSource(string groupId)
        {
            return anchorsById.TryGetValue(groupId,out var word) && word.anchor!=null && word.spatialValid
                ? word.anchor.GetComponent<WordSoundAttachment>()?.Source : null;
        }

        void BeginWordImage(PhysicalWordAnchor anchor,WordSoundAttachment sound)
        {
            if(!liveConversation || workspaceId!="demo" || !experienceActive || Flow?.IsLive!=true ||
                Flow.Record==null || wordImages==null || !PhysicalWordInputPolicy.IsRecognizedWord(anchor.word)) return;
            string imageWorkspace=workspaceId,imageExperience=workspaceCaptureSession,imageSession=Flow.Record.sessionId;
            int referenceRevision=table.ReferenceRevision;
            wordImages.Begin(imageWorkspace,imageExperience,imageSession,anchor.groupId,anchor.word,
                anchor.anchor.GetComponent<PinchWordBox>(),sound,
                ()=>initialized && isActiveAndEnabled && !resetting && experienceActive && liveConversation &&
                    workspaceId==imageWorkspace && workspaceCaptureSession==imageExperience && Flow?.Record?.sessionId==imageSession &&
                    table!=null && table.ReferenceRevision==referenceRevision && anchor.spatialValid && anchor.anchor!=null &&
                    anchorsById.TryGetValue(anchor.groupId,out var current) && ReferenceEquals(current,anchor),
                attachment=>Flow.RequestWordImageReplay(attachment));
        }

        void OnWordSoundPrepared(string groupId,AudioClip clip)
        {
            if(anchorsById.TryGetValue(groupId,out var word) && word.anchor!=null)
                word.anchor.GetComponent<WordSoundAttachment>()?.Attach(clip);
        }

        void OnWordRecaptureRequested(string groupId)
        {
            wordImages?.Cancel(groupId);
            // A dialogue failure must not cancel an unrelated capture or erase
            // other accepted words waiting in this workspace's queue.
            conversationQueue.RemoveAll(word=>word.groupId==groupId);
            RefreshFinishPending();
            waitingForConversationRecapture=true;
            if(anchorsById.TryGetValue(groupId,out var word))
            {
                anchorsById.Remove(groupId);anchors.Remove(word);
                if(word.anchor!=null) Destroy(word.anchor.gameObject);
            }
            SetStatus("Couldn't finish that request. Double pinch to capture this word again.");
            PlayInputFeedback(recognitionRetry);
        }

        Vector3? ResolveSavedWordPosition(string groupId)
        {
            return !string.IsNullOrEmpty(groupId) && anchorsById.TryGetValue(groupId,out var word) && word.anchor != null && word.spatialValid
                ? word.anchor.position : (Vector3?)null;
        }

        void FinishWaiting(string message)
        {
            awaitingFrame=false; awaitingRecognition=false; poll=null;
            SetStatus(message);
        }

        void RecognitionNeedsRetry(string message,bool requestDifferentWord=false)
        {
            FinishWaiting(message);
            var cue=requestDifferentWord ? differentWordRetry : recognitionRetry;
            PlayInputFeedback(cue);
        }

        void OnReferenceReset(int _,string reason)
        {
            if(!initialized) return;
            // Tracking invalidates positions and in-flight captures, not the child's saved story.
            wordImages?.CancelAll();
            CancelRecognition(); table?.CancelPinchTarget(); sender?.CancelPending(reason); ClearPendingPinchBox();
            foreach(var word in anchors)
            {
                word.spatialValid=false;
                if(word.anchor==null) continue;
                foreach(var renderer in word.anchor.GetComponentsInChildren<Renderer>()) renderer.enabled=false;
                foreach(var presenter in word.anchor.GetComponentsInChildren<WordMediaPresenter>())
                { presenter.CancelPlayback(); presenter.PresentationEnabled=false; }
                var source=word.anchor.GetComponent<WordSoundAttachment>()?.Source;
                word.anchor.GetComponent<WordSoundAttachment>()?.CancelImageReplay();
                if(source!=null) { source.Stop(); source.spatialBlend=0; source.spatialize=false; }
            }
            if(experienceActive) SetStatus("Tracking changed. Saved words and answers are kept; reacquire the table for new captures.");
            Debug.Log("[PhysicalStory] Preserved experience after tracking reset: "+reason);
        }

        /// <summary>The menu owns this gate through pointer release, so its pinches never arm a photo.</summary>
        public void SetFinishMenuOpen(bool open)
        {
            if(open && !finishMenuOpen)
            {
                table?.CancelPinchTarget();
                if(!WaitingForRecognition) ClearPendingPinchBox();
            }
            finishMenuOpen=open;
        }

        public bool CreateStoryFromSavedProgress()
        {
            if(!initialized || !experienceActive || Flow==null || SavedWordCount==0) return false;
            if(!liveConversation) return false;
            var queued=new List<WordRecord>();
            foreach(var word in liveConversation ? conversationQueue : anchors)
                if(word.anchor!=null && PhysicalWordInputPolicy.IsRecognizedWord(word.word))
                    queued.Add(new WordRecord { index=word.index, groupId=word.groupId, word=word.word,
                        componentIds=word.componentIds, worldPosition=word.anchor.position,
                        completedAt=word.captureTimestampUtc });
            if(!Flow.CreateStoryFromSavedProgress(queued)) return false;
            CancelRecognition();
            sender?.CancelPending("Creating the story from saved progress");
            table?.CancelPinchTarget();
            ClearPendingPinchBox();
            if(inputFeedback!=null) inputFeedback.Stop();
            foreach(var presenter in FindObjectsByType<WordMediaPresenter>(FindObjectsInactive.Include,FindObjectsSortMode.None))
                if(presenter.SessionId==workspaceCaptureSession) presenter.CancelPlayback();
            conversationQueue.Clear();
            waitingForConversationRecapture=false;
            SetStatus("Creating your story from "+Flow.Record.words.Count+" saved words and "+Flow.Record.turns.Count+" saved answers.");
            return true;
        }

        public void EndSessionToStart()
        {
            if(!initialized) return;
            SetFinishMenuOpen(true);
            experienceActive=false;
            ResetPhysicalSession("Session ended");
            SessionEnded?.Invoke();
            if(statusHint!=null) statusHint.gameObject.SetActive(false);
        }

        public void ResetPhysicalSession(string reason="Physical story reset")
        {
            if(resetting) return;
            resetting=true;
            try
            {
                SessionStore?.End(reason);
                wordImages?.CancelAll();
                storyVideos?.CancelAll();
                CancelRecognition();
                if(inputFeedback != null) inputFeedback.Stop();
                table?.CancelPinchTarget();
                sender?.CancelPending(reason);
                PresentationEvents.ResetSession(workspaceCaptureSession);
                ClearAnchors();
                workspaceCaptureSession=Guid.NewGuid().ToString("N");nextCaptureIndex=0;
                if(experienceActive) SessionStore?.Begin(workspaceId,workspaceCaptureSession);
                sender?.SetExperienceContext(workspaceId,workspaceCaptureSession);
                waitingForConversationRecapture=false;
                if(Flow != null)
                {
                    Flow.SetExperienceContext(workspaceId,workspaceCaptureSession);
                    if(Flow.IsLive!=liveConversation) Flow.ConfigureLive(liveConversation);
                    else Flow.ResetSession();
                    Flow.ConfigurePhysicalWords(true);
                    Flow.Palette.LiveEffectsEnabled=liveConversation;
                    observedStorySession=Flow.Record.sessionId;
                    Images?.ResetSession(observedStorySession);
                    Flow.SetSpatialReady(table != null && table.HeadTrackingValid);
                }
                BindStagePresenter();
                SetStatus(reason+". Reacquire the table and begin a new story.");
            }
            finally { resetting=false; }
        }

        void OnFlowChanged()
        {
            if(!initialized || resetting || Flow?.Record == null) return;
            if(observedStorySession!=Flow.Record.sessionId)
            {
                // API conversation sessions may advance independently of
                // captured anchors and words queued for the following story.
                wordImages?.CancelAll();
                storyVideos?.CancelAll();
                observedStorySession=Flow.Record.sessionId;
                Images?.ResetSession(observedStorySession);
            }
            if(experienceActive) SessionStore?.RecordConversation(Flow.Record);
            string message=Flow.State+" | "+Flow.Status;
            if(message!=lastFlowMessage) { lastFlowMessage=message; Debug.Log("[PhysicalStory] "+message); }
            if(liveConversation && !WaitingForRecognition) ShowStatusHint(Flow.Status);
        }

        void OnStoryOutputReady(StoryOutput output)
        {
            SessionStore?.RecordStory(output);
            EmitPresentationEvent("story.ready");
            if(Images == null || Flow?.Record == null) return;
            if(observedStorySession!=Flow.Record.sessionId) OnFlowChanged();
            Images.Prepare(output);
        }

        /// <summary>Called only after the final story narration finishes in the current experience.</summary>
        public bool BeginStoryVideo(StoryOutput output)
        {
            if(!initialized || !experienceActive || !liveConversation || workspaceId!="demo" ||
                Flow?.Record==null || !Flow.Record.completed || output==null || storyVideos==null ||
                output.workspaceId!=workspaceId || output.experienceSessionId!=workspaceCaptureSession ||
                output.sessionId!=Flow.Record.sessionId || rig?.centerEyeAnchor==null) return false;
            string experience=workspaceCaptureSession, conversation=Flow.Record.sessionId;
            return storyVideos.Begin(output,rig.centerEyeAnchor,
                ()=>isActiveAndEnabled && experienceActive && workspaceId=="demo" &&
                    workspaceCaptureSession==experience && Flow?.Record?.sessionId==conversation,
                SetStatus,stage=>SessionStore?.SetStage(stage));
        }

        void OnStoryVideoReady(string path)
        {
            if(experienceActive) EmitPresentationEvent("video.ready");
        }

        void CancelRecognition()
        {
            ++generation;
            awaitingFrame=false; awaitingRecognition=false;
            if(activePoll != null) { activePoll.Abort(); activePoll.Dispose(); activePoll=null; }
            if(poll != null) { StopCoroutine(poll); poll=null; }
        }

        void ClearAnchors()
        {
            ClearPendingPinchBox();
            foreach(var word in anchors) if(word.anchor != null) Destroy(word.anchor.gameObject);
            anchors.Clear(); anchorsById.Clear();conversationQueue.Clear();lastAcceptedGroupId=null;
        }

        void ClearPendingPinchBox()
        {
            if(pendingPinchBox!=null) Destroy(pendingPinchBox.gameObject);
            pendingPinchBox=null;pendingPinchSession=null;pendingPresentationGroupId=null;
        }

        void SetStatus(string message)
        {
            if(Status==message) return;
            Status=message; Debug.Log("[PhysicalStory] "+message);
            ShowStatusHint(message);
            StatusChanged?.Invoke(message);
        }

        void ShowStatusHint(string message)
        {
            if(presentationOwnsStatus || !experienceActive || rig==null || rig.centerEyeAnchor==null) return;
            if(statusHint==null)
            {
                var root=new GameObject("Story progress hint");
                root.transform.SetParent(rig.centerEyeAnchor,false);
                root.transform.localPosition=new Vector3(0,-.20f,.7f);
                statusHint=root.AddComponent<TextMesh>();
                statusHint.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                statusHint.GetComponent<MeshRenderer>().sharedMaterial=statusHint.font.material;
                statusHint.anchor=TextAnchor.MiddleCenter;statusHint.alignment=TextAlignment.Center;
                statusHint.fontSize=48;statusHint.characterSize=.008f;
                statusHint.color=new Color(.85f,.95f,1f);
            }
            statusHint.gameObject.SetActive(true);
            string value=message ?? "";
            if(value.Length>150) value=value.Substring(0,147)+"...";
            var lines=new List<string>();
            while(value.Length>48)
            {
                int at=value.LastIndexOf(' ',48);if(at<1) at=48;
                lines.Add(value.Substring(0,at));value=value.Substring(at).TrimStart();
            }
            lines.Add(value);statusHint.text=string.Join("\n",lines);
        }

        void OnDisable()
        {
            if(!initialized) return;
            experienceActive=false;
            ResetPhysicalSession("Physical story disabled");
            Flow?.SetSpatialReady(false);
        }

        void OnDestroy()
        {
            wordImages?.CancelAll();
            storyVideos?.CancelAll();
            if(storyVideos!=null) storyVideos.VideoReady-=OnStoryVideoReady;
            CancelRecognition();
            if(table != null)
            {
                if(table.CapturePermitted==ownedCaptureGate) table.CapturePermitted=null;
                if(table.CapturedContextStillValid==ownedCapturedContextGate) table.CapturedContextStillValid=null;
                if(table.CaptureFeedbackPermitted==ownedCaptureFeedbackGate) table.CaptureFeedbackPermitted=null;
                table.PinchTargetSelected-=OnPinchTargetSelected;
                table.CaptureStarted-=OnCaptureStarted; table.ReferenceReset-=OnReferenceReset;
            }
            if(sender != null) sender.Completed-=OnCaptureCompleted;
            if(Flow != null)
            {
                Flow.Changed-=OnFlowChanged; Flow.StoryOutputReady-=OnStoryOutputReady;
                Flow.WordSoundPrepared-=OnWordSoundPrepared;Flow.WordRecaptureRequested-=OnWordRecaptureRequested;
                Flow.QuestionStarted-=OnQuestionStarted;Flow.AnswerSaved-=OnAnswerSaved;
                Flow.ResolveWordAudioSource=null;
            }
            if(statusHint!=null) Destroy(statusHint.gameObject);
            PresentationEvents.ResetSession(workspaceCaptureSession);
            ClearAnchors();
        }
    }
}
