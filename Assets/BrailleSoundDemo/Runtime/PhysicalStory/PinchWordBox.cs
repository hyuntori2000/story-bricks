using System;
using BrailleSoundDemo.TableDepthTest;
using UnityEngine;

namespace BrailleSoundDemo.PhysicalStory
{
    /// <summary>A fixed target outline and captured-photo plane; neither changes the recognition or sound anchor.</summary>
    // Editor verification creates real preview materials; execute lifecycle cleanup in edit mode too.
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class PinchWordBox : MonoBehaviour
    {
        public const float WidthMeters = HandRegionProjection.TargetWidthMeters;
        public const float DepthMeters = HandRegionProjection.TargetDepthMeters;
        public const float VisualLiftMeters = .01f;
        public const float AcceptedWidthMeters = .04f;
        public const float AcceptedDepthMeters = .025f;
        public const double CapturedOutlineSeconds = 2;
        public const float CapturePlaneInclinationDegrees = 65;
        static readonly Color PendingColor = new Color(1f,.78f,.25f,1f);
        static readonly Color AcceptedColor = new Color(.2f,.85f,1f,1f);
        static readonly Color PlaneColor = new Color(.9f,.9f,.86f,1f);
        LineRenderer outline;
        Material outlineMaterial;
        GameObject capturePlane;
        Mesh capturePlaneMesh;
        Material capturePlaneMaterial;
        bool outlineHidden;

        public Vector3 CenterWorld { get; private set; }
        public Quaternion RotationWorld { get; private set; } = Quaternion.identity;
        public Vector3 VisualPosition => transform.position;
        public bool Accepted { get; private set; }
        public bool Captured { get; private set; }
        public double CapturedAt { get; private set; } = -1;
        public bool OutlineVisible => outline!=null && outline.enabled;
        public Transform CapturePlane => capturePlane!=null ? capturePlane.transform : null;

        /// <summary>
        /// tableCenter is already projected onto the locked tabletop. The marker never follows a hand or head.
        /// The owning scene must keep this object's parent fixed with unit scale, as for other saved anchors.
        /// Reconfiguration explicitly replaces the pending target and reuses the renderer/material.
        /// </summary>
        public void Configure(Vector3 tableCenter) => Configure(tableCenter,Quaternion.identity);

        public void Configure(Vector3 tableCenter,Quaternion targetRotation)
        {
            if(!Finite(tableCenter)) throw new ArgumentException("Pinch box center must be finite.",nameof(tableCenter));
            Quaternion rotation=HandRegionProjection.TabletopRotation(targetRotation);
            ConfigureAtPose(new Pose(tableCenter,rotation));
        }

        /// <summary>Presentation for a legacy recognized plane preserves that plane's exact pose.</summary>
        public void ConfigureAtPose(Pose planePose)
        {
            if(!Finite(planePose.position) || !ValidRotation(planePose.rotation))
                throw new ArgumentException("Box plane pose must be finite with a valid rotation.",nameof(planePose));
            Quaternion rotation=planePose.rotation.normalized;
            if(outlineMaterial==null)
            {
                var shader=Shader.Find("Unlit/Color");
                if(shader==null) throw new InvalidOperationException("Pinch word box shader is unavailable.");
                outlineMaterial=new Material(shader) { name="Pinch word box outline" };
            }
            if(outline==null) outline=GetComponent<LineRenderer>();
            if(outline==null) outline=gameObject.AddComponent<LineRenderer>();
            ClearCapturePlane();
            CenterWorld=planePose.position;
            RotationWorld=rotation;
            Accepted=false;
            Captured=false;CapturedAt=-1;outlineHidden=false;
            transform.localScale=Vector3.one;
            transform.SetPositionAndRotation(planePose.position+rotation*Vector3.up*VisualLiftMeters,rotation);
            outline.useWorldSpace=false;
            outline.loop=true;
            outline.positionCount=4;
            outline.startWidth=outline.endWidth=.003f;
            outline.numCornerVertices=outline.numCapVertices=2;
            outline.sharedMaterial=outlineMaterial;
            outlineMaterial.color=PendingColor;
            float x=WidthMeters*.5f,z=DepthMeters*.5f;
            outline.SetPositions(new[] {new Vector3(-x,0,-z),new Vector3(x,0,-z),
                new Vector3(x,0,z),new Vector3(-x,0,z)});
            outline.enabled=true;
        }

        /// <summary>Acknowledged photo: blue once, one inclined plane, and a two-second outline lifetime.</summary>
        public void MarkCaptured()
        {
            RequireConfigured();
            if(Captured) return;
            CreateCapturePlane();
            Captured=true;
            CapturedAt=Time.realtimeSinceStartupAsDouble;
            outlineMaterial.color=AcceptedColor;
            outline.enabled=true;
        }

        /// <summary>Promote the existing marker without changing its frozen pose.</summary>
        public void MarkAccepted()
        {
            RequireConfigured();
            MarkCaptured();
            Accepted=true;
            outlineMaterial.color=AcceptedColor;
            outline.startWidth=outline.endWidth=.0015f;
            float x=AcceptedWidthMeters*.5f,z=AcceptedDepthMeters*.5f;
            outline.SetPositions(new[]{new Vector3(-x,0,-z),new Vector3(x,0,-z),
                new Vector3(x,0,z),new Vector3(-x,0,z)});
            RefreshCaptureVisuals(Time.realtimeSinceStartupAsDouble);
        }

        /// <summary>Only the outline expires. Its hidden state remains latched until an explicit new target.</summary>
        public void RefreshCaptureVisuals(double monotonicSeconds)
        {
            if(!Captured || outline==null) return;
            if(double.IsFinite(monotonicSeconds) && monotonicSeconds>=CapturedAt+CapturedOutlineSeconds)
                outlineHidden=true;
            if(outlineHidden) outline.enabled=false;
        }

        void LateUpdate() => RefreshCaptureVisuals(Time.realtimeSinceStartupAsDouble);

        void RequireConfigured()
        {
            if(outline==null || outlineMaterial==null)
                throw new InvalidOperationException("Configure the pinch word box before changing its capture state.");
        }

        void CreateCapturePlane()
        {
            if(capturePlane!=null) return;
            capturePlane=new GameObject("Captured photo plane");
            capturePlane.transform.SetParent(transform,false);
            float halfWidth=WidthMeters*.5f;
            float angle=CapturePlaneInclinationDegrees*Mathf.Deg2Rad;
            Vector3 rise=new Vector3(0,Mathf.Sin(angle)*DepthMeters,Mathf.Cos(angle)*DepthMeters);
            capturePlaneMesh=new Mesh {name="Captured photo plane mesh"};
            capturePlaneMesh.vertices=new[] {new Vector3(-halfWidth,0,0),new Vector3(halfWidth,0,0),
                new Vector3(-halfWidth,0,0)+rise,new Vector3(halfWidth,0,0)+rise};
            // The front normal points toward local -Z, with the top edge rising away from the user.
            capturePlaneMesh.triangles=new[] {0,2,1,1,2,3};
            capturePlaneMesh.uv=new[] {new Vector2(0,0),new Vector2(1,0),new Vector2(0,1),new Vector2(1,1)};
            capturePlaneMesh.RecalculateNormals();capturePlaneMesh.RecalculateBounds();
            capturePlane.AddComponent<MeshFilter>().sharedMesh=capturePlaneMesh;
            capturePlaneMaterial=new Material(outlineMaterial.shader) {name="Captured photo neutral plane",color=PlaneColor};
            var renderer=capturePlane.AddComponent<MeshRenderer>();
            renderer.sharedMaterial=capturePlaneMaterial;
            renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows=false;
        }

        void ClearCapturePlane()
        {
            if(capturePlane!=null) {capturePlane.SetActive(false);DestroyOwned(capturePlane);}
            DestroyOwned(capturePlaneMesh);DestroyOwned(capturePlaneMaterial);
            capturePlane=null;capturePlaneMesh=null;capturePlaneMaterial=null;
        }

        void OnDestroy()
        {
            ClearCapturePlane();
            DestroyOwned(outlineMaterial);
            outlineMaterial=null;
        }

        static void DestroyOwned(UnityEngine.Object value)
        {
            if(value==null) return;
            if(Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }

        static bool Finite(Vector3 p)=>float.IsFinite(p.x)&&float.IsFinite(p.y)&&float.IsFinite(p.z);
        static bool ValidRotation(Quaternion q)
        {
            float norm=q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w;
            return float.IsFinite(norm) && norm>.000001f;
        }
    }
}
