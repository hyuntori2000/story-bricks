using System;

namespace BrailleSoundDemo.TableDepthTest
{
    /// <summary>
    /// Same-hand double pinch authorizes one photo after three seconds.
    /// Hand tracking is used only to recognize the input; it cannot stall an armed timer.
    /// </summary>
    public sealed class DoublePinchTimerPolicy
    {
        public const double DoublePinchSeconds = .65;
        public const double MinimumPinchSeparationSeconds = .08;
        public const double CaptureDelaySeconds = 3.0;
        public const double MaximumPinchObservationGapSeconds = .25;

        public struct StepResult
        {
            public bool ArmedNow, CaptureNow;
            public string ArmedHand;
            public int CountdownSeconds;
        }

        struct PinchHistory
        {
            bool sawRelease, wasPinched;
            double? firstPinchAt;

            public bool Observe(double now, bool tracked, bool pinched)
            {
                if (!tracked) { this = default; return false; }
                if (firstPinchAt.HasValue && now-firstPinchAt.Value > DoublePinchSeconds)
                    firstPinchAt = null;
                if (!pinched)
                { sawRelease=true; wasPinched=false; return false; }
                bool rising=sawRelease && !wasPinched;
                wasPinched=true;
                if (!rising) return false;
                if (firstPinchAt.HasValue)
                {
                    double separation=now-firstPinchAt.Value;
                    if (separation>=MinimumPinchSeparationSeconds && separation<=DoublePinchSeconds)
                    { this=default; return true; }
                }
                firstPinchAt=now;
                return false;
            }
        }

        public bool IsArmed { get; private set; }
        public double ArmedAt { get; private set; } = -1;
        public string ArmedHand { get; private set; }
        public string Status { get; private set; } = "Double pinch above the word to take a photo in 3 seconds.";
        PinchHistory leftPinch, rightPinch;
        double lastTime=-1;
        int lastCountdown;

        public StepResult Observe(double now, bool contextValid, bool captureAllowed,
            bool leftTracked, bool leftPinched, bool rightTracked, bool rightPinched, bool cameraReady)
        {
            if (!Finite(now) || now<0 || (lastTime>=0 && now<=lastTime))
            { Reset(); Status="Capture canceled: invalid observation timing."; return default; }
            double gap=lastTime>=0 ? now-lastTime : 0;
            lastTime=now;
            if (!contextValid || !captureAllowed)
            { Reset(); Status="Capture unavailable; double pinch when the story is ready."; return default; }
            if (IsArmed)
            {
                if (now < ArmedAt+CaptureDelaySeconds)
                {
                    int remaining=(int)Math.Ceiling(ArmedAt+CaptureDelaySeconds-now);
                    Status="Photo in "+remaining+" seconds. Move your hands aside and keep the box in view.";
                    if(remaining!=lastCountdown)
                    { lastCountdown=remaining; return new StepResult { CountdownSeconds=remaining }; }
                    return default;
                }
                string hand=ArmedHand;
                Reset();
                if (!cameraReady)
                { Status="Photo canceled: camera unavailable at the timer deadline. Double pinch again."; return default; }
                Status="Timer elapsed; capturing one frame.";
                return new StepResult { CaptureNow=true, ArmedHand=hand };
            }
            if (gap>MaximumPinchObservationGapSeconds) leftPinch=rightPinch=default;
            bool leftDouble=leftPinch.Observe(now,leftTracked,leftPinched);
            bool rightDouble=rightPinch.Observe(now,rightTracked,rightPinched);
            if (!leftDouble && !rightDouble)
            { Status="Double pinch above the word to take a photo in 3 seconds."; return default; }
            IsArmed=true; ArmedAt=now; ArmedHand=leftDouble ? "left" : "right";
            leftPinch=rightPinch=default;
            lastCountdown=(int)Math.Ceiling(CaptureDelaySeconds);
            Status="Photo scheduled for 3 seconds after this double pinch.";
            return new StepResult { ArmedNow=true, ArmedHand=ArmedHand, CountdownSeconds=lastCountdown };
        }

        public void Reset()
        {
            IsArmed=false; ArmedAt=-1; ArmedHand=null; lastTime=-1; lastCountdown=0;
            leftPinch=rightPinch=default;
            Status="Double pinch above the word to take a photo in 3 seconds.";
        }

        static bool Finite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
    }
}
