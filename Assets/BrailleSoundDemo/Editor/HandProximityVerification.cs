using System;
using BrailleSoundDemo.TableDepthTest;
using UnityEditor;
using UnityEngine;

namespace BrailleSoundDemo.Editor
{
    /// <summary>Pure geometry/history checks; does not claim to validate device tracking or physical contact.</summary>
    public static class HandProximityVerification
    {
        [MenuItem("Word Story Demo/Table Depth Test/Verify Hand Proximity")]
        public static void Run()
        {
            int checks = Verify();
            Debug.Log("[HandProximityVerification] Passed " + checks + " geometry/history checks. Sensor accuracy remains an on-device test.");
        }

        public static int Verify()
        {
            int checks = 0;
            var points = new[] { new Vector3(0, .78f, .5f), new Vector3(1, .74f, .5f), new Vector3(2, .70f, .5f) };
            HandProximityDiagnostic.GetGaps(points, .75f, out float signed, out float minSigned, out float minAbs, out Vector3 closest);
            Check(Near(signed, -.01f) && Near(minSigned, -.05f) && Near(minAbs, .01f) && closest.x == 1,
                "Choose minimum absolute clearance, preserving its sign and independent lowest signed height.", ref checks);
            HandProximityDiagnostic.GetGaps(Array.Empty<Vector3>(), .75f, out signed, out minSigned, out minAbs, out closest);
            Check(float.IsNaN(signed) && float.IsNaN(minAbs) && float.IsNaN(closest.x), "No tips must return unknown values.", ref checks);
            points[2] = new Vector3(float.NaN, 0, 0);
            HandProximityDiagnostic.GetGaps(points, .75f, out signed, out minSigned, out minAbs, out closest);
            Check(float.IsNaN(signed) && float.IsNaN(minSigned) && float.IsNaN(closest.x), "Invalid tip must not leak a partial valid measurement.", ref checks);

            var index = new Vector3(.02f, .76f, .5f);
            var allTips = new[] { new Vector3(0, .751f, .5f), index, new Vector3(.04f, .78f, .5f),
                new Vector3(.06f, .79f, .5f), new Vector3(.08f, .80f, .5f) };
            Check(HandProximityDiagnostic.TryGetApproachGap(allTips, index, true, .75f, out signed, out closest) &&
                closest == index && Near(signed, .01f), "Index mode keeps the index even when the thumb is nearer the plane.", ref checks);
            var noThumb = new[] { allTips[1], allTips[2], allTips[3], allTips[4] };
            Check(HandProximityDiagnostic.TryGetApproachGap(noThumb, index, true, .75f, out signed, out closest) &&
                closest == index && Near(signed, .01f), "Losing the optional thumb does not change the approach landmark or clearance.", ref checks);
            Check(!HandProximityDiagnostic.TryGetApproachGap(noThumb, null, true, .75f, out signed, out closest) &&
                float.IsNaN(signed) && float.IsNaN(closest.x), "Missing current index never falls back to another visible fingertip.", ref checks);
            Check(HandProximityDiagnostic.TryGetApproachGap(allTips, index, false, .75f, out signed, out closest) &&
                closest == allTips[0] && !HandProximityDiagnostic.TryGetApproachGap(noThumb, index, false, .75f, out _, out _),
                "Default strict mode still requires five tips and selects the closest one.", ref checks);

            var h = new Harness();
            h.Push(.01f); var expected = h.LastSample;
            h.Push(.035f); h.Push(.10f);
            Check(h.History.LiftPending && h.Events == 0, "Tracked upward separation starts a dwell, not an immediate event.", ref checks);
            for (int i = 0; i < 5; i++) h.Push(.10f);
            Check(h.Events == 1 && h.Snapshot.sample.frame == expected.frame && Near(h.Snapshot.sample.signedGap, .01f),
                "Emit one frozen closest pre-lift sample.", ref checks);
            Check(h.Snapshot.frameAge == h.Snapshot.confirmedFrame - expected.frame &&
                Math.Abs(h.Snapshot.sampleAgeSeconds - (h.Snapshot.confirmedAt - expected.time)) < .000001 &&
                Near(h.Snapshot.headToPointDistance, Vector3.Distance(expected.headPosition, expected.fingerPosition)),
                "Snapshot time, frame age, and distance use the saved head and fingertip sample.", ref checks);
            for (int i = 0; i < 20; i++) h.Push(.15f);
            Check(h.Events == 1 && h.History.AwaitingReturn, "Holding clear does not emit repeatedly.", ref checks);
            h.Push(.055f); h.Push(.15f);
            for (int i = 0; i < 5; i++) h.Push(.15f);
            Check(h.Events == 1, "Returning only partway does not rearm.", ref checks);
            h.Push(.02f); h.Push(.12f);
            for (int i = 0; i < 5; i++) h.Push(.12f);
            Check(h.Events == 2, "Returning within two centimetres of prior clearance enables a new approach.", ref checks);

            h = new Harness(); h.Push(.01f); h.Push(.10f); h.Push(.10f, false);
            for (int i = 0; i < 10; i++) h.Push(.10f);
            Check(h.Events == 0 && !h.History.LiftPending, "Tracking loss clears pending lift and cannot trigger on recovery aloft.", ref checks);
            h = new Harness(); h.Push(.01f); h.Push(.10f); h.Push(.03f);
            Check(!h.History.LiftPending && h.Events == 0, "Insufficient separation interrupts confirmation.", ref checks);
            h = new Harness(); h.Push(.01f); h.Push(.10f); h.Push(.10f, true, .6);
            for (int i = 0; i < 5; i++) h.Push(.10f);
            Check(h.Events == 0, "Long observation gaps never complete a lift dwell.", ref checks);
            h = new Harness(); h.Push(.01f); h.Push(.10f); h.Push(.10f, true, .05, .85f);
            for (int i = 0; i < 5; i++) h.Push(.10f, true, .05, .85f);
            Check(h.Events == 0, "A changed plane resets prior approach evidence.", ref checks);

            h = new Harness(); h.Push(-.04f); int beforeLift = h.LastSample.frame; h.Push(.03f);
            for (int i = 0; i < 5; i++) h.Push(.025f);
            Check(h.Events == 1 && h.Snapshot.sample.frame == beforeLift && Near(h.Snapshot.sample.signedGap, -.04f),
                "A closer sample after lift begins must not replace the frozen pre-lift candidate.", ref checks);
            h = new Harness(); h.Push(.01f);
            for (int i = 0; i < 25; i++) h.Push(.069f);
            for (int i = 0; i < 6; i++) h.Push(.15f);
            Check(h.Events == 0, "A near-plane sample older than the one-second lookback cannot start a lift.", ref checks);
            h = new Harness();
            for (int i = 0; i < 2000; i++) h.Push(.2f, true, .001);
            Check(h.History.Count <= 512 && h.Events == 0, "High-rate history has a strict capacity bound.", ref checks);
            h = new Harness();
            for (int i = 0; i < 150; i++) h.Push(.2f);
            Check(h.History.Count <= 62 && h.Events == 0, "History drops samples older than three seconds.", ref checks);
            h = new Harness(); h.Push(.01f); h.Push(.10f);
            var duplicate = h.LastSample;
            Check(!h.History.Observe(duplicate, true, out _) && h.History.Count == 0,
                "Duplicate frame/time invalidates candidate evidence.", ref checks);
            h = new Harness(); h.Push(.01f); h.Push(float.NaN);
            Check(h.History.Count == 0, "Nonfinite gap data clears history.", ref checks);
            h = new Harness(); h.Push(.01f); h.Push(.10f); h.History.Reset();
            for (int i = 0; i < 6; i++) h.Push(.10f);
            Check(h.Events == 0, "Explicit reset prevents a pending event after pause or origin change.", ref checks);
            return checks;
        }

        sealed class Harness
        {
            public readonly RecentHandApproachHistory History = new RecentHandApproachHistory("left");
            public int Events;
            public HandApproachSnapshot Snapshot;
            public HandApproachSample LastSample;
            double now;
            int frame;
            public void Push(float gap, bool valid = true, double seconds = .05, float surface = .75f)
            {
                now += seconds; frame++;
                LastSample = new HandApproachSample { time = now, frame = frame, signedGap = gap, surfaceHeight = surface,
                    headPosition = new Vector3(0, 1.6f, 0), fingerPosition = new Vector3(0, surface + gap, .5f) };
                if (!History.Observe(LastSample, valid, out var snapshot)) return;
                Events++; Snapshot = snapshot;
            }
        }
        static bool Near(float a, float b) => Mathf.Abs(a - b) < .00001f;
        static void Check(bool condition, string message, ref int checks)
        {
            if (!condition) throw new InvalidOperationException("Hand proximity verification: " + message);
            checks++;
        }
    }
}
