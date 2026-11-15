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
            Material ownedPlaneMaterial=null;
            Mesh ownedPlaneMesh=null;
            Texture2D ownedImage=null;
            AudioClip fixtureSound=null;
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
                check(!box.Accepted && !box.Captured && box.CapturePlane==null && marker.GetComponents<Collider>().Length==0,
                    "A newly selected target is pending and adds no physical collision geometry.");
                Vector3 frozen=box.VisualPosition;Color pending=line.sharedMaterial.color;
                box.MarkCaptured();
                double capturedAt=box.CapturedAt;
                var plane=box.CapturePlane;
                ownedPlaneMesh=plane.GetComponent<MeshFilter>().sharedMesh;
                ownedPlaneMaterial=plane.GetComponent<MeshRenderer>().sharedMaterial;
                check(box.Captured && !box.Accepted && box.OutlineVisible && line.sharedMaterial.color!=pending &&
                    box.VisualPosition==frozen && plane!=null && marker.GetComponentsInChildren<Collider>().Length==0,
                    "Photo capture turns the current outline blue and creates one non-interactive plane before recognition.");
                var vertices=ownedPlaneMesh.vertices;
                Vector3 bottom=(vertices[0]+vertices[1])*.5f, rise=vertices[2]-vertices[0];
                check(bottom==Vector3.zero && Vector3.Distance(plane.TransformPoint(bottom),box.VisualPosition)<.00001f &&
                    Mathf.Abs(Vector3.Distance(vertices[0],vertices[1])-.20f)<.00001f &&
                    Mathf.Abs(rise.magnitude-.12f)<.00001f &&
                    Mathf.Abs(Vector3.Angle(rise,Vector3.forward)-65)<.001f &&
                    Vector3.Dot(ownedPlaneMesh.normals[0],Vector3.back)>0,
                    "The neutral twenty by twelve centimeter plane starts at the box center, rises sixty-five degrees, and faces local minus Z.");
                box.MarkCaptured();
                check(box.CapturedAt==capturedAt && box.CapturePlane==plane &&
                    marker.GetComponentsInChildren<MeshRenderer>().Length==1,
                    "Repeated capture acknowledgement does not duplicate the plane or restart its outline timer.");
                box.RefreshCaptureVisuals(capturedAt+1.999);
                check(box.OutlineVisible,"The blue outline remains visible immediately before its two-second deadline.");
                box.RefreshCaptureVisuals(capturedAt+2);
                check(!box.OutlineVisible && plane.gameObject.activeSelf && plane.GetComponent<MeshRenderer>().enabled &&
                    box.VisualPosition==frozen,
                    "At two seconds only the outline hides; the plane and fixed anchor remain.");
                box.MarkAccepted();box.MarkAccepted();
                var acceptedCorners=new Vector3[4];line.GetPositions(acceptedCorners);
                check(Mathf.Abs(Vector3.Distance(acceptedCorners[0],acceptedCorners[1])-.04f)<.00001f &&
                    Mathf.Abs(Vector3.Distance(acceptedCorners[1],acceptedCorners[2])-.025f)<.00001f,
                    "Accepted words leave a four by two-and-a-half centimeter sound marker.");
                check(box.Accepted && box.VisualPosition==frozen && box.CenterWorld==center &&
                    marker.GetComponents<LineRenderer>().Length==1 && line.sharedMaterial==ownedMaterial &&
                    line.sharedMaterial.color!=pending && !box.OutlineVisible &&
                    box.CapturedAt==capturedAt && box.CapturePlane==plane,
                    "Acceptance changes appearance while preserving the exact target pose, renderer and material.");
                Vector3 replacement=new Vector3(-.2f,.8f,.1f);
                box.Configure(replacement);
                check(!box.Accepted && box.CenterWorld==replacement &&
                    Vector3.Distance(box.VisualPosition,replacement+Vector3.up*.01f)<.00001f &&
                    marker.GetComponents<LineRenderer>().Length==1 && line.sharedMaterial==ownedMaterial &&
                    !box.Captured && box.CapturedAt<0 && box.CapturePlane==null && box.OutlineVisible &&
                    line.sharedMaterial.color==pending && plane==null && ownedPlaneMesh==null && ownedPlaneMaterial==null,
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
                var legacyRotation=Quaternion.Euler(12,27,4);
                box.ConfigureAtPose(new Pose(center,legacyRotation));
                check(Vector3.Distance(box.VisualPosition,center+legacyRotation*Vector3.up*PinchWordBox.VisualLiftMeters)<.00001f &&
                    Quaternion.Angle(box.RotationWorld,legacyRotation)<.001f,
                    "Fallback recognition presentation preserves its existing plane pose and sound-anchor position.");
                box.MarkAccepted();
                ownedPlaneMesh=box.CapturePlane.GetComponent<MeshFilter>().sharedMesh;
                ownedPlaneMaterial=box.CapturePlane.GetComponent<MeshRenderer>().sharedMaterial;
                box.RefreshCaptureVisuals(box.CapturedAt+2);
                line.SetPositions(acceptedCorners);line.startWidth*=1.1f;line.endWidth*=1.1f;
                box.MarkAccepted();
                check(!box.OutlineVisible && box.CapturePlane.GetComponent<MeshRenderer>().enabled,
                    "Small-marker geometry and width animation cannot revive a hidden outline or hide the plane.");
                double imageCapturedAt=box.CapturedAt;
                Vector3 imageAnchor=box.VisualPosition;
                ownedImage=new Texture2D(4,4,TextureFormat.RGBA32,false);
                ownedImage.SetPixel(0,0,new Color(1,0,0,0));ownedImage.Apply();
                byte[] png=ownedImage.EncodeToPNG();
                check(WordImagePipeline.TryReadPngDimensions(png,out int pngWidth,out int pngHeight) &&
                    pngWidth==4 && pngHeight==4 && !WordImagePipeline.TryReadPngDimensions(null,out _,out _),
                    "The download validator accepts PNG dimensions and rejects missing data before texture decoding.");
                png[16]=1;
                check(!WordImagePipeline.TryReadPngDimensions(png,out _,out _) &&
                    WordImagePipeline.IsImagePath("/word-images/0123456789abcdef0123456789abcdef.png") &&
                    !WordImagePipeline.IsImagePath("https://example.com/image.png") &&
                    !WordImagePipeline.IsImagePath("/word-images/../../secrets.png"),
                    "Oversize PNG dimensions and paths outside the local generated-image route are rejected.");
                check(box.SetWordImage(ownedImage),"An accepted box accepts its generated texture.");
                ownedPlaneMaterial=box.CapturePlane.GetComponent<MeshRenderer>().sharedMaterial;
                vertices=ownedPlaneMesh.vertices;rise=vertices[2]-vertices[0];
                check(Mathf.Abs(Vector3.Distance(vertices[0],vertices[1])-.12f)<.00001f &&
                    Mathf.Abs(rise.magnitude-.12f)<.00001f && (vertices[0]+vertices[1])*.5f==Vector3.zero &&
                    Mathf.Abs(Vector3.Angle(rise,Vector3.forward)-65)<.001f &&
                    ownedPlaneMaterial.shader.name=="BrailleSoundDemo/Word Image Alpha" &&
                    ownedPlaneMaterial.GetTag("RenderType",false)=="Transparent" &&
                    ownedPlaneMaterial.mainTexture==ownedImage && ownedImage.GetPixel(0,0).a==0 &&
                    !box.OutlineVisible && box.CapturedAt==imageCapturedAt && box.VisualPosition==imageAnchor,
                    "A square alpha image fits twelve by twelve centimeters on the same sixty-five-degree plane without reviving its outline.");
                var firstImage=ownedImage;
                var firstImageMaterial=ownedPlaneMaterial;
                ownedImage=new Texture2D(4,2,TextureFormat.RGBA32,false);
                check(box.SetWordImage(ownedImage) && box.SetWordImage(ownedImage),
                    "Repeated display of the same image is harmless.");
                ownedPlaneMaterial=box.CapturePlane.GetComponent<MeshRenderer>().sharedMaterial;
                vertices=ownedPlaneMesh.vertices;
                check(Mathf.Abs(Vector3.Distance(vertices[0],vertices[1])-.20f)<.00001f &&
                    Mathf.Abs((vertices[2]-vertices[0]).magnitude-.10f)<.00001f &&
                    firstImage==null && firstImageMaterial==null && marker.GetComponentsInChildren<Collider>().Length==0,
                    "A wider image fits without stretching, releases replaced assets, and creates no collider.");

                var sound=marker.AddComponent<WordSoundAttachment>();
                fixtureSound=AudioClip.Create("Existing generated sound fixture",64,1,16000,false);
                sound.Configure("fixture-image-first","MOON");
                sound.MarkImageDisplayed();
                check(!sound.TryTakeImageReplay(out _),"An image arriving first cannot fabricate or play a missing sound.");
                sound.Attach(fixtureSound);sound.MarkPrimaryStarted(fixtureSound);
                check(!sound.TryTakeImageReplay(out _),"The image cannot interrupt the original word effect.");
                sound.MarkPrimaryCompleted(fixtureSound);
                check(sound.TryTakeImageReplay(out var replay) && replay==fixtureSound && sound.Source.clip==fixtureSound,
                    "An early image reuses the existing clip once its primary playback completes.");
                sound.MarkImageDisplayed();sound.MarkPrimaryCompleted(fixtureSound);
                check(!sound.TryTakeImageReplay(out _),"Repeated image and sound completions cannot replay a second time.");
                sound.Configure("fixture-sound-first","MOON");sound.Attach(fixtureSound);
                sound.MarkPrimaryStarted(fixtureSound);sound.MarkPrimaryCompleted(fixtureSound);
                check(!sound.TryTakeImageReplay(out _),"A completed sound waits until its image was actually displayed.");
                sound.MarkImageDisplayed();
                check(sound.TryTakeImageReplay(out replay) && replay==fixtureSound && !sound.TryTakeImageReplay(out _),
                    "A late image also replays the existing effect exactly once.");
                sound.Configure("fixture-reset","MOON");sound.Attach(fixtureSound);sound.MarkPrimaryStarted(fixtureSound);
                sound.CancelImageReplay();sound.MarkPrimaryCompleted(fixtureSound);sound.MarkImageDisplayed();
                check(!sound.ImageReplayPending && !sound.TryTakeImageReplay(out _) && box.VisualPosition==imageAnchor,
                    "Reset or tracking cancellation rejects late image/primary completion while keeping the audio anchor fixed.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if(fixtureSound!=null) UnityEngine.Object.DestroyImmediate(fixtureSound);
            }
            check(ownedMaterial==null,"Destroying the marker releases its owned outline material in edit mode.");
            check(ownedPlaneMesh==null && ownedPlaneMaterial==null,
                "Destroying the marker also releases its owned plane mesh and material.");
            check(ownedImage==null,"Destroying the marker releases the generated texture it owns.");
            report.passed=true;
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/pinch-word-box-verification.json",JsonUtility.ToJson(report,true));
            Debug.Log("PINCH_WORD_BOX_VERIFICATION_OK "+report.checks.Count);
        }
    }
}
