using System;
using System.Collections.Generic;
using System.IO;
using BrailleSoundDemo.CameraTest;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    public static class TrackedBlockVerification
    {
        [Serializable] sealed class Report
        {
            public bool passed;
            public bool simulated = true;
            public bool headsetTested = false;
            public bool realBlockTrackerTested = false;
            public List<string> checks = new List<string>();
        }
        static TrackedBlockObservation Block(string id = "brick-001", float confidence = .95f,
            Vector3? position = null, Quaternion? rotation = null, Vector3? size = null)
            => new TrackedBlockObservation(id, new Pose(position ?? new Vector3(0,.01f,1), rotation ?? Quaternion.identity), size ?? new Vector3(.04f,.02f,.08f), confidence);
        static TrackedBlockRegistry New() => new TrackedBlockRegistry("test-session", 1, .5, .7f);
        static bool Submit(TrackedBlockRegistry registry, long sequence, double capture, double now,
            params TrackedBlockObservation[] blocks)
            => registry.ReplaceSnapshot("test-session", 1, sequence, capture, now, blocks, out _);

        public static void Run()
        {
            var report = new Report();
            Action<bool,string> check = (ok,message) => { if(!ok) throw new InvalidOperationException(message); report.checks.Add(message); };
            var registry = New();
            check(!registry.TryGetFreshBlocks(0,out var empty,out _) && empty.Count==0 && !registry.TryGetFreshBounds(0,out _,out _),
                "An empty registry has no fallback blocks or valid target bounds.");
            check(Submit(registry,0,1,1.1,Block()) && registry.TryGetFreshBlocks(1.2,out var fresh,out _) && fresh.Count==1,
                "A fresh real-observation-shaped snapshot is available.");
            check(!Submit(registry,0,1.2,1.2,Block("duplicate-sequence")) && registry.TryGetFreshBlocks(1.2,out fresh,out _) && fresh[0].Id=="brick-001",
                "Repeated or old sequences cannot replace a newer accepted set.");
            check(!registry.ReplaceSnapshot("old-session",1,100,1.2,1.2,new[]{Block()},out _) &&
                  !registry.ReplaceSnapshot("test-session",0,100,1.2,1.2,new[]{Block()},out _) &&
                  registry.TryGetFreshBlocks(1.2,out _,out _),
                "Late session/revision messages cannot poison the current tracked set.");
            check(!registry.TryGetFreshBlocks(1.51,out empty,out _) && empty.Count==0 && !registry.TryGetFreshBounds(1.51,out _,out _),
                "Snapshot expiry removes both blocks and bounds instead of implying hands-off.");

            registry = New();
            Submit(registry,0,2,2,Block());
            check(!Submit(registry,1,2.1,2.1) && !registry.TryGetFreshBlocks(2.1,out _,out _),
                "An empty newer detection invalidates an otherwise fresh previous snapshot.");
            check(!Submit(registry,0,2.1,2.1,Block()) && !registry.TryGetFreshBlocks(2.1,out _,out _),
                "An old response cannot resurrect blocks after a newer empty snapshot.");
            check(Submit(registry,2,2.2,2.2,Block()) && !Submit(registry,3,2.3,2.3,Block(confidence:.2f)) && !registry.TryGetFreshBlocks(2.3,out _,out _),
                "Low-confidence updates fail closed even while the previous set would be fresh.");

            registry = New();
            check(!Submit(registry,0,3,4,Block()),"Network delay is measured from capture time, so stale arrivals are rejected.");
            check(!Submit(registry,1,5,4,Block()),"A future timestamp from another clock domain is rejected.");
            check(Submit(registry,2,4,4,Block()) && !Submit(registry,3,3.99,4.1,Block()),
                "A higher sequence cannot move accepted source capture time backwards.");

            foreach(var bad in new[] {
                Block(position:new Vector3(float.NaN,0,0)),
                Block(rotation:new Quaternion(0,0,0,0)),
                Block(rotation:new Quaternion(float.PositiveInfinity,0,0,1)),
                Block(size:new Vector3(.04f,0,.08f)),
                Block(size:new Vector3(.04f,.02f,float.NaN)),
                Block(confidence:float.NaN), Block(confidence:1.1f), Block(id:" ") })
            {
                registry=New();
                check(!Submit(registry,0,1,1,bad) && !registry.TryGetFreshBlocks(1,out _,out _),
                    "Invalid geometry/identity/confidence cannot produce available blocks: check "+report.checks.Count);
            }
            registry=New();
            check(!Submit(registry,0,1,1,Block("same"),Block("same")),"Duplicate physical IDs in one snapshot are rejected.");
            check(Submit(registry,1,1,1,Block("A-001"),Block("A-002")),"Repeated letters can retain distinct physical brick IDs.");

            registry=New();
            var input = new List<TrackedBlockObservation>{Block("original",position:new Vector3(2,3,4),rotation:Quaternion.Euler(0,90,0),size:new Vector3(.04f,.02f,.08f))};
            check(registry.ReplaceSnapshot("test-session",1,0,1,1,input,out _),"An oriented block snapshot is accepted.");
            input[0]=Block("mutated"); input.Clear();
            check(registry.TryGetFreshBlocks(1,out fresh,out _) && fresh.Count==1 && fresh[0].Id=="original" && fresh[0].WorldPose.position==new Vector3(2,3,4),
                "Producer list mutation cannot change the retained observation snapshot.");
            bool mutationRejected=false;
            try { ((IList<TrackedBlockObservation>)fresh)[0]=Block("changed"); }
            catch(NotSupportedException) { mutationRejected=true; }
            check(mutationRejected,"Consumers cannot mutate the exposed read-only snapshot.");
            check(registry.TryGetFreshBounds(1,out var bounds,out _) && Vector3.Distance(bounds.center,new Vector3(2,3,4))<.0001f &&
                  Mathf.Abs(bounds.size.x-.08f)<.0001f && Mathf.Abs(bounds.size.z-.04f)<.0001f,
                "Bounds include rotated physical block extents, not an axis-aligned size at the center.");
            check(!registry.TryGetFreshBlocks(.9,out _,out _),"A backwards monotonic clock invalidates availability.");
            registry.ResetContext("new-session",2);
            check(registry.LastSequence==-1 && !registry.TryGetFreshBlocks(0,out _,out _),"Context reset clears blocks, sequence, and source age.");
            report.passed=true;
            Directory.CreateDirectory("Documentation");
            File.WriteAllText("Documentation/TRACKED_BLOCK_TEST_RESULTS.json",JsonUtility.ToJson(report,true));
            Debug.Log("TRACKED_BLOCK_TESTS_PASSED "+report.checks.Count);
        }
    }
}
