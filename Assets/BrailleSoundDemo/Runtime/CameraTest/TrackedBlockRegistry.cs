using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace BrailleSoundDemo.CameraTest
{
    /// <summary>A tracked physical block. SizeMeters is its local X/Y/Z extent,
    /// centered at WorldPose. IDs identify bricks, not their letter labels.</summary>
    public sealed class TrackedBlockObservation
    {
        public string Id { get; }
        public Pose WorldPose { get; }
        public Vector3 SizeMeters { get; }
        public float Confidence { get; }

        public TrackedBlockObservation(string id, Pose worldPose, Vector3 sizeMeters, float confidence)
        {
            Id = id; WorldPose = worldPose; SizeMeters = sizeMeters; Confidence = confidence;
        }
    }

    /// <summary>
    /// Transport-neutral boundary for complete snapshots from a real block tracker.
    /// This class detects no blocks and creates no fallback geometry.
    /// capturedAt and now MUST both use Unity Time.realtimeSinceStartupAsDouble.
    /// For remote recognition, recover capturedAt from Unity's original frame cache;
    /// substituting response receipt time would incorrectly make old detections fresh.
    /// Every snapshot replaces the full tracked set; it is not a delta update.
    /// </summary>
    public sealed class TrackedBlockRegistry
    {
        const int MaximumBlocks = 512;
        static readonly IReadOnlyList<TrackedBlockObservation> Empty =
            new ReadOnlyCollection<TrackedBlockObservation>(new List<TrackedBlockObservation>());

        IReadOnlyList<TrackedBlockObservation> current = Empty;
        Bounds currentBounds;
        bool available;
        double lastObservedNow = -1;
        double latestAcceptedCaptureTime = -1;
        string unavailableReason = "No block observations have been received.";

        public string SessionId { get; private set; }
        public int FrameRevision { get; private set; }
        public long LastSequence { get; private set; } = -1;
        public double CapturedAt { get; private set; } = -1;
        public double MaxAgeSeconds { get; }
        public float MinimumConfidence { get; }

        public TrackedBlockRegistry(string sessionId, int revision,
            double maxAgeSeconds = .5, float minimumConfidence = .7f)
        {
            if (!Finite(maxAgeSeconds) || maxAgeSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxAgeSeconds));
            if (!float.IsFinite(minimumConfidence) || minimumConfidence < 0 || minimumConfidence > 1)
                throw new ArgumentOutOfRangeException(nameof(minimumConfidence));
            MaxAgeSeconds = maxAgeSeconds; MinimumConfidence = minimumConfidence;
            ResetContext(sessionId, revision);
        }

        public void ResetContext(string sessionId, int revision)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session ID is required.", nameof(sessionId));
            if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
            SessionId = sessionId; FrameRevision = revision; LastSequence = -1;
            CapturedAt = latestAcceptedCaptureTime = lastObservedNow = -1;
            Invalidate("No blocks in the current session and coordinate revision.");
        }

        /// <summary>
        /// Returns true only for a fresh, nonempty, fully valid snapshot.
        /// A newer matching-context snapshot with missing/invalid/low-confidence
        /// blocks invalidates the old set. Old sequence/context messages are ignored.
        /// Thus missing detections cannot become evidence of hands-off.
        /// </summary>
        public bool ReplaceSnapshot(string sessionId, int revision, long sequence,
            double capturedAt, double now, IEnumerable<TrackedBlockObservation> observations,
            out string reason)
        {
            if (!string.Equals(sessionId, SessionId, StringComparison.Ordinal) || revision != FrameRevision)
            { reason = "Snapshot belongs to another session or coordinate revision."; return false; }
            if (sequence < 0 || sequence <= LastSequence)
            { reason = "Duplicate or out-of-order block snapshot."; return false; }

            // Advance the high-water mark even when the newer snapshot is unknown,
            // so delayed old results cannot resurrect an invalidated tracked set.
            LastSequence = sequence;
            if (!ObserveClock(now, out reason)) return false;
            if (!Finite(capturedAt) || capturedAt < 0 || capturedAt > now)
                return Reject("Capture time is invalid or ahead of the Unity monotonic clock.", out reason);
            if (capturedAt < latestAcceptedCaptureTime)
                return Reject("Capture time moved backwards despite an advancing sequence.", out reason);
            if (now - capturedAt > MaxAgeSeconds)
                return Reject("Block snapshot was already stale when it arrived.", out reason);
            if (observations == null)
                return Reject("Block tracker returned an unknown snapshot.", out reason);

            var copy = new List<TrackedBlockObservation>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            Vector3 min = Vector3.zero, max = Vector3.zero;
            bool firstCorner = true;
            try
            {
                foreach (var observation in observations)
                {
                    if (copy.Count >= MaximumBlocks)
                        return Reject("Block snapshot exceeds the supported block count.", out reason);
                    if (observation == null || string.IsNullOrWhiteSpace(observation.Id) || !ids.Add(observation.Id))
                        return Reject("Every physical block needs a distinct, nonempty ID.", out reason);
                    var pose = observation.WorldPose;
                    var size = observation.SizeMeters;
                    if (!Finite(pose.position) || !Finite(size) || size.x <= 0 || size.y <= 0 || size.z <= 0)
                        return Reject("Block position or physical size is invalid.", out reason);
                    if (!TryNormalize(pose.rotation, out Quaternion rotation))
                        return Reject("Block rotation is nonfinite or has zero magnitude.", out reason);
                    if (!float.IsFinite(observation.Confidence) || observation.Confidence < MinimumConfidence || observation.Confidence > 1)
                        return Reject("A block's tracking confidence is insufficient or invalid.", out reason);

                    var normalized = new Pose(pose.position, rotation);
                    copy.Add(new TrackedBlockObservation(observation.Id, normalized, size, observation.Confidence));
                    for (int x = -1; x <= 1; x += 2)
                    for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                    {
                        var corner = pose.position + rotation * new Vector3(x * size.x / 2, y * size.y / 2, z * size.z / 2);
                        if (!Finite(corner)) return Reject("Block bounds are not finite.", out reason);
                        if (firstCorner) { min = max = corner; firstCorner = false; }
                        else { min = Vector3.Min(min, corner); max = Vector3.Max(max, corner); }
                    }
                }
            }
            catch (Exception error)
            {
                return Reject("Block snapshot enumeration failed: " + error.GetType().Name, out reason);
            }

            if (copy.Count == 0)
                return Reject("No blocks are currently tracked. This is not hands-off evidence.", out reason);
            var bounds = new Bounds(); bounds.SetMinMax(min, max);
            if (!Finite(bounds.center) || !Finite(bounds.extents))
                return Reject("Combined block bounds are invalid.", out reason);

            current = new ReadOnlyCollection<TrackedBlockObservation>(copy);
            currentBounds = bounds; CapturedAt = latestAcceptedCaptureTime = capturedAt;
            available = true; unavailableReason = ""; reason = "Fresh block snapshot accepted.";
            return true;
        }

        public bool TryGetFreshBlocks(double now, out IReadOnlyList<TrackedBlockObservation> blocks, out string reason)
        {
            blocks = Empty;
            if (!ObserveClock(now, out reason)) return false;
            if (!available) { reason = unavailableReason; return false; }
            if (now - CapturedAt > MaxAgeSeconds)
                return Reject("Block observations expired; wait for a fresh tracker snapshot.", out reason);
            blocks = current; reason = "Fresh tracked blocks are available."; return true;
        }

        /// <summary>World-axis bounds enclosing the oriented blocks. Check the bool;
        /// the default bounds on failure must never be used as a physical target.</summary>
        public bool TryGetFreshBounds(double now, out Bounds bounds, out string reason)
        {
            bounds = default;
            if (!TryGetFreshBlocks(now, out _, out reason)) return false;
            bounds = currentBounds; return true;
        }

        bool ObserveClock(double now, out string reason)
        {
            if (!Finite(now) || now < 0 || now < lastObservedNow)
                return Reject("Unity monotonic time is invalid or moved backwards.", out reason);
            lastObservedNow = now; reason = ""; return true;
        }
        bool Reject(string message, out string reason)
        { Invalidate(message); reason = message; return false; }
        void Invalidate(string message)
        { available = false; current = Empty; currentBounds = default; unavailableReason = message; }
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        static bool TryNormalize(Quaternion value, out Quaternion normalized)
        {
            normalized = default;
            if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z) || !float.IsFinite(value.w)) return false;
            double square = (double)value.x * value.x + (double)value.y * value.y + (double)value.z * value.z + (double)value.w * value.w;
            if (square < 1e-12) return false;
            double magnitude = Math.Sqrt(square);
            normalized = new Quaternion((float)(value.x / magnitude), (float)(value.y / magnitude), (float)(value.z / magnitude), (float)(value.w / magnitude));
            return true;
        }
    }
}
