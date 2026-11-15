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
    /// <summary>
    /// Physical camera/color test with explicit capture and a manually selected
    /// block-top plane. Neither the plane nor the image regions detect blocks.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class ManualColorCameraDemo : MonoBehaviour
    {
        public OVRCameraRig rig;
        public PassthroughCameraAccess cameraAccess;
        public QuestFrameCapture capture;
        public string endpoint = "http://127.0.0.1:8767/frames";
        public float boardWidth = .60f, boardDepth = .40f;

        const string CameraPermission = "horizonos.permission.HEADSET_CAMERA";
        readonly List<XRInputSubsystem> inputSubsystems = new List<XRInputSubsystem>();
        Transform board;
        GameObject controllerPoint;
        LineRenderer outline;
        QuestLetterOverlay letterOverlay;
        TextMesh statusText;
        AudioSource cue;
        AudioClip receivedCue;
        string sessionId;
        string result = "No frame sent. Keep real blocks visible and unobstructed.";
        bool placed, paused, focused = true, requestingPermission;
        int revision;
        float nextHeightStep;

        void Start()
        {
            sessionId = Guid.NewGuid().ToString("N");
            capture.Configure(cameraAccess, endpoint);
            capture.Completed += CaptureCompleted;
            letterOverlay = gameObject.AddComponent<QuestLetterOverlay>();
            letterOverlay.Configure(rig.centerEyeAnchor, endpoint);
            board = new GameObject("Manually selected block-top plane").transform;
            outline = board.gameObject.AddComponent<LineRenderer>();
            outline.useWorldSpace = false;
            outline.loop = true;
            outline.positionCount = 4;
            outline.widthMultiplier = .005f;
            outline.material = new Material(Shader.Find("Unlit/Color"));
            outline.material.color = new Color(.3f, 1f, .7f);
            outline.SetPositions(new[] {
                new Vector3(-boardWidth/2,.001f,-boardDepth/2), new Vector3(boardWidth/2,.001f,-boardDepth/2),
                new Vector3(boardWidth/2,.001f,boardDepth/2), new Vector3(-boardWidth/2,.001f,boardDepth/2)
            });
            outline.enabled = false;
            controllerPoint = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            controllerPoint.name = "Controller tracking-point height guide";
            Destroy(controllerPoint.GetComponent<Collider>());
            controllerPoint.transform.localScale = Vector3.one * .012f;
            controllerPoint.GetComponent<MeshRenderer>().sharedMaterial = outline.material;

            var label = new GameObject("Manual color test status");
            label.transform.SetParent(rig.centerEyeAnchor, false);
            label.transform.localPosition = new Vector3(0, .21f, 1.15f);
            statusText = label.AddComponent<TextMesh>();
            statusText.anchor = TextAnchor.UpperCenter;
            statusText.alignment = TextAlignment.Center;
            statusText.fontSize = 48;
            statusText.characterSize = .0038f;
            statusText.color = Color.white;
            cue = gameObject.AddComponent<AudioSource>();
            cue.playOnAwake = false;
            cue.spatialBlend = 0;
            cue.volume = .5f;
            const int rate = 24000;
            var samples = new float[rate / 6];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = .3f * Mathf.Sin(2 * Mathf.PI * 780 * i / rate) * Mathf.Sin(Mathf.PI * i / samples.Length);
            receivedCue = AudioClip.Create("Frame received", samples.Length, 1, rate, false);
            receivedCue.SetData(samples, 0);
            SubsystemManager.GetSubsystems(inputSubsystems);
            foreach (var subsystem in inputSubsystems) subsystem.trackingOriginUpdated += TrackingOriginChanged;
            EnsurePermission();
            Debug.Log("[ManualColorCamera] A: select block-top height at controller dot. Stick up/down: adjust height. Right trigger: capture. B: reset. Endpoint: " + endpoint);
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
            callbacks.PermissionGranted += _ => {
                requestingPermission = false;
                cameraAccess.enabled = true;
                result = "Camera allowed. Hold the right controller near the bricks, then press A.";
            };
            callbacks.PermissionDenied += _ => {
                requestingPermission = false;
                result = "Camera permission denied. Press A to request again.";
            };
            Permission.RequestUserPermission(CameraPermission, callbacks);
#else
            result = "Use the Quest APK for the real camera test.";
#endif
        }

        void LateUpdate()
        {
            if (rig == null || capture == null || statusText == null) return;
            if (OVRInput.GetDown(OVRInput.RawButton.A, OVRInput.Controller.RTouch))
            {
                if (!PermissionGranted()) EnsurePermission(); else PlaceBlockTopPlane();
            }
            if (OVRInput.GetDown(OVRInput.RawButton.B, OVRInput.Controller.RTouch))
                ClearPlacement("Surface reset. Hold the right controller near the bricks and press A.");
            AdjustSurfaceHeight();
            if (OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger, OVRInput.Controller.RTouch)) CaptureFrame();
            bool controllerTracked = RightControllerTracked();
            controllerPoint.SetActive(!placed && controllerTracked);
            if (controllerTracked) controllerPoint.transform.position = rig.rightControllerAnchor.position;

            string cameraState = !PermissionGranted() ? "Camera permission required" :
                cameraAccess.IsPlaying ? "Camera active" : "Waiting for camera";
            statusText.text = "PHYSICAL COLOR TEST\n" +
                (placed ? "Surface height: " + board.position.y.ToString("F3") + " m | Stick up/down: adjust\n" : "Right controller dot: surface-height guide | A: set surface\n") +
                "Align the green plane with the BRICK TOPS.\n" +
                "Right trigger: capture | B: reset surface and labels\n" +
                cameraState + " | Received: " + capture.AcceptedCount + "\n" +
                Wrap(capture.Busy ? capture.Status : result, 62) + "\n" +
                Wrap(letterOverlay.Status, 62) + "\n" +
                "Mac: select face corners, then Recognize colors.\nManual surface/regions; no automatic block detection.";
        }

        void CaptureFrame()
        {
            if (!placed) { result = "Set the block-top surface with A first."; return; }
            if (capture.Busy) { result = "Wait for the current frame to finish."; return; }
            if (!HeadTracked() || !FloorTrackingActive())
            { result = "Wait for stable head and floor tracking."; return; }
            if (!capture.IsReady) { result = "Camera is not ready. Check permission and try again."; return; }
            int captureRevision = revision;
            letterOverlay.Clear("Taking a new image; old labels cleared.");
            var context = new QuestCaptureContext {
                SessionId = sessionId, FrameRevision = revision, BoardPose = new Pose(board.position, board.rotation),
                BoardWidth = boardWidth, BoardDepth = boardDepth, BlockHeight = 0,
                ClearSeconds = 0, TriggerType = "manual"
            };
            bool started = capture.RequestCapture(context, () => placed && revision == captureRevision && HeadTracked() && FloorTrackingActive());
            result = started ? "Taking the next camera frame. Keep the blocks visible." : capture.Status;
        }

        void PlaceBlockTopPlane()
        {
            if (capture.Busy || letterOverlay.WaitingForResult)
            { result = "Finish recognition or press B before changing the surface."; return; }
            if (!HeadTracked() || !FloorTrackingActive())
            { result = "Quest must report floor-level tracking. Check room/floor setup."; return; }
            if (!RightControllerTracked()) { result = "Right controller position is not tracked."; return; }
            var up = rig.trackingSpace.up;
            var forward = Vector3.ProjectOnPlane(rig.centerEyeAnchor.forward, up).normalized;
            if (forward.sqrMagnitude < .1f) forward = rig.trackingSpace.forward;
            // Controller anchors are tracking points, not measured contact tips.
            // The user visually aligns the plane with the brick tops using the stick.
            board.SetPositionAndRotation(rig.rightControllerAnchor.position, Quaternion.LookRotation(forward, up));
            revision++;
            placed = true;
            outline.enabled = true;
            letterOverlay.Clear("Surface selected. New capture required.");
            result = "Adjust the green plane to the BRICK TOPS with the right stick, then capture.";
            Debug.Log("[ManualColorCamera] Manual block-top plane revision=" + revision + " world=" + board.position.ToString("F3"));
        }

        bool RightControllerTracked() => rig.rightControllerAnchor != null &&
            OVRInput.GetControllerPositionTracked(OVRInput.Controller.RTouch) &&
            OVRInput.GetControllerOrientationTracked(OVRInput.Controller.RTouch);

        void AdjustSurfaceHeight()
        {
            if (!placed || capture.Busy || letterOverlay.WaitingForResult || !RightControllerTracked()) return;
            float axis = OVRInput.Get(OVRInput.RawAxis2D.RThumbstick, OVRInput.Controller.RTouch).y;
            if (Mathf.Abs(axis) < .6f) { nextHeightStep = 0; return; }
            if (Time.unscaledTime < nextHeightStep) return;
            nextHeightStep = Time.unscaledTime + .15f;
            board.position += board.up * (Mathf.Sign(axis) * .005f);
            revision++;
            letterOverlay.Clear("Surface height changed. Capture again for labels.");
            result = "Manual surface height adjusted by 5 mm. Align with the brick tops.";
        }

        bool HeadTracked()
        {
            if (paused || !focused) return false;
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            return head.isValid && head.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked &&
                head.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState state) &&
                (state & (InputTrackingState.Position | InputTrackingState.Rotation)) == (InputTrackingState.Position | InputTrackingState.Rotation);
        }

        bool FloorTrackingActive()
        {
            foreach (var subsystem in inputSubsystems)
                if (subsystem.running && subsystem.GetTrackingOriginMode() == TrackingOriginModeFlags.Floor) return true;
            return false;
        }

        void CaptureCompleted(bool success, string message)
        {
            result = success ? message : "Capture failed: " + message;
            if (success)
            {
                cue.PlayOneShot(receivedCue);
                int capturedRevision = revision;
                letterOverlay.Begin(capture.LastMetadata, () => placed && revision == capturedRevision && HeadTracked() && FloorTrackingActive());
            }
            Debug.Log("[ManualColorCamera] " + result);
        }

        void ClearPlacement(string message)
        {
            revision++;
            placed = false;
            capture?.CancelPending("Surface reference invalidated");
            letterOverlay?.Clear("Surface reference cleared; labels removed.");
            if (outline != null) outline.enabled = false;
            result = message;
        }

        void TrackingOriginChanged(XRInputSubsystem _) => ClearPlacement("Tracking origin changed. Set the surface again with A.");
        void OnApplicationPause(bool value) { paused = value; if (value) ClearPlacement("App paused. Set the surface again with A."); }
        void OnApplicationFocus(bool value)
        {
            focused = value;
            if (!value) { capture?.CancelPending("Application lost focus"); letterOverlay?.Clear("Focus lost; capture again for labels."); }
        }

        static string Wrap(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length > 250) text = text.Substring(0, 247) + "...";
            var lines = new List<string>();
            while (text.Length > width)
            {
                int split = text.LastIndexOf(' ', width, width);
                if (split <= 0) split = width;
                lines.Add(text.Substring(0, split));
                text = text.Substring(split).TrimStart();
            }
            lines.Add(text);
            return string.Join("\n", lines);
        }

        void OnDestroy()
        {
            if (capture != null) capture.Completed -= CaptureCompleted;
            foreach (var subsystem in inputSubsystems) subsystem.trackingOriginUpdated -= TrackingOriginChanged;
            if (board != null) { if (outline != null) Destroy(outline.material); Destroy(board.gameObject); }
            if (controllerPoint != null) Destroy(controllerPoint);
            if (receivedCue != null) Destroy(receivedCue);
        }
    }
}
