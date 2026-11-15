using System;
using BrailleSoundDemo.CameraTest;
using Meta.XR;
using UnityEngine;

namespace BrailleSoundDemo.TableDepthTest
{
    /// <summary>One explicit photo request three seconds after the second pinch. No hand-clearance or depth revalidation gate.</summary>
    public sealed class DoublePinchCaptureDriver
    {
        readonly TableDepthTestDemo table;
        readonly QuestFrameCapture sender;
        readonly PassthroughCameraAccess camera;
        readonly Transform head;
        readonly OVRHand[] hands;
        readonly OVRSkeleton[] skeletons;
        readonly DoublePinchTimerPolicy policy = new DoublePinchTimerPolicy();
        readonly string session = Guid.NewGuid().ToString("N");
        readonly Action armedCue, captureStarted, retryCue;
        readonly Action<HandApproachSample,string,Quaternion> targetSelected;
        int lastInputBits=-1;
        public bool LeftPinchTracked { get; private set; }
        public bool RightPinchTracked { get; private set; }
        public bool LeftPinched { get; private set; }
        public bool RightPinched { get; private set; }
        HandApproachSample target;
        Quaternion targetRotation=Quaternion.identity;
        string armedHand;
        int revision;
        bool haveTarget, usedSavedTable;
        Func<QuestFrameCapture.CaptureMetadata,bool> preparation;
        public string Status { get; private set; } = "Double pinch above the word. Photo follows in 3 seconds.";
        public bool IsArmed => policy.IsArmed;
        public string LeftClearanceStatus => "Not used: timed photo";
        public string RightClearanceStatus => "Not used: timed photo";

        public DoublePinchCaptureDriver(TableDepthTestDemo table, QuestFrameCapture sender,
            PassthroughCameraAccess camera, Transform head, OVRHand left, OVRHand right,
            OVRSkeleton leftBones, OVRSkeleton rightBones, Action armedCue, Action captureStarted, Action retryCue,
            Action<HandApproachSample,string,Quaternion> targetSelected)
        {
            this.table=table;this.sender=sender;this.camera=camera;this.head=head;
            hands=new[] {left,right};skeletons=new[] {leftBones,rightBones};
            this.armedCue=armedCue;this.captureStarted=captureStarted;this.retryCue=retryCue;this.targetSelected=targetSelected;
        }

        public void Reset()
        {
            policy.Reset();haveTarget=false;
            if(sender.PrepareMetadata==preparation) sender.PrepareMetadata=null;
            preparation=null;
        }

        bool Tracked(int side) => hands[side]!=null && hands[side].isActiveAndEnabled &&
            hands[side].IsTracked && hands[side].IsDataValid && hands[side].IsDataHighConfidence &&
            !hands[side].PoseSourceInferred && !hands[side].IsSystemGestureInProgress &&
            skeletons[side]!=null && skeletons[side].isActiveAndEnabled && skeletons[side].IsInitialized &&
            skeletons[side].IsDataValid && skeletons[side].IsDataHighConfidence;

        bool IndexTip(int side,out Vector3 point)
        {
            point=default;var skeleton=skeletons[side];
            if(!Tracked(side) || skeleton.Bones==null) return false;
            var kind=skeleton.GetSkeletonType();
            bool xr=kind==OVRSkeleton.SkeletonType.XRHandLeft || kind==OVRSkeleton.SkeletonType.XRHandRight;
            var id=xr?OVRSkeleton.BoneId.XRHand_IndexTip:OVRSkeleton.BoneId.Hand_IndexTip;
            foreach(var bone in skeleton.Bones)
                if(bone.Id==id && bone.Transform!=null) {point=bone.Transform.position;return Finite(point);}
            return false;
        }

        bool PinchTracked(int side) => IndexTip(side,out var point) &&
            hands[side].GetFingerConfidence(OVRHand.HandFinger.Index)==OVRHand.TrackingConfidence.High &&
            hands[side].GetFingerConfidence(OVRHand.HandFinger.Thumb)==OVRHand.TrackingConfidence.High && InImage(point);

        bool InImage(Vector3 point)
        {
            if(!camera.IsPlaying) return false;
            var pose=camera.GetCameraPose();
            if((Quaternion.Inverse(pose.rotation)*(point-pose.position)).z<=.05f) return false;
            var p=camera.WorldToViewportPoint(point,pose);
            return float.IsFinite(p.x)&&float.IsFinite(p.y)&&p.x>=0&&p.x<=1&&p.y>=0&&p.y<=1;
        }

        public void Tick(bool captureAllowed)
        {
            if(sender.Busy) return;
            double now=Time.realtimeSinceStartupAsDouble;
            bool context=table.HeadTrackingValid && (!haveTarget || revision==table.ReferenceRevision);
            LeftPinchTracked=PinchTracked(0);RightPinchTracked=PinchTracked(1);
            LeftPinched=LeftPinchTracked&&hands[0].GetFingerIsPinching(OVRHand.HandFinger.Index);
            RightPinched=RightPinchTracked&&hands[1].GetFingerIsPinching(OVRHand.HandFinger.Index);
            int bits=(LeftPinchTracked?1:0)|(RightPinchTracked?2:0)|(LeftPinched?4:0)|(RightPinched?8:0)|(context?16:0)|(captureAllowed?32:0);
            if(bits!=lastInputBits)
            {
                lastInputBits=bits;
                Debug.Log("[DoublePinch] Input leftTracked="+LeftPinchTracked+" leftPinched="+LeftPinched+
                    " rightTracked="+RightPinchTracked+" rightPinched="+RightPinched+" context="+context+" captureAllowed="+captureAllowed);
            }
            bool wasArmed=policy.IsArmed;
            var result=policy.Observe(now,context,captureAllowed,LeftPinchTracked,LeftPinched,RightPinchTracked,RightPinched,sender.IsReady);
            Status=policy.Status;
            if(wasArmed && !policy.IsArmed && !result.CaptureNow)
            {Debug.Log("[DoublePinch] Timer canceled: "+Status);retryCue?.Invoke();}
            if(result.ArmedNow)
            {
                int side=result.ArmedHand=="left"?0:1;
                if(!IndexTip(side,out var tip))
                {Reset();Status="Pinching hand position unavailable.";retryCue?.Invoke();return;}
                usedSavedTable=table.HasLockedTableReference;
                float height=usedSavedTable?table.LockedTablePoint.y:tip.y;
                target=new HandApproachSample {time=now,frame=Time.frameCount,headPosition=head.position,
                    fingerPosition=tip,surfaceHeight=height,signedGap=tip.y-height};
                targetRotation=HandRegionProjection.TabletopRotation(head.rotation);
                armedHand=result.ArmedHand;revision=table.ReferenceRevision;haveTarget=true;
                targetSelected?.Invoke(target,armedHand,targetRotation);armedCue?.Invoke();
                Debug.Log("[DoublePinch] Timer armed hand="+armedHand+" delaySeconds="+DoublePinchTimerPolicy.CaptureDelaySeconds+
                    " targetTime="+now.ToString("F6")+" targetYaw="+targetRotation.eulerAngles.y.ToString("F1"));
            }
            else if(result.CountdownSeconds>0) armedCue?.Invoke();
            if(result.CountdownSeconds>0) Debug.Log("[DoublePinch] Countdown seconds="+result.CountdownSeconds);
            if(result.CaptureNow) Request();
            if(!policy.IsArmed && !result.CaptureNow) haveTarget=false;
        }

        bool ContextValid() => haveTarget && table.isActiveAndEnabled && table.HeadTrackingValid &&
            revision==table.ReferenceRevision;

        void Request()
        {
            Vector3 center=target.fingerPosition;center.y=target.surfaceHeight;
            var context=new QuestCaptureContext {SessionId=session,FrameRevision=revision,BoardPose=new Pose(center,targetRotation),
                BoardWidth=HandRegionProjection.TargetWidthMeters,BoardDepth=HandRegionProjection.TargetDepthMeters,
                BlockHeight=0,ClearSeconds=0,TriggerType="manual"};
            preparation=metadata=> {
                if(!ContextValid()) return false;
                // A bad/out-of-view ROI must not prevent the explicitly requested full-frame photo.
                // Receiver saves the photo and requests a retry if this region cannot be recognized.
                if(HandRegionProjection.TryBuildRegion(metadata,target.fingerPosition,target.surfaceHeight,targetRotation,out var region,out var reason))
                    metadata.recognitionRegion=region;
                else {metadata.recognitionRegion=null;metadata.recognitionRegionIssue=reason;}
                metadata.trigger.gesture="double_pinch_timer";
                metadata.trigger.delaySeconds=(float)DoublePinchTimerPolicy.CaptureDelaySeconds;
                metadata.trigger.depthFreshAtCapture=table.ReferenceMeasurementFresh;
                metadata.trigger.savedTableReference=usedSavedTable;
                metadata.handApproach=new QuestFrameCapture.HandApproachMetadata {
                    hand=armedHand,interpretation="Index position and horizontal view orientation frozen at double pinch; explicit photo requested after a 3-second timer. Hand clearance is not measured.",
                    positionWorld=target.fingerPosition,headPositionWorld=target.headPosition,signedGapMeters=target.signedGap,
                    surfaceHeightMeters=target.surfaceHeight,headToPointDistanceMeters=Vector3.Distance(target.headPosition,target.fingerPosition),
                    sampleMonotonicSeconds=target.time,liftStartedMonotonicSeconds=target.time,confirmedMonotonicSeconds=Time.realtimeSinceStartupAsDouble,
                    sampleFrame=target.frame,confirmedFrame=Time.frameCount,frameAgeAtConfirmation=Time.frameCount-target.frame,
                    sampleAgeAtConfirmationSeconds=Time.realtimeSinceStartupAsDouble-target.time,
                    sampleAgeAtCaptureObservationSeconds=metadata.captureTimings.frameObservedMonotonicSeconds-target.time};
                return true;
            };
            sender.PrepareMetadata=preparation;
            int capturedRevision=revision;
            bool accepted=sender.RequestCapture(context,ContextValid,()=>table.isActiveAndEnabled && table.HeadTrackingValid &&
                capturedRevision==table.ReferenceRevision && table.CapturedContextStillValid!=null && table.CapturedContextStillValid());
            Status=accepted?"Timer elapsed; taking one photo.":sender.Status;
            if(accepted) captureStarted?.Invoke();else retryCue?.Invoke();
            Debug.Log("[DoublePinch] "+Status+" elapsedSeconds="+(Time.realtimeSinceStartupAsDouble-target.time).ToString("F3"));
        }
        static bool Finite(Vector3 p)=>float.IsFinite(p.x)&&float.IsFinite(p.y)&&float.IsFinite(p.z);
    }
}
