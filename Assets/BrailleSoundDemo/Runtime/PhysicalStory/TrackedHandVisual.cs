using UnityEngine;

namespace BrailleSoundDemo.PhysicalStory
{
    /// <summary>Shows the SDK hand mesh independently of table/capture eligibility.</summary>
    [DefaultExecutionOrder(250)]
    public sealed class TrackedHandVisual : MonoBehaviour
    {
        OVRHand hand;
        OVRSkeleton skeleton;
        OVRMesh mesh;
        OVRMeshRenderer meshRenderer;
        SkinnedMeshRenderer skin;
        bool focused = true, paused;
        string lastState;

        void Awake()
        {
            hand = GetComponent<OVRHand>();
            skeleton = GetComponent<OVRSkeleton>();
            mesh = GetComponent<OVRMesh>();
            meshRenderer = GetComponent<OVRMeshRenderer>();
            skin = GetComponent<SkinnedMeshRenderer>();
        }

        void LateUpdate()
        {
            if (hand == null || skeleton == null || mesh == null || meshRenderer == null || skin == null) return;
            // On-device the SDK renderer tries initialization in Start only.
            // Bind when tracking initialization completes later than that frame.
            if (!meshRenderer.IsInitialized && mesh.IsInitialized && skeleton.IsInitialized)
                meshRenderer.ForceRebind();
            bool visible = focused && !paused && hand.isActiveAndEnabled && hand.IsDataValid &&
                hand.IsTracked && hand.IsDataHighConfidence && !hand.PoseSourceInferred &&
                skeleton.IsDataValid && skeleton.IsDataHighConfidence && meshRenderer.IsInitialized;
            skin.enabled = visible;
            string state = "visible=" + visible + " meshReady=" + mesh.IsInitialized +
                " rendererReady=" + meshRenderer.IsInitialized + " tracked=" + hand.IsTracked;
            if (state != lastState)
            {
                Debug.Log("[TrackedHandVisual] " + gameObject.name + " " + state);
                lastState = state;
            }
        }

        void OnApplicationFocus(bool value) { focused = value; if (!value && skin != null) skin.enabled = false; }
        void OnApplicationPause(bool value) { paused = value; if (value && skin != null) skin.enabled = false; }
        void OnDisable() { if (skin != null) skin.enabled = false; }
    }
}
