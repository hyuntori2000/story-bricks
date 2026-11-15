using System.Collections.Generic;
using BrailleSoundDemo.PhysicalStory;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace BrailleSoundDemo.Presentation
{
    /// <summary>Routes existing tracked-hand pointers into the imported UGUI, without another XR rig.</summary>
    [DefaultExecutionOrder(180)]
    public sealed class WorldCanvasHandInput : MonoBehaviour
    {
        Canvas canvas;
        OVRCameraRig rig;
        PhysicalStoryCoordinator core;
        EventSystem events;
        readonly OVRHand[] hands=new OVRHand[2];
        readonly bool[] pressedLast=new bool[5], released=new bool[5];
        readonly Button[] recent=new Button[5], hovered=new Button[5];
        readonly float[] recentAt=new float[5];
        readonly LineRenderer[] rays=new LineRenderer[4];
        Material rayMaterial;
        bool held;
        float graceUntil, nextHandSearch;

        public void Configure(Canvas target,OVRCameraRig cameraRig,PhysicalStoryCoordinator coordinator)
        {
            canvas=target; rig=cameraRig; core=coordinator;
            events=EventSystem.current;
            if(events==null) events=new GameObject("Story Bricks UI events").AddComponent<EventSystem>();
            rayMaterial=new Material(Shader.Find("Unlit/Color"));
            rayMaterial.color=new Color(.78f,.82f,.86f,.8f);
            for(int i=0;i<rays.Length;i++)
            {
                var go=new GameObject("UI pointer "+i); go.transform.SetParent(transform,false);
                var line=go.AddComponent<LineRenderer>(); line.sharedMaterial=rayMaterial;
                line.positionCount=2; line.startWidth=.001f; line.endWidth=.002f;
                line.useWorldSpace=true; line.enabled=false; rays[i]=line;
            }
        }

        void Update()
        {
            if(canvas==null || rig==null || core==null || events==null) return;
            if(Time.unscaledTime>=nextHandSearch)
            {
                if(hands[0]==null) hands[0]=rig.leftHandAnchor.GetComponentInChildren<OVRHand>(true);
                if(hands[1]==null) hands[1]=rig.rightHandAnchor.GetComponentInChildren<OVRHand>(true);
                nextHandSearch=Time.unscaledTime+1;
            }
            bool over=false, anyPressed=false, activated=false;
            var buttons=canvas.GetComponentsInChildren<Button>(false);
            for(int i=0;i<pressedLast.Length;i++)
            {
                bool valid=ReadPointer(i,out var ray,out bool pressed,out bool tracked);
                anyPressed|=pressed;
                if(!tracked) { released[i]=false; recent[i]=null; }
                else if(!pressed) released[i]=true;
                bool rising=tracked && released[i] && pressed && !pressedLast[i];
                pressedLast[i]=pressed;
                Button target=null; float distance=3;
                if(valid && !core.FinishDialogOpen)
                    foreach(var button in buttons)
                        if(Available(button) && Hit(button.transform as RectTransform,ray,out float t) && t<distance)
                        { distance=t; target=button; }
                if(i<rays.Length)
                {
                    rays[i].enabled=target!=null;
                    if(target!=null) { rays[i].SetPosition(0,ray.origin); rays[i].SetPosition(1,ray.GetPoint(distance)); }
                }
                var data=new PointerEventData(events) { pointerId=100+i,button=PointerEventData.InputButton.Left };
                if(hovered[i]!=target)
                {
                    if(hovered[i]!=null) ExecuteEvents.Execute(hovered[i].gameObject,data,ExecuteEvents.pointerExitHandler);
                    hovered[i]=target;
                    if(target!=null) ExecuteEvents.Execute(target.gameObject,data,ExecuteEvents.pointerEnterHandler);
                }
                if(target!=null)
                {
                    over=true;
                    if(!pressed) { recent[i]=target; recentAt[i]=Time.unscaledTime; }
                    if(pressed) held=true;
                }
                var chosen=target;
                if(chosen==null && Time.unscaledTime-recentAt[i]<=.2f) chosen=recent[i];
                if(!activated && !core.FinishDialogOpen && rising && Available(chosen))
                {
                    // Block capture before invoking a callback that can change the selected route.
                    held=true; graceUntil=Time.unscaledTime+.2f;
                    core.SetPresentationInputBlocked(true);
                    ExecuteEvents.Execute(chosen.gameObject,data,ExecuteEvents.pointerDownHandler);
                    ExecuteEvents.Execute(chosen.gameObject,data,ExecuteEvents.pointerUpHandler);
                    ExecuteEvents.Execute(chosen.gameObject,data,ExecuteEvents.pointerClickHandler);
                    System.Array.Clear(recent,0,recent.Length);
                    activated=true;
                }
            }
            if(held && !anyPressed) { held=false; graceUntil=Time.unscaledTime+.2f; }
            core.SetPresentationInputBlocked(over || held || Time.unscaledTime<graceUntil);
        }

        static bool Available(Button button)
        {
            if(button==null || !button.isActiveAndEnabled || !button.IsInteractable()) return false;
            foreach(var group in button.GetComponentsInParent<CanvasGroup>())
                if(group.alpha<.05f || !group.interactable || !group.blocksRaycasts) return false;
            return true;
        }
        static bool Hit(RectTransform rect,Ray ray,out float distance)
        {
            distance=0;
            if(rect==null) return false;
            var plane=new Plane(rect.forward,rect.position);
            if(!plane.Raycast(ray,out distance) || distance<=0 || distance>3) return false;
            var p=rect.InverseTransformPoint(ray.GetPoint(distance));
            return rect.rect.Contains(new Vector2(p.x,p.y));
        }
        bool ReadPointer(int i,out Ray ray,out bool pressed,out bool tracked)
        {
            ray=default; pressed=false; tracked=false;
            Transform pose=null;
            if(i<2)
            {
                var hand=hands[i];
                if(hand==null || !hand.isActiveAndEnabled || !hand.IsTracked || !hand.IsDataValid ||
                    hand.IsSystemGestureInProgress || hand.PoseSourceInferred) return false;
                tracked=true; pressed=hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
                if(!hand.IsPointerPoseValid) return false;
                pose=hand.PointerPose;
            }
            else if(i<4)
            {
                var controller=i==2?OVRInput.Controller.LTouch:OVRInput.Controller.RTouch;
                if(!OVRInput.GetControllerPositionTracked(controller) || !OVRInput.GetControllerOrientationTracked(controller)) return false;
                tracked=true; pressed=OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger,controller);
                pose=i==2?rig.leftControllerAnchor:rig.rightControllerAnchor;
            }
            else
            {
#if ENABLE_INPUT_SYSTEM
                if(!UnityEngine.XR.XRSettings.isDeviceActive && Mouse.current!=null && canvas.worldCamera!=null)
                {
                    tracked=true; pressed=Mouse.current.leftButton.isPressed;
                    ray=canvas.worldCamera.ScreenPointToRay(Mouse.current.position.ReadValue()); return true;
                }
#endif
                return false;
            }
            if(pose==null) return false;
            ray=new Ray(pose.position,pose.forward); return true;
        }
        void OnDisable()
        {
            core?.SetPresentationInputBlocked(false);
            foreach(var line in rays) if(line!=null) line.enabled=false;
            System.Array.Clear(recent,0,recent.Length);
            System.Array.Clear(released,0,released.Length);
        }
        void OnDestroy() { if(rayMaterial!=null) Destroy(rayMaterial); }
    }
}
