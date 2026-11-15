using System;
using System.Collections.Generic;
using BrailleSoundDemo.CameraTest;
using UnityEngine;

namespace BrailleSoundDemo.TableDepthTest
{
    public static class HandRegionProjection
    {
        public const float RegionWidthMeters = .24f;
        public const float RegionDepthMeters = .16f;
        public const float TargetWidthMeters = .20f;
        public const float TargetDepthMeters = .12f;
        public const int PaddingPixels = 20;

        /// <summary>Freeze the view's horizontal right axis, including when looking straight down.</summary>
        public static Quaternion TabletopRotation(Quaternion viewRotation)
        {
            if(!TryTabletopRotation(viewRotation,out var rotation))
                throw new ArgumentException("Target rotation must be a finite, nonzero quaternion.",nameof(viewRotation));
            return rotation;
        }

        static bool TryTabletopRotation(Quaternion value,out Quaternion rotation)
        {
            rotation=Quaternion.identity;
            float norm=value.x*value.x+value.y*value.y+value.z*value.z+value.w*value.w;
            if(!float.IsFinite(norm) || norm<.000001f) return false;
            var normalized=value.normalized;
            Vector3 right=normalized*Vector3.right;right.y=0;
            if(right.sqrMagnitude<.000001f)
            {
                Vector3 forward=normalized*Vector3.forward;forward.y=0;
                right=Vector3.Cross(Vector3.up,forward);
            }
            if(!Finite(right) || right.sqrMagnitude<.000001f) return false;
            right.Normalize();
            rotation=Quaternion.LookRotation(Vector3.Cross(right,Vector3.up),Vector3.up);
            return true;
        }

        /// <summary>
        /// Conservative current-joint overlap gate using the captured camera viewpoint.
        /// This is not hand segmentation or proof that the exposure was unobstructed:
        /// joints can move after exposure and do not describe every pixel of a hand.
        /// Invalid supplied data returns true so callers do not transmit on uncertainty.
        /// </summary>
        public static bool IsPotentiallyOccluded(QuestFrameCapture.CaptureMetadata metadata,
            QuestFrameCapture.RecognitionRegionMetadata region,IReadOnlyList<Vector3> currentHandPoints,out string reason)
        {
            if(metadata==null || region==null || region.width<=0 || region.height<=0 || region.x<0 || region.y<0 ||
                (float)region.x+region.width>metadata.imageWidth || (float)region.y+region.height>metadata.imageHeight ||
                region.pixelOrigin!="top-left" ||
                !CapturePlaneProjection.TryPixelToRay(metadata,new Vector2(metadata.imageWidth*.5f,metadata.imageHeight*.5f),out _))
            { reason="Cannot validate hand clearance against this camera region"; return true; }
            if(currentHandPoints==null || currentHandPoints.Count==0)
            { reason="Current tracked hand points are unavailable"; return true; }
            const float margin=10;
            Quaternion inverse=Quaternion.Inverse(metadata.cameraRotationWorld.normalized);
            foreach(var point in currentHandPoints)
            {
                if(!Finite(point)) { reason="Current tracked hand points are invalid"; return true; }
                Vector3 local=inverse*(point-metadata.cameraPositionWorld);
                if(!Finite(local)) { reason="Current hand projection is invalid"; return true; }
                if(local.z<=0) continue;
                if(!TryWorldToPixel(metadata,point,out var pixel))
                { reason="Current hand point cannot be projected reliably"; return true; }
                if(pixel.x>=region.x-margin && pixel.x<=(float)region.x+region.width+margin &&
                    pixel.y>=region.y-margin && pixel.y<=(float)region.y+region.height+margin)
                { reason="A tracked hand joint still overlaps the suggested camera region"; return true; }
            }
            reason="No supplied hand joint overlaps the region; pixel visibility is not guaranteed";
            return false;
        }

        /// <summary>
        /// Exact inverse of CapturePlaneProjection's MRUK209 pinhole/crop formula.
        /// Returns out-of-image coordinates for callers to clip; behind-camera points fail.
        /// Uses only frozen capture metadata, never the current headset transform.
        /// </summary>
        public static bool TryWorldToPixel(QuestFrameCapture.CaptureMetadata metadata, Vector3 worldPoint,
            out Vector2 pixelTopLeft)
        {
            pixelTopLeft = default;
            if (metadata == null || !Finite(worldPoint) ||
                !CapturePlaneProjection.TryPixelToRay(metadata,new Vector2(metadata.imageWidth*.5f,metadata.imageHeight*.5f),out _))
                return false;
            Vector3 local = Quaternion.Inverse(metadata.cameraRotationWorld.normalized)*(worldPoint-metadata.cameraPositionWorld);
            if (!Finite(local) || local.z <= .001f) return false;
            var calibration = metadata.intrinsics;
            float sensorWidth = calibration.sensorResolution.x, sensorHeight = calibration.sensorResolution.y;
            float cropX,cropY,cropWidth,cropHeight;
            if (calibration.sensorCrop != null)
            {
                cropX=calibration.sensorCrop.x; cropY=calibration.sensorCrop.y;
                cropWidth=calibration.sensorCrop.width; cropHeight=calibration.sensorCrop.height;
            }
            else
            {
                float scaleX=metadata.imageWidth/sensorWidth,scaleY=metadata.imageHeight/sensorHeight;
                float scale=Mathf.Max(scaleX,scaleY);
                cropWidth=sensorWidth*scaleX/scale; cropHeight=sensorHeight*scaleY/scale;
                cropX=(sensorWidth-cropWidth)*.5f; cropY=(sensorHeight-cropHeight)*.5f;
            }
            float sensorX=calibration.focalLength.x*local.x/local.z+calibration.principalPoint.x;
            float sensorY=calibration.focalLength.y*local.y/local.z+(sensorHeight-calibration.principalPoint.y);
            pixelTopLeft = new Vector2((sensorX-cropX)/cropWidth*metadata.imageWidth,
                (1-(sensorY-cropY)/cropHeight)*metadata.imageHeight);
            return Finite(pixelTopLeft);
        }

        public static bool TryBuildRegion(QuestFrameCapture.CaptureMetadata metadata, Vector3 historicalPoint,
            float surfaceHeight, out QuestFrameCapture.RecognitionRegionMetadata region, out string reason)
            => TryBuildRegion(metadata,historicalPoint,surfaceHeight,Quaternion.identity,
                RegionWidthMeters,RegionDepthMeters,false,out region,out reason);

        /// <summary>The timed pinch crop uses the same frozen footprint and orientation as its visible box.</summary>
        public static bool TryBuildRegion(QuestFrameCapture.CaptureMetadata metadata, Vector3 historicalPoint,
            float surfaceHeight, Quaternion targetRotation, out QuestFrameCapture.RecognitionRegionMetadata region, out string reason)
            => TryBuildRegion(metadata,historicalPoint,surfaceHeight,targetRotation,
                TargetWidthMeters,TargetDepthMeters,true,out region,out reason);

        static bool TryBuildRegion(QuestFrameCapture.CaptureMetadata metadata, Vector3 historicalPoint,
            float surfaceHeight, Quaternion targetRotation, float physicalWidth, float physicalDepth,
            bool includeReadingFrame,
            out QuestFrameCapture.RecognitionRegionMetadata region, out string reason)
        {
            region=null;
            if (!Finite(historicalPoint) || !float.IsFinite(surfaceHeight))
            { reason="Historical hand point is invalid"; return false; }
            if(!TryTabletopRotation(targetRotation,out var rotation))
            { reason="Frozen target rotation is invalid"; return false; }
            Vector3 center=historicalPoint; center.y=surfaceHeight;
            if (!TryWorldToPixel(metadata,center,out var centerPixel) || centerPixel.x<0 || centerPixel.y<0 ||
                centerPixel.x>=metadata.imageWidth || centerPixel.y>=metadata.imageHeight)
            { reason="Last hand region is outside the captured camera view"; return false; }
            float minX=float.PositiveInfinity,minY=float.PositiveInfinity,maxX=float.NegativeInfinity,maxY=float.NegativeInfinity;
            var readingQuad=includeReadingFrame?new Vector2[4]:null;
            // The frozen target rectangle is a suggested search region,
            // not a segmented block or measured word boundary.
            for(int z=-1;z<=1;z+=2)
                for(int x=-1;x<=1;x+=2)
                {
                    Vector3 corner=center+rotation*new Vector3(x*physicalWidth*.5f,0,z*physicalDepth*.5f);
                    if(!TryWorldToPixel(metadata,corner,out var pixel))
                    { reason="Suggested hand region crosses behind the camera"; return false; }
                    // User-confirmed acquisition: "One aligned row; touching blocks are OK."
                    // Its explicit reading convention has printed labels toward the viewer:
                    // +forward is the stud/top edge, -forward is the label/bottom edge.
                    // Left/right follows the frozen box right axis, not current camera axes.
                    if(readingQuad!=null)
                        readingQuad[z>0?(x<0?0:1):(x>0?2:3)]=pixel;
                    minX=Mathf.Min(minX,pixel.x); maxX=Mathf.Max(maxX,pixel.x);
                    minY=Mathf.Min(minY,pixel.y); maxY=Mathf.Max(maxY,pixel.y);
                }
            if(maxX-minX<4 || maxY-minY<4)
            { reason="Suggested table region is edge-on or too small in this frame"; return false; }
            minX-=PaddingPixels; minY-=PaddingPixels; maxX+=PaddingPixels; maxY+=PaddingPixels;
            float rawArea=(maxX-minX)*(maxY-minY);
            float clippedMinX=Mathf.Clamp(minX,0,metadata.imageWidth),clippedMaxX=Mathf.Clamp(maxX,0,metadata.imageWidth);
            float clippedMinY=Mathf.Clamp(minY,0,metadata.imageHeight),clippedMaxY=Mathf.Clamp(maxY,0,metadata.imageHeight);
            float retained=(clippedMaxX-clippedMinX)*(clippedMaxY-clippedMinY)/rawArea;
            if (!float.IsFinite(rawArea) || rawArea<=0 || !float.IsFinite(retained) || retained<.60f)
            { reason="Most of the suggested hand region is outside the captured view"; return false; }
            int left=Mathf.FloorToInt(clippedMinX),top=Mathf.FloorToInt(clippedMinY);
            int width=Mathf.CeilToInt(clippedMaxX)-left,height=Mathf.CeilToInt(clippedMaxY)-top;
            if(width<16 || height<12)
            { reason="Suggested hand region is too small in this frame"; return false; }
            region=new QuestFrameCapture.RecognitionRegionMetadata {
                x=left,y=top,width=width,height=height,
                clipped=minX<0 || minY<0 || maxX>metadata.imageWidth || maxY>metadata.imageHeight,
                retainedFraction=retained,centerWorld=center,
                physicalWidthMeters=physicalWidth,physicalDepthMeters=physicalDepth,
                readingOrientation=includeReadingFrame?"labels_toward_viewer":null,
                readingQuad=readingQuad
            };
            reason="Full frame with a suggested historical-hand region";
            return true;
        }

        static bool Finite(Vector3 p)=>float.IsFinite(p.x)&&float.IsFinite(p.y)&&float.IsFinite(p.z);
        static bool Finite(Vector2 p)=>float.IsFinite(p.x)&&float.IsFinite(p.y);
    }
}
