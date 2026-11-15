using System;
using System.Collections.Generic;
using System.IO;
using BrailleSoundDemo.TableDepthTest;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    public static class DoublePinchCaptureVerification
    {
        [Serializable] sealed class Report
        {
            public bool passed;
            public bool physicalGestureVerified = false;
            public List<string> checks = new List<string>();
        }

        sealed class Harness
        {
            public readonly DoublePinchCapturePolicy Policy = new DoublePinchCapturePolicy();
            public double Now;
            public int Arms, Captures;
            public DoublePinchCapturePolicy.StepResult Last;
            public void Step(bool left = false, bool right = false, bool leftTracked = true, bool rightTracked = true,
                HandClearEvidence leftEvidence = HandClearEvidence.Unknown,
                HandClearEvidence rightEvidence = HandClearEvidence.Unknown,
                bool target = true, bool camera = true, bool context = true, bool allowed = true, double dt = .1)
            {
                Now += dt;
                Last = Policy.Observe(Now,context,allowed,leftTracked,left,rightTracked,right,
                    leftEvidence,rightEvidence,target,camera);
                if (Last.ArmedNow) Arms++;
                if (Last.CaptureNow) Captures++;
            }
            public void Arm(bool right = false)
            { Step(); Step(left:!right,right:right); Step(); Step(left:!right,right:right); }
            public void Clear(bool target = true, bool camera = true, int count = 5)
            {
                for (int i=0;i<count;i++) Step(leftEvidence:HandClearEvidence.VisibleClear,
                    rightEvidence:HandClearEvidence.VisibleClear,target:target,camera:camera);
            }
        }

        public static void Run()
        {
            var report = new Report();
            Action<bool,string> check = (ok,message) => {
                if (!ok) throw new InvalidOperationException("Double pinch: " + message);
                report.checks.Add(message);
            };
            var h = new Harness(); h.Arm();
            check(h.Arms==1 && h.Policy.IsArmed && h.Last.ArmedHand=="left" && h.Captures==0,
                "Two released-separated pinches on one hand arm once without capturing immediately.");
            h.Clear();
            check(h.Captures==1 && !h.Policy.IsArmed,
                "Both visible-clear hands held for the dwell trigger exactly one photo.");
            h.Clear(count:20);
            check(h.Captures==1 && h.Arms==1, "Continuing to hold hands clear cannot take another photo.");

            h=new Harness(); h.Arm(true);
            check(h.Arms==1 && h.Last.ArmedHand=="right", "The right hand can independently arm capture.");
            h=new Harness(); for(int i=0;i<20;i++) h.Step(left:true);
            check(h.Arms==0, "Starting with a held pinch never counts as a click.");
            h.Step(); h.Step(left:true); for(int i=0;i<10;i++) h.Step(left:true);
            check(h.Arms==0, "One press held down never becomes a double pinch.");
            h=new Harness(); h.Step(); h.Step(left:true); h.Step(); h.Step(right:true);
            check(h.Arms==0, "One pinch per hand is not a same-hand double pinch.");
            h=new Harness(); h.Step(); h.Step(left:true); h.Step(leftTracked:false); h.Step(left:true);
            check(h.Arms==0, "Tracking loss resets click history and a recovered held pinch does not arm.");
            h=new Harness(); h.Step(); h.Step(left:true); for(int i=0;i<7;i++) h.Step(); h.Step(left:true);
            check(h.Arms==0, "Pinches outside the double-pinch window do not arm.");
            h=new Harness(); h.Step(); h.Step(left:true); h.Step(dt:.01); h.Step(left:true,dt:.01);
            check(h.Arms==0, "Contact bounce below minimum separation does not arm.");

            h=new Harness();
            for(int i=0;i<10;i++) h.Step(leftTracked:false,rightTracked:false,
                leftEvidence:HandClearEvidence.Untracked,rightEvidence:HandClearEvidence.Untracked);
            check(h.Captures==0 && h.Arms==0, "Missing hands cannot autonomously arm or capture a photo.");
            h=new Harness(); h.Arm();
            for(int i=0;i<4;i++) h.Step(leftTracked:false,rightTracked:false,
                leftEvidence:HandClearEvidence.Untracked,rightEvidence:HandClearEvidence.Untracked);
            check(h.Captures==0 && h.Policy.IsArmed, "An explicit arm still requires the full 0.35-second clear dwell.");
            h.Step(leftTracked:false,rightTracked:false,
                leftEvidence:HandClearEvidence.Untracked,rightEvidence:HandClearEvidence.Untracked);
            check(h.Captures==1 && !h.Policy.IsArmed,
                "Both hands leaving tracking after an explicit double pinch produces one photo after the dwell.");
            for(int i=0;i<20;i++) h.Step(leftTracked:false,rightTracked:false,
                leftEvidence:HandClearEvidence.Untracked,rightEvidence:HandClearEvidence.Untracked);
            check(h.Captures==1, "Continuing hand absence cannot repeat the consumed capture.");
            h=new Harness(); h.Arm();
            for(int i=0;i<5;i++) h.Step(leftTracked:false,leftEvidence:HandClearEvidence.Untracked,
                rightEvidence:HandClearEvidence.VisibleClear);
            check(h.Captures==1, "One absent hand and one visible hand outside the word region can complete capture.");
            h=new Harness(); h.Arm();
            for(int i=0;i<10;i++) h.Step(leftEvidence:HandClearEvidence.VisibleBlocked,
                rightEvidence:HandClearEvidence.Untracked);
            check(h.Captures==0 && h.Policy.IsArmed, "One hand still overlapping the word prevents capture.");
            h=new Harness(); h.Arm();
            for(int i=0;i<10;i++) h.Step(leftEvidence:HandClearEvidence.Unknown,
                rightEvidence:HandClearEvidence.Untracked);
            check(h.Captures==0, "Low-confidence tracked-but-unknown hands do not count as absent.");
            h=new Harness(); h.Arm(); h.Clear(target:false);
            check(h.Captures==0 && h.Policy.IsArmed, "An invalid target prevents capture even with clear hands.");
            h.Clear();
            check(h.Captures==1, "A pending clear gesture captures only after its target is valid.");
            h=new Harness(); h.Arm(); h.Clear(camera:false);
            check(h.Captures==0, "An unavailable camera cannot produce a capture event.");
            h=new Harness(); h.Arm(); h.Clear(count:3);
            h.Step(leftEvidence:HandClearEvidence.VisibleBlocked,rightEvidence:HandClearEvidence.VisibleClear);
            h.Clear(count:4);
            check(h.Captures==0, "A hand reentering the word area restarts the entire clear dwell.");
            h.Clear(count:1);
            check(h.Captures==1, "Capture proceeds after a new full clear dwell following reentry.");
            h=new Harness(); h.Arm(); h.Clear(count:3);
            h.Step(leftEvidence:HandClearEvidence.Unknown,rightEvidence:HandClearEvidence.VisibleClear);
            h.Clear(count:4);
            check(h.Captures==0, "Unknown hand evidence restarts the clear dwell.");
            h=new Harness(); h.Arm(); h.Clear(count:3);
            h.Step(leftEvidence:HandClearEvidence.Untracked,rightEvidence:HandClearEvidence.Untracked,dt:.5);
            check(h.Captures==0, "A stalled observation interval cannot complete an unobserved clear dwell.");
            for(int i=0;i<4;i++) h.Step(leftEvidence:HandClearEvidence.Untracked,rightEvidence:HandClearEvidence.Untracked);
            check(h.Captures==1, "A fresh full clear dwell is required after an observation stall.");

            h=new Harness(); h.Arm(); h.Step(context:false); h.Clear();
            check(h.Captures==0 && !h.Policy.IsArmed, "Context loss cancels the armed request.");
            h=new Harness(); h.Arm(); h.Step(allowed:false); h.Clear();
            check(h.Captures==0 && !h.Policy.IsArmed, "Conversation capture closure cancels the armed request.");
            h=new Harness(); h.Arm(); h.Policy.Reset(); h.Clear();
            check(h.Captures==0, "An explicit reference or session reset cancels the request.");
            h=new Harness(); h.Arm(); for(int i=0;i<101;i++) h.Step(); h.Clear();
            check(h.Captures==0 && !h.Policy.IsArmed, "An uncompleted request expires after ten seconds.");
            h=new Harness(); h.Arm(); h.Step(dt:0); h.Clear();
            check(h.Captures==0, "Non-increasing time fails closed.");
            h=new Harness(); h.Arm();
            var invalid=h.Policy.Observe(double.NaN,true,true,true,false,true,false,
                HandClearEvidence.VisibleClear,HandClearEvidence.VisibleClear,true,true);
            check(!invalid.CaptureNow && !h.Policy.IsArmed, "Nonfinite observation time cancels capture.");

            report.passed=true;
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/double-pinch-capture-verification.json",JsonUtility.ToJson(report,true));
            Debug.Log("DOUBLE_PINCH_CAPTURE_VERIFICATION_OK " + report.checks.Count);
        }
    }
}
