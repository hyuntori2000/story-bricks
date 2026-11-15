using System.Collections;
using UnityEngine;

/// Puts the panel in front of the wearer shortly after launch (and again on Recenter()).
public class PlaceInFrontOfHead : MonoBehaviour
{
    public float distance = 0.55f;
    public float below = 0.12f;

    IEnumerator Start()
    {
        yield return new WaitForSeconds(0.6f);
        Recenter();
    }

    public void Recenter()
    {
        var cam = Camera.main;
        if (cam == null) return;
        var head = cam.transform;
        var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
        transform.position = head.position + fwd * distance + Vector3.down * below;
        transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
    }
}
