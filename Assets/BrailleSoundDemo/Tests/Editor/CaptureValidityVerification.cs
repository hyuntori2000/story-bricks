using System;
using System.Collections.Generic;
using System.IO;
using BrailleSoundDemo.CameraTest;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    public static class CaptureValidityVerification
    {
        [Serializable] sealed class Report
        {
            public bool passed;
            public bool hardwareTransportVerified = false;
            public List<string> checks = new List<string>();
        }

        public static void Run()
        {
            var report = new Report();
            Action<bool,string> check = (ok, message) => {
                if (!ok) throw new InvalidOperationException("Capture validity: " + message);
                report.checks.Add(message);
            };
            bool handsAndTableValid = true, appActive = true;
            string session = "story-a";
            int referenceRevision = 4, wordIndex = 1, liveCalls = 0;
            var physical = new CaptureValidityGuard(
                () => { liveCalls++; return handsAndTableValid; },
                () => appActive && session == "story-a" && referenceRevision == 4 && wordIndex == 1);
            check(physical.CanContinue(true, true), "A current frame can be selected while live capture gates pass.");
            handsAndTableValid = false;
            check(!physical.CanContinue(true, true), "Hands or depth becoming invalid before capture still rejects the frame.");
            check(!physical.TryFreeze(true, true) && !physical.SnapshotFrozen,
                "An invalid live frame cannot cross the snapshot boundary.");
            handsAndTableValid = true;
            check(!physical.TryFreeze(true, false), "A stale camera cannot produce a frozen snapshot.");
            session = "story-b";
            check(!physical.TryFreeze(true, true), "A changed story session rejects the snapshot before transport.");
            session = "story-a";
            check(physical.TryFreeze(true, true) && physical.SnapshotFrozen,
                "A valid private image can freeze its capture requirements.");
            int callsAtFreeze = liveCalls;
            handsAndTableValid = false;
            check(physical.CanContinue(true, false),
                "After the image freezes, hands leaving view, stale live depth and a stopped camera do not discard its ACK.");
            check(liveCalls == callsAtFreeze, "Frozen transport does not consult live hand or sample-age predicates.");
            referenceRevision = 5;
            check(!physical.CanContinue(true, true), "A reset tracking/table reference still rejects a late response.");
            referenceRevision = 4; session = "story-b";
            check(!physical.CanContinue(true, true), "A reset conversation still rejects a late response.");
            session = "story-a"; wordIndex = 2;
            check(!physical.CanContinue(true, true), "A response for an obsolete word step is not accepted.");
            wordIndex = 1; appActive = false;
            check(!physical.CanContinue(true, true), "An inactive or paused application rejects frozen-image continuation.");
            appActive = true;
            check(!physical.CanContinue(false, true), "Operation cancellation or replacement invalidates even an otherwise valid snapshot.");

            bool legacyLive = true;
            var legacy = new CaptureValidityGuard(() => legacyLive);
            check(legacy.TryFreeze(true, true), "A default diagnostic can encode its image normally.");
            legacyLive = false;
            check(!legacy.CanContinue(true, true), "Default diagnostic behavior still requires live hands through its response.");
            legacyLive = true;
            check(!legacy.CanContinue(true, false), "Default diagnostic behavior still requires camera freshness through its response.");
            bool throwContext = false;
            var throwing = new CaptureValidityGuard(() => true,
                () => throwContext ? throw new InvalidOperationException("test invalid context") : true);
            check(throwing.TryFreeze(true, true), "A configured post-snapshot gate is checked before freezing.");
            throwContext = true;
            check(!throwing.CanContinue(true, false), "A failing post-snapshot predicate rejects the response instead of accepting it.");
            var missing = new CaptureValidityGuard(null, () => true);
            check(!missing.TryFreeze(true, true), "An absent live predicate cannot bypass frame-selection checks.");
            report.passed = true;
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/capture-validity-verification.json", JsonUtility.ToJson(report, true));
            Debug.Log("CAPTURE_VALIDITY_VERIFICATION_OK " + report.checks.Count);
        }
    }
}
