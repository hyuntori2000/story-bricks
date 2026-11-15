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
        bool startingConversationWord, waitingForConversationRecapture;
        int generation, captureRevision, captureWordIndex, nextCaptureIndex;
        string captureStorySession, observedStorySession, lastFlowMessage;
        string workspaceCaptureSession, lastAcceptedGroupId, pendingPresentationGroupId;
        Coroutine poll;
        UnityWebRequest activePoll;
        Material outlineMaterial;
        PinchWordBox pendingPinchBox;
        HandApproachSample pendingPinchSample;
        string pendingPinchSession;
        int pendingPinchWordIndex, pendingPinchRevision;
        AudioSource inputFeedback;
        AudioClip recognitionRetry, differentWordRetry;
        TextMesh statusHint;
        Func<bool> ownedCaptureGate;
        Func<bool> ownedCapturedContextGate;
        Func<bool> ownedCaptureFeedbackGate;

        public ConversationFlow Flow { get; private set; }
        public StoryImagePipeline Images { get; private set; }
        public IReadOnlyList<PhysicalWordAnchor> Anchors => anchors;
        public bool WaitingForRecognition => awaitingFrame || awaitingRecognition;
        public string Status { get; private set; } = "Waiting for physical story initialization";
        public string WorkspaceId => workspaceId;
        public bool LiveConversationEnabled => liveConversation;
        public int QueuedWordCount => conversationQueue.Count;

        public void ConfigureWorkspace(string id,bool enableLiveConversation,WordMediaPresenter presenterPrefab=null)
        {
            if(id!="demo" && id!="judy") throw new ArgumentException("Workspace must be demo or judy.",nameof(id));
            bool changed=workspaceId!=id || liveConversation!=enableLiveConversation ||
                (presenterPrefab!=null && mediaPresenterPrefab!=presenterPrefab);
            if(initialized && changed) ResetPhysicalSession("Workspace changed");
            workspaceId=id;liveConversation=enableLiveConversation;
            if(presenterPrefab!=null) mediaPresenterPrefab=presenterPrefab;
            ApplyPhysicalPresentation();
            if(initialized && changed)
            {
                Flow.ConfigureLive(liveConversation);
                Flow.ConfigurePhysicalWords(true);
                Flow.Palette.LiveEffectsEnabled=liveConversation;
                observedStorySession=Flow.Record.sessionId;
                BindStagePresenter();
            }
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
            SetStatus("Ready. Double pinch above printed labels; one photo follows after 3 seconds. You can create words during conversation.");
            Debug.Log("[PhysicalStory] Recognition="+recognitionBaseUrl+" | Live speech="+LiveStoryProvider.BaseUrl+
                " | Workspace="+workspaceId+" liveConversation="+liveConversation+"; word creation is independent of the "+Flow.StoryWordLimit+"-round story queue.");
        }

        void Update()
        {
            if(!initialized) return;
            // Fresh depth is required by the capture loop, but existing world-fixed
            // audio only needs valid head/origin tracking. Looking away is permitted.
            Flow.SetSpatialReady(table.HeadTrackingValid);
            if(liveConversation && OVRInput.GetDown(OVRInput.RawButton.A) && Flow.CanAnswer) Flow.AnswerAction();
            TryStartQueuedConversation();
        }

        bool CanCapture()
        {
            if(!initialized || !isActiveAndEnabled || resetting || awaitingFrame || awaitingRecognition || table == null ||
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
            if(original == null || original.frameRevision!=captureRevision ||
                table.ReferenceRevision!=captureRevision || workspaceCaptureSession!=captureStorySession)
            { SetStatus("Discarded a photo from an obsolete tracking reference or workspace."); return; }
            // Preserve the camera/plane values belonging to this exact JPEG.
            var metadata=JsonUtility.FromJson<QuestFrameCapture.CaptureMetadata>(JsonUtility.ToJson(original));
            awaitingRecognition=true;
            SetStatus("Photo received. Reading the word automatically.");
            poll=StartCoroutine(PollRecognition(metadata,generation,captureStorySession,captureWordIndex));
        }

        IEnumerator PollRecognition(QuestFrameCapture.CaptureMetadata metadata,int token,string storySession,int wordIndex)
        {
            float deadline=Time.realtimeSinceStartup+Mathf.Max(5,recognitionWaitSeconds);
            int lastAnalysis=-1;
            string url=recognitionBaseUrl+"/recognition/result?sessionId="+Uri.EscapeDataString(metadata.sessionId)+
                "&frameId="+Uri.EscapeDataString(metadata.frameId);
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
                    if(result == null || result.sessionId!=metadata.sessionId || result.frameId!=metadata.frameId)
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
            lastAcceptedGroupId=anchor.groupId;
            WordMediaPresenter.Attach(mediaPresenterPrefab,anchor.anchor,workspaceCaptureSession,
                anchor.groupId,anchor.index,anchor.word,workspaceId,!liveConversation);
            PublishPresentation("word.accepted",anchor.groupId,anchor.index,anchor.word,anchor.anchor);
            if(liveConversation)
            {
                if(waitingForConversationRecapture) conversationQueue.Insert(0,anchor);
                else conversationQueue.Add(anchor);
                waitingForConversationRecapture=false;
            }
            FinishWaiting("Created "+geometry.Word+(liveConversation?". Queued for conversation; you can capture another word.":". Prepared media is ready; no automatic conversation."));
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
            root.transform.SetPositionAndRotation(position,geometry.Rotation);
            var outline=root.AddComponent<LineRenderer>();
            outline.useWorldSpace=false; outline.loop=true; outline.positionCount=4;
            outline.startWidth=.003f; outline.endWidth=.003f;
            outline.numCornerVertices=2; outline.numCapVertices=2;
            if(outlineMaterial == null)
            {
                outlineMaterial=new Material(Shader.Find("Unlit/Color"));
                outlineMaterial.color=new Color(.2f,.85f,1);
            }
            outline.sharedMaterial=outlineMaterial;
            float x=PinchWordBox.AcceptedWidthMeters*.5f,z=PinchWordBox.AcceptedDepthMeters*.5f;
            outline.SetPositions(new[]{new Vector3(-x,0,-z),new Vector3(x,0,-z),new Vector3(x,0,z),new Vector3(-x,0,z)});
            return new PhysicalWordAnchor { index=index,groupId=geometry.GroupId,word=geometry.Word,
                frameId=metadata.frameId,captureTimestampUtc=metadata.captureTimestampUtc,
                capturedWorldPosition=position,rotation=geometry.Rotation,
                size=new Vector2(PinchWordBox.AcceptedWidthMeters,PinchWordBox.AcceptedDepthMeters),anchor=root.transform };
        }

        void TryStartQueuedConversation()
        {
            if(!initialized || resetting || startingConversationWord || !liveConversation || waitingForConversationRecapture ||
                Flow==null || Flow.Record==null || conversationQueue.Count==0 ||
                (inputFeedback!=null && inputFeedback.isPlaying)) return;
            int round=Flow.ExpectedWordIndex;
            if(round>=Flow.StoryWordLimit || !Flow.CanComplete(round)) return;
            for(int i=0;i<conversationQueue.Count;i++)
            {
                var word=conversationQueue[i];
                if(word.anchor==null || !anchorsById.ContainsKey(word.groupId))
                { conversationQueue.RemoveAt(i--);continue; }
                // Duplicate visuals remain valid. A repeated word can join the
                // following story rather than blocking later distinct words.
                if(PhysicalWordInputPolicy.IsRepeatedWord(word.word,Flow.Record.words)) continue;
                startingConversationWord=true;
                try
                {
                    if(Flow.BeginWord(round,word.groupId,word.word,round==Flow.StoryWordLimit-1,
                        word.anchor.position,word.componentIds))
                    {
                        conversationQueue.Remove(word);
                        Debug.Log("[ConversationQueue] Started round="+(round+1)+"/"+Flow.StoryWordLimit+
                            " captureIndex="+word.index+" pending="+conversationQueue.Count);
                    }
                }
                finally { startingConversationWord=false; }
                return;
            }
        }

        /// <summary>Start another story without removing captured word anchors or queued words.</summary>
        public bool StartNextConversation()
        {
            if(!initialized || !liveConversation || Flow==null ||
                (Flow.State!=ConversationState.Finished && Flow.State!=ConversationState.Error)) return false;
            waitingForConversationRecapture=false;
            Flow.ResetSession();
            observedStorySession=Flow.Record.sessionId;
            Images?.ResetSession(observedStorySession);
            SetStatus("New conversation ready. Saved word anchors and queued words are kept.");
            return true;
        }

        void OnQuestionStarted(WordRecord word) => EmitPresentationEvent("question.started",word?.groupId);
        void OnAnswerSaved(ConversationTurn answer) => EmitPresentationEvent("answer.saved",answer?.groupId);

        /// <summary>Manual Judy cues and external video completion use the same presentation events.</summary>
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
            return anchorsById.TryGetValue(groupId,out var word) && word.anchor!=null
                ? word.anchor.GetComponent<WordSoundAttachment>()?.Source : null;
        }

        void OnWordSoundPrepared(string groupId,AudioClip clip)
        {
            if(anchorsById.TryGetValue(groupId,out var word) && word.anchor!=null)
                word.anchor.GetComponent<WordSoundAttachment>()?.Attach(clip);
        }

        void OnWordRecaptureRequested(string groupId)
        {
            // A dialogue failure must not cancel an unrelated capture or erase
            // other accepted words waiting in this workspace's queue.
            conversationQueue.RemoveAll(word=>word.groupId==groupId);
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
            return !string.IsNullOrEmpty(groupId) && anchorsById.TryGetValue(groupId,out var word) && word.anchor != null
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
            ResetPhysicalSession("Tracking reference reset: "+reason);
        }

        public void ResetPhysicalSession(string reason="Physical story reset")
        {
            if(resetting) return;
            resetting=true;
            try
            {
                CancelRecognition();
                if(inputFeedback != null) inputFeedback.Stop();
                table?.CancelPinchTarget();
                sender?.CancelPending(reason);
                PresentationEvents.ResetSession(workspaceCaptureSession);
                ClearAnchors();
                workspaceCaptureSession=Guid.NewGuid().ToString("N");nextCaptureIndex=0;
                waitingForConversationRecapture=false;
                Flow?.ResetSession();
                if(Flow != null)
                {
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
                observedStorySession=Flow.Record.sessionId;
                Images?.ResetSession(observedStorySession);
            }
            string message=Flow.State+" | "+Flow.Status;
            if(message!=lastFlowMessage) { lastFlowMessage=message; Debug.Log("[PhysicalStory] "+message); }
            if(liveConversation && !WaitingForRecognition) ShowStatusHint(Flow.Status);
        }

        void OnStoryOutputReady(StoryOutput output)
        {
            EmitPresentationEvent("story.ready");
            if(Images == null || Flow?.Record == null) return;
            if(observedStorySession!=Flow.Record.sessionId) OnFlowChanged();
            Images.Prepare(output);
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
            if(pendingPinchBox!=null) Destroy(pendingPinchBox.gameObject);
            pendingPinchBox=null;pendingPinchSession=null;pendingPresentationGroupId=null;
            foreach(var word in anchors) if(word.anchor != null) Destroy(word.anchor.gameObject);
            anchors.Clear(); anchorsById.Clear();conversationQueue.Clear();lastAcceptedGroupId=null;
        }

        void SetStatus(string message)
        {
            if(Status==message) return;
            Status=message; Debug.Log("[PhysicalStory] "+message);
            ShowStatusHint(message);
        }

        void ShowStatusHint(string message)
        {
            if(rig==null || rig.centerEyeAnchor==null) return;
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
            ResetPhysicalSession("Physical story disabled");
            Flow?.SetSpatialReady(false);
        }

        void OnDestroy()
        {
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
            if(outlineMaterial != null) Destroy(outlineMaterial);
            PresentationEvents.ResetSession(workspaceCaptureSession);
            ClearAnchors();
        }
    }
}
