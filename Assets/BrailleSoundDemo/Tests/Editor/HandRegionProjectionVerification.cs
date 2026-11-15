using System;
using System.Collections.Generic;
using System.IO;
using BrailleSoundDemo.CameraTest;
using BrailleSoundDemo.TableDepthTest;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    public static class HandRegionProjectionVerification
    {
        [Serializable] sealed class Report
        {
            public bool passed;
            public bool syntheticGeometry=true;
            public bool physicalRegionAlignmentVerified=false;
            public List<string> checks=new List<string>();
        }

        static QuestFrameCapture.CaptureMetadata Fixture()
        {
            return new QuestFrameCapture.CaptureMetadata {
                imageWidth=640,imageHeight=480,cameraPositionWorld=new Vector3(0,1,0),
                cameraRotationWorld=Quaternion.Euler(90,0,0),
                intrinsics=new QuestFrameCapture.IntrinsicsMetadata {
                    focalLength=new Vector2(320,320),principalPoint=new Vector2(320,240),
                    sensorResolution=new QuestFrameCapture.PixelSize {x=640,y=480},
                    sensorCrop=new QuestFrameCapture.SensorCropMetadata {x=0,y=0,width=640,height=480}
                }
            };
        }

        static bool RoundTrip(QuestFrameCapture.CaptureMetadata metadata,Vector2 pixel)
        {
            return CapturePlaneProjection.TryPixelToRay(metadata,pixel,out var ray) &&
                HandRegionProjection.TryWorldToPixel(metadata,ray.GetPoint(1.3f),out var recovered) &&
                Vector2.Distance(pixel,recovered)<.001f;
        }

        // Recorded Quest metadata, not synthetic recognition ground truth. Python independently
        // replayed this camera/box pose to establish the expected reading quad below.
        static QuestFrameCapture.CaptureMetadata RecordedPinchFixture()
        {
            return new QuestFrameCapture.CaptureMetadata {
                frameId="c5633673ced640eb813b12007c967be8",imageWidth=1280,imageHeight=960,
                cameraPositionWorld=new Vector3(-.0559406355f,1.1454713345f,.1006721556f),
                cameraRotationWorld=new Quaternion(-.3765529394f,-.0609936863f,.0140639897f,-.9242780209f),
                intrinsics=new QuestFrameCapture.IntrinsicsMetadata {
                    focalLength=new Vector2(1154.359130859375f,1154.359130859375f),
                    principalPoint=new Vector2(866.7760009765625f,865.9873046875f),
                    sensorResolution=new QuestFrameCapture.PixelSize {x=1728,y=1728},
                    sensorCrop=new QuestFrameCapture.SensorCropMetadata {x=0,y=215.9999542236328f,width=1728,height=1296.0001220703125f}
                },
                board=new QuestFrameCapture.BoardMetadata {
                    position=new Vector3(-.0055030435f,.8472619057f,.2081162632f),
                    rotation=new Quaternion(0,.0639518648f,0,.9979529977f),width=.20f,height=.12f,blockHeight=0
                }
            };
        }

        public static void Run()
        {
            var report=new Report();
            Action<bool,string> check=(ok,message)=>{
                if(!ok) throw new InvalidOperationException("Hand region projection: "+message);
                report.checks.Add(message);
            };
            var m=Fixture();
            check(HandRegionProjection.TryWorldToPixel(m,Vector3.zero,out var pixel) && Vector2.Distance(pixel,new Vector2(320,240))<.001f,
                "A point below a downward-facing captured camera maps to image center.");
            check(RoundTrip(m,new Vector2(64,48)) && RoundTrip(m,new Vector2(320,240)) && RoundTrip(m,new Vector2(576,432)),
                "Pixel rays invert correctly at asymmetric locations without a second Y flip.");
            m.cameraPositionWorld=new Vector3(-.4f,1.5f,2); m.cameraRotationWorld=Quaternion.Euler(65,125,12);
            check(RoundTrip(m,new Vector2(123,379)),"Camera world translation and rotation are applied from frozen capture metadata.");
            m=Fixture();m.intrinsics.principalPoint=new Vector2(300,210);
            check(RoundTrip(m,new Vector2(100,370)),"Off-center native sensor principal points invert correctly.");
            m.imageHeight=360;m.intrinsics.sensorCrop=new QuestFrameCapture.SensorCropMetadata {x=0,y=60,width=640,height=360};
            check(RoundTrip(m,new Vector2(500,77)),"Centered 16:9 sensor crops invert without changing sensor calibration.");
            m.intrinsics.sensorCrop=null;
            check(RoundTrip(m,new Vector2(500,77)),"Legacy metadata derives the same crop when an explicit crop is absent.");

            m=Fixture();
            check(HandRegionProjection.TryBuildRegion(m,new Vector3(0,.06f,0),0,out var region,out _) &&
                region.x==261 && region.y==194 && region.width==118 && region.height==92 && !region.clipped,
                "A tabletop rectangle produces an outward-rounded pixel crop with twenty-pixel padding.");
            check(region.centerWorld.y==0 && region.physicalWidthMeters==.24f && region.physicalDepthMeters==.16f,
                "Historical fingertip clearance is projected onto the stored tabletop, not used as object depth.");
            check(string.IsNullOrEmpty(region.readingOrientation) && region.readingQuad==null,
                "The legacy hand region does not invent a frozen reading direction.");
            check(HandRegionProjection.TryBuildRegion(m,Vector3.zero,0,Quaternion.identity,out var targetZero,out _) &&
                targetZero.physicalWidthMeters==.20f && targetZero.physicalDepthMeters==.12f &&
                targetZero.width<region.width && targetZero.height<region.height,
                "Explicit pinch projection uses the same twenty by twelve centimeter footprint as the visible box.");
            var expectedReadingQuad=new[] {new Vector2(288,220.8f),new Vector2(352,220.8f),
                new Vector2(352,259.2f),new Vector2(288,259.2f)};
            bool orderedReadingQuad=targetZero.readingOrientation=="labels_toward_viewer" && targetZero.readingQuad?.Length==4;
            for(int i=0;orderedReadingQuad && i<4;i++)
                orderedReadingQuad &= Vector2.Distance(targetZero.readingQuad[i],expectedReadingQuad[i])<.001f;
            check(orderedReadingQuad,
                "Reading corners are the unpadded box in top-left, top-right, bottom-right, bottom-left order; labels occupy the near edge.");
            var serializedTarget=JsonUtility.FromJson<QuestFrameCapture.RecognitionRegionMetadata>(JsonUtility.ToJson(targetZero));
            check(serializedTarget.readingOrientation=="labels_toward_viewer" && serializedTarget.readingQuad.Length==4 &&
                Vector2.Distance(serializedTarget.readingQuad[0],expectedReadingQuad[0])<.001f &&
                Vector2.Distance(serializedTarget.readingQuad[2],expectedReadingQuad[2])<.001f,
                "The JSON contract preserves the reading convention and full-image pixel coordinates.");
            check(HandRegionProjection.TryBuildRegion(m,Vector3.zero,0,Quaternion.Euler(0,90,0),out var targetTurn,out _) &&
                Math.Abs(targetTurn.width-targetZero.height)<=2 && Math.Abs(targetTurn.height-targetZero.width)<=2,
                "A ninety-degree frozen target rotation rotates the projected rectangle instead of retaining world axes.");
            var expectedTurnQuad=new[] {new Vector2(339.2f,208),new Vector2(339.2f,272),
                new Vector2(300.8f,272),new Vector2(300.8f,208)};
            bool orderedTurnQuad=targetTurn.readingQuad?.Length==4;
            for(int i=0;orderedTurnQuad && i<4;i++)
                orderedTurnQuad &= Vector2.Distance(targetTurn.readingQuad[i],expectedTurnQuad[i])<.001f;
            check(orderedTurnQuad,
                "Rotating the frozen box retains semantic reading corner order instead of sorting image coordinates.");
            var view=Quaternion.Euler(90,37,0);
            var frozenRotation=HandRegionProjection.TabletopRotation(view);
            Vector3 expectedRight=view*Vector3.right;expectedRight.y=0;expectedRight.Normalize();
            check(Vector3.Dot(frozenRotation*Vector3.right,expectedRight)>.9999f &&
                Vector3.Dot(frozenRotation*Vector3.up,Vector3.up)>.9999f,
                "The tabletop heading remains defined while the camera looks vertically down.");
            var rolled=HandRegionProjection.TabletopRotation(Quaternion.Euler(0,57,90));
            check(Vector3.Dot(rolled*Vector3.up,Vector3.up)>.9999f &&
                Quaternion.Angle(rolled,Quaternion.Euler(0,57,0))<.01f,
                "A vertical view-right axis falls back to the horizontal forward heading.");
            check(HandRegionProjection.TryBuildRegion(m,Vector3.zero,0,frozenRotation,out var rotatedRegion,out _),
                "A view-aligned frozen target produces a valid camera crop.");
            bool containsCorners=true;
            for(int z=-1;z<=1;z+=2)
                for(int x=-1;x<=1;x+=2)
                {
                    var corner=frozenRotation*new Vector3(x*.1f,0,z*.06f);
                    containsCorners &= HandRegionProjection.TryWorldToPixel(m,corner,out var cornerPixel) &&
                        cornerPixel.x>=rotatedRegion.x+HandRegionProjection.PaddingPixels-1 &&
                        cornerPixel.x<=rotatedRegion.x+rotatedRegion.width-HandRegionProjection.PaddingPixels+1 &&
                        cornerPixel.y>=rotatedRegion.y+HandRegionProjection.PaddingPixels-1 &&
                        cornerPixel.y<=rotatedRegion.y+rotatedRegion.height-HandRegionProjection.PaddingPixels+1;
                }
            check(containsCorners,"The crop contains all corners of the same frozen rotated target plus pixel padding.");
            var expectedLocalCorners=new[] {new Vector3(-.1f,0,.06f),new Vector3(.1f,0,.06f),
                new Vector3(.1f,0,-.06f),new Vector3(-.1f,0,-.06f)};
            bool matchesFrozenBox=rotatedRegion.readingQuad?.Length==4;
            for(int i=0;matchesFrozenBox && i<4;i++)
                matchesFrozenBox &= HandRegionProjection.TryWorldToPixel(m,frozenRotation*expectedLocalCorners[i],out var readingCornerPixel) &&
                    Vector2.Distance(rotatedRegion.readingQuad[i],readingCornerPixel)<.001f;
            check(matchesFrozenBox,"Reading geometry uses the exact rotated twenty by twelve centimeter marker without padding.");
            m.cameraPositionWorld+=new Vector3(.1f,0,.05f);
            check(HandRegionProjection.TryBuildRegion(m,Vector3.zero,0,frozenRotation,out var laterRegion,out _) &&
                Vector2.Distance(laterRegion.readingQuad[0],rotatedRegion.readingQuad[0])>10 &&
                Vector2.Distance(targetZero.readingQuad[0],expectedReadingQuad[0])<.001f,
                "A later capture projects its own camera pose without changing earlier frozen reading coordinates.");
            m=Fixture();
            var scaledRotation=new Quaternion(frozenRotation.x*2,frozenRotation.y*2,frozenRotation.z*2,frozenRotation.w*2);
            check(HandRegionProjection.TryBuildRegion(m,Vector3.zero,0,scaledRotation,out var scaledRegion,out _) &&
                scaledRegion.x==rotatedRegion.x && scaledRegion.y==rotatedRegion.y &&
                scaledRegion.width==rotatedRegion.width && scaledRegion.height==rotatedRegion.height,
                "Equivalent non-unit target quaternions normalize to the same frozen pixel region.");
            foreach(var invalidRotation in new[] {new Quaternion(0,0,0,0),new Quaternion(0,float.NaN,0,1),new Quaternion(0,0,float.PositiveInfinity,1)})
            {
                check(!HandRegionProjection.TryBuildRegion(m,Vector3.zero,0,invalidRotation,out var rejectedRegion,out _) && rejectedRegion==null,
                    "An invalid frozen target rotation cannot generate a crop.");
                bool rejected=false;
                try {HandRegionProjection.TabletopRotation(invalidRotation);}
                catch(ArgumentException) {rejected=true;}
                check(rejected,"Invalid view orientation is rejected before a target can be selected.");
            }
            check(HandRegionProjection.TryBuildRegion(m,new Vector3(-.9f,.02f,0),0,out region,out _) &&
                region.clipped && region.x==0 && region.retainedFraction>=.60f,
                "Moderate edge clipping is explicit and clamped to full-image pixel bounds.");
            check(HandRegionProjection.TryBuildRegion(m,new Vector3(-.94f,0,0),0,Quaternion.identity,out var clippedReading,out _) &&
                clippedReading.clipped && clippedReading.x==0 && clippedReading.readingQuad[0].x<0 &&
                clippedReading.readingQuad[3].x<0 &&
                Mathf.Abs(clippedReading.readingQuad[1].x-clippedReading.readingQuad[0].x-64)<.001f,
                "A clipped image ROI retains the complete physical reading quad; clamping cannot distort its homography.");
            check(!HandRegionProjection.TryBuildRegion(m,new Vector3(-.99f,0,0),0,out _,out _),
                "A mostly clipped search region is rejected instead of silently transmitting a misleading crop.");
            check(!HandRegionProjection.TryBuildRegion(m,new Vector3(2,0,0),0,out _,out _),
                "A historical region outside the new camera view is rejected.");
            check(!HandRegionProjection.TryWorldToPixel(m,new Vector3(0,2,0),out _),
                "Points behind the captured camera cannot produce a recognition region.");
            check(!HandRegionProjection.TryWorldToPixel(m,new Vector3(float.NaN,0,0),out _),
                "Non-finite historical positions are rejected.");
            m.cameraRotationWorld=new Quaternion(0,0,0,0);
            check(!HandRegionProjection.TryWorldToPixel(m,Vector3.zero,out _),"Invalid camera rotation is rejected.");
            m=Fixture();m.orientation="rotated-90";
            check(!HandRegionProjection.TryWorldToPixel(m,Vector3.zero,out _),"Unsupported image orientation fails closed.");
            m=Fixture();m.intrinsics.sensorCrop.width=1000;
            check(!HandRegionProjection.TryWorldToPixel(m,Vector3.zero,out _),"A crop extending beyond the sensor is rejected.");
            m=Fixture();m.cameraRotationWorld=Quaternion.identity;
            check(!HandRegionProjection.TryBuildRegion(m,new Vector3(0,1,1),1,out _,out _),
                "An edge-on tabletop is rejected before padding can disguise its zero projected area.");
            m=Fixture();
            check(!HandRegionProjection.TryBuildRegion(m,Vector3.zero,float.NaN,out _,out _),"Invalid stored surface height is rejected.");

            HandRegionProjection.TryBuildRegion(m,Vector3.zero,0,out region,out _);
            check(HandRegionProjection.IsPotentiallyOccluded(m,region,new[]{new Vector3(0,.2f,0)},out _),
                "A current hand joint projected inside the region blocks transmission.");
            check(!HandRegionProjection.IsPotentiallyOccluded(m,region,new[]{new Vector3(.8f,.2f,0)},out _),
                "A joint outside the projected region does not obstruct this search region.");
            check(!HandRegionProjection.IsPotentiallyOccluded(m,region,new[]{new Vector3(0,2,0)},out _),
                "A finite joint behind the captured camera is not projected onto the image.");
            check(HandRegionProjection.IsPotentiallyOccluded(m,region,null,out _) &&
                HandRegionProjection.IsPotentiallyOccluded(m,region,new Vector3[0],out _),
                "Missing or empty supplied joint data fails closed.");
            check(HandRegionProjection.IsPotentiallyOccluded(m,region,new[]{new Vector3(float.NaN,0,0)},out _),
                "Invalid supplied joint data fails closed.");
            CapturePlaneProjection.TryPixelToRay(m,new Vector2(region.x-5,240),out var marginRay);
            check(HandRegionProjection.IsPotentiallyOccluded(m,region,new[]{marginRay.GetPoint(.8f)},out _),
                "A ten-pixel margin catches a nearby joint just outside the crop edge.");
            CapturePlaneProjection.TryPixelToRay(m,new Vector2(region.x-15,240),out var clearRay);
            check(!HandRegionProjection.IsPotentiallyOccluded(m,region,new[]{clearRay.GetPoint(.8f)},out _),
                "A joint beyond the extra margin remains clear.");
            check(HandRegionProjection.IsPotentiallyOccluded(null,region,new[]{Vector3.zero},out _),
                "Invalid capture metadata cannot claim hand clearance.");

            var recorded=RecordedPinchFixture();
            check(HandRegionProjection.TryBuildRegion(recorded,recorded.board.position,recorded.board.position.y,
                recorded.board.rotation,out var recordedRegion,out _) && recordedRegion.clipped &&
                recordedRegion.x==380 && recordedRegion.y==698 && recordedRegion.width==734 && recordedRegion.height==262,
                "The recorded JOY capture keeps its original ROI and clipping behavior when reading metadata is added.");
            var expectedRecordedQuad=new[] {new Vector2(464.2921022f,718.1179639f),new Vector2(978.1740759f,725.4457632f),
                new Vector2(1093.0943335f,1099.4510801f),new Vector2(400.2223904f,1092.8575080f)};
            bool matchesRecordedReplay=recordedRegion.readingQuad?.Length==4;
            for(int i=0;matchesRecordedReplay && i<4;i++)
                matchesRecordedReplay &= Vector2.Distance(recordedRegion.readingQuad[i],expectedRecordedQuad[i])<.02f;
            check(matchesRecordedReplay && recordedRegion.readingQuad[2].y>recorded.imageHeight &&
                recordedRegion.readingQuad[3].y>recorded.imageHeight,
                "Unity reading-corner order matches the independent Python replay within .02 pixels, including off-image physical corners.");

            report.passed=true;
            Directory.CreateDirectory("Documentation");
            File.WriteAllText("Documentation/HAND_REGION_PROJECTION_TEST_RESULTS.json",JsonUtility.ToJson(report,true));
            Debug.Log("HAND_REGION_PROJECTION_TESTS_PASSED "+report.checks.Count);
        }
    }
}
