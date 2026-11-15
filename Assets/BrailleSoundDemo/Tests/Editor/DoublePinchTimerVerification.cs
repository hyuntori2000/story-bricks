using System;
using System.Collections.Generic;
using System.IO;
using BrailleSoundDemo.TableDepthTest;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    public static class DoublePinchTimerVerification
    {
        [Serializable] sealed class Report
        {
            public bool passed;
            public bool physicalGestureVerified=false;
            public List<string> checks=new List<string>();
        }
        sealed class Harness
        {
            public readonly DoublePinchTimerPolicy Policy=new DoublePinchTimerPolicy();
            public double Now;
            public int Arms,Captures;
            public DoublePinchTimerPolicy.StepResult Last;
            public void Step(bool left=false,bool right=false,bool leftTracked=true,bool rightTracked=true,
                bool camera=true,bool context=true,bool allowed=true,double dt=.1)
            { At(Now+dt,left,right,leftTracked,rightTracked,camera,context,allowed); }
            public void At(double now,bool left=false,bool right=false,bool leftTracked=true,bool rightTracked=true,
                bool camera=true,bool context=true,bool allowed=true)
            {
                Now=now;
                Last=Policy.Observe(now,context,allowed,leftTracked,left,rightTracked,right,camera);
                if(Last.ArmedNow) Arms++;
                if(Last.CaptureNow) Captures++;
            }
            public void Arm(bool right=false)
            { Step();Step(left:!right,right:right);Step();Step(left:!right,right:right); }
        }

        public static void Run()
        {
            var report=new Report();
            Action<bool,string> check=(ok,message)=> {
                if(!ok) throw new InvalidOperationException("Double pinch timer: "+message);
                report.checks.Add(message);
            };
            var h=new Harness();h.Arm();double armedAt=h.Policy.ArmedAt;
            check(h.Arms==1 && h.Policy.IsArmed && h.Last.ArmedHand=="left" && h.Captures==0,
                "The second released-separated pinch arms one timer without taking a photo immediately.");
            h.At(armedAt+1.5,leftTracked:false,rightTracked:false);
            check(h.Captures==0 && h.Policy.IsArmed,"The previous 1.5-second deadline no longer captures a photo.");
            h.At(armedAt+2.999,leftTracked:false,rightTracked:false);
            check(h.Captures==0 && h.Policy.IsArmed,"No photo is requested at 2.999 seconds.");
            h.At(armedAt+3.0,leftTracked:false,rightTracked:false);
            check(h.Captures==1 && !h.Policy.IsArmed && h.Last.ArmedHand=="left",
                "At 3.0 seconds the photo is requested even after both hands leave tracking.");
            for(int i=0;i<20;i++) h.Step(leftTracked:false,rightTracked:false);
            check(h.Captures==1,"Continued hand absence does not repeat the consumed timer.");
            h=new Harness();h.Arm(true);armedAt=h.Policy.ArmedAt;h.At(armedAt+3.0,left:true,right:true);
            check(h.Captures==1 && h.Last.ArmedHand=="right","Either hand can arm; hand poses do not gate the elapsed timer.");
            h=new Harness();for(int i=0;i<20;i++)h.Step(left:true);
            check(h.Arms==0,"A pinch already held at startup does not count as a press.");
            h.Step();h.Step(left:true);for(int i=0;i<20;i++)h.Step(left:true);
            check(h.Arms==0,"Holding one pinch cannot become a double pinch.");
            h=new Harness();h.Step();h.Step(left:true);h.Step();h.Step(right:true);
            check(h.Arms==0,"One pinch on each hand is not a same-hand double pinch.");
            h=new Harness();h.Step();h.Step(left:true);h.Step(leftTracked:false);h.Step(left:true);
            check(h.Arms==0,"Tracking loss before arming clears the incomplete double pinch.");
            h=new Harness();h.Step();h.Step(left:true);for(int i=0;i<7;i++)h.Step();h.Step(left:true);
            check(h.Arms==0,"Separated pinches outside the double-pinch window do not arm.");
            h=new Harness();h.Step();h.Step(left:true);h.Step(dt:.01);h.Step(left:true,dt:.01);
            check(h.Arms==0,"Contact bounce below minimum separation does not arm.");
            h=new Harness();h.Step();h.Step(left:true);h.Step(dt:.3);h.Step(left:true);
            check(h.Arms==0,"A stalled observation interval cannot complete an unobserved double pinch.");
            h=new Harness();for(int i=0;i<30;i++)h.Step(leftTracked:false,rightTracked:false);
            check(h.Arms==0 && h.Captures==0,"A timer is never created just because hands are absent.");
            h=new Harness();h.Arm();armedAt=h.Policy.ArmedAt;h.At(armedAt+.5,camera:false);h.At(armedAt+3.0);
            check(h.Captures==1,"Temporary camera unavailability before the deadline does not delay a ready camera at deadline.");
            h=new Harness();h.Arm();armedAt=h.Policy.ArmedAt;h.At(armedAt+3.0,camera:false);
            check(h.Captures==0 && !h.Policy.IsArmed && h.Policy.Status.Contains("camera unavailable"),
                "An unavailable camera at deadline cancels with a retry status instead of waiting indefinitely.");
            h.Step();h.Step();
            check(h.Captures==0,"A canceled timer cannot fire later when the camera returns.");
            h=new Harness();h.Arm();armedAt=h.Policy.ArmedAt;h.At(armedAt+.5,context:false);h.At(armedAt+3.0);
            check(h.Captures==0 && !h.Policy.IsArmed,"Lost context cancels the timer before deadline.");
            h=new Harness();h.Arm();armedAt=h.Policy.ArmedAt;h.At(armedAt+.5,allowed:false);h.At(armedAt+3.0);
            check(h.Captures==0,"A closed conversation gate cancels the timer.");
            h=new Harness();h.Arm();armedAt=h.Policy.ArmedAt;h.Policy.Reset();h.At(armedAt+3.0);
            check(h.Captures==0,"An explicit reset prevents the scheduled capture.");
            h=new Harness();h.Arm();armedAt=h.Policy.ArmedAt;h.At(armedAt+3.1);
            check(h.Captures==1,"The first active update after the deadline consumes the timer once.");
            h=new Harness();h.Arm();h.At(h.Now);h.Step(dt:2);
            check(h.Captures==0,"Non-increasing observation time cancels the timer.");
            h=new Harness();h.Arm();h.At(double.NaN);
            check(h.Captures==0 && !h.Policy.IsArmed,"Nonfinite observation time fails closed.");
            h=new Harness();h.Arm();armedAt=h.Policy.ArmedAt;h.At(armedAt+3.0);h.Arm();armedAt=h.Policy.ArmedAt;h.At(armedAt+3.0);
            check(h.Arms==2 && h.Captures==2,"A new released-separated double pinch can explicitly schedule the next photo.");
            h=new Harness();h.Arm();armedAt=h.Policy.ArmedAt;
            check(h.Last.CountdownSeconds==3,"Arming emits the first three-second countdown cue.");
            h.At(armedAt+.999);
            check(h.Last.CountdownSeconds==0 && h.Captures==0,"Repeated frames do not repeat the countdown cue.");
            h.At(armedAt+1.001);
            check(h.Last.CountdownSeconds==2 && h.Captures==0,"The second countdown cue follows one second after arming.");
            h.At(armedAt+2.001);
            check(h.Last.CountdownSeconds==1 && h.Captures==0,"The final countdown cue still precedes capture by one second.");
            h.At(armedAt+3.0);
            check(h.Last.CountdownSeconds==0 && h.Captures==1,"The three-second deadline captures without an extra waiting cue.");
            h=new Harness();h.Arm();armedAt=h.Policy.ArmedAt;h.At(armedAt+1.1,context:false);
            check(h.Last.CountdownSeconds==0 && h.Captures==0,"Canceling the context also cancels remaining countdown cues.");
            // Onboarding consumes only ArmedNow, then discards the photo timer.
            h=new Harness();h.Arm();h.Policy.Reset();
            h.Step(left:true);h.Step(left:true);h.At(h.Now+3.1,left:true);
            check(!h.Policy.IsArmed && h.Captures==0 && h.Arms==1,
                "Consuming entry on the second pinch cannot become a photo or repeat while that pinch is held.");
            h=new Harness();h.Step();h.Step(left:true);h.Policy.Reset();
            h.Step(left:true);h.Step();h.Step(left:true);
            check(h.Arms==0,"A pinch begun before narrator completion cannot carry into entry after reset.");
            h.Step();h.Step(left:true);
            check(h.Arms==1 && h.Captures==0,"A fresh released-separated pair enters immediately without a photo.");
            report.passed=true;
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/double-pinch-timer-verification.json",JsonUtility.ToJson(report,true));
            Debug.Log("DOUBLE_PINCH_TIMER_VERIFICATION_OK "+report.checks.Count);
        }
    }
}
