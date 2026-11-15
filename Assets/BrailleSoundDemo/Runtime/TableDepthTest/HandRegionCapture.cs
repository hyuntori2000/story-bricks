using System;
using System.Collections.Generic;
using BrailleSoundDemo.CameraTest;
using Meta.XR;
using UnityEngine;

namespace BrailleSoundDemo.TableDepthTest
{
    /// <summary>
    /// Sends one full camera frame with a suggested tabletop ROI around a frozen
    /// pre-lift hand sample. This does not recognize blocks or control optical focus.
    /// </summary>
    public sealed class HandRegionCapture : MonoBehaviour
    {
        PassthroughCameraAccess cameraAccess;
        QuestFrameCapture sender;
        Func<QuestFrameCapture.CaptureMetadata,bool> ownedPreparation;
        readonly string sessionId = Guid.NewGuid().ToString("N");
        public string LastError { get; private set; } = "";
        public string Status => !string.IsNullOrEmpty(LastError) ? LastError : sender != null ? sender.Status : "Capture is not configured";
        public QuestFrameCapture.RecognitionRegionMetadata LastRegion { get; private set; }
        /// <summary>Optional current high-confidence joints; missing/invalid data from a configured provider cancels capture.</summary>
        public Func<IReadOnlyList<Vector3>> CurrentHandPoints { get; set; }

        public void Configure(PassthroughCameraAccess camera, QuestFrameCapture captureSender)
        {
            ClearOwnedPreparation();
            cameraAccess = camera;
            sender = captureSender;
            LastError = "";
            LastRegion = null;
        }

        public bool Request(HandApproachSnapshot snapshot, int revision, Func<bool> stillValid,
            Func<bool> frozenSnapshotStillValid = null)
        {
            if (sender == null || cameraAccess == null || !isActiveAndEnabled || sender.Busy)
            { LastError = "Camera capture is unavailable or busy"; return false; }
            if (snapshot == null || stillValid == null || !ValidSnapshot(snapshot))
            { LastError = "Hand sample is invalid or too old"; return false; }

            // Copy every value now; metadata preparation may happen several render frames later.
            var hand = new QuestFrameCapture.HandApproachMetadata {
                hand = snapshot.hand, interpretation = snapshot.interpretation,
                positionWorld = snapshot.sample.fingerPosition, headPositionWorld = snapshot.sample.headPosition,
                signedGapMeters = snapshot.sample.signedGap, surfaceHeightMeters = snapshot.sample.surfaceHeight,
                headToPointDistanceMeters = snapshot.headToPointDistance,
                sampleMonotonicSeconds = snapshot.sample.time, liftStartedMonotonicSeconds = snapshot.liftStartedAt,
                confirmedMonotonicSeconds = snapshot.confirmedAt, sampleFrame = snapshot.sample.frame,
                confirmedFrame = snapshot.confirmedFrame, sampleAgeAtConfirmationSeconds = snapshot.sampleAgeSeconds,
                frameAgeAtConfirmation = snapshot.frameAge
            };
            Vector3 planeCenter = hand.positionWorld; planeCenter.y = hand.surfaceHeightMeters;
            var context = new QuestCaptureContext {
                SessionId = sessionId, FrameRevision = revision, BoardPose = new Pose(planeCenter,Quaternion.identity),
                BoardWidth = HandRegionProjection.RegionWidthMeters, BoardDepth = HandRegionProjection.RegionDepthMeters,
                BlockHeight = 0, ClearSeconds = (float)(snapshot.confirmedAt-snapshot.liftStartedAt), TriggerType = "hands_off"
            };
            LastError = "";
            LastRegion = null;
            var previous = sender.PrepareMetadata;
            ownedPreparation = metadata => {
                if (!HandRegionProjection.TryBuildRegion(metadata,hand.positionWorld,hand.surfaceHeightMeters,
                    out var region,out var reason))
                { LastError = reason; return false; }
                if (CurrentHandPoints != null)
                {
                    IReadOnlyList<Vector3> points;
                    try { points = CurrentHandPoints(); }
                    catch (Exception ex) { LastError = "Current hand data is unavailable: "+ex.Message; return false; }
                    if (HandRegionProjection.IsPotentiallyOccluded(metadata,region,points,out reason))
                    { LastError = reason; return false; }
                }
                hand.sampleAgeAtCaptureObservationSeconds = metadata.captureTimings != null
                    ? metadata.captureTimings.frameObservedMonotonicSeconds-hand.sampleMonotonicSeconds
                    : Time.realtimeSinceStartupAsDouble-hand.sampleMonotonicSeconds;
                metadata.handApproach = hand;
                metadata.recognitionRegion = region;
                LastRegion = region;
                return true;
            };
            sender.PrepareMetadata = ownedPreparation;
            Func<bool> frozenGuard = frozenSnapshotStillValid == null ? null
                : () => isActiveAndEnabled && frozenSnapshotStillValid();
            bool requested = sender.RequestCapture(context,() => isActiveAndEnabled &&
                Time.realtimeSinceStartupAsDouble-hand.sampleMonotonicSeconds <= 3 && stillValid(), frozenGuard);
            if (!requested)
            {
                sender.PrepareMetadata = previous;
                ownedPreparation = null;
                LastError = sender.Status;
            }
            return requested;
        }

        void OnDisable() { ClearOwnedPreparation(); }

        void ClearOwnedPreparation()
        {
            if (sender != null && ownedPreparation != null && sender.PrepareMetadata == ownedPreparation)
                sender.PrepareMetadata = null;
            ownedPreparation = null;
        }

        static bool ValidSnapshot(HandApproachSnapshot snapshot)
        {
            var sample = snapshot.sample;
            double now = Time.realtimeSinceStartupAsDouble;
            return Finite(sample.fingerPosition) && Finite(sample.headPosition) &&
                float.IsFinite(sample.surfaceHeight) && float.IsFinite(sample.signedGap) &&
                float.IsFinite(snapshot.headToPointDistance) && snapshot.headToPointDistance >= 0 &&
                Finite(sample.time) && sample.time >= 0 && now >= sample.time && now-sample.time <= 3 &&
                Finite(snapshot.liftStartedAt) && Finite(snapshot.confirmedAt) &&
                snapshot.liftStartedAt >= sample.time && snapshot.confirmedAt >= snapshot.liftStartedAt &&
                snapshot.confirmedAt <= now+.01 && Finite(snapshot.sampleAgeSeconds) && snapshot.sampleAgeSeconds >= 0 &&
                sample.frame >= 0 && snapshot.confirmedFrame >= sample.frame && snapshot.frameAge >= 0;
        }

        static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
        static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
    }
}
