using System.Collections;
using UnityEngine;

/// Puts the panel in front of the wearer shortly after launch (and again on Recenter()).
public class PlaceInFrontOfHead : MonoBehaviour
{
    public float distance = 0.55f;
    public float below = 0.12f;

    bool placed;

    // Place the panel the moment it is shown, so it never appears somewhere else and then jumps.
    void OnEnable() { placed = false; Recenter(); }

    void LateUpdate() { if (!placed) Recenter(); }

    public void Recenter()
    {
        var cam = Camera.main;
        if (cam == null) return;
        var head = cam.transform;
        if (head.position == Vector3.zero && Time.timeSinceLevelLoad < 3f) return;   // head pose not ready yet
        placed = true;
        var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
        transform.position = head.position + fwd * distance + Vector3.down * below;
        transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
    }
}
