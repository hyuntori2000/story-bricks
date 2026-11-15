using System;
using System.Collections.Generic;
using System.IO;
using BrailleSoundDemo.TableDepthTest;
using Meta.XR;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    public static class TableSurfaceVerification
    {
        [Serializable] sealed class Report
        {
            public bool passed;
            public bool syntheticGeometry = true;
            public bool physicalDepthAccuracyVerified = false;
            public List<string> checks = new List<string>();
        }

        static readonly Vector3 Head = new Vector3(0, 1.5f, 0);

        static EnvironmentRaycastHit[] Patch(float height = .75f)
        {
            var hits = new EnvironmentRaycastHit[9];
            for (int z = 0; z < 3; ++z)
                for (int x = 0; x < 3; ++x)
                    hits[z*3+x] = new EnvironmentRaycastHit {
                        status = EnvironmentRaycastHitStatus.Hit,
                        point = new Vector3((x-1)*.05f, height, .5f + (z-1)*.05f),
                        normal = Vector3.up, normalConfidence = 1
                    };
            return hits;
        }

        static bool Estimate(EnvironmentRaycastHit[] hits, out TableSurfaceEstimator.Estimate estimate)
            => TableSurfaceEstimator.TryEstimate(hits, 4, Head, out estimate, out _);

        public static void Run()
        {
            var report = new Report();
            Action<bool,string> check = (ok,message) => {
                if (!ok) throw new InvalidOperationException("Table surface: " + message);
                report.checks.Add(message);
            };

            var hits = Patch();
            check(Estimate(hits,out var result) && Mathf.Abs(result.Height-.75f)<.00001f && result.InlierCount==9,
                "A horizontal nine-hit patch yields the median height.");
            check(Mathf.Abs(result.VerticalGap-.75f)<.00001f && Mathf.Abs(result.HeadDistance-Mathf.Sqrt(.75f*.75f+.5f*.5f))<.00001f,
                "Straight-line distance differs correctly from vertical headset clearance.");
            check(Vector3.Distance(result.Center,new Vector3(0,.75f,.5f))<.00001f && result.HeightSpread==0,
                "Representative point uses the central ray and reports within-patch height spread.");
            check(!TableSurfaceEstimator.TryEstimate(null,0,Head,out _,out _), "Missing input cannot produce a surface.");
            check(!TableSurfaceEstimator.TryEstimate(hits,99,Head,out _,out _), "Invalid center index is rejected.");
            check(!TableSurfaceEstimator.TryEstimate(hits,4,new Vector3(float.NaN,1,0),out _,out _), "Non-finite headset pose is rejected.");

            hits = Patch(); hits[4].status = EnvironmentRaycastHitStatus.NotReady;
            check(!Estimate(hits,out _), "A not-ready center ray is not replaced by neighboring hits.");
            hits = Patch(); hits[4].point.x = float.PositiveInfinity;
            check(!Estimate(hits,out _), "A non-finite center point is rejected.");
            hits = Patch(); hits[4].normal = Vector3.zero;
            check(!Estimate(hits,out _), "Native Hit with invalid orientation cannot qualify as horizontal.");
            hits = Patch(); hits[4].normalConfidence = 0;
            check(!Estimate(hits,out _), "Low-confidence center normal is rejected.");
            hits = Patch(); hits[4].normalConfidence = float.NaN;
            check(!Estimate(hits,out _), "Non-finite confidence is rejected.");
            hits = Patch(); for(int i=0;i<hits.Length;i++) hits[i].normal=Vector3.right;
            check(!Estimate(hits,out _), "A vertical wall is rejected.");
            hits = Patch(); for(int i=0;i<hits.Length;i++) hits[i].normal=Vector3.down;
            check(!Estimate(hits,out _), "Down-facing geometry is not treated as a tabletop.");

            hits = Patch(); hits[0].point.y = 0;
            check(Estimate(hits,out result) && result.InlierCount==8 && Mathf.Abs(result.Height-.75f)<.00001f,
                "One distant height outlier is excluded without shifting the median.");
            hits = Patch(); hits[4].point.y = 0;
            check(!Estimate(hits,out _), "A center ray on a different level rejects the patch.");
            hits = Patch(); for(int i=0;i<3;i++) hits[i].point.y=0;
            check(!Estimate(hits,out _), "A table-edge mixture without enough height agreement is rejected.");
            hits = Patch(); for(int i=0;i<3;i++) hits[i].status=EnvironmentRaycastHitStatus.NoHit;
            check(!Estimate(hits,out _), "Misses count against support instead of disappearing from the denominator.");
            hits = Patch(); for(int i=0;i<hits.Length;i++) hits[i].point.y += (i%3-1)*.024f;
            check(!Estimate(hits,out _), "An uneven patch exceeding the height range is rejected.");
            hits = Patch(); for(int i=0;i<hits.Length;i++) hits[i].normal = new Vector3(i%2==0?.5f:-.5f,1,0).normalized;
            check(!Estimate(hits,out _), "Locally conflicting normals reject the surface estimate.");
            hits = Patch(); for(int i=0;i<hits.Length;i++) hits[i].point.x=0;
            check(!Estimate(hits,out _), "Collinear hits do not establish a two-dimensional surface.");
            hits = Patch(); for(int i=0;i<hits.Length;i++) hits[i].point=new Vector3(0,.75f,.5f);
            check(!Estimate(hits,out _), "Repeated identical hits are not surface evidence.");
            hits = Patch(); hits[0].point.x=-1;
            check(!Estimate(hits,out _), "An excessive footprint is rejected.");
            hits = Patch(1.49f);
            check(!Estimate(hits,out _), "A surface at headset height is not accepted as a work surface.");
            hits = Patch(); for(int i=0;i<hits.Length;i++) hits[i].point.z+=4;
            check(!Estimate(hits,out _), "Implausibly distant hits are rejected.");
            check(!TableSurfaceEstimator.TryEstimate(Patch(),4,Head,out _,out _,new TableSurfaceEstimator.Settings { MinimumInlierFraction=float.NaN }),
                "Invalid settings fail closed.");

            hits=Patch();
            check(TableSurfaceEstimator.TryValidateReference(hits,.75f,out var accepted,out var range,out _) && accepted==9 && range==0,
                "Current unobstructed depth validates a locked horizontal reference.");
            hits[4].point.y=1.0f;
            check(TableSurfaceEstimator.TryValidateReference(hits,.75f,out accepted,out _,out _) && accepted==8,
                "A raised hand on the central ray does not move or invalidate a sufficiently supported reference.");
            hits=Patch(); for(int i=0;i<4;i++) hits[i].status=EnvironmentRaycastHitStatus.NoHit;
            check(TableSurfaceEstimator.TryValidateReference(hits,.75f,out accepted,out _,out _) && accepted==5,
                "Five matching hits out of nine can validate an occluded reference patch.");
            hits[4].status=EnvironmentRaycastHitStatus.NoHit;
            check(!TableSurfaceEstimator.TryValidateReference(hits,.75f,out accepted,out _,out _) && accepted==4,
                "Four matching hits are insufficient and cannot refresh reference validity.");
            hits=Patch(1.0f);
            check(!TableSurfaceEstimator.TryValidateReference(hits,.75f,out accepted,out _,out _) && accepted==0,
                "A coherent raised surface cannot silently replace the locked tabletop height.");
            hits=Patch(); for(int i=0;i<5;i++) hits[i].normal=Vector3.zero;
            check(!TableSurfaceEstimator.TryValidateReference(hits,.75f,out _,out _,out _),
                "Hits without valid normals do not count as reference support.");
            hits=Patch(); hits[0].point.y=.74f; hits[8].point.y=.765f;
            check(TableSurfaceEstimator.TryValidateReference(hits,.75f,out accepted,out range,out _) && accepted==9 && Mathf.Abs(range-.025f)<.00001f,
                "Reference validation reports the actual range of matching heights.");
            check(!TableSurfaceEstimator.TryValidateReference(hits,float.NaN,out _,out _,out _),
                "A non-finite locked reference is rejected.");
            hits=Patch(); for(int i=0;i<hits.Length;i++) hits[i].status=EnvironmentRaycastHitStatus.NotReady;
            check(!TableSurfaceEstimator.TryValidateReference(hits,.75f,out accepted,out _,out _) && accepted==0,
                "Not-ready depth cannot refresh the locked reference.");
            hits=Patch(); for(int i=0;i<hits.Length;i++) hits[i].point=new Vector3(0,.75f,.5f);
            check(!TableSurfaceEstimator.TryValidateReference(hits,.75f,out accepted,out _,out _) && accepted==9,
                "Nine coincident reference hits do not establish a surface footprint.");
            hits=Patch(); for(int i=0;i<hits.Length;i++) hits[i].point.x=0;
            check(!TableSurfaceEstimator.TryValidateReference(hits,.75f,out _,out _,out _),
                "Collinear reference hits cannot refresh the locked surface.");
            hits=Patch();
            for(int i=0;i<hits.Length;i++)
            {
                if(i<5) hits[i].point=new Vector3(.14f,.75f,.4f+i*.05f);
                else hits[i].status=EnvironmentRaycastHitStatus.NoHit;
            }
            check(!TableSurfaceEstimator.TryValidateReference(hits,.75f,out accepted,out _,out _) && accepted==5,
                "Five matching points along only one edge are insufficient surface evidence.");
            hits=Patch(); for(int i=0;i<hits.Length;i++) hits[i].point=new Vector3(i*.02f,.75f,.5f+i*.02f);
            check(!TableSurfaceEstimator.TryValidateReference(hits,.75f,out _,out _,out _),
                "A diagonal line is rejected even when both X and Z ranges are nonzero.");
            hits=Patch(); hits[0].point.x=-1;
            check(!TableSurfaceEstimator.TryValidateReference(hits,.75f,out _,out _,out _),
                "An excessive reference footprint cannot refresh the local surface patch.");

            check(TableReferenceContinuity.CanUseHandHistory(true,true,true,false,false,2),
                "Depth history survives a brief outage through the two-second boundary.");
            check(!TableReferenceContinuity.CanUseHandHistory(true,true,true,false,false,2.001),
                "Expired depth cannot preserve hand history.");
            foreach (double age in new[] { -1d, double.NaN, double.PositiveInfinity })
                check(!TableReferenceContinuity.CanUseHandHistory(true,true,true,false,false,age),
                    "Invalid depth age rejects held history: " + age);
            check(!TableReferenceContinuity.CanUseHandHistory(false,true,true,false,false,.1),
                "Legacy mode never holds stale depth for hand history.");
            check(TableReferenceContinuity.CanUseHandHistory(false,true,true,true,false,0),
                "Fresh depth remains usable in legacy mode.");
            check(!TableReferenceContinuity.CanUseHandHistory(true,false,true,true,false,0),
                "Reset reference cannot retain history even with a fresh flag.");
            check(!TableReferenceContinuity.CanUseHandHistory(true,true,false,true,false,0),
                "Lost head tracking invalidates history.");
            check(!TableReferenceContinuity.CanUseHandHistory(true,true,true,true,true,0),
                "A contradictory plane invalidates history even with a fresh flag.");
            check(TableReferenceContinuity.CanWaitForFreshDepth(true,true,true,.8) &&
                !TableReferenceContinuity.CanWaitForFreshDepth(true,true,true,.801),
                "Pending lift wait is bounded at eight tenths of a second.");
            foreach (double age in new[] { -1d, double.NaN, double.PositiveInfinity })
                check(!TableReferenceContinuity.CanWaitForFreshDepth(true,true,true,age),
                    "Invalid pending age cannot extend a lift: " + age);
            check(!TableReferenceContinuity.CanWaitForFreshDepth(false,true,true,.1) &&
                !TableReferenceContinuity.CanWaitForFreshDepth(true,false,true,.1) &&
                !TableReferenceContinuity.CanWaitForFreshDepth(true,true,false,.1),
                "Waiting requires enabled continuity, usable history and current hands.");
            var reference = new Vector3(0,.75f,.5f);
            check(TableReferenceContinuity.TryViewPatchCenter(Head,new Vector3(.2f,-.75f,.5f),reference,out var center) &&
                Mathf.Abs(center.y-reference.y)<.00001f && Mathf.Abs(center.x-.2f)<.00001f && reference.x==0,
                "Adaptive patch follows the view while retaining locked height and reference.");
            check(!TableReferenceContinuity.TryViewPatchCenter(Head,Vector3.forward,reference,out center) && center==reference,
                "A grazing view falls back to the original reference patch.");
            check(!TableReferenceContinuity.TryViewPatchCenter(Head,new Vector3(2,-.75f,.5f),reference,out _),
                "Adaptive patch cannot move beyond one meter of the locked reference.");
            check(!TableReferenceContinuity.TryViewPatchCenter(Head,new Vector3(float.NaN,-1,0),reference,out _) &&
                !TableReferenceContinuity.TryViewPatchCenter(reference,Vector3.down,reference,out _),
                "Nonfinite rays and a head at table height cannot move the patch.");
            check(TableReferenceContinuity.HasContradictoryPlane(Patch(1),Head,.75f) &&
                !TableReferenceContinuity.HasContradictoryPlane(Patch(),Head,.75f),
                "Strong raised-plane evidence contradicts the reference; matching depth does not.");
            hits=Patch(); hits[4].point.y=1;
            check(!TableReferenceContinuity.HasContradictoryPlane(hits,Head,.75f),
                "One raised central hit does not constitute a contradictory plane.");
            hits=Patch(1); for(int i=0;i<5;i++) hits[i].status=EnvironmentRaycastHitStatus.NoHit;
            check(!TableReferenceContinuity.HasContradictoryPlane(hits,Head,.75f),
                "Sparse raised hits cannot establish a contradictory plane.");

            report.passed=true;
            Directory.CreateDirectory("Documentation");
            File.WriteAllText("Documentation/TABLE_SURFACE_TEST_RESULTS.json",JsonUtility.ToJson(report,true));
            Debug.Log("TABLE_SURFACE_TESTS_PASSED " + report.checks.Count);
        }
    }
}
