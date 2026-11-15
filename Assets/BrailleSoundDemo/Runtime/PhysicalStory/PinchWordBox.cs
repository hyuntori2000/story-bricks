using System;
using BrailleSoundDemo.TableDepthTest;
using UnityEngine;

namespace BrailleSoundDemo.PhysicalStory
{
    /// <summary>A fixed tabletop marker created when a pinch target is selected, before recognition.</summary>
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
        static readonly Color PendingColor = new Color(1f,.78f,.25f,1f);
        static readonly Color AcceptedColor = new Color(.2f,.85f,1f,1f);
        LineRenderer outline;
        Material outlineMaterial;

        public Vector3 CenterWorld { get; private set; }
        public Quaternion RotationWorld { get; private set; } = Quaternion.identity;
        public Vector3 VisualPosition => transform.position;
        public bool Accepted { get; private set; }

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
            if(outlineMaterial==null)
            {
                var shader=Shader.Find("Unlit/Color");
                if(shader==null) throw new InvalidOperationException("Pinch word box shader is unavailable.");
                outlineMaterial=new Material(shader) { name="Pinch word box outline" };
            }
            if(outline==null) outline=GetComponent<LineRenderer>();
            if(outline==null) outline=gameObject.AddComponent<LineRenderer>();
            CenterWorld=tableCenter;
            RotationWorld=rotation;
            Accepted=false;
            transform.localScale=Vector3.one;
            transform.SetPositionAndRotation(tableCenter+Vector3.up*VisualLiftMeters,rotation);
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

        /// <summary>Promote the existing marker without changing its frozen pose.</summary>
        public void MarkAccepted()
        {
            if(outline==null || outlineMaterial==null)
                throw new InvalidOperationException("Configure the pinch word box before accepting it.");
            Accepted=true;
            outlineMaterial.color=AcceptedColor;
            outline.startWidth=outline.endWidth=.0015f;
            float x=AcceptedWidthMeters*.5f,z=AcceptedDepthMeters*.5f;
            outline.SetPositions(new[]{new Vector3(-x,0,-z),new Vector3(x,0,-z),
                new Vector3(x,0,z),new Vector3(-x,0,z)});
        }

        void OnDestroy()
        {
            if(outlineMaterial==null) return;
            if(Application.isPlaying) Destroy(outlineMaterial);
            else DestroyImmediate(outlineMaterial);
            outlineMaterial=null;
        }

        static bool Finite(Vector3 p)=>float.IsFinite(p.x)&&float.IsFinite(p.y)&&float.IsFinite(p.z);
    }
}
