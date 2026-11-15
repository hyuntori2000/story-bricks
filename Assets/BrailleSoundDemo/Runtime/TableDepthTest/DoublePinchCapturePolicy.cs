using System;

namespace BrailleSoundDemo.TableDepthTest
{
    /// <summary>Geometry/tracking comes from the caller. Missing hands count only after explicit arming.</summary>
    public enum HandClearEvidence { Unknown, VisibleBlocked, VisibleClear, Untracked }

    /// <summary>
    /// Deterministic same-hand double-pinch arming followed by a bounded two-hand clear dwell.
    /// No camera, SDK, audio or world-state access. Reset on reference, story or tracking-origin changes.
    /// </summary>
    public sealed class DoublePinchCapturePolicy
    {
        public const double DoublePinchSeconds = .65;
        public const double MinimumPinchSeparationSeconds = .08;
        public const double ArmTimeoutSeconds = 10;
        public const double ClearDwellSeconds = .35;
        public const double MaximumObservationGapSeconds = .25;

        public struct StepResult
        {
            public bool ArmedNow, CaptureNow;
            public string ArmedHand;
        }

        struct PinchHistory
        {
            bool sawRelease, wasPinched;
            double? firstPinchAt;

            public bool Observe(double now, bool tracked, bool pinched)
            {
                if (!tracked) { this = default; return false; }
                if (firstPinchAt.HasValue && now - firstPinchAt.Value > DoublePinchSeconds)
                    firstPinchAt = null;
                if (!pinched)
                {
                    sawRelease = true;
                    wasPinched = false;
                    return false;
                }
                bool rising = sawRelease && !wasPinched;
                wasPinched = true;
                if (!rising) return false;
                if (firstPinchAt.HasValue)
                {
                    double separation = now - firstPinchAt.Value;
                    if (separation >= MinimumPinchSeparationSeconds && separation <= DoublePinchSeconds)
                    { this = default; return true; }
                }
                firstPinchAt = now;
                return false;
            }
        }

        public bool IsArmed { get; private set; }
        public double ArmedAt { get; private set; } = -1;
        public string ArmedHand { get; private set; }
        public string Status { get; private set; } = "Double pinch with one hand to prepare a photo.";
        PinchHistory leftPinch, rightPinch;
        double lastTime = -1, clearSince = -1;

        public StepResult Observe(double now, bool contextValid, bool captureAllowed,
            bool leftPinchTracked, bool leftPinched, bool rightPinchTracked, bool rightPinched,
            HandClearEvidence leftEvidence, HandClearEvidence rightEvidence, bool targetValid, bool cameraReady)
        {
            if (!Finite(now) || now < 0 || (lastTime >= 0 && now <= lastTime))
            { Reset(); Status = "Capture canceled: invalid observation timing."; return default; }
            double gap = lastTime >= 0 ? now - lastTime : 0;
            lastTime = now;
            if (!contextValid || !captureAllowed)
            { Reset(); Status = "Capture unavailable; double pinch when the story is ready."; return default; }
            if (gap > MaximumObservationGapSeconds)
            {
                leftPinch = rightPinch = default;
                clearSince = -1;
            }
            if (IsArmed)
            {
                if (now - ArmedAt > ArmTimeoutSeconds)
                { Reset(); Status = "Photo preparation expired. Double pinch again."; return default; }
                bool leftClear = IsClearAfterArm(leftEvidence);
                bool rightClear = IsClearAfterArm(rightEvidence);
                if (!leftClear || !rightClear)
                {
                    clearSince = -1;
                    Status = "Photo prepared. Move both hands clear of the word.";
                    return default;
                }
                if (clearSince < 0) clearSince = now;
                if (now - clearSince < ClearDwellSeconds)
                { Status = "Confirming that both hands are clear."; return default; }
                if (!targetValid || !cameraReady)
                { Status = "Hands clear; waiting for a valid saved target and camera."; return default; }
                string hand = ArmedHand;
                Reset();
                Status = "Capturing the prepared word once.";
                return new StepResult { CaptureNow = true, ArmedHand = hand };
            }
            bool leftDouble = leftPinch.Observe(now, leftPinchTracked, leftPinched);
            bool rightDouble = rightPinch.Observe(now, rightPinchTracked, rightPinched);
            if (!leftDouble && !rightDouble)
            { Status="Double pinch with one hand above the word to prepare a photo."; return default; }
            // Left wins an exact simultaneous double; either path still creates just one arm.
            IsArmed = true;
            ArmedAt = now;
            ArmedHand = leftDouble ? "left" : "right";
            clearSince = -1;
            leftPinch = rightPinch = default;
            Status = "Photo prepared. Move both hands clear of the word.";
            return new StepResult { ArmedNow = true, ArmedHand = ArmedHand };
        }

        static bool IsClearAfterArm(HandClearEvidence evidence)
            => evidence == HandClearEvidence.VisibleClear || evidence == HandClearEvidence.Untracked;

        public void Reset()
        {
            IsArmed = false; ArmedAt = -1; ArmedHand = null;
            lastTime = clearSince = -1;
            leftPinch = rightPinch = default;
            Status = "Double pinch with one hand to prepare a photo.";
        }

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
