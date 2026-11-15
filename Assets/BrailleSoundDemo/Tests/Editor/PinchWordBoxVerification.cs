using System;
using System.Collections.Generic;
using System.IO;
using BrailleSoundDemo.PhysicalStory;
using BrailleSoundDemo.TableDepthTest;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    public static class PinchWordBoxVerification
    {
        [Serializable] sealed class Report
        {
            public bool passed;
            public bool physicalAlignmentVerified=false;
            public List<string> checks=new List<string>();
        }

        public static void Run()
        {
            var report=new Report();
            Action<bool,string> check=(ok,message)=>{
                if(!ok) throw new InvalidOperationException("Pinch word box: "+message);
                report.checks.Add(message);
            };
            var root=new GameObject("Temporary pinch box verification root");
            var marker=new GameObject("Temporary pinch word box");
            marker.transform.SetParent(root.transform,false);
            Material ownedMaterial=null;
            try
            {
                var box=marker.AddComponent<PinchWordBox>();
                Vector3 center=new Vector3(.31f,.76f,-.42f);
                box.Configure(center);
                var line=marker.GetComponent<LineRenderer>();
                ownedMaterial=line.sharedMaterial;
                check(box.CenterWorld==center && Vector3.Distance(box.VisualPosition,new Vector3(.31f,.77f,-.42f))<.00001f,
                    "Pinch table coordinates are preserved with only a one-centimeter visual lift.");
                check(marker.transform.rotation==Quaternion.identity && marker.transform.localScale==Vector3.one &&
                    marker.transform.parent==root.transform,
                    "The pending box uses world-aligned axes and unit local scale under its stable parent.");
                check(line!=null && line.enabled && !line.useWorldSpace && line.loop && line.positionCount==4,
                    "The pending target has one closed four-corner local-space outline immediately.");
                var corners=new Vector3[4];line.GetPositions(corners);
                check(Vector3.Distance(corners[0],new Vector3(-.10f,0,-.06f))<.00001f &&
                    Vector3.Distance(corners[2],new Vector3(.10f,0,.06f))<.00001f &&
                    Array.TrueForAll(corners,p=>p.y==0),
                    "The outline covers twenty by twelve centimeters on the horizontal plane.");
                check(!box.Accepted && marker.GetComponents<Collider>().Length==0,
                    "A newly selected target is pending and adds no physical collision geometry.");
                Vector3 frozen=box.VisualPosition;Color pending=line.sharedMaterial.color;
                box.MarkAccepted();box.MarkAccepted();
                var acceptedCorners=new Vector3[4];line.GetPositions(acceptedCorners);
                check(Mathf.Abs(Vector3.Distance(acceptedCorners[0],acceptedCorners[1])-.04f)<.00001f &&
                    Mathf.Abs(Vector3.Distance(acceptedCorners[1],acceptedCorners[2])-.025f)<.00001f,
                    "Accepted words leave a four by two-and-a-half centimeter sound marker.");
                check(box.Accepted && box.VisualPosition==frozen && box.CenterWorld==center &&
                    marker.GetComponents<LineRenderer>().Length==1 && line.sharedMaterial==ownedMaterial &&
                    line.sharedMaterial.color!=pending,
                    "Acceptance changes appearance while preserving the exact target pose, renderer and material.");
                Vector3 replacement=new Vector3(-.2f,.8f,.1f);
                box.Configure(replacement);
                check(!box.Accepted && box.CenterWorld==replacement &&
                    Vector3.Distance(box.VisualPosition,replacement+Vector3.up*.01f)<.00001f &&
                    marker.GetComponents<LineRenderer>().Length==1 && line.sharedMaterial==ownedMaterial,
                    "Explicit reconfiguration replaces the pending target without duplicate renderers or materials.");
                foreach(float invalid in new[] {float.NaN,float.PositiveInfinity,float.NegativeInfinity})
                {
                    bool rejected=false;
                    try {box.Configure(new Vector3(invalid,.8f,0));}
                    catch(ArgumentException) {rejected=true;}
                    check(rejected && box.CenterWorld==replacement &&
                        Vector3.Distance(box.VisualPosition,replacement+Vector3.up*.01f)<.00001f,
                        "Nonfinite input is rejected without corrupting the existing marker: "+invalid);
                }
                var view=Quaternion.Euler(89.9f,37,0);
                var rotation=HandRegionProjection.TabletopRotation(view);
                box.Configure(replacement,rotation);
                Vector3 projectedRight=view*Vector3.right;projectedRight.y=0;projectedRight.Normalize();
                check(Vector3.Dot(marker.transform.right,projectedRight)>.9999f &&
                    Vector3.Dot(marker.transform.up,Vector3.up)>.9999f,
                    "The selected box's width aligns to the view's horizontal right axis even when looking almost straight down.");
                Vector3 worldEdge=marker.transform.TransformPoint(corners[1])-marker.transform.TransformPoint(corners[0]);
                check(Mathf.Abs(worldEdge.magnitude-HandRegionProjection.TargetWidthMeters)<.00001f &&
                    Vector3.Dot(worldEdge.normalized,projectedRight)>.9999f &&
                    PinchWordBox.DepthMeters==HandRegionProjection.TargetDepthMeters,
                    "The displayed rotated footprint shares the exact target dimensions used for capture projection.");
                var frozenRotation=box.RotationWorld;var frozenPosition=box.VisualPosition;
                box.MarkAccepted();
                check(Quaternion.Angle(marker.transform.rotation,frozenRotation)<.001f && box.VisualPosition==frozenPosition,
                    "Accepting a rotated target preserves its frozen position and orientation.");
                foreach(var invalidRotation in new[] {new Quaternion(0,0,0,0),new Quaternion(float.NaN,0,0,1)})
                {
                    bool rejected=false;
                    try {box.Configure(Vector3.zero,invalidRotation);}
                    catch(ArgumentException) {rejected=true;}
                    check(rejected && box.CenterWorld==replacement &&
                        Quaternion.Angle(box.RotationWorld,frozenRotation)<.001f && box.VisualPosition==frozenPosition,
                        "Invalid rotation is rejected without moving or rotating the existing target.");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
            check(ownedMaterial==null,"Destroying the marker releases its owned outline material in edit mode.");
            report.passed=true;
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/pinch-word-box-verification.json",JsonUtility.ToJson(report,true));
            Debug.Log("PINCH_WORD_BOX_VERIFICATION_OK "+report.checks.Count);
        }
    }
}
