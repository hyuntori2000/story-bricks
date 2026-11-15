using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace BrailleSoundDemo.TableDepthTest
{
    /// <summary>
    /// Current fingertip distance to an estimated horizontal plane, not physical contact.
    /// Eye-camera frustum membership is a visibility proxy, not proof of visibility in RGB pixels.
    /// The infinite plane does not describe the table footprint or identify a brick.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class HandProximityDiagnostic : MonoBehaviour
    {
        [SerializeField] OVRHand leftHand, rightHand;
        [SerializeField] OVRSkeleton leftSkeleton, rightSkeleton;
        [SerializeField] Transform head;
        public float viewportInset = .05f;
        public float minimumDistance = .15f;
        public float maximumDistance = 1.5f;
        /// <summary>Main-story opt-in: use the same index tip throughout an approach instead of requiring all five tips.</summary>
        public bool UseIndexTipForApproach;
        /// <summary>Optional current RGB-camera projection gate. This is still a field-of-view proxy, not pixel visibility proof.</summary>
        public Func<Vector3, bool> CameraVisibilityCheck;

        public bool LeftTracked { get; private set; }
        public bool RightTracked { get; private set; }
        public bool LeftHighConfidence { get; private set; }
        public bool RightHighConfidence { get; private set; }
        public bool HeadTracked { get; private set; }
        public bool LeftVisible { get; private set; }
        public bool RightVisible { get; private set; }
        public bool LeftValid { get; private set; }
        public bool RightValid { get; private set; }
        public bool SurfaceValid { get; private set; }
        public string LeftStatus { get; private set; } = "Unknown: hand not sampled.";
        public string RightStatus { get; private set; } = "Unknown: hand not sampled.";
        public float LeftSignedGap { get; private set; } = float.NaN;
        public float RightSignedGap { get; private set; } = float.NaN;
        public float LeftMinSignedHeight { get; private set; } = float.NaN;
        public float RightMinSignedHeight { get; private set; } = float.NaN;
        public float LeftMinAbsHeight { get; private set; } = float.NaN;
        public float RightMinAbsHeight { get; private set; } = float.NaN;
        /// <summary>Approach point: closest tip in strict mode, stable current index tip in index mode.</summary>
        public Vector3 LeftClosestPoint { get; private set; }
        public Vector3 RightClosestPoint { get; private set; }
        /// <summary>Currently valid, in-frustum tips, even before a surface is available.</summary>
        public IReadOnlyList<Vector3> VisibleTips => visibleTips;
        public IReadOnlyList<Vector3> LeftVisibleTips => leftTips;
        public IReadOnlyList<Vector3> RightVisibleTips => rightTips;

        /// <summary>SDK joint telemetry only. It does not pass or relax the capture visibility/confidence gates.</summary>
        [Serializable]
        public sealed class RawHandDiagnostic
        {
            public bool componentEnabled, tracked, dataValid, highConfidence, poseInferred, systemGesture;
            public bool skeletonEnabled, skeletonInitialized, skeletonDataValid, skeletonHighConfidence;
            public bool wristValid, indexTipValid, wristTablePositionValid, indexTipTablePositionValid, referenceFresh;
            public Vector3 wristWorldPosition, indexTipWorldPosition, wristTablePosition, indexTipTablePosition;
            public string status, showState, skeletonType, controllerInHandState;
        }

        public static RawHandDiagnostic ReadRawTracking(OVRHand hand, OVRSkeleton skeleton, bool right,
            bool hasReference, Vector3 reference, bool freshReference)
        {
            var result = new RawHandDiagnostic {
                componentEnabled = hand != null && hand.isActiveAndEnabled,
                tracked = hand != null && hand.IsTracked, dataValid = hand != null && hand.IsDataValid,
                highConfidence = hand != null && hand.IsDataHighConfidence,
                poseInferred = hand != null && hand.PoseSourceInferred,
                systemGesture = hand != null && hand.IsSystemGestureInProgress,
                skeletonEnabled = skeleton != null && skeleton.isActiveAndEnabled,
                skeletonInitialized = skeleton != null && skeleton.IsInitialized,
                skeletonDataValid = skeleton != null && skeleton.IsDataValid,
                skeletonHighConfidence = skeleton != null && skeleton.IsDataHighConfidence,
                showState = hand != null ? hand.m_showState.ToString() : "Missing",
                skeletonType = skeleton != null ? skeleton.GetSkeletonType().ToString() : "Missing",
                controllerInHandState = OVRPlugin.initialized
                    ? OVRInput.GetControllerIsInHandState(right ? OVRInput.Hand.HandRight : OVRInput.Hand.HandLeft).ToString() : "XR not initialized",
                referenceFresh = freshReference
            };
            if (!result.componentEnabled) result.status = "Hand component missing or disabled.";
            else if (!result.tracked) result.status = "SDK reports hand not tracked; camera/table filters were not applied.";
            else if (!result.dataValid) result.status = "SDK hand data invalid; inspect show state, controller state and skeleton version.";
            else if (!result.skeletonEnabled) result.status = "Skeleton component missing or disabled.";
            else if (!result.skeletonInitialized) result.status = "Skeleton not initialized.";
            else if (!result.skeletonDataValid) result.status = "SDK skeleton data invalid.";
            else
            {
                var type = skeleton.GetSkeletonType();
                bool xr = type == OVRSkeleton.SkeletonType.XRHandLeft || type == OVRSkeleton.SkeletonType.XRHandRight;
                bool ovr = type == OVRSkeleton.SkeletonType.HandLeft || type == OVRSkeleton.SkeletonType.HandRight;
                if (!xr && !ovr) { result.status = "Unsupported hand skeleton."; return result; }
                result.wristValid = TryReadBone(skeleton, xr ? OVRSkeleton.BoneId.XRHand_Wrist : OVRSkeleton.BoneId.Hand_WristRoot,
                    out result.wristWorldPosition);
                result.indexTipValid = TryReadBone(skeleton, xr ? OVRSkeleton.BoneId.XRHand_IndexTip : OVRSkeleton.BoneId.Hand_IndexTip,
                    out result.indexTipWorldPosition);
                result.wristTablePositionValid = result.wristValid && hasReference && Finite(reference);
                result.indexTipTablePositionValid = result.indexTipValid && hasReference && Finite(reference);
                if (result.wristTablePositionValid) result.wristTablePosition = result.wristWorldPosition - reference;
                if (result.indexTipTablePositionValid) result.indexTipTablePosition = result.indexTipWorldPosition - reference;
                result.status = !result.wristValid || !result.indexTipValid ? "A requested joint is missing or nonfinite."
                    : result.poseInferred ? "SDK joint estimate is inferred; not accepted for capture."
                    : !result.highConfidence || !result.skeletonHighConfidence ? "SDK joints available at low confidence; capture gates still apply."
                    : "SDK joints available; camera/table capture gates are separate.";
            }
            return result;
        }

        static bool TryReadBone(OVRSkeleton skeleton, OVRSkeleton.BoneId id, out Vector3 position)
        {
            position = Vector3.zero;
            if (skeleton.Bones == null) return false;
            foreach (var bone in skeleton.Bones)
                if (bone.Id == id && bone.Transform != null && Finite(bone.Transform.position))
                { position = bone.Transform.position; return true; }
            return false;
        }

        static readonly OVRSkeleton.BoneId[] OvrTips = {
            OVRSkeleton.BoneId.Hand_ThumbTip, OVRSkeleton.BoneId.Hand_IndexTip,
            OVRSkeleton.BoneId.Hand_MiddleTip, OVRSkeleton.BoneId.Hand_RingTip, OVRSkeleton.BoneId.Hand_PinkyTip };
        static readonly OVRSkeleton.BoneId[] XrTips = {
            OVRSkeleton.BoneId.XRHand_ThumbTip, OVRSkeleton.BoneId.XRHand_IndexTip,
            OVRSkeleton.BoneId.XRHand_MiddleTip, OVRSkeleton.BoneId.XRHand_RingTip, OVRSkeleton.BoneId.XRHand_LittleTip };
        readonly List<Vector3> leftTips = new List<Vector3>(5), rightTips = new List<Vector3>(5), visibleTips = new List<Vector3>(10);
        readonly List<XRInputSubsystem> inputSubsystems = new List<XRInputSubsystem>();
        Camera eyeCamera;
        float surfaceHeight;
        bool paused, focused = true;

        public void Configure(OVRHand left, OVRSkeleton leftSkeleton,
            OVRHand right, OVRSkeleton rightSkeleton, Transform head)
        {
            leftHand = left; this.leftSkeleton = leftSkeleton;
            rightHand = right; this.rightSkeleton = rightSkeleton;
            this.head = head;
            eyeCamera = head != null ? head.GetComponent<Camera>() : null;
            ClearSamples("Unknown: waiting for current hand tracking.");
        }

        public void SetSurface(float height, bool valid)
        {
            SurfaceValid = valid && float.IsFinite(height);
            surfaceHeight = height;
            if (!SurfaceValid) ClearGaps("Unknown: a current table-plane estimate is required.");
        }

        void OnEnable()
        {
            SubsystemManager.GetSubsystems(inputSubsystems);
            foreach (var subsystem in inputSubsystems) subsystem.trackingOriginUpdated += TrackingOriginChanged;
            ClearSamples("Unknown: waiting for current hand tracking.");
        }

        void LateUpdate()
        {
            ClearSamples("Unknown: current tracking unavailable.");
            if (paused || !focused || head == null || !Finite(head.position) || eyeCamera == null || !eyeCamera.isActiveAndEnabled)
            {
                LeftStatus = RightStatus = "Unknown: active eye camera required.";
                return;
            }
            var device = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            HeadTracked = device.isValid && device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked &&
                device.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState state) &&
                (state & (InputTrackingState.Position | InputTrackingState.Rotation)) == (InputTrackingState.Position | InputTrackingState.Rotation);
            LeftTracked = leftHand != null && leftHand.IsTracked;
            RightTracked = rightHand != null && rightHand.IsTracked;
            LeftHighConfidence = leftHand != null && leftHand.IsDataHighConfidence;
            RightHighConfidence = rightHand != null && rightHand.IsDataHighConfidence;
            if (!HeadTracked)
            {
                LeftStatus = RightStatus = "Unknown: head tracking unavailable.";
                return;
            }

            LeftVisible = ReadTips(leftHand, leftSkeleton, leftTips, out var leftIndex, out string leftReason);
            RightVisible = ReadTips(rightHand, rightSkeleton, rightTips, out var rightIndex, out string rightReason);
            LeftStatus = leftReason; RightStatus = rightReason;
            visibleTips.AddRange(leftTips); visibleTips.AddRange(rightTips);
            if (!SurfaceValid)
            {
                if (LeftVisible) LeftStatus = "Hand tracked in view; table-plane estimate required.";
                if (RightVisible) RightStatus = "Hand tracked in view; table-plane estimate required.";
                return;
            }
            if (LeftVisible && TryGetApproachGap(leftTips, leftIndex, UseIndexTipForApproach, surfaceHeight,
                out float leftGap, out Vector3 leftPoint))
            {
                GetGaps(leftTips, surfaceHeight, out _, out float minSigned, out float minAbs, out _);
                LeftSignedGap = leftGap; LeftMinSignedHeight = minSigned; LeftMinAbsHeight = minAbs; LeftClosestPoint = leftPoint;
                LeftValid = true; LeftStatus = UseIndexTipForApproach
                    ? "Estimated index-tip-to-plane gap; not contact." : "Estimated fingertip-to-plane gap; not contact.";
            }
            if (RightVisible && TryGetApproachGap(rightTips, rightIndex, UseIndexTipForApproach, surfaceHeight,
                out float rightGap, out Vector3 rightPoint))
            {
                GetGaps(rightTips, surfaceHeight, out _, out float minSigned, out float minAbs, out _);
                RightSignedGap = rightGap; RightMinSignedHeight = minSigned; RightMinAbsHeight = minAbs; RightClosestPoint = rightPoint;
                RightValid = true; RightStatus = UseIndexTipForApproach
                    ? "Estimated index-tip-to-plane gap; not contact." : "Estimated fingertip-to-plane gap; not contact.";
            }
        }

        bool ReadTips(OVRHand hand, OVRSkeleton skeleton, List<Vector3> points, out Vector3? currentIndex, out string reason)
        {
            points.Clear();
            currentIndex = null;
            reason = null;
            if (hand == null) reason = "Unknown: hand component missing.";
            else if (skeleton == null) reason = "Unknown: skeleton component missing.";
            else if (!hand.isActiveAndEnabled) reason = "Unknown: hand component disabled.";
            else if (!skeleton.isActiveAndEnabled) reason = "Unknown: skeleton component disabled.";
            else if (!hand.IsDataValid) reason = "Unknown: SDK hand data invalid.";
            else if (!hand.IsTracked) reason = "Unknown: SDK reports hand not tracked.";
            else if (!hand.IsDataHighConfidence) reason = "Unknown: hand confidence is low.";
            else if (hand.PoseSourceInferred) reason = "Unknown: inferred hand pose is excluded.";
            else if (hand.IsSystemGestureInProgress) reason = "Unknown: system hand gesture in progress.";
            else if (!skeleton.IsInitialized) reason = "Unknown: skeleton not initialized.";
            else if (!skeleton.IsDataValid) reason = "Unknown: skeleton data invalid.";
            else if (!skeleton.IsDataHighConfidence) reason = "Unknown: skeleton confidence is low.";
            else if (skeleton.Bones == null) reason = "Unknown: skeleton bones unavailable.";
            if (reason != null) return false;
            OVRSkeleton.BoneId[] ids;
            switch (skeleton.GetSkeletonType())
            {
                case OVRSkeleton.SkeletonType.HandLeft:
                case OVRSkeleton.SkeletonType.HandRight: ids = OvrTips; break;
                case OVRSkeleton.SkeletonType.XRHandLeft:
                case OVRSkeleton.SkeletonType.XRHandRight: ids = XrTips; break;
                default: reason = "Unknown: unsupported hand skeleton."; return false;
            }
            string indexIssue = "Unknown: current index fingertip is unavailable.";
            for (int finger = 0; finger < ids.Length; finger++)
            {
                if (hand.GetFingerConfidence((OVRHand.HandFinger)finger) != OVRHand.TrackingConfidence.High)
                {
                    string issue = "Unknown: " + (OVRHand.HandFinger)finger + " fingertip confidence is low.";
                    if (finger == (int)OVRHand.HandFinger.Index) indexIssue = issue;
                    if (!UseIndexTipForApproach) { points.Clear(); reason = issue; return false; }
                    continue;
                }
                bool found = false;
                foreach (var bone in skeleton.Bones)
                {
                    if (bone.Id != ids[finger] || bone.Transform == null) continue;
                    Vector3 p = bone.Transform.position;
                    if (InView(p))
                    {
                        points.Add(p); found = true;
                        if (finger == (int)OVRHand.HandFinger.Index) currentIndex = p;
                    }
                    break;
                }
                if (!found)
                {
                    string issue = "Unknown: " + (OVRHand.HandFinger)finger + " fingertip unavailable, outside camera view/distance, or camera stale.";
                    if (finger == (int)OVRHand.HandFinger.Index) indexIssue = issue;
                    if (!UseIndexTipForApproach) { points.Clear(); reason = issue; return false; }
                }
            }
            if (UseIndexTipForApproach && !currentIndex.HasValue) { reason = indexIssue; return false; }
            reason = UseIndexTipForApproach ? "Current index tip inside the view proxy; other reliable tips retained for occlusion."
                : "Five current fingertips inside the view proxy.";
            return true;
        }

        /// <summary>Pure selection policy. The caller must first validate current SDK tracking and per-tip visibility/confidence.</summary>
        public static bool TryGetApproachGap(IReadOnlyList<Vector3> currentTips, Vector3? currentIndex,
            bool useIndexTip, float height, out float signedGap, out Vector3 point)
        {
            signedGap = float.NaN; point = UnknownPosition;
            if (!float.IsFinite(height)) return false;
            if (useIndexTip)
            {
                // Never replace a missing index with another finger: changing
                // landmarks can look like a lift even when the hand is stationary.
                if (!currentIndex.HasValue || !Finite(currentIndex.Value)) return false;
                point = currentIndex.Value; signedGap = point.y - height;
                return true;
            }
            if (currentTips == null || currentTips.Count != 5) return false;
            GetGaps(currentTips, height, out signedGap, out _, out _, out point);
            return float.IsFinite(signedGap) && Finite(point);
        }

        bool InView(Vector3 point)
        {
            if (!Finite(point)) return false;
            float distance = Vector3.Distance(head.position, point);
            if (distance < minimumDistance || distance > maximumDistance) return false;
            if (CameraVisibilityCheck != null) return CameraVisibilityCheck(point);
            Vector3 viewport = eyeCamera.WorldToViewportPoint(point);
            return Finite(viewport) && viewport.z > 0 &&
                viewport.x >= viewportInset && viewport.x <= 1 - viewportInset &&
                viewport.y >= viewportInset && viewport.y <= 1 - viewportInset;
        }

        public static void GetGaps(IReadOnlyList<Vector3> points, float height,
            out float signedGap, out float minSigned, out float minAbs, out Vector3 closest)
        {
            signedGap = minSigned = minAbs = float.NaN;
            closest = UnknownPosition;
            if (points == null || points.Count == 0 || !float.IsFinite(height)) return;
            float bestAbs = float.PositiveInfinity, lowest = float.PositiveInfinity, bestSigned = float.NaN;
            Vector3 bestPoint = UnknownPosition;
            foreach (Vector3 point in points)
            {
                if (!Finite(point)) return;
                float gap = point.y - height;
                lowest = Mathf.Min(lowest, gap);
                if (Mathf.Abs(gap) >= bestAbs) continue;
                bestAbs = Mathf.Abs(gap); bestSigned = gap; bestPoint = point;
            }
            signedGap = bestSigned; closest = bestPoint; minSigned = lowest; minAbs = bestAbs;
        }

        static Vector3 UnknownPosition => new Vector3(float.NaN, float.NaN, float.NaN);
        static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        void ClearGaps(string reason)
        {
            LeftValid = RightValid = false;
            LeftSignedGap = RightSignedGap = LeftMinSignedHeight = RightMinSignedHeight = LeftMinAbsHeight = RightMinAbsHeight = float.NaN;
            LeftClosestPoint = RightClosestPoint = UnknownPosition;
            LeftStatus = RightStatus = reason;
        }
        void ClearSamples(string reason)
        {
            ClearGaps(reason);
            LeftTracked = RightTracked = LeftHighConfidence = RightHighConfidence = HeadTracked = LeftVisible = RightVisible = false;
            leftTips.Clear(); rightTips.Clear(); visibleTips.Clear();
        }
        void TrackingOriginChanged(XRInputSubsystem _)
        {
            SurfaceValid = false;
            ClearSamples("Unknown: tracking origin changed; reacquire the plane.");
        }
        void OnApplicationPause(bool value)
        {
            paused = value;
            SurfaceValid = false;
            ClearSamples("Unknown: app pause changed; reacquire the plane.");
        }
        void OnApplicationFocus(bool value)
        {
            focused = value;
            SurfaceValid = false;
            ClearSamples("Unknown: app focus changed; reacquire the plane.");
        }
        void OnDisable()
        {
            foreach (var subsystem in inputSubsystems) subsystem.trackingOriginUpdated -= TrackingOriginChanged;
            inputSubsystems.Clear();
            SurfaceValid = false;
            ClearSamples("Unknown: hand diagnostic disabled.");
        }
    }
}
