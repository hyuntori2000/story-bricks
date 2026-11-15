using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace BrailleSoundDemo
{
    /// <summary>Pure state policy. A lost XR session never becomes a desktop session.</summary>
    public sealed class SpatialTrackingState
    {
        public bool IsXrSession { get; private set; }
        public bool IsTracked { get; private set; }
        public bool AlignmentValid { get; private set; } = true;
        public string Reason { get; private set; } = "Desktop mock positions";
        public bool Ready => AlignmentValid && (!IsXrSession || IsTracked);

        public void Observe(bool xrExpected, bool tracked)
        {
            if (xrExpected && !IsXrSession)
            {
                IsXrSession = true;
                Invalidate("Press A to place the mock board");
            }
            if (IsXrSession && IsTracked && !tracked)
                Invalidate("Head tracking lost. Recover tracking, then press A to place the mock board");
            IsTracked = IsXrSession && tracked;
        }

        public bool Align()
        {
            if (IsXrSession && !IsTracked) return false;
            AlignmentValid = true;
            Reason = IsXrSession ? "Mock board placed in the current tracking frame" : "Desktop mock positions";
            return true;
        }

        public void Invalidate(string reason)
        {
            AlignmentValid = false;
            Reason = reason;
        }
    }

    /// <summary>
    /// Shares one world frame between the head listener and the board. This is mock
    /// board placement, not physical-marker calibration or persistent anchoring.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class QuestAudioTracking : MonoBehaviour
    {
        public bool IsXrSession => state.IsXrSession;
        public bool IsTracked => state.IsTracked;
        public bool IsSpatialReady => initialized && state.Ready;
        public TrackingOriginModeFlags OriginMode { get; private set; }
        public string Status => state.Reason + (IsXrSession ? " | Origin: " + OriginMode : "");
        public event Action<bool> SpatialReadyChanged;
        public int FrameRevision { get; private set; }
        public event Action BoardFrameChanged;

        readonly SpatialTrackingState state = new SpatialTrackingState();
        readonly List<XRInputSubsystem> inputs = new List<XRInputSubsystem>();
        readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        readonly HashSet<XRInputSubsystem> subscribed = new HashSet<XRInputSubsystem>();
        Transform rig, board;
        Camera listenerCamera;
        bool initialized, previousReady, enteredXr;

        public void Initialize(Transform listenerRig, Camera camera, Transform detectionOrigin)
        {
            if (listenerRig == null || camera == null || detectionOrigin == null)
                throw new ArgumentNullException("Tracking requires a rig, listener camera and detection origin.");
            rig = listenerRig; listenerCamera = camera; board = detectionOrigin;
            initialized = true;
            state.Observe(Application.platform == RuntimePlatform.Android, false);
            previousReady = IsSpatialReady;
        }

        void OnEnable() { Application.onBeforeRender += BeforeRender; }
        void OnDisable()
        {
            Application.onBeforeRender -= BeforeRender;
            foreach (var input in subscribed) input.trackingOriginUpdated -= OriginChanged;
            subscribed.Clear();
            if (initialized && IsXrSession) InvalidateAlignment("Tracking paused. Press A to place the mock board after recovery");
        }

        void Update()
        {
            if (!initialized) return;
            inputs.Clear(); SubsystemManager.GetSubsystems(inputs);
            displays.Clear(); SubsystemManager.GetSubsystems(displays);
            bool running = false, inputRunning = false;
            foreach (var display in displays) running |= display.running;
            foreach (var input in inputs)
            {
                if (!input.running) continue;
                inputRunning = true;
                if (subscribed.Add(input))
                {
                    // Request Floor once, but use the ACTUAL reported frame. No guessed height offset.
                    input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                    input.trackingOriginUpdated += OriginChanged;
                }
                OriginMode = input.GetTrackingOriginMode();
            }
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            bool valid = TryHeadPose(head, out Vector3 position, out Quaternion rotation);
            bool expected = Application.platform == RuntimePlatform.Android || running || head.isValid;
            state.Observe(expected, running && inputRunning && valid);
            if (IsXrSession && !enteredXr)
            {
                rig.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                enteredXr = true;
            }
            if (IsTracked) listenerCamera.transform.SetLocalPositionAndRotation(position, rotation);
            PublishReadiness();
        }

        void BeforeRender()
        {
            if (!initialized || !IsTracked) return;
            if (TryHeadPose(InputDevices.GetDeviceAtXRNode(XRNode.Head), out var position, out var rotation))
                listenerCamera.transform.SetLocalPositionAndRotation(position, rotation);
            else
            {
                state.Observe(true, false);
                PublishReadiness();
            }
        }

        static bool TryHeadPose(InputDevice head, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero; rotation = Quaternion.identity;
            if (!head.isValid || !head.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || !tracked) return false;
            if (!head.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState flags) ||
                (flags & (InputTrackingState.Position | InputTrackingState.Rotation)) != (InputTrackingState.Position | InputTrackingState.Rotation)) return false;
            return head.TryGetFeatureValue(CommonUsages.devicePosition, out position) &&
                   head.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation) &&
                   Finite(position) && Finite(rotation);
        }

        void OriginChanged(XRInputSubsystem input)
        {
            OriginMode = input.GetTrackingOriginMode();
            InvalidateAlignment("Tracking origin changed. Press A to place the mock board again");
        }

        /// <summary>Supplies an explicit world pose. UI uses this only for virtual-board placement.</summary>
        public void CalibrateBoardAt(Vector3 worldPosition, Quaternion worldRotation)
        {
            if (!initialized || !Finite(worldPosition) || !Finite(worldRotation)) return;
            if (!state.Align()) return;
            board.SetPositionAndRotation(worldPosition, worldRotation.normalized);
            AdvanceFrame();
            PublishReadiness(true);
            Debug.Log("[Spatial audio] " + Status + "; board world position=" + board.position.ToString("F3"));
        }

        public void InvalidateAlignment(string reason)
        {
            state.Invalidate(reason);
            AdvanceFrame();
            PublishReadiness(true);
            Debug.LogWarning("[Spatial audio paused] " + reason);
        }

        void OnApplicationPause(bool paused)
        {
            if (paused && initialized && IsXrSession)
                InvalidateAlignment("App suspended. Press A to place the mock board after recovery");
        }

        void AdvanceFrame() { FrameRevision++; BoardFrameChanged?.Invoke(); }

        void PublishReadiness(bool frameAlreadyChanged = false)
        {
            bool ready = IsSpatialReady;
            if (ready == previousReady) return;
            if (!ready && !frameAlreadyChanged) AdvanceFrame();
            previousReady = ready;
            SpatialReadyChanged?.Invoke(ready);
        }

        static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
        static bool Finite(Quaternion q) => float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w) && q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w > .00001f;
    }
}
