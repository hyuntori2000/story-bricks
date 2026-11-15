using System;
using System.Collections.Generic;
using System.IO;
using BrailleSoundDemo.CameraTest;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    public static class HandsOffVerification
    {
        [Serializable] sealed class Report { public bool passed; public bool simulated=true; public bool headsetTested=false; public List<string> checks=new List<string>(); }
        public static void Run()
        {
            var r=new Report();
            Action<bool,string> check=(ok,message)=> { if(!ok) throw new InvalidOperationException(message); r.checks.Add(message); };
            var gate=new HandsOffGate(1,.2);
            check(!gate.Observe(0,true,false,true) && !gate.Observe(2,true,false,true),"Starting with hands clear does not upload.");
            gate.Observe(3,true,true,true); gate.Observe(3.3,true,true,true);
            check(gate.Armed,"A witnessed hand in the area arms a capture.");
            gate.Observe(4,true,false,true);
            check(!gate.Observe(4.9,true,false,true),"A short release does not trigger.");
            check(gate.Observe(5.01,true,false,true),"Sustained hands-clear triggers exactly one capture.");
            check(!gate.Observe(8,true,false,true),"Remaining hands-off cannot repeatedly upload.");
            gate.Observe(9,true,true,true); gate.Observe(9.3,true,true,true); gate.Observe(10,true,false,true);
            check(!gate.Observe(10.8,false,false,true),"Lost tracking never counts as release.");
            check(!gate.Observe(12,true,false,true) && !gate.Observe(12.8,true,false,true),"Reacquired hands must complete a fresh clear dwell.");
            check(gate.Observe(13.01,true,false,true),"A fresh valid dwell can capture after tracking recovers.");
            gate.Observe(14,true,true,true); gate.Observe(14.3,true,true,true); gate.Observe(15,true,false,true);
            check(!gate.Observe(16,true,true,true),"Re-entry cancels the pending release.");
            gate.Observe(17,true,false,false);
            check(!gate.Observe(20,true,false,false),"Camera-not-ready or busy does not accumulate clear time.");
            check(!gate.Observe(21,true,false,true) && gate.Observe(22.01,true,false,true),"Readiness recovery starts a fresh dwell.");
            gate.Reset();
            check(!gate.Observe(23,true,false,true) && !gate.Armed,"Recalibration/session reset discards the armed state.");
            check(!gate.Observe(1,true,false,true),"A backwards clock resets safely.");
            r.passed=true; Directory.CreateDirectory("Documentation");
            File.WriteAllText("Documentation/HANDS_OFF_GATE_TEST_RESULTS.json",JsonUtility.ToJson(r,true));
            Debug.Log("HANDS_OFF_GATE_TESTS_PASSED "+r.checks.Count);
        }
    }
}
