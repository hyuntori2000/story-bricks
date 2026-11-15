using System;

namespace BrailleSoundDemo.CameraTest
{
    /// <summary>Only a witnessed hands-in → sustained hands-clear transition emits a capture.
    /// Unknown tracking interrupts the dwell; it is never evidence that a hand left.</summary>
    public sealed class HandsOffGate
    {
        public enum Phase { WaitingForHands, HandsIn, Clearing, Captured, Unknown }
        public Phase State { get; private set; } = Phase.WaitingForHands;
        public bool Armed { get; private set; }
        public double ClearSince { get; private set; } = -1;
        public readonly double ClearSeconds;
        public readonly double EnterSeconds;
        double enterSince = -1;
        double lastTime = -1;

        public HandsOffGate(double clearSeconds = 1.0, double enterSeconds = .2)
        {
            if (clearSeconds <= 0 || enterSeconds < 0) throw new ArgumentOutOfRangeException();
            ClearSeconds = clearSeconds; EnterSeconds = enterSeconds;
        }

        public bool Observe(double now, bool valid, bool handsInArea, bool canCapture)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || now < lastTime) { Reset(); return false; }
            lastTime = now;
            if (!valid)
            {
                enterSince = ClearSince = -1; State = Phase.Unknown; return false;
            }
            if (handsInArea)
            {
                ClearSince = -1;
                if (enterSince < 0) enterSince = now;
                if (now - enterSince >= EnterSeconds) Armed = true;
                State = Phase.HandsIn; return false;
            }
            enterSince = -1;
            if (!Armed)
            {
                ClearSince = -1;
                if (State != Phase.Captured) State = Phase.WaitingForHands;
                return false;
            }
            if (!canCapture) { ClearSince = -1; State = Phase.HandsIn; return false; }
            if (ClearSince < 0) ClearSince = now;
            State = Phase.Clearing;
            if (now - ClearSince < ClearSeconds) return false;
            Armed = false; ClearSince = -1; State = Phase.Captured;
            return true;
        }

        public void Reset()
        {
            Armed = false; enterSince = ClearSince = lastTime = -1; State = Phase.WaitingForHands;
        }
    }
}
