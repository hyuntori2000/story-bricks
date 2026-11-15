using System;
using System.Collections.Generic;
using System.IO;
using BrailleSoundDemo.CameraTest;
using Meta.XR;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.XR;

namespace BrailleSoundDemo.TableDepthTest
{
    /// <summary>Independent depth/hand/ROI diagnostic. No block classifier or focus control.</summary>
    [DefaultExecutionOrder(300)]
    public sealed class TableDepthTestDemo : MonoBehaviour
    {
        [SerializeField] OVRCameraRig rig;
        [SerializeField] EnvironmentRaycastManager raycasts;
        [SerializeField] OVRHand leftHand, rightHand;
        [SerializeField] OVRSkeleton leftSkeleton, rightSkeleton;
        [SerializeField] PassthroughCameraAccess cameraAccess;
        [SerializeField] QuestFrameCapture sender;
        [SerializeField] bool showDiagnostics = true;
        [SerializeField] bool diagnosticSpeechEnabled = true;
        [SerializeField] bool requestMicrophonePermission;
        public bool ShowDiagnostics
        {
            get => showDiagnostics;
            set { showDiagnostics = value; if(hud != null) hud.gameObject.SetActive(value); if(!value) { marker?.SetActive(false); reticle?.SetActive(false); } }
        }
        public bool DiagnosticSpeechEnabled
        {
            get => diagnosticSpeechEnabled;
            set { diagnosticSpeechEnabled = value; if(!value && voice != null) voice.Stop(); }
        }
        public bool RequestMicrophonePermission { get => requestMicrophonePermission; set => requestMicrophonePermission = value; }
        public Func<bool> CapturePermitted { get; set; }
        /// <summary>Physical-story opt-in; defaults retain live hand validation until the receiver ACK.</summary>
        public bool FreezeCaptureAfterReadback { get; set; }
        public Func<bool> CapturedContextStillValid { get; set; }
        /// <summary>Main physical mode only: view-centered validation and bounded depth-only hand-history continuity.</summary>
        public bool ResilientPhysicalCapture { get; set; }
        public bool DoublePinchCapture { get; set; }
        public bool ReferenceHistoryAvailable => ReferenceHistoryUsable;
        public bool CaptureTargetReferenceValid => locked && !referenceContradicted && HeadTracked();
        public Vector3 LockedTablePoint => referencePoint;
        public bool HasLockedTableReference => locked;
        DoublePinchCaptureDriver doublePinch;
        AudioClip armedTone, gestureRetryTone, shutterTone;
        public event Action CaptureStarted;
        public event Action<HandApproachSample,string,Quaternion> PinchTargetSelected;
        public void CancelPinchTarget() => doublePinch?.Reset();
        public event Action<int,string> ReferenceReset;
        public OVRCameraRig Rig => rig;
        public QuestFrameCapture FrameSender => sender;
        public int ReferenceRevision => revision;
        public bool HeadTrackingValid => HeadTracked();
        public bool ReferenceMeasurementFresh => ReferenceFresh;
        public bool RequiredPermissionsGranted => PermissionGranted(OVRPermissionsRequester.ScenePermission) &&
            PermissionGranted("horizonos.permission.HEADSET_CAMERA") &&
            (!requestMicrophonePermission || PermissionGranted(Permission.Microphone));
        const float SampleInterval = .10f;
        const float ReferenceRadius = .14f;
        const float ReferenceFreshSeconds = .25f;
        readonly EnvironmentRaycastHit[] hits = new EnvironmentRaycastHit[9];
        readonly List<XRInputSubsystem> subsystems = new List<XRInputSubsystem>();
        readonly RecentHandApproachHistory leftHistory = new RecentHandApproachHistory("left");
        readonly RecentHandApproachHistory rightHistory = new RecentHandApproachHistory("right");
        HandProximityDiagnostic hands;
        HandRegionCapture regionCapture;
        TextMesh hud;
        GameObject marker, reticle;
        AudioSource voice;
        AudioClip intro, tableReady, captured;
        Camera eye;
        bool locked, referenceValidated, paused, focused = true, permissionRequested;
        int revision, referenceHits;
        Vector3 referencePoint, candidatePoint;
        float referenceSpread, nextSample, nextLog, lastValidAt = float.NegativeInfinity;
        double stableSince = -1;
        string surfaceStatus = "Look at a clear part of the table.", actionStatus = "Waiting for a table reference.";
        string logPath, sessionId;
        string latencyStatus = "Latency: waiting for a received frame.";
        HandApproachSnapshot lastApproach;
        HandApproachSnapshot pendingApproach;
        readonly List<Vector3> captureHandPoints = new List<Vector3>(64);
        bool haveApproach;
        DateTime observedCameraTimestamp;
        double cameraAdvancedAt = double.NegativeInfinity;
        Pose observedCameraPose;
        bool observedCameraPoseValid;
        bool referenceContradicted, validationCenterFromView;
        Vector3 validationCenter;
        double previousDiagnosticTime = -1;
        int previousDiagnosticFrame;

        public void Configure(OVRCameraRig cameraRig, EnvironmentRaycastManager raycaster,
            OVRHand left, OVRSkeleton leftBones, OVRHand right, OVRSkeleton rightBones)
        { rig = cameraRig; raycasts = raycaster; leftHand = left; leftSkeleton = leftBones; rightHand = right; rightSkeleton = rightBones; }

        public void ConfigureCapture(PassthroughCameraAccess camera, QuestFrameCapture capture)
        { cameraAccess = camera; sender = capture; }

        void Start()
        {
            sessionId = Guid.NewGuid().ToString("N");
            logPath = Path.Combine(Application.persistentDataPath, "table-depth-" + sessionId + ".jsonl");
            eye = rig.centerEyeAnchor.GetComponent<Camera>();
            hands = gameObject.AddComponent<HandProximityDiagnostic>();
            hands.Configure(leftHand, leftSkeleton, rightHand, rightSkeleton, rig.centerEyeAnchor);
            hands.UseIndexTipForApproach = ResilientPhysicalCapture;
            hands.CameraVisibilityCheck = InRgbCameraView;
            regionCapture = gameObject.AddComponent<HandRegionCapture>();
            regionCapture.Configure(cameraAccess, sender);
            regionCapture.CurrentHandPoints = CurrentHandPoints;
            sender.Completed += CaptureCompleted;
            var label = new GameObject("Depth diagnostic readout");
            label.transform.SetParent(rig.centerEyeAnchor, false);
            label.transform.localPosition = new Vector3(0, .29f, 1.1f);
            hud = label.AddComponent<TextMesh>();
            hud.anchor = TextAnchor.UpperCenter; hud.alignment = TextAlignment.Center;
            hud.fontSize = 48; hud.characterSize = .0029f; hud.color = Color.white;
            hud.gameObject.SetActive(showDiagnostics);
            marker = MakeDot("Measured table point", new Color(.2f, .85f, 1), .018f);
            reticle = MakeDot("Head direction", Color.white, .004f);
            reticle.transform.SetParent(rig.centerEyeAnchor, false);
            reticle.transform.localPosition = new Vector3(0, 0, .75f);
            voice = gameObject.AddComponent<AudioSource>(); voice.spatialBlend = 0; voice.volume = .7f;
            intro = Resources.Load<AudioClip>("TableDepthCues/intro");
            tableReady = Resources.Load<AudioClip>("TableDepthCues/ready");
            captured = Resources.Load<AudioClip>("TableDepthCues/captured");
            SubsystemManager.GetSubsystems(subsystems);
            foreach (var subsystem in subsystems) subsystem.trackingOriginUpdated += OriginChanged;
            if(DoublePinchCapture)
            {
                // Three short countdown ticks; a distinct double click marks the actual capture request.
                armedTone=AudioClip.Create("Photo countdown tick",1920,1,24000,false);
                var samples=new float[1920];
                for(int i=0;i<samples.Length;i++) samples[i]=.13f*Mathf.Sin(2*Mathf.PI*660*i/24000f)*Mathf.Sin(Mathf.PI*i/samples.Length);
                armedTone.SetData(samples,0);
                shutterTone=AudioClip.Create("Photo capture start",3360,1,24000,false);
                samples=new float[3360];
                for(int i=0;i<samples.Length;i++)
                {
                    int pulse=i%1680;
                    samples[i]=pulse<960 ? .2f*Mathf.Sin(2*Mathf.PI*1320*i/24000f)*Mathf.Sin(Mathf.PI*pulse/960f) : 0;
                }
                shutterTone.SetData(samples,0);
                gestureRetryTone=AudioClip.Create("Capture retry",4800,1,24000,false);
                samples=new float[4800];
                for(int i=0;i<samples.Length;i++) samples[i]=.18f*Mathf.Sin(2*Mathf.PI*220*i/24000f)*Mathf.Sin(Mathf.PI*i/4800f);
                gestureRetryTone.SetData(samples,0);
                doublePinch=new DoublePinchCaptureDriver(this,sender,cameraAccess,rig.centerEyeAnchor,leftHand,rightHand,
                    leftSkeleton,rightSkeleton,()=>PlayCaptureFeedback(armedTone),()=>{PlayCaptureFeedback(shutterTone);CaptureStarted?.Invoke();},
                    ()=>PlayCaptureFeedback(gestureRetryTone),(sample,hand,rotation)=>PinchTargetSelected?.Invoke(sample,hand,rotation));
            }
            RequestPermissions();
            Speak(intro);
            Debug.Log("[TableDepth] Session=" + sessionId + " Log=" + logPath + " Receiver is configured on QuestFrameCapture.");
        }

        static GameObject MakeDot(string name, Color color, float size)
        {
            var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere); dot.name = name;
            Destroy(dot.GetComponent<Collider>()); dot.transform.localScale = Vector3.one * size;
            dot.GetComponent<Renderer>().material = new Material(Shader.Find("Unlit/Color")) { color = color };
            dot.SetActive(false); return dot;
        }

        bool PermissionGranted(string permission)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Permission.HasUserAuthorizedPermission(permission);
#else
            return false;
#endif
        }

        void RequestPermissions()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (permissionRequested) return;
            var missing = new List<string>();
            if (!PermissionGranted(OVRPermissionsRequester.ScenePermission)) missing.Add(OVRPermissionsRequester.ScenePermission);
            if (!PermissionGranted("horizonos.permission.HEADSET_CAMERA")) missing.Add("horizonos.permission.HEADSET_CAMERA");
            if (requestMicrophonePermission && !PermissionGranted(Permission.Microphone)) missing.Add(Permission.Microphone);
            if (missing.Count == 0) return;
            permissionRequested = true;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionDenied += _ => { surfaceStatus = "Spatial data and camera permissions are needed. Retry with A or in app settings."; };
            Permission.RequestUserPermissions(missing.ToArray(), callbacks);
#endif
        }

        bool HeadTracked()
        {
            if (paused || !focused || !OVRPlugin.initialized) return false;
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            return head.isValid && head.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked &&
                head.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState state) &&
                (state & (InputTrackingState.Position | InputTrackingState.Rotation)) == (InputTrackingState.Position | InputTrackingState.Rotation);
        }

        bool ReferenceFresh => locked && referenceValidated && HeadTracked() && Time.unscaledTime - lastValidAt <= ReferenceFreshSeconds;
        bool ReferenceHistoryUsable => TableReferenceContinuity.CanUseHandHistory(ResilientPhysicalCapture,
            locked, HeadTracked(), ReferenceFresh, referenceContradicted, Time.unscaledTime - lastValidAt);

        void Update()
        {
            if (hands == null) return;
            if (OVRInput.GetDown(OVRInput.RawButton.B)) ResetReference("Reference reset. Look at a clear tabletop.");
            if (OVRInput.GetDown(OVRInput.RawButton.A)) { permissionRequested = false; RequestPermissions(); }
            bool tracked = HeadTracked();
            if (!tracked)
            {
                if (locked || stableSince >= 0) ResetReference("Head tracking unavailable. Reacquire the table.");
                hands.SetSurface(0, false); return;
            }
            if (PermissionGranted("horizonos.permission.HEADSET_CAMERA") && !cameraAccess.enabled) cameraAccess.enabled = true;
            observedCameraPoseValid = cameraAccess.IsPlaying && cameraAccess.Timestamp != default;
            if (observedCameraPoseValid)
            {
                if (cameraAccess.Timestamp != observedCameraTimestamp)
                { observedCameraTimestamp = cameraAccess.Timestamp; cameraAdvancedAt = Time.realtimeSinceStartupAsDouble; }
                observedCameraPoseValid = Time.realtimeSinceStartupAsDouble - cameraAdvancedAt <= .5;
                if (observedCameraPoseValid) observedCameraPose = cameraAccess.GetCameraPose();
            }
            if (!PermissionGranted(OVRPermissionsRequester.ScenePermission))
            { surfaceStatus = "Allow spatial data to measure the table."; hands.SetSurface(0, false); return; }
            if (raycasts == null)
            {
                // IsSupported is cached by the SDK, so ask only after XR initialization.
                if (!EnvironmentRaycastManager.IsSupported)
                { surfaceStatus = "Environment raycasting is not supported on this runtime."; return; }
                raycasts = new GameObject("Native environment raycasts").AddComponent<EnvironmentRaycastManager>();
                raycasts.CustomTrackingSpace = rig.trackingSpace;
            }
            if (Time.unscaledTime >= nextSample)
            {
                nextSample = Time.unscaledTime + SampleInterval;
                if (locked) ValidateReference(); else AcquireReference();
            }
            hands.SetSurface(referencePoint.y, ReferenceHistoryUsable);
        }

        void AcquireReference()
        {
            var head = rig.centerEyeAnchor;
            int index = 0;
            for (int row = -1; row <= 1; row++)
                for (int column = -1; column <= 1; column++)
                {
                    Vector3 direction = (head.forward + .10f * column * head.right + .10f * row * head.up).normalized;
                    raycasts.Raycast(new Ray(head.position, direction), out hits[index++], 3f);
                }
            if (!TableSurfaceEstimator.TryEstimate(hits, 4, head.position, out var estimate, out string reason))
            { stableSince = -1; surfaceStatus = "Look at a clear tabletop: " + reason; return; }
            foreach (var tip in hands.VisibleTips)
                if (Vector3.Distance(tip, estimate.Center) < .24f)
                { stableSince = -1; surfaceStatus = "Move your hands aside while the table reference is acquired."; return; }
            double now = Time.unscaledTimeAsDouble;
            if (stableSince < 0 || Mathf.Abs(estimate.Height - candidatePoint.y) > .015f ||
                Vector2.Distance(new Vector2(estimate.Center.x, estimate.Center.z), new Vector2(candidatePoint.x, candidatePoint.z)) > .07f)
            { stableSince = now; candidatePoint = estimate.Center; }
            surfaceStatus = "Checking a horizontal surface... " + Mathf.Clamp01((float)(now - stableSince)) .ToString("P0");
            if (now - stableSince < .8) return;
            referencePoint = estimate.Center; locked = true; referenceValidated = true; revision++;
            referenceContradicted = false; validationCenter = referencePoint; validationCenterFromView = false;
            lastValidAt = Time.unscaledTime; referenceSpread = estimate.HeightSpread; referenceHits = estimate.InlierCount;
            surfaceStatus = "Reference acquired. Keep this part of the table in view.";
            actionStatus = "Move both hands above the table. Lower them, then move them away.";
            leftHistory.Reset(); rightHistory.Reset(); Speak(tableReady);
            Debug.Log("[TableDepth] Reference acquired world=" + referencePoint.ToString("F3"));
        }

        void ValidateReference()
        {
            validationCenter = referencePoint;
            validationCenterFromView = ResilientPhysicalCapture && TableReferenceContinuity.TryViewPatchCenter(
                rig.centerEyeAnchor.position, rig.centerEyeAnchor.forward, referencePoint, out validationCenter);
            int index = 0;
            for (int row = -1; row <= 1; row++)
                for (int column = -1; column <= 1; column++)
                {
                    Vector3 target = validationCenter + new Vector3(column * ReferenceRadius, 0, row * ReferenceRadius);
                    if (!InView(target)) { hits[index++] = new EnvironmentRaycastHit { status = EnvironmentRaycastHitStatus.NoHit }; continue; }
                    var direction = target - rig.centerEyeAnchor.position;
                    raycasts.Raycast(new Ray(rig.centerEyeAnchor.position, direction.normalized), out hits[index++], 3f);
                }
            referenceValidated = TableSurfaceEstimator.TryValidateReference(hits, referencePoint.y,
                out referenceHits, out referenceSpread, out string reason);
            if (referenceValidated)
            { lastValidAt = Time.unscaledTime; referenceContradicted = false; surfaceStatus = "Table reference verified in current depth."; }
            else
            {
                if (ResilientPhysicalCapture && TableReferenceContinuity.HasContradictoryPlane(hits,
                    rig.centerEyeAnchor.position, referencePoint.y)) referenceContradicted = true;
                surfaceStatus = referenceContradicted ? "Current depth conflicts with the locked table height; hand history invalidated."
                    : "Table measurement unavailable: " + reason;
            }
        }

        bool InView(Vector3 point)
        {
            if (eye == null) return false;
            Vector3 viewport = eye.WorldToViewportPoint(point);
            return viewport.z > .15f && viewport.x >= .05f && viewport.x <= .95f && viewport.y >= .05f && viewport.y <= .95f;
        }

        bool InRgbCameraView(Vector3 point)
        {
            if (!observedCameraPoseValid || !cameraAccess.IsPlaying || Time.realtimeSinceStartupAsDouble - cameraAdvancedAt > .5) return false;
            Vector3 local = Quaternion.Inverse(observedCameraPose.rotation) * (point - observedCameraPose.position);
            if (!float.IsFinite(local.z) || local.z <= .15f) return false;
            Vector2 viewport = cameraAccess.WorldToViewportPoint(point, observedCameraPose);
            return float.IsFinite(viewport.x) && float.IsFinite(viewport.y) &&
                viewport.x >= .05f && viewport.x <= .95f && viewport.y >= .05f && viewport.y <= .95f;
        }

        void LateUpdate()
        {
            if (hud == null || hands == null) return;
            if(DoublePinchCapture && doublePinch!=null)
            {
                doublePinch.Tick(CaptureAllowed());
                actionStatus=doublePinch.Status;
            }
            else
            {
                ObserveHand(leftHistory, "left", hands.LeftValid, hands.LeftClosestPoint, hands.LeftSignedGap);
                ObserveHand(rightHistory, "right", hands.RightValid, hands.RightClosestPoint, hands.RightSignedGap);
                TryPendingCapture();
            }
            bool valid = ReferenceFresh;
            hud.gameObject.SetActive(showDiagnostics);
            marker.SetActive(showDiagnostics && valid); reticle.SetActive(showDiagnostics && !locked && HeadTracked());
            if (valid) marker.transform.position = referencePoint + Vector3.up * .008f;
            string distance = valid ? "Head to table point: " + Vector3.Distance(rig.centerEyeAnchor.position, referencePoint).ToString("F2") + " m" +
                " | Vertical gap: " + (rig.centerEyeAnchor.position.y - referencePoint.y).ToString("F2") + " m" : "Head to table: -- (no current measurement)";
            string last = haveApproach ? "Last approach: " + lastApproach.hand + " | " + (lastApproach.sample.signedGap * 100).ToString("F1") +
                " cm | " + lastApproach.frameAge + " frames before confirmation" : "No approach sample selected yet.";
            hud.text = "TABLE DEPTH + HAND REGION TEST\n" + distance + "\n" +
                "L fingertip to plane: " + Gap(hands.LeftValid, hands.LeftSignedGap) + " | R: " + Gap(hands.RightValid, hands.RightSignedGap) + "\n" +
                "L: " + ShortHand(hands.LeftVisible, hands.LeftTracked) + " | R: " + ShortHand(hands.RightVisible, hands.RightTracked) + "\n" +
                Wrap(surfaceStatus, 78) + "\n" + (valid ? "Depth support: " + referenceHits + "/9 | Height spread: " + (referenceSpread * 100).ToString("F1") + " cm (not accuracy)\n" : "") +
                last + "\n" + latencyStatus + "\n" + Wrap(actionStatus, 78) + "\n" + Wrap(sender.Busy ? sender.Status : regionCapture.Status, 78) +
                "\nNo block recognition or optical focus control. B: reacquire table.";
            if (Time.unscaledTime >= nextLog) { nextLog = Time.unscaledTime + .5f; WriteDiagnostic(); }
        }

        void ObserveHand(RecentHandApproachHistory history, string hand, bool valid, Vector3 finger, float gap)
        {
            var sample = new HandApproachSample { time = Time.realtimeSinceStartupAsDouble, frame = Time.frameCount,
                headPosition = rig.centerEyeAnchor.position, fingerPosition = finger, signedGap = gap, surfaceHeight = referencePoint.y };
            if (!history.Observe(sample, valid && ReferenceHistoryUsable, out var snapshot)) return;
            lastApproach = snapshot; haveApproach = true;
            WriteLine(JsonUtility.ToJson(new ApproachLog { sessionId = sessionId, timestampUtc = DateTime.UtcNow.ToString("o"), approach = snapshot }));
            Debug.Log("[TableDepth] Closest sample " + JsonUtility.ToJson(snapshot));
            if (pendingApproach == null || Mathf.Abs(snapshot.sample.signedGap) < Mathf.Abs(pendingApproach.sample.signedGap))
                pendingApproach = snapshot;
            actionStatus = "Approach saved. Move both visible hands clear of the region.";
        }

        void TryPendingCapture()
        {
            if (!CaptureAllowed()) { pendingApproach = null; return; }
            if (pendingApproach == null) return;
            if (!HeadTracked() || !hands.LeftValid || !hands.RightValid)
            { pendingApproach = null; actionStatus = "Approach saved; photo canceled because tracking or table measurement became unavailable."; return; }
            double pendingAge = Time.realtimeSinceStartupAsDouble - pendingApproach.confirmedAt;
            if (pendingAge > (ResilientPhysicalCapture ? TableReferenceContinuity.PendingLiftSeconds : .45))
            { pendingApproach = null; actionStatus = "Approach saved; photo skipped because hands did not clear in time. Lower and lift again."; return; }
            if (!ReferenceFresh)
            {
                if (TableReferenceContinuity.CanWaitForFreshDepth(ResilientPhysicalCapture, ReferenceHistoryUsable,
                    hands.LeftValid && hands.RightValid, pendingAge))
                { actionStatus = "Lift confirmed; waiting briefly for fresh table depth before taking the photo."; return; }
                pendingApproach = null;
                actionStatus = "Approach saved; photo canceled because current table depth is unavailable.";
                return;
            }
            if (sender.Busy || !sender.IsReady || !CaptureClear(pendingApproach)) return;
            var snapshot = pendingApproach;
            int capturedRevision = revision;
            var capturedContextGuard = CapturedContextStillValid;
            Func<bool> frozenGuard = FreezeCaptureAfterReadback
                ? () => isActiveAndEnabled && focused && !paused && locked && capturedRevision == revision &&
                    capturedContextGuard != null && capturedContextGuard()
                : null;
            bool accepted = regionCapture.Request(snapshot, revision,
                () => capturedRevision == revision && CaptureClear(snapshot), frozenGuard);
            pendingApproach = null;
            actionStatus = accepted ? "Approach saved; capturing a fresh frame for its region." : "Approach saved. " + regionCapture.Status;
            if (accepted) CaptureStarted?.Invoke();
        }

        bool CaptureAllowed()
        {
            if(CapturePermitted == null) return true;
            try { return CapturePermitted(); }
            catch(Exception ex) { Debug.LogWarning("[TableDepth] Capture gate failed: "+ex.Message); return false; }
        }

        bool CaptureClear(HandApproachSnapshot snapshot)
        {
            if (!ReferenceFresh || !hands.LeftValid || !hands.RightValid || !HeadTracked()) return false;
            foreach (var tip in hands.VisibleTips)
            {
                Vector3 delta = tip - snapshot.sample.fingerPosition;
                if (Mathf.Abs(delta.x) < .15f && Mathf.Abs(delta.z) < .11f && tip.y - referencePoint.y < .08f) return false;
            }
            return true;
        }

        IReadOnlyList<Vector3> CurrentHandPoints()
        {
            captureHandPoints.Clear();
            foreach (var skeleton in new[] { leftSkeleton, rightSkeleton })
                if (skeleton != null && skeleton.IsDataValid && skeleton.IsDataHighConfidence && skeleton.Bones != null)
                    foreach (var bone in skeleton.Bones)
                        if (bone.Transform != null) captureHandPoints.Add(bone.Transform.position);
            return captureHandPoints;
        }

        void CaptureCompleted(bool accepted, string message)
        {
            actionStatus = accepted ? "Python received the photo and hand-selected region." : message;
            if(!accepted && DoublePinchCapture && gestureRetryTone!=null) PlayCaptureFeedback(gestureRetryTone);
            if (accepted)
            {
                Speak(captured);
                if (sender.LastMetadata != null)
                {
                    var metadata = sender.LastMetadata;
                    if (metadata.captureTimings != null && metadata.handApproach != null)
                    {
                        double milliseconds = 1000 * (metadata.captureTimings.acknowledgedMonotonicSeconds - metadata.handApproach.liftStartedMonotonicSeconds);
                        latencyStatus = (DoublePinchCapture ? "Double pinch to Python ACK: " : "Lift threshold to Python ACK: ") + milliseconds.ToString("F0") + " ms | " +
                            (DoublePinchCapture ? "includes the 3-second countdown" : milliseconds <= 1000 ? "within 1 s target" : "over 1 s target");
                        Debug.Log("[TableDepth] " + latencyStatus);
                    }
                    WriteLine(JsonUtility.ToJson(metadata));
                }
            }
        }
        static string Gap(bool valid, float gap) => valid ? (gap * 100).ToString("F1") + " cm" : "--";
        static string ShortHand(bool visible, bool tracked) => visible ? "tracked / in view" : tracked ? "uncertain / outside view" : "not tracked";
        static string Wrap(string value, int width)
        { if (string.IsNullOrEmpty(value) || value.Length <= width) return value; int cut = value.LastIndexOf(' ', Math.Min(width, value.Length - 1)); return cut > 0 ? value.Substring(0, cut) + "\n" + value.Substring(cut + 1) : value; }
        public Func<bool> CaptureFeedbackPermitted { get; set; }
        void PlayCaptureFeedback(AudioClip clip)
        {
            if(voice!=null && clip!=null && (CaptureFeedbackPermitted==null || CaptureFeedbackPermitted())) voice.PlayOneShot(clip);
        }
        void Speak(AudioClip clip) { if (!diagnosticSpeechEnabled || voice == null || clip == null) return; voice.Stop(); voice.PlayOneShot(clip); }

        void ResetReference(string reason)
        {
            locked = referenceValidated = false; stableSince = -1; lastValidAt = float.NegativeInfinity; revision++;
            referenceContradicted = validationCenterFromView = false; validationCenter = Vector3.zero;
            leftHistory.Reset(); rightHistory.Reset(); haveApproach = false;
            pendingApproach = null;
            doublePinch?.Reset();
            observedCameraPoseValid = false;
            hands?.SetSurface(0, false); sender?.CancelPending(reason); surfaceStatus = reason;
            ReferenceReset?.Invoke(revision,reason);
        }
        void OriginChanged(XRInputSubsystem _) => ResetReference("Tracking origin changed. Reacquire the table.");
        void OnApplicationPause(bool value) { paused = value; ResetReference("Pause changed. Reacquire the table."); }
        void OnApplicationFocus(bool value) { focused = value; ResetReference("Focus changed. Reacquire the table."); }
        void OnDestroy()
        { doublePinch?.Reset(); if(armedTone!=null) Destroy(armedTone); if(gestureRetryTone!=null) Destroy(gestureRetryTone); if(shutterTone!=null) Destroy(shutterTone); if (sender != null) sender.Completed -= CaptureCompleted; foreach (var subsystem in subsystems) subsystem.trackingOriginUpdated -= OriginChanged; }

        [Serializable] sealed class ApproachLog { public string type = "closest_hand_sample"; public string sessionId, timestampUtc; public HandApproachSnapshot approach; }
        [Serializable] sealed class DiagnosticLog
        {
            public string type = "depth_sample", sessionId, timestampUtc, surfaceStatus;
            public double monotonicTime;
            public int frame, supportingDepthHits;
            public bool tableValid, leftValid, rightValid;
            public Vector3 headPosition, tablePoint;
            public float headToTableMeters, verticalMeters, heightSpreadMeters, leftGapMeters, rightGapMeters;
            public bool hasTableReference, leftHandPositionValid, rightHandPositionValid;
            public int referenceRevision;
            public string handPositionFrame = "Closest fingertip; meters; world-aligned XYZ, Y up; table position = world position minus locked tablePoint.";
            public Vector3 leftHandWorldPosition, rightHandWorldPosition, leftHandTablePosition, rightHandTablePosition;
            public bool leftTracked, rightTracked, leftHighConfidence, rightHighConfidence, leftVisible, rightVisible;
            public string leftStatus, rightStatus, leftHistoryStatus, rightHistoryStatus;
            public int leftHistorySampleCount, rightHistorySampleCount;
            public bool leftLiftPending, rightLiftPending, leftAwaitingReturn, rightAwaitingReturn;
            public bool headTracked, cameraPlaying, captureAllowed, senderReady, senderBusy, pendingApproach;
            public double cameraFrameAgeSeconds;
            public float observedAppFps;
            public string senderStatus, regionCaptureStatus, actionStatus;
            public HandProximityDiagnostic.RawHandDiagnostic leftRawHand, rightRawHand;
            public string rawHandPositionFrame = "SDK wrist/index-tip estimates in world meters; table positions subtract locked tablePoint, even when referenceFresh is false. Not capture acceptance.";
            public string activeController, connectedControllers;
            public bool appFocused, appPaused, vrFocus, inputFocus;
            public bool historyReferenceUsable, depthHeldForHistory, referenceContradicted, validationCenterFromView;
            public float referenceAgeSeconds;
            public Vector3 validationCenter;
            public string handApproachLandmark, captureGesture, gestureStatus, leftClearanceStatus, rightClearanceStatus;
            public bool gestureArmed, leftPinchTracked, rightPinchTracked, leftPinched, rightPinched;
        }
        void WriteDiagnostic()
        {
            bool valid = ReferenceFresh;
            double now = Time.realtimeSinceStartupAsDouble;
            bool leftPositionValid = ReferenceHistoryUsable && hands.LeftValid && FinitePosition(hands.LeftClosestPoint);
            bool rightPositionValid = ReferenceHistoryUsable && hands.RightValid && FinitePosition(hands.RightClosestPoint);
            double cameraAge = now - cameraAdvancedAt;
            if (!double.IsFinite(cameraAge) || cameraAge < 0) cameraAge = -1;
            double elapsed = now - previousDiagnosticTime;
            float fps = previousDiagnosticTime >= 0 && elapsed > 0
                ? (float)((Time.frameCount - previousDiagnosticFrame) / elapsed)
                : Time.unscaledDeltaTime > 0 ? 1f / Time.unscaledDeltaTime : 0;
            if (!float.IsFinite(fps) || fps < 0) fps = 0;
            previousDiagnosticTime = now; previousDiagnosticFrame = Time.frameCount;
            var entry = new DiagnosticLog { sessionId = sessionId, timestampUtc = DateTime.UtcNow.ToString("o"),
                monotonicTime = now, frame = Time.frameCount, surfaceStatus = surfaceStatus,
                supportingDepthHits = referenceHits, tableValid = valid, leftValid = hands.LeftValid, rightValid = hands.RightValid,
                headPosition = rig.centerEyeAnchor.position, tablePoint = locked ? referencePoint : Vector3.zero,
                headToTableMeters = valid ? Vector3.Distance(rig.centerEyeAnchor.position, referencePoint) : 0,
                verticalMeters = valid ? rig.centerEyeAnchor.position.y - referencePoint.y : 0,
                heightSpreadMeters = valid ? referenceSpread : 0,
                leftGapMeters = hands.LeftValid ? hands.LeftSignedGap : 0, rightGapMeters = hands.RightValid ? hands.RightSignedGap : 0,
                hasTableReference = locked, referenceRevision = revision,
                leftHandPositionValid = leftPositionValid, rightHandPositionValid = rightPositionValid,
                leftHandWorldPosition = leftPositionValid ? hands.LeftClosestPoint : Vector3.zero,
                rightHandWorldPosition = rightPositionValid ? hands.RightClosestPoint : Vector3.zero,
                leftHandTablePosition = leftPositionValid ? hands.LeftClosestPoint - referencePoint : Vector3.zero,
                rightHandTablePosition = rightPositionValid ? hands.RightClosestPoint - referencePoint : Vector3.zero,
                leftTracked = hands.LeftTracked, rightTracked = hands.RightTracked,
                leftHighConfidence = hands.LeftHighConfidence, rightHighConfidence = hands.RightHighConfidence,
                leftVisible = hands.LeftVisible, rightVisible = hands.RightVisible,
                leftStatus = hands.LeftStatus, rightStatus = hands.RightStatus,
                leftHistoryStatus = leftHistory.Status, rightHistoryStatus = rightHistory.Status,
                leftHistorySampleCount = leftHistory.Count, rightHistorySampleCount = rightHistory.Count,
                leftLiftPending = leftHistory.LiftPending, rightLiftPending = rightHistory.LiftPending,
                leftAwaitingReturn = leftHistory.AwaitingReturn, rightAwaitingReturn = rightHistory.AwaitingReturn,
                headTracked = HeadTracked(), cameraPlaying = cameraAccess != null && cameraAccess.IsPlaying,
                cameraFrameAgeSeconds = cameraAge, observedAppFps = fps, captureAllowed = CaptureAllowed(),
                senderReady = sender != null && sender.IsReady, senderBusy = sender != null && sender.Busy,
                senderStatus = sender != null ? sender.Status : "Capture sender unavailable.",
                regionCaptureStatus = regionCapture != null ? regionCapture.Status : "Region capture unavailable.",
                pendingApproach = pendingApproach != null, actionStatus = actionStatus,
                leftRawHand = HandProximityDiagnostic.ReadRawTracking(leftHand, leftSkeleton, false, locked, referencePoint, valid),
                rightRawHand = HandProximityDiagnostic.ReadRawTracking(rightHand, rightSkeleton, true, locked, referencePoint, valid),
                activeController = OVRPlugin.initialized ? OVRInput.GetActiveController().ToString() : "XR not initialized",
                connectedControllers = OVRPlugin.initialized ? OVRInput.GetConnectedControllers().ToString() : "XR not initialized",
                appFocused = focused, appPaused = paused,
                vrFocus = OVRPlugin.initialized && OVRManager.hasVrFocus,
                inputFocus = OVRPlugin.initialized && OVRManager.hasInputFocus,
                historyReferenceUsable = ReferenceHistoryUsable, depthHeldForHistory = !valid && ReferenceHistoryUsable,
                referenceContradicted = referenceContradicted,
                referenceAgeSeconds = float.IsFinite(lastValidAt) ? Mathf.Max(0, Time.unscaledTime - lastValidAt) : -1,
                validationCenter = validationCenter, validationCenterFromView = validationCenterFromView,
                captureGesture = DoublePinchCapture ? "double_pinch_timer" : "automatic_lift",
                gestureArmed = doublePinch!=null && doublePinch.IsArmed, gestureStatus = doublePinch?.Status,
                leftClearanceStatus=doublePinch?.LeftClearanceStatus,rightClearanceStatus=doublePinch?.RightClearanceStatus,
                leftPinchTracked=doublePinch!=null && doublePinch.LeftPinchTracked, rightPinchTracked=doublePinch!=null && doublePinch.RightPinchTracked,
                leftPinched=doublePinch!=null && doublePinch.LeftPinched, rightPinched=doublePinch!=null && doublePinch.RightPinched,
                handApproachLandmark = ResilientPhysicalCapture ? "index_tip" : "closest_fingertip",
                handPositionFrame = (ResilientPhysicalCapture ? "Index tip" : "Closest fingertip") +
                    "; meters; world-aligned XYZ, Y up; table position subtracts locked tablePoint; held depth is history-only, not capture acceptance." };
            WriteLine(JsonUtility.ToJson(entry));
        }
        static bool FinitePosition(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        void WriteLine(string json)
        { try { if (!string.IsNullOrEmpty(logPath)) File.AppendAllText(logPath, json + "\n"); } catch (IOException ex) { Debug.LogWarning("[TableDepth] Log write failed: " + ex.Message); } }
    }
}
