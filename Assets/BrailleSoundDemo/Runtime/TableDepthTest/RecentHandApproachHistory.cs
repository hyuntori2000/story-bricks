using System;
using UnityEngine;

namespace BrailleSoundDemo.TableDepthTest
{
    [Serializable]
    public struct HandApproachSample
    {
        public double time;
        public int frame;
        public Vector3 headPosition, fingerPosition;
        public float signedGap, surfaceHeight;
    }

    [Serializable]
    public sealed class HandApproachSnapshot
    {
        public string hand;
        public string interpretation = "Closest estimated fingertip-to-plane sample before a tracked lift; not proof of brick contact.";
        public HandApproachSample sample;
        public double liftStartedAt, confirmedAt, sampleAgeSeconds;
        public int confirmedFrame, frameAge;
        public float headToPointDistance;
    }

    /// <summary>
    /// One bounded history per hand. Freezes a prior near-plane sample when a tracked upward
    /// separation begins, then confirms the lift. No images, OCR, contact claims, or focus controls.
    /// </summary>
    public sealed class RecentHandApproachHistory
    {
        public string Status { get; private set; } = "Waiting for a visible near-plane hand.";
        public int Count => count;
        public bool AwaitingReturn => awaitingReturn;
        public bool LiftPending => liftStartedAt >= 0;
        readonly string hand;
        readonly float nearThreshold, liftDistance, rearmMargin;
        readonly double liftHoldSeconds, lookbackSeconds, historySeconds;
        const double MaximumObservationGap = .25;
        const float MaximumPlaneChange = .02f;
        readonly HandApproachSample[] samples = new HandApproachSample[512];
        int start, count;
        double lastTime = -1, liftStartedAt = -1;
        int lastFrame = -1;
        float referencePlane, returnGap;
        bool haveReferencePlane, awaitingReturn;
        HandApproachSample frozen;

        public RecentHandApproachHistory(string hand = "left", float nearThreshold = .06f,
            float liftDistance = .06f, double liftHoldSeconds = .15, double lookbackSeconds = 1,
            double historySeconds = 3, float rearmMargin = .02f)
        {
            if (!float.IsFinite(nearThreshold) || nearThreshold <= 0 || !float.IsFinite(liftDistance) || liftDistance <= 0 ||
                !Finite(liftHoldSeconds) || liftHoldSeconds <= 0 || !Finite(lookbackSeconds) || lookbackSeconds <= 0 ||
                !Finite(historySeconds) || historySeconds < lookbackSeconds ||
                !float.IsFinite(rearmMargin) || rearmMargin < 0 || rearmMargin >= liftDistance)
                throw new ArgumentOutOfRangeException(nameof(nearThreshold), "Use finite positive durations and separated lift/rearm thresholds.");
            this.hand = hand;
            this.nearThreshold = nearThreshold;
            this.liftDistance = liftDistance;
            this.liftHoldSeconds = liftHoldSeconds;
            this.lookbackSeconds = lookbackSeconds;
            this.historySeconds = historySeconds;
            this.rearmMargin = rearmMargin;
        }

        /// <param name="valid">Current visible, high-confidence hand AND a fresh valid surface. False clears all history.</param>
        public bool Observe(HandApproachSample sample, bool valid, out HandApproachSnapshot snapshot)
        {
            snapshot = null;
            if (!valid || !Valid(sample))
            {
                Reset(); Status = "Unknown tracking or surface; history cleared."; return false;
            }
            if (lastTime >= 0 && (sample.time <= lastTime || sample.frame <= lastFrame))
            {
                Reset(); Status = "Non-increasing sample time/frame; history cleared."; return false;
            }
            if ((lastTime >= 0 && sample.time - lastTime > MaximumObservationGap) ||
                (haveReferencePlane && Mathf.Abs(sample.surfaceHeight - referencePlane) > MaximumPlaneChange))
                Reset();
            lastTime = sample.time; lastFrame = sample.frame;
            if (!haveReferencePlane) { referencePlane = sample.surfaceHeight; haveReferencePlane = true; }
            Trim(sample.time - historySeconds);

            if (awaitingReturn)
            {
                if (Mathf.Abs(sample.signedGap - returnGap) > rearmMargin || Mathf.Abs(sample.signedGap) > nearThreshold)
                {
                    Add(sample); Status = "Lift logged. Return near the prior plane clearance to rearm."; return false;
                }
                awaitingReturn = false;
                start = count = 0;
                referencePlane = sample.surfaceHeight;
            }

            if (liftStartedAt >= 0)
            {
                if (sample.signedGap - frozen.signedGap < liftDistance)
                {
                    liftStartedAt = -1;
                    Add(sample); Status = "Lift interrupted; waiting for another visible approach."; return false;
                }
                Add(sample);
                if (sample.time - liftStartedAt < liftHoldSeconds)
                {
                    Status = "Confirming tracked upward separation."; return false;
                }
                snapshot = new HandApproachSnapshot {
                    hand = hand, sample = frozen, liftStartedAt = liftStartedAt, confirmedAt = sample.time,
                    confirmedFrame = sample.frame, sampleAgeSeconds = sample.time - frozen.time,
                    frameAge = sample.frame - frozen.frame,
                    headToPointDistance = Vector3.Distance(frozen.headPosition, frozen.fingerPosition)
                };
                returnGap = frozen.signedGap;
                awaitingReturn = true; liftStartedAt = -1;
                Status = "Logged closest pre-lift plane sample; physical contact is unverified.";
                return true;
            }

            if (TryClosestBefore(sample.time, out var candidate) && sample.signedGap - candidate.signedGap >= liftDistance)
            {
                frozen = candidate;
                liftStartedAt = sample.time;
                Status = "Confirming tracked lift; the prior sample is frozen.";
            }
            else Status = Mathf.Abs(sample.signedGap) <= nearThreshold
                ? "Visible near-plane sample recorded; waiting for upward separation."
                : "Waiting for a visible near-plane hand.";
            Add(sample);
            return false;
        }

        bool TryClosestBefore(double now, out HandApproachSample candidate)
        {
            candidate = default;
            float best = float.PositiveInfinity;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                var sample = samples[(start + i) % samples.Length];
                if (sample.time < now - lookbackSeconds || sample.time >= now) continue;
                float gap = Mathf.Abs(sample.signedGap);
                // Prefer the latest equally close sample rather than an older stationary frame.
                if (gap > nearThreshold || gap > best) continue;
                best = gap; candidate = sample; found = true;
            }
            return found;
        }

        void Add(HandApproachSample sample)
        {
            if (count == samples.Length) { start = (start + 1) % samples.Length; count--; }
            samples[(start + count) % samples.Length] = sample;
            count++;
        }
        void Trim(double before)
        {
            while (count > 0 && samples[start].time < before) { start = (start + 1) % samples.Length; count--; }
        }
        public void Reset()
        {
            start = count = 0;
            lastTime = liftStartedAt = -1; lastFrame = -1;
            haveReferencePlane = awaitingReturn = false;
            referencePlane = returnGap = 0; frozen = default;
            Status = "Waiting for a visible near-plane hand.";
        }
        static bool Valid(HandApproachSample sample) => Finite(sample.time) && sample.time >= 0 && sample.frame >= 0 &&
            Finite(sample.headPosition) && Finite(sample.fingerPosition) && float.IsFinite(sample.signedGap) && float.IsFinite(sample.surfaceHeight);
        static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
