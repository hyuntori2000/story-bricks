using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.InputSystem;
using XrUsages = UnityEngine.XR.CommonUsages;

namespace BrailleSoundDemo
{
    public sealed class WorldActionButton : MonoBehaviour { public int action; }

    public sealed class DemoExperience : MonoBehaviour
    {
        public ConversationFlow Flow { get; private set; }
        public MockLetterLayout Layout { get; private set; }
        public Camera ListenerCamera { get; private set; }
        public QuestAudioTracking Tracking { get; private set; }
        public WordInputPipeline InputPipeline { get; private set; }
        public StoryImagePipeline Images { get; private set; }
        public Transform DetectionOrigin { get; private set; }
        public bool IsReady { get; private set; }
        public AudioSource ProbeSource { get; private set; }
        public bool CanProbe => IsReady && InputPipeline.AudioReady && (Application.platform != RuntimePlatform.Android || Flow.SpatializerConfigured) && (Flow.State==ConversationState.Ready || Flow.State==ConversationState.Silent || Flow.State==ConversationState.Finished);
        readonly Color navy = new Color(.035f,.06f,.09f), mint = new Color(.35f,.94f,.72f);
        readonly List<Material> materials = new List<Material>();
        readonly List<WorldActionButton> worldButtons = new List<WorldActionButton>();
        readonly List<Renderer> buttonRenderers = new List<Renderer>();
        readonly List<Color> buttonColors = new List<Color>();
        readonly Dictionary<int,TextMesh> mediaButtonLabels = new Dictionary<int,TextMesh>();
        Transform rig;
        TextMesh worldStatus, placementPrompt;
        LineRenderer controllerBeam;
        readonly Dictionary<string,Vector3> externalLocalPoses = new Dictionary<string,Vector3>();
        readonly Dictionary<string,int> recognizedGroupIndices = new Dictionary<string,int>();
        AudioClip probeClip;
        bool triggerDown, primaryDown, probeFollowsWord;
        int probeIndex;
        float yaw,pitch=27;
        GUIStyle titleStyle,bodyStyle,mutedStyle,tinyStyle,buttonStyle,logStyle;
        Texture2D panelTexture,cardTexture;
        Vector2 logScroll;
        int lastLogCount;
        string imageSessionId;
        bool CanChangeMediaOptions => IsReady && (Flow.State==ConversationState.Ready || Flow.State==ConversationState.Silent || Flow.State==ConversationState.Finished);
        void Start() { Initialize(); }
        public void Initialize()
        {
            if(IsReady) return;
            MakeWorld();
            Layout=gameObject.AddComponent<MockLetterLayout>(); Layout.Initialize(DetectionOrigin); Layout.Recognized+=OnRecognized;
            Flow=gameObject.AddComponent<ConversationFlow>();
            Tracking=gameObject.AddComponent<QuestAudioTracking>(); Tracking.Initialize(rig,ListenerCamera,DetectionOrigin);
            InputPipeline=gameObject.AddComponent<WordInputPipeline>(); InputPipeline.Initialize(this);
            Tracking.SpatialReadyChanged+=OnSpatialReady;
            Tracking.BoardFrameChanged+=OnBoardFrameChanged;
            bool automated=Application.isBatchMode || HasArgument("--story-smoke-test") || HasArgument("--python-input-smoke") || HasArgument("--backend-audio-smoke") || HasArgument("--conversation-parallel-smoke");
            Flow.ConfigureLive(!automated);
            Flow.Palette.ResolveWorldPosition=ResolveSavedGroupPosition;
            Images=gameObject.AddComponent<StoryImagePipeline>();
            Flow.StoryOutputReady+=OnStoryOutputReady;
            SyncImageSession();
            Flow.SetSpatialReady(InputPipeline.AudioReady);
            MakeProbe();
            IsReady=true;
            gameObject.AddComponent<DemoSmokeCheck>().experience=this;
            gameObject.AddComponent<BackendPlaybackSmokeCheck>().experience=this;
            gameObject.AddComponent<PythonInputSmokeCheck>().experience=this;
        }
        public bool Complete(int index)
        {
            if(!IsReady || InputPipeline.PythonMode || !InputPipeline.AudioReady || !Flow.BeginAssembly(index)) return false;
            ProbeSource.Stop(); StartCoroutine(Layout.Assemble(index)); return true;
        }
        void OnRecognized(RecognizedWordObservation observation) { SubmitRecognition(observation); }
        /// <summary>Shared input boundary. A future recognizer supplies stable group/component IDs and a calibrated Unity world pose.</summary>
        public bool SubmitRecognition(RecognizedWordObservation observation)
        {
            return IsReady && InputPipeline.SubmitLocal(observation);
        }
        /// <summary>Validated pipeline sink. Recognition producers should use InputPipeline.</summary>
        public bool ApplyRecognition(RecognizedWordObservation observation)
        {
            if(!Tracking.IsSpatialReady || observation==null || string.IsNullOrWhiteSpace(observation.groupId) || observation.componentIds==null || observation.componentIds.Length==0 || !Finite(observation.worldPosition)) return false;
            if(Flow.State!=ConversationState.Assembling && !Flow.BeginAssembly(observation.groupIndex)) return false;
            ProbeSource.Stop();
            Debug.Log("[Letter recognition] "+observation.rawText+" | "+observation.groupId+" | "+string.Join(", ",observation.componentIds)+" | world metres "+observation.worldPosition.ToString("F3"));
            bool accepted=Flow.WordReady(observation.groupIndex,observation.groupId,observation.word,observation.isFinish,observation.worldPosition,observation.componentIds);
            if(accepted)
            {
                externalLocalPoses[observation.groupId]=DetectionOrigin.InverseTransformPoint(observation.worldPosition);
                recognizedGroupIndices[observation.groupId]=observation.groupIndex;
                if(observation.groupIndex>=0 && observation.groupIndex<3 && observation.groupId==MockLetterLayout.GroupIds[observation.groupIndex])
                    Layout.GroupTransform(observation.groupIndex).position=observation.worldPosition;
                else if(observation.groupIndex>=0 && observation.groupIndex<3)
                {
                    Layout.DisplayCompletedGroup(observation.groupIndex,externalLocalPoses[observation.groupId],Quaternion.identity);
                    Layout.Observations[observation.groupIndex]=observation;
                }
            }
            return accepted;
        }
        /// <summary>Adapter pose updates in DetectionOrigin-local metres. Unrelated identities cannot move the active source.</summary>
        public bool UpdateRecognizedGroupPose(string groupId, Vector3 localPosition, Quaternion localRotation)
            => ApplyRecognizedGroupPose(groupId,localPosition,localRotation);
        public bool ApplyRecognizedGroupPose(string groupId, Vector3 localPosition, Quaternion localRotation)
        {
            if(!Finite(localPosition) || !Finite(localRotation) || string.IsNullOrEmpty(groupId)) return false;
            for(int i=0;i<MockLetterLayout.GroupIds.Length;i++) if(groupId==MockLetterLayout.GroupIds[i])
            {
                var group=Layout.GroupTransform(i); group.localPosition=localPosition; group.localRotation=localRotation.normalized;
                return Flow.UpdateSourcePosition(groupId,group.position);
            }
            if(groupId!=Flow.ActiveGroupId) return false;
            externalLocalPoses[groupId]=localPosition;
            if(recognizedGroupIndices.TryGetValue(groupId,out int index)) Layout.SetGroupPose(index,localPosition,localRotation);
            return Flow.UpdateSourcePosition(groupId,DetectionOrigin.TransformPoint(localPosition));
        }
        public void SimulateAnswer() { if(IsReady && Tracking.IsSpatialReady) Flow.AnswerAction(); }
        public void ResetDemo()
        {
            StopAllCoroutines(); externalLocalPoses.Clear(); recognizedGroupIndices.Clear(); ProbeSource.Stop(); probeFollowsWord=false; Layout.ResetLayout(); Flow.ResetSession(); InputPipeline?.ResetSession(); Flow.SetSpatialReady(InputPipeline.AudioReady);
            SyncImageSession();
        }
        void SyncImageSession()
        {
            if(Images==null || Flow.Record==null || imageSessionId==Flow.Record.sessionId) return;
            imageSessionId=Flow.Record.sessionId;
            Images.ResetSession(imageSessionId);
        }
        void OnStoryOutputReady(StoryOutput snapshot)
        {
            SyncImageSession();
            // Local request preparation only. This build never calls an image-generation API.
            Images.Prepare(snapshot);
        }
        void ToggleSoundProfile()
        {
            if(!CanChangeMediaOptions) return;
            Flow.Palette.SetSoundProfile(Flow.Palette.Profile==SoundProfile.Standard ? SoundProfile.Gentle : SoundProfile.Standard);
            ProbeSource.volume=.70f*Flow.Palette.WordGainMultiplier;
        }
        void ToggleStoryAmbience()
        {
            if(CanChangeMediaOptions) Flow.Palette.StoryAmbienceEnabled=!Flow.Palette.StoryAmbienceEnabled;
        }
        void ToggleImagePreparation()
        {
            if(!CanChangeMediaOptions) return;
            Images.EnableImagePreparation=!Images.EnableImagePreparation;
        }
        public void PlaceMockBoard()
        {
            if(!Tracking.IsTracked && Tracking.IsXrSession) return;
            var forward=Vector3.ProjectOnPlane(ListenerCamera.transform.forward,Vector3.up).normalized;
            if(forward.sqrMagnitude < .01f) forward=Vector3.forward;
            Tracking.CalibrateBoardAt(ListenerCamera.transform.position+forward*1.2f-Vector3.up*.7f,Quaternion.LookRotation(forward,Vector3.up));
            SyncSourcePositions();
            Flow.SetSpatialReady(InputPipeline.AudioReady);
        }
        void OnSpatialReady(bool ready)
        {
            if(ready) SyncSourcePositions();
            if(Flow!=null) Flow.SetSpatialReady(InputPipeline!=null ? InputPipeline.AudioReady : ready);
            if(!ready && ProbeSource!=null) ProbeSource.Stop();
        }
        void OnBoardFrameChanged() { SyncSourcePositions(); if(InputPipeline!=null) Flow.SetSpatialReady(InputPipeline.AudioReady); }
        Vector3? ResolveSavedGroupPosition(string groupId)
        {
            if(string.IsNullOrEmpty(groupId) || !recognizedGroupIndices.TryGetValue(groupId,out int index)) return null;
            if(index>=0 && index<MockLetterLayout.GroupIds.Length && groupId==MockLetterLayout.GroupIds[index])
                return Layout.GroupTransform(index).position;
            if(externalLocalPoses.TryGetValue(groupId,out var localPose)) return DetectionOrigin.TransformPoint(localPose);
            return null;
        }
        void SyncSourcePositions()
        {
            if(Flow==null || Layout==null) return;
            if(Flow.ActiveIndex>=0 && Flow.ActiveIndex<3 && Flow.ActiveGroupId==MockLetterLayout.GroupIds[Flow.ActiveIndex])
                Flow.UpdateSourcePosition(Flow.ActiveGroupId,Layout.GroupWorldPosition(Flow.ActiveIndex));
            else if(!string.IsNullOrEmpty(Flow.ActiveGroupId) && externalLocalPoses.TryGetValue(Flow.ActiveGroupId,out var localPose))
                Flow.UpdateSourcePosition(Flow.ActiveGroupId,DetectionOrigin.TransformPoint(localPose));
            if(ProbeSource!=null) ProbeSource.transform.position=probeFollowsWord ? Flow.ActiveWorldPosition : ProbePosition(probeIndex);
        }
        public bool PlayProbe(int index)
        {
            if(!CanProbe || index<0 || index>2) return false;
            if(ProbeSource.isPlaying && !probeFollowsWord && probeIndex==index) { ProbeSource.Stop(); return true; }
            probeFollowsWord=false; probeIndex=index; ProbeSource.Stop(); ProbeSource.volume=.70f*Flow.Palette.WordGainMultiplier; ProbeSource.clip=probeClip; ProbeSource.transform.position=ProbePosition(index); ProbeSource.Play(); return true;
        }
        public bool ReplayActiveWord()
        {
            if(!CanProbe || Flow.ActiveIndex<0) return false;
            if(ProbeSource.isPlaying && probeFollowsWord) { ProbeSource.Stop(); return true; }
            var clip=Resources.Load<AudioClip>("SatieAudio/"+Flow.ActiveWord.ToLowerInvariant());
            if(clip==null) return false;
            ProbeSource.Stop(); probeFollowsWord=true; ProbeSource.volume=.70f*Flow.Palette.WordGainMultiplier; ProbeSource.clip=clip; ProbeSource.transform.position=Flow.ActiveWorldPosition; ProbeSource.Play(); return true;
        }
        Vector3 ProbePosition(int index) => DetectionOrigin.TransformPoint(index==0 ? new Vector3(-1.65f,.15f,0) : index==1 ? new Vector3(1.65f,.15f,0) : new Vector3(0,.15f,1.1f));
        void MakeProbe()
        {
            var go=new GameObject("Spatial diagnostic probe"); go.transform.SetParent(transform,false); ProbeSource=go.AddComponent<AudioSource>();
            ProbeSource.playOnAwake=false; ProbeSource.loop=true; ProbeSource.spatialBlend=1; ProbeSource.spatialize=string.Equals(AudioSettings.GetSpatializerPluginName(),"Meta XR Audio",StringComparison.Ordinal); ProbeSource.dopplerLevel=0; ProbeSource.rolloffMode=AudioRolloffMode.Linear; ProbeSource.minDistance=.2f; ProbeSource.maxDistance=10; ProbeSource.volume=.70f;
            var meta=go.AddComponent<MetaXRAudioSource>(); meta.EnableSpatialization=string.Equals(AudioSettings.GetSpatializerPluginName(),"Meta XR Audio",StringComparison.Ordinal); meta.EnableAcoustics=false; meta.GainBoostDb=0;
            int rate=24000; var samples=new float[rate*2];
            for(int i=0;i<samples.Length;i++) { float t=i/(float)rate; float envelope=Mathf.Sin(Mathf.PI*(t%(.5f))/.5f); samples[i]=.24f*envelope*(Mathf.Sin(2*Mathf.PI*440*t)+.35f*Mathf.Sin(2*Mathf.PI*880*t)); }
            probeClip=AudioClip.Create("Offline spatial diagnostic pulse",samples.Length,1,rate,false); probeClip.SetData(samples,0); ProbeSource.clip=probeClip;
        }
        void Update()
        {
            if(!IsReady) return;
            SyncImageSession();
            SyncSourcePositions();
            Flow.SetSpatialReady(InputPipeline.AudioReady);
            ListenerCamera.rect=Tracking.IsXrSession ? new Rect(0,0,1,1) : new Rect(.29f,0,.71f,1);
            placementPrompt.gameObject.SetActive(Tracking.IsXrSession && !Tracking.IsSpatialReady);
            if(placementPrompt.gameObject.activeSelf) placementPrompt.text="PLACE MOCK BOARD\nPress A on the right controller\n"+Wrap(Tracking.Status,48);
            if(ProbeSource.isPlaying) ProbeSource.transform.position=probeFollowsWord ? Flow.ActiveWorldPosition : ProbePosition(probeIndex);
            if(!InputPipeline.AudioReady && ProbeSource.isPlaying) ProbeSource.Stop();
            if(!Tracking.IsXrSession)
            {
                var keyboard=Keyboard.current; var mouse=Mouse.current;
                if(mouse!=null && mouse.rightButton.isPressed) { Vector2 delta=mouse.delta.ReadValue(); yaw+=delta.x*.12f; pitch=Mathf.Clamp(pitch-delta.y*.12f,-60,75); }
                ListenerCamera.transform.localRotation=Quaternion.Euler(pitch,yaw,0);
                Vector3 move=Vector3.zero;
                if(keyboard!=null)
                {
                    if(keyboard.wKey.isPressed) move+=Vector3.forward; if(keyboard.sKey.isPressed) move+=Vector3.back;
                    if(keyboard.aKey.isPressed) move+=Vector3.left; if(keyboard.dKey.isPressed) move+=Vector3.right;
                    if(keyboard.qKey.isPressed) move+=Vector3.down; if(keyboard.eKey.isPressed) move+=Vector3.up;
                    if(keyboard.rKey.wasPressedThisFrame) ResetView();
                    if(keyboard.digit1Key.wasPressedThisFrame) Complete(0); if(keyboard.digit2Key.wasPressedThisFrame) Complete(1); if(keyboard.digit3Key.wasPressedThisFrame) Complete(2);
                    if(keyboard.spaceKey.wasPressedThisFrame) SimulateAnswer();
                }
                rig.position+=Quaternion.Euler(0,yaw,0)*move*Time.deltaTime*1.4f;
                if(mouse!=null && mouse.leftButton.wasPressedThisFrame && !OverPanel(mouse.position.ReadValue())) ActivateRay(ListenerCamera.ScreenPointToRay(mouse.position.ReadValue()));
            }
            UpdateController();
            worldStatus.text=Wrap(Tracking.IsXrSession && !Tracking.IsSpatialReady ? Tracking.Status : Flow.Status,64)+"\n"+(Flow.IsLive ? (Flow.IsRecording ? "Listening / ANSWER finishes recording" : "Live AI voice / word observations simulated") : "Offline speech fixtures / no microphone")+"\n"+(InputPipeline.PythonMode ? InputPipeline.Bridge.Status : "Random word positions / PYTHON switches input");
            worldStatus.text+="\n"+Flow.PipelineSummary;
            if(mediaButtonLabels.TryGetValue(11,out var soundLabel)) soundLabel.text="SOUND: "+Flow.Palette.Profile.ToString().ToUpperInvariant();
            if(mediaButtonLabels.TryGetValue(12,out var ambienceLabel)) ambienceLabel.text="AMBIENCE: "+(Flow.Palette.StoryAmbienceEnabled ? "ON" : "OFF");
            if(mediaButtonLabels.TryGetValue(13,out var imageLabel)) imageLabel.text="IMAGE PREP: "+(Images.EnableImagePreparation ? "ON" : "OFF");
            for(int i=0;i<worldButtons.Count;i++) buttonRenderers[i].sharedMaterial.color=EnabledAction(worldButtons[i].action)?buttonColors[i]:new Color(.16f,.21f,.24f);
        }
        void UpdateController()
        {
            controllerBeam.enabled=false;
            if(!Tracking.IsXrSession) return;
            var hand=InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            bool pressed=hand.TryGetFeatureValue(XrUsages.triggerButton,out bool trigger)&&trigger;
            bool primary=hand.TryGetFeatureValue(XrUsages.primaryButton,out bool p)&&p;
            if(primary&&!primaryDown) PlaceMockBoard();
            if(Tracking.IsTracked && hand.isValid && hand.TryGetFeatureValue(XrUsages.isTracked,out bool handTracked) && handTracked && hand.TryGetFeatureValue(XrUsages.devicePosition,out Vector3 hp) && hand.TryGetFeatureValue(XrUsages.deviceRotation,out Quaternion hr) && Finite(hp) && Finite(hr))
            {
                var ray=new Ray(rig.TransformPoint(hp),rig.TransformDirection(hr*Vector3.forward));
                controllerBeam.enabled=true; controllerBeam.SetPosition(0,ray.origin); controllerBeam.SetPosition(1,Physics.Raycast(ray,out var hit,12)?hit.point:ray.GetPoint(5));
                if(pressed&&!triggerDown) ActivateRay(ray);
            }
            triggerDown=pressed; primaryDown=primary;
        }
        void ActivateRay(Ray ray)
        {
            if(!Physics.Raycast(ray,out var hit,12)) return;
            var action=hit.collider.GetComponent<WorldActionButton>(); if(action!=null && EnabledAction(action.action)) RunAction(action.action);
        }
        bool EnabledAction(int action)
        {
            if(!IsReady) return false;
            if(action<3) return !InputPipeline.PythonMode && InputPipeline.AudioReady && Flow.CanComplete(action);
            if(action==3) return Tracking.IsSpatialReady && (Flow.CanAnswer || Flow.CanFinishAnswer);
            if(action==4) return true;
            if(action==8) return true;
            if(action==9) return CanProbe && Flow.ActiveIndex>=0;
            if(action==10) return CanProbe && Flow.ActiveIndex>=0 && !InputPipeline.PythonMode;
            if(action>=11 && action<=13) return CanChangeMediaOptions;
            return CanProbe;
        }
        void RunAction(int action) { if(action<3) Complete(action); else if(action==3) SimulateAnswer(); else if(action==4) ResetDemo(); else if(action==8) InputPipeline.SetPythonMode(!InputPipeline.PythonMode); else if(action==9) ReplayActiveWord(); else if(action==10) MoveActive(Vector3.right*.25f); else if(action==11) ToggleSoundProfile(); else if(action==12) ToggleStoryAmbience(); else if(action==13) ToggleImagePreparation(); else PlayProbe(action-5); }
        void ResetView() { rig.position=new Vector3(0,3.10f,-3.95f); rig.rotation=Quaternion.identity; ListenerCamera.transform.localPosition=Vector3.zero; yaw=0; pitch=27; }
        void MakeWorld()
        {
            foreach(var listener in FindObjectsByType<AudioListener>(FindObjectsSortMode.None)) listener.enabled=false;
            foreach(var camera in FindObjectsByType<Camera>(FindObjectsSortMode.None)) camera.enabled=false;
            rig=new GameObject("Listener rig / tracked head").transform; rig.SetParent(transform,false);
            var cameraObject=new GameObject("Main Camera"); cameraObject.tag="MainCamera"; cameraObject.transform.SetParent(rig,false);
            ListenerCamera=cameraObject.AddComponent<Camera>(); ListenerCamera.clearFlags=CameraClearFlags.SolidColor; ListenerCamera.backgroundColor=navy; ListenerCamera.nearClipPlane=.03f; ListenerCamera.farClipPlane=40; ListenerCamera.fieldOfView=54; cameraObject.AddComponent<AudioListener>(); ResetView();
            placementPrompt=Label("",cameraObject.transform,new Vector3(0,-.12f,1.25f),.024f,Color.white); placementPrompt.characterSize=.024f*.12f; placementPrompt.gameObject.SetActive(false);
            var beamObject=new GameObject("Right controller ray"); beamObject.transform.SetParent(transform,false); controllerBeam=beamObject.AddComponent<LineRenderer>(); controllerBeam.positionCount=2; controllerBeam.startWidth=.006f; controllerBeam.endWidth=.003f; controllerBeam.sharedMaterial=Mat(mint); controllerBeam.enabled=false;
            RenderSettings.ambientLight=new Color(.62f,.68f,.76f); RenderSettings.fog=true; RenderSettings.fogColor=navy; RenderSettings.fogMode=FogMode.ExponentialSquared; RenderSettings.fogDensity=.022f;
            var lightObject=new GameObject("Soft key"); lightObject.transform.SetParent(transform); var light=lightObject.AddComponent<Light>(); light.type=LightType.Directional; light.intensity=1.1f; lightObject.transform.rotation=Quaternion.Euler(42,-30,0);
            Cube("Floor",transform,new Vector3(0,-.07f,1),new Vector3(16,.1f,16),new Color(.045f,.08f,.11f));
            for(int x=-7;x<=7;x++) Cube("Floor grid",transform,new Vector3(x,-.012f,1),new Vector3(.008f,.004f,14),new Color(.08f,.13f,.17f));
            for(int z=-6;z<=8;z++) Cube("Floor grid",transform,new Vector3(0,-.012f,z),new Vector3(14,.004f,.008f),new Color(.08f,.13f,.17f));
            DetectionOrigin=new GameObject("DetectionOrigin / local metres").transform; DetectionOrigin.SetParent(transform,false); DetectionOrigin.localPosition=new Vector3(0,.85f,1);
            Cube("Mock table",DetectionOrigin,new Vector3(0,-.09f,-.12f),new Vector3(4.45f,.16f,3.25f),new Color(.12f,.19f,.23f));
            Cube("Mint edge",DetectionOrigin,new Vector3(0,-.02f,-1.76f),new Vector3(4.4f,.018f,.018f),mint);
            Label("WORDS TO WORLDS",DetectionOrigin,new Vector3(0,1.23f,1.75f),.085f,Color.white);
            Label("BUILD / LISTEN / IMAGINE",DetectionOrigin,new Vector3(0,1.01f,1.75f),.047f,mint);
            worldStatus=Label("",DetectionOrigin,new Vector3(0,.69f,1.75f),.035f,new Color(.73f,.83f,.88f));
            MakeWorldButton("1 COMPLETE",0,new Vector3(-1.55f,.04f,-1.43f),mint);
            MakeWorldButton("2 COMPLETE",1,new Vector3(-.77f,.04f,-1.43f),new Color(.48f,.70f,1));
            MakeWorldButton("3 FINISH",2,new Vector3(.01f,.04f,-1.43f),new Color(1,.68f,.43f));
            MakeWorldButton("ANSWER",3,new Vector3(.79f,.04f,-1.43f),mint);
            MakeWorldButton("RESET",4,new Vector3(1.57f,.04f,-1.43f),new Color(.65f,.73f,.78f));
            MakeWorldButton("PROBE L",5,new Vector3(-1.55f,.04f,1.28f),new Color(.65f,.73f,.78f));
            MakeWorldButton("PROBE R",6,new Vector3(1.55f,.04f,1.28f),new Color(.65f,.73f,.78f));
            MakeWorldButton("PROBE FAR",7,new Vector3(0,.04f,1.28f),new Color(.65f,.73f,.78f));
            MakeWorldButton("PYTHON",8,new Vector3(-.8f,.04f,1.65f),mint);
            MakeWorldButton("REPLAY WORD",9,new Vector3(0,.04f,1.65f),mint);
            MakeWorldButton("MOVE +0.25",10,new Vector3(.8f,.04f,1.65f),mint);
            MakeWorldButton("SOUND: STANDARD",11,new Vector3(-.8f,.04f,-1.93f),new Color(.65f,.73f,.78f));
            MakeWorldButton("AMBIENCE: OFF",12,new Vector3(0,.04f,-1.93f),new Color(.65f,.73f,.78f));
            MakeWorldButton("IMAGE PREP: OFF",13,new Vector3(.8f,.04f,-1.93f),new Color(.65f,.73f,.78f));
        }
        void MakeWorldButton(string text,int action,Vector3 position,Color color)
        {
            var go=Cube(text,DetectionOrigin,position,new Vector3(.70f,.055f,.25f),color); var button=go.AddComponent<WorldActionButton>(); button.action=action;
            var label=Label(text,DetectionOrigin,position+Vector3.up*.035f,.032f,navy); label.transform.localRotation=Quaternion.Euler(90,0,0);
            worldButtons.Add(button); buttonRenderers.Add(go.GetComponent<Renderer>()); buttonColors.Add(color);
            if(action>=11) mediaButtonLabels[action]=label;
        }
        Material Mat(Color color) { var material=new Material(Shader.Find("Standard")); material.color=color; materials.Add(material); return material; }
        GameObject Cube(string name,Transform parent,Vector3 position,Vector3 scale,Color color)
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube); go.name=name; go.transform.SetParent(parent,false); go.transform.localPosition=position; go.transform.localScale=scale; go.GetComponent<Renderer>().sharedMaterial=Mat(color); return go;
        }
        TextMesh Label(string text,Transform parent,Vector3 position,float size,Color color)
        {
            var go=new GameObject(text); go.transform.SetParent(parent,false); go.transform.localPosition=position;
            var label=go.AddComponent<TextMesh>(); label.text=text; label.fontSize=80; label.characterSize=size*.5f; label.anchor=TextAnchor.MiddleCenter; label.alignment=TextAlignment.Center; label.color=color; label.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); go.GetComponent<MeshRenderer>().sharedMaterial=label.font.material; return label;
        }
        bool OverPanel(Vector3 mouse)
        {
            float scale=UiScale(); var point=new Vector2(mouse.x/scale,(Screen.height-mouse.y)/scale); float w=Screen.width/scale,h=Screen.height/scale;
            return new Rect(20,20,340,h-40).Contains(point)||new Rect(380,h-210,w-400,190).Contains(point)||new Rect(w-345,20,325,610).Contains(point);
        }
        float UiScale() => Mathf.Max(.25f,Mathf.Min(Screen.width/1280f,Screen.height/900f));
        void EnsureStyles()
        {
            if(titleStyle!=null) return;
            var font=Resources.Load<Font>("Fonts/UiFont");
            titleStyle=new GUIStyle(GUI.skin.label){font=font,fontSize=26,fontStyle=FontStyle.Bold,normal={textColor=Color.white}};
            bodyStyle=new GUIStyle(GUI.skin.label){font=font,fontSize=15,wordWrap=true,normal={textColor=new Color(.9f,.94f,.96f)}};
            mutedStyle=new GUIStyle(bodyStyle){fontSize=12,normal={textColor=new Color(.63f,.75f,.81f)}};
            tinyStyle=new GUIStyle(mutedStyle){fontSize=11};
            buttonStyle=new GUIStyle(GUI.skin.button){font=font,fontSize=14,alignment=TextAnchor.MiddleCenter,padding=new RectOffset(8,8,6,6)};
            logStyle=new GUIStyle(tinyStyle){wordWrap=true};
            panelTexture=Texture(new Color(.035f,.06f,.085f,.97f)); cardTexture=Texture(new Color(.10f,.16f,.20f,.96f));
        }
        Texture2D Texture(Color color) { var t=new Texture2D(1,1); t.SetPixel(0,0,color); t.Apply(); return t; }
        void OnGUI()
        {
            if(!IsReady || Tracking.IsXrSession) return;
            EnsureStyles(); float scale=UiScale(); GUI.matrix=Matrix4x4.Scale(Vector3.one*scale); float width=Screen.width/scale,height=Screen.height/scale;
            GUI.DrawTexture(new Rect(20,20,340,height-40),panelTexture);
            GUI.Label(new Rect(42,39,294,20),"A TACTILE STORY EXPERIMENT",tinyStyle);
            GUI.Label(new Rect(42,68,300,40),"Words to Worlds",titleStyle);
            GUI.Label(new Rect(42,116,294,47),"Scattered letters become words.\nYour answers become a story.",mutedStyle);
            GUI.contentColor=mint; GUI.Label(new Rect(42,172,290,23),Flow.IsLive ? "LIVE VOICE / MOCK WORDS" : "OFFLINE TEST FIXTURES",bodyStyle); GUI.contentColor=Color.white;
            DrawAction(new Rect(42,211,296,46),"1   COMPLETE  /  CAT",0,mint);
            DrawAction(new Rect(42,265,296,46),"2   COMPLETE  /  RAIN",1,new Color(.48f,.70f,1));
            DrawAction(new Rect(42,319,296,46),"3   FINISH WORD  /  SLEEP #",2,new Color(1,.68f,.43f));
            GUI.Label(new Rect(42,369,296,17),"Final word / one last question before the story",tinyStyle);
            GUI.DrawTexture(new Rect(42,387,296,126),cardTexture);
            GUI.Label(new Rect(56,399,268,19),Flow.State.ToString().ToUpperInvariant(),tinyStyle);
            string promptText=Flow.State==ConversationState.Story ? "Telling your story...\nCAT + RAIN + SLEEP\nFull story in the session console." : Flow.State==ConversationState.Finished ? "Story complete.\n3 answers saved. Story JSON exported.\nRead the full story in the console." : string.IsNullOrEmpty(Flow.CurrentPrompt)?Flow.Status:Flow.CurrentPrompt;
            GUI.Label(new Rect(56,424,268,77),promptText,bodyStyle);
            DrawAction(new Rect(42,525,296,43),Flow.IsLive ? (Flow.IsRecording ? "DONE SPEAKING" : "RETRY MICROPHONE") : Flow.CanAnswer?"SIMULATE ANSWER  /  "+(Flow.Record.turns.Count==0?"PINK":Flow.Record.turns.Count==1?"INSIDE":"BLANKET"):"SIMULATE ANSWER",3,mint);
            GUI.Label(new Rect(42,580,294,18),"SAVED ANSWERS  /  "+Flow.Record.turns.Count+" OF 3",tinyStyle);
            string saved=Flow.Record.turns.Count==0?"Your answers will appear here.":"";
            foreach(var turn in Flow.Record.turns) saved+=turn.word+"  /  "+turn.answer+"\n";
            GUI.Label(new Rect(42,604,294,60),saved,mutedStyle);
            GUI.Label(new Rect(42,672,294,18),"SPATIAL CHECK / SELECT AGAIN TO STOP",tinyStyle);
            DrawAction(new Rect(42,697,93,34),"LEFT",5,Color.gray); DrawAction(new Rect(143,697,93,34),"RIGHT",6,Color.gray); DrawAction(new Rect(244,697,94,34),"FAR",7,Color.gray);
            GUI.Label(new Rect(42,745,294,18),"MOVE ACTIVE WORD SOURCE",tinyStyle);
            GUI.enabled=CanProbe && Flow.ActiveIndex>=0 && !InputPipeline.PythonMode;
            GUI.backgroundColor=Color.gray;
            if(GUI.Button(new Rect(42,769,143,30),"LEFT  0.25 m",buttonStyle)) MoveActive(Vector3.left*.25f);
            if(GUI.Button(new Rect(195,769,143,30),"RIGHT  0.25 m",buttonStyle)) MoveActive(Vector3.right*.25f);
            GUI.enabled=true;
            DrawAction(new Rect(42,height-76,296,34),"RESET SESSION",4,Color.gray);
            GUI.Label(new Rect(42,height-37,296,18),"Sound by Satie / evaluation / "+(Flow.IsLive ? "AI-generated voice" : "fixture voice"),tinyStyle);
            GUI.DrawTexture(new Rect(width-345,20,325,610),panelTexture);
            GUI.Label(new Rect(width-327,33,290,19),"AUDIO + POSITION",tinyStyle);
            GUI.Label(new Rect(width-327,58,290,44),"Word effects: "+(Flow.WordSource.spatialize?"Meta XR spatializer":"Unity 3D panning")+"\nQuestions / story: centered voice",mutedStyle);
            GUI.Label(new Rect(width-327,105,290,17),Tracking.Status,tinyStyle);
            GUI.Label(new Rect(width-327,137,290,19),"SOUND OPTIONS / EXISTING AUDIO",tinyStyle);
            DrawAction(new Rect(width-327,163,290,32),"SOUND: "+Flow.Palette.Profile.ToString().ToUpperInvariant(),11,mint);
            DrawAction(new Rect(width-327,203,290,32),"STORY AMBIENCE: "+(Flow.Palette.StoryAmbienceEnabled ? "ON" : "OFF"),12,mint);
            GUI.Label(new Rect(width-327,241,290,35),"Standard / Gentle changes effect volume.\nCAT + RAIN: Satie reuse / SLEEP: local tone",tinyStyle);
            GUI.Label(new Rect(width-327,287,290,19),"PARALLEL JOBS",tinyStyle);
            GUI.Label(new Rect(width-327,311,290,75),"Word sound: "+Flow.WordSoundJobStatus+"\nQuestion: "+Flow.QuestionJobStatus+"\nStory: "+Flow.StoryJobStatus+" / media: "+Flow.StoryMediaJobStatus,tinyStyle);
            DrawAction(new Rect(width-327,395,290,32),"IMAGE REQUEST PREP: "+(Images.EnableImagePreparation ? "ON" : "OFF"),13,mint);
            GUI.Label(new Rect(width-327,435,290,39),Images.Status,tinyStyle);
            if(Images.Texture!=null) GUI.DrawTexture(new Rect(width-327,480,290,130),Images.Texture,ScaleMode.ScaleToFit);
            else GUI.Label(new Rect(width-327,488,290,115),"Optional image output\n\nOFF by default. ON exports a local request after the story is written. No image API calls in this build. New images can load at runtime.",mutedStyle);
            GUI.DrawTexture(new Rect(380,height-210,width-400,190),panelTexture);
            GUI.Label(new Rect(398,height-199,width-436,19),"SESSION CONSOLE",tinyStyle);
            GUI.Label(new Rect(570,height-199,Mathf.Max(1,width-940),19),InputPipeline.PythonMode ? InputPipeline.Bridge.Status : "Random word positions",tinyStyle);
            DrawAction(new Rect(width-327,height-207,140,28),InputPipeline.PythonMode ? "PYTHON INPUT" : "RANDOM INPUT",8,mint);
            DrawAction(new Rect(width-180,height-207,140,28),"REPLAY WORD",9,mint);
            float contentHeight=0;
            foreach(var line in Flow.Logs) contentHeight+=logStyle.CalcHeight(new GUIContent(line),width-456)+5;
            if(lastLogCount!=Flow.Logs.Count) { lastLogCount=Flow.Logs.Count; logScroll.y=contentHeight; }
            logScroll=GUI.BeginScrollView(new Rect(398,height-172,width-436,105),logScroll,new Rect(0,0,width-456,contentHeight));
            float ly=0; foreach(var line in Flow.Logs) { float lh=logStyle.CalcHeight(new GUIContent(line),width-456); GUI.Label(new Rect(0,ly,width-456,lh),line,logStyle); ly+=lh+5; } GUI.EndScrollView();
            string pose=Flow.ActiveIndex>=0 ? Flow.ActiveWord+" source "+Flow.ActiveWorldPosition.ToString("F2") : "13 tracked letter fixtures / 3 distant word areas";
            GUI.Label(new Rect(398,height-60,width-436,18),pose,tinyStyle);
            GUI.Label(new Rect(398,height-39,width-436,18),"WASD move / Q,E height / Right drag look / R reset view / 1,2,3 complete / Space answer",tinyStyle);
            GUI.backgroundColor=Color.white; GUI.contentColor=Color.white; GUI.enabled=true; GUI.matrix=Matrix4x4.identity;
        }
        void DrawAction(Rect rect,string text,int action,Color color) { GUI.enabled=EnabledAction(action); GUI.backgroundColor=color; if(GUI.Button(rect,text,buttonStyle)) RunAction(action); GUI.enabled=true; }
        void MoveActive(Vector3 delta) { int i=Flow.ActiveIndex; if(i<0) return; var group=Layout.GroupTransform(i); UpdateRecognizedGroupPose(Flow.ActiveGroupId,group.localPosition+delta,group.localRotation); }
        static bool HasArgument(string value) => Array.IndexOf(Environment.GetCommandLineArgs(),value)>=0;
        static bool Finite(Vector3 p) => !float.IsNaN(p.x)&&!float.IsInfinity(p.x)&&!float.IsNaN(p.y)&&!float.IsInfinity(p.y)&&!float.IsNaN(p.z)&&!float.IsInfinity(p.z);
        static bool Finite(Quaternion q) => Finite(new Vector3(q.x,q.y,q.z))&&!float.IsNaN(q.w)&&!float.IsInfinity(q.w)&&Quaternion.Dot(q,q)>.0001f;
        static string Wrap(string value,int length) { if(value.Length<=length) return value; int cut=value.LastIndexOf(' ',Mathf.Min(length,value.Length-1)); if(cut<=0) cut=length; return value.Substring(0,cut)+"\n"+value.Substring(cut).TrimStart(); }
        void OnDestroy()
        {
            if(Flow!=null) Flow.StoryOutputReady-=OnStoryOutputReady;
            if(Layout!=null) Layout.Recognized-=OnRecognized; if(Tracking!=null) { Tracking.SpatialReadyChanged-=OnSpatialReady; Tracking.BoardFrameChanged-=OnBoardFrameChanged; }
            foreach(var material in materials) if(material!=null) Destroy(material);
            if(probeClip!=null) Destroy(probeClip); if(panelTexture!=null) Destroy(panelTexture); if(cardTexture!=null) Destroy(cardTexture);
        }
    }
}
