using System;
using System.Collections.Generic;
using Meta.XR;
using UnityEngine;
using UnityEngine.XR;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace BrailleSoundDemo.CameraTest
{
    [DefaultExecutionOrder(100)]
    public sealed class HandsOffCameraDemo : MonoBehaviour
    {
        public OVRCameraRig rig;
        public OVRHand leftHand, rightHand;
        public OVRSkeleton leftSkeleton, rightSkeleton;
        public PassthroughCameraAccess cameraAccess;
        public QuestFrameCapture capture;
        public string endpoint = "http://127.0.0.1:8767/frames";
        public float boardWidth = .60f, boardDepth = .40f, blockHeight = .02f;
        public float clearSeconds = 1f;
        const string CameraPermission = "horizonos.permission.HEADSET_CAMERA";
        HandsOffGate gate;
        Transform board;
        TextMesh statusText;
        LineRenderer outline;
        AudioSource cue;
        AudioClip capturedCue;
        string sessionId, result = "No image sent yet.", handStatus = "Waiting for tracked hands.";
        int revision, sentCount;
        bool placed, focused = true, paused, requestingPermission;
        readonly List<XRInputSubsystem> inputSubsystems = new List<XRInputSubsystem>();
        readonly List<Vector3> leftPoints = new List<Vector3>(), rightPoints = new List<Vector3>();

        void Start()
        {
            sessionId = Guid.NewGuid().ToString("N");
            gate = new HandsOffGate(clearSeconds);
            capture.Configure(cameraAccess, endpoint);
            capture.Completed += CaptureCompleted;
            board = new GameObject("Physical floor test area").transform;
            outline = board.gameObject.AddComponent<LineRenderer>();
            outline.useWorldSpace = false; outline.loop = true; outline.positionCount = 4;
            outline.widthMultiplier = .006f;
            outline.material = new Material(Shader.Find("Unlit/Color"));
            outline.material.color = new Color(.3f, 1f, .7f);
            outline.SetPositions(new[] {new Vector3(-boardWidth/2,.008f,-boardDepth/2),new Vector3(boardWidth/2,.008f,-boardDepth/2),new Vector3(boardWidth/2,.008f,boardDepth/2),new Vector3(-boardWidth/2,.008f,boardDepth/2)});
            outline.enabled = false;
            var label = new GameObject("Camera capture status");
            label.transform.SetParent(rig.centerEyeAnchor, false);
            label.transform.localPosition = new Vector3(0, .16f, 1.15f);
            statusText = label.AddComponent<TextMesh>(); statusText.anchor = TextAnchor.UpperCenter;
            statusText.alignment = TextAlignment.Center; statusText.fontSize = 48; statusText.characterSize = .0047f;
            statusText.color = Color.white;
            cue = gameObject.AddComponent<AudioSource>(); cue.playOnAwake = false; cue.spatialBlend = 0; cue.volume = .4f;
            const int rate = 24000; var samples = new float[rate/5];
            for(int i=0;i<samples.Length;i++) samples[i] = .3f*Mathf.Sin(2*Mathf.PI*780*i/rate)*Mathf.Sin(Mathf.PI*i/samples.Length);
            capturedCue = AudioClip.Create("Frame received cue", samples.Length, 1, rate, false); capturedCue.SetData(samples, 0);
            SubsystemManager.GetSubsystems(inputSubsystems);
            foreach(var subsystem in inputSubsystems) subsystem.trackingOriginUpdated += TrackingOriginChanged;
            EnsurePermission();
            Debug.Log("[HandsOffCamera] Capture-only test. No OCR, microphone, OpenAI, or story calls. Endpoint: " + endpoint);
        }

        bool PermissionGranted()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Permission.HasUserAuthorizedPermission(CameraPermission);
#else
            return false;
#endif
        }
        void EnsurePermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (PermissionGranted()) { cameraAccess.enabled = true; return; }
            if (requestingPermission) return;
            requestingPermission = true;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => { requestingPermission = false; cameraAccess.enabled = true; result = "Camera permission granted. Place the floor area with A."; };
            callbacks.PermissionDenied += _ => { requestingPermission = false; result = "Camera permission denied. Press A to request again."; };
            Permission.RequestUserPermission(CameraPermission, callbacks);
#else
            result = "Real capture requires the Quest APK. Editor is not a camera test.";
#endif
        }

        void LateUpdate()
        {
            if (gate == null || rig == null) return;
            if (OVRInput.GetDown(OVRInput.Button.One))
            {
                if (!PermissionGranted()) EnsurePermission(); else PlaceFloorArea();
            }
            if (OVRInput.GetDown(OVRInput.Button.Two)) ClearPlacement("Area cleared. Look at the floor and press A.");
            bool valid = CheckObservation(out bool near, out string reason);
            handStatus = reason;
            if (gate.Observe(Time.unscaledTimeAsDouble, valid, near, capture.IsReady && !capture.Busy))
            {
                int capturedRevision = revision;
                var context = new QuestCaptureContext {
                    SessionId = sessionId, FrameRevision = revision,
                    BoardPose = new Pose(board.position,board.rotation), BoardWidth = boardWidth,
                    BoardDepth = boardDepth, BlockHeight = blockHeight, ClearSeconds = clearSeconds
                };
                bool requested = capture.RequestCapture(context, () => placed && capturedRevision == revision && CheckObservation(out var inside,out _) && !inside);
                if (!requested) result = "Capture could not start. Move hands into the area and out to retry.";
            }
            statusText.text = "HANDS-OFF CAMERA TEST\n" +
                (!placed ? "Look at the physical floor. Press A to place the area.\n" : "Place blocks inside the green area. Put controllers aside.\n") +
                (!placed ? "Floor level must match the Quest room setup.\n" : "Move hands in, then to either side where they remain tracked.\n") +
                handStatus + "\n" + gate.State + " | Received: " + sentCount + "\n" +
                capture.Status + "\n" + result + "\nB: clear area | No word recognition yet";
            if (placed) outline.material.color = !valid ? new Color(1,.55f,.2f) : near ? Color.yellow : new Color(.3f,1,.7f);
        }

        bool HeadTracked()
        {
            if (!focused || paused) return false;
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            return head.isValid && head.TryGetFeatureValue(CommonUsages.isTracked,out bool tracked) && tracked &&
                head.TryGetFeatureValue(CommonUsages.trackingState,out InputTrackingState state) &&
                (state & (InputTrackingState.Position|InputTrackingState.Rotation)) == (InputTrackingState.Position|InputTrackingState.Rotation);
        }

        void PlaceFloorArea()
        {
            if (!HeadTracked() || capture.Busy) { result = "Wait for head tracking and the current request to finish."; return; }
            if (!FloorTrackingActive()) { result = "Quest must report floor-level tracking. Check room / floor setup."; return; }
            var up = rig.trackingSpace.up;
            var floor = new Plane(up,rig.trackingSpace.position);
            var ray = new Ray(rig.centerEyeAnchor.position,rig.centerEyeAnchor.forward);
            if (!floor.Raycast(ray,out float distance) || distance < .25f || distance > 3.5f)
            { result = "Look down at a clear floor area within reach, then press A."; return; }
            var forward = Vector3.ProjectOnPlane(rig.centerEyeAnchor.forward,up).normalized;
            if(forward.sqrMagnitude < .1f) forward = rig.trackingSpace.forward;
            board.SetPositionAndRotation(ray.GetPoint(distance),Quaternion.LookRotation(forward,up));
            revision++; placed = true; outline.enabled = true; gate.Reset();
            result = "Area placed. Enter the area with a hand, then move both hands clear.";
            Debug.Log("[HandsOffCamera] Floor area placed revision="+revision+" world="+board.position.ToString("F3"));
        }

        bool CheckObservation(out bool near, out string reason)
        {
            near = false;
            if (!placed) { reason="Area not placed."; return false; }
            if (!HeadTracked()) { reason="Head tracking unavailable. Capture paused."; return false; }
            if (!FloorTrackingActive()) { reason="Floor-level tracking is not active. Capture paused."; return false; }
            if (!cameraAccess.IsPlaying) { reason="Waiting for camera permission / valid camera feed."; return false; }
            if (!ReadHand(leftHand,leftSkeleton,leftPoints) || !ReadHand(rightHand,rightSkeleton,rightPoints))
            { reason="Both hands must be tracked with high confidence. Unknown is not hands-off."; return false; }
            if (!BoardVisible(out Rect bounds)) { reason="Keep the entire green area in the camera view."; return false; }
            near = HandInArea(leftPoints,bounds) || HandInArea(rightPoints,bounds);
            reason = near ? "Hands in / covering the area. Waiting for hands-off." : "Both hands clear and tracked.";
            return true;
        }

        bool FloorTrackingActive()
        {
            foreach (var subsystem in inputSubsystems)
                if (subsystem.running && subsystem.GetTrackingOriginMode()==TrackingOriginModeFlags.Floor) return true;
            return false;
        }

        static bool ReadHand(OVRHand hand,OVRSkeleton skeleton,List<Vector3> points)
        {
            points.Clear();
            if(hand==null || skeleton==null || !hand.IsDataValid || !hand.IsTracked || !hand.IsDataHighConfidence || hand.PoseSourceInferred || hand.IsSystemGestureInProgress || !skeleton.IsInitialized || !skeleton.IsDataValid || !skeleton.IsDataHighConfidence) return false;
            for(int i=0;i<5;i++) if(hand.GetFingerConfidence((OVRHand.HandFinger)i)!=OVRHand.TrackingConfidence.High) return false;
            foreach(var bone in skeleton.Bones)
            {
                if(bone.Transform==null) return false;
                var p=bone.Transform.position;
                if(!float.IsFinite(p.x)||!float.IsFinite(p.y)||!float.IsFinite(p.z)) return false;
                points.Add(p);
            }
            return points.Count>=20;
        }

        bool BoardVisible(out Rect bounds)
        {
            bounds = new Rect(); Vector2 min=Vector2.one, max=Vector2.zero;
            Pose pose=cameraAccess.GetCameraPose();
            for(int x=-1;x<=1;x+=2) for(int z=-1;z<=1;z+=2)
            {
                Vector3 p=board.TransformPoint(new Vector3(x*boardWidth*.5f,blockHeight,z*boardDepth*.5f));
                if(Vector3.Dot(p-pose.position,pose.rotation*Vector3.forward)<=0) return false;
                Vector2 v=cameraAccess.WorldToViewportPoint(p);
                if(!float.IsFinite(v.x)||!float.IsFinite(v.y)||v.x<.03f||v.x>.97f||v.y<.03f||v.y>.97f) return false;
                min=Vector2.Min(min,v); max=Vector2.Max(max,v);
            }
            bounds=Rect.MinMaxRect(min.x,min.y,max.x,max.y); return true;
        }

        bool HandInArea(List<Vector3> points,Rect imageArea)
        {
            Vector2 min=new Vector2(float.PositiveInfinity,float.PositiveInfinity),max=new Vector2(float.NegativeInfinity,float.NegativeInfinity);
            var cameraPose=cameraAccess.GetCameraPose();
            foreach(var p in points)
            {
                var local=board.InverseTransformPoint(p);
                if(Mathf.Abs(local.x)<boardWidth*.5f+.05f && Mathf.Abs(local.z)<boardDepth*.5f+.05f && local.y>-.08f && local.y<blockHeight+.18f) return true;
                if(Vector3.Dot(p-cameraPose.position,cameraPose.rotation*Vector3.forward)<=0) continue;
                Vector2 v=cameraAccess.WorldToViewportPoint(p); min=Vector2.Min(min,v); max=Vector2.Max(max,v);
            }
            // Conservative image-space overlap also blocks a hand hovering above the letters.
            return float.IsFinite(min.x) && Rect.MinMaxRect(min.x-.025f,min.y-.025f,max.x+.025f,max.y+.025f).Overlaps(imageArea);
        }

        void CaptureCompleted(bool success,string message)
        {
            if(success) { sentCount++; cue.PlayOneShot(capturedCue); result="Python received one image. Re-enter the area to capture again."; }
            else result="Capture failed: "+message+". Re-enter the area to retry.";
            Debug.Log("[HandsOffCamera] "+result);
        }
        void ClearPlacement(string message)
        {
            revision++; placed=false; gate?.Reset(); if(outline!=null) outline.enabled=false; result=message;
        }
        void TrackingOriginChanged(XRInputSubsystem _) => ClearPlacement("Tracking origin changed. Place the area again with A.");
        void OnApplicationPause(bool value) { paused=value; if(value) ClearPlacement("App paused. Place the area again after resuming."); }
        void OnApplicationFocus(bool value) { focused=value; if(!value) gate?.Reset(); }
        void OnDestroy()
        {
            if(capture!=null) capture.Completed-=CaptureCompleted;
            foreach(var subsystem in inputSubsystems) subsystem.trackingOriginUpdated-=TrackingOriginChanged;
            if(board!=null) { if(outline!=null) Destroy(outline.material); Destroy(board.gameObject); }
            if(capturedCue!=null) Destroy(capturedCue);
        }
    }
}
