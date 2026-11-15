using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace BrailleSoundDemo.CameraTest
{
    /// <summary>
    /// Displays only the recognition result for the exact captured frame.
    /// Positions use that frame's camera geometry and the selected block-top plane.
    /// Rotation faces the viewer; positions never follow the viewer.
    /// </summary>
    public sealed class QuestLetterOverlay : MonoBehaviour
    {
        readonly List<GameObject> labels = new List<GameObject>();
        Transform eye;
        string resultEndpoint;
        Coroutine polling;
        UnityWebRequest activeRequest;
        Material acceptedMaterial, unknownMaterial, panelMaterial;
        int generation;
        public bool WaitingForResult { get; private set; }
        public string Status { get; private set; } = "No recognized labels yet.";

        public void Configure(Transform viewer, string captureEndpoint)
        {
            Clear("No recognized labels yet.");
            eye = viewer;
            resultEndpoint = new Uri(new Uri(captureEndpoint), "/color/result").AbsoluteUri;
            var shader = Shader.Find("Unlit/Color");
            acceptedMaterial = new Material(shader) { color = new Color(.20f, .95f, .80f) };
            unknownMaterial = new Material(shader) { color = new Color(.65f, .65f, .65f) };
            panelMaterial = new Material(shader) { color = new Color(.025f, .045f, .06f, 1f) };
        }

        public void Begin(QuestFrameCapture.CaptureMetadata metadata, Func<bool> stillValid)
        {
            Clear("Waiting for recognition: select faces on the Mac.");
            if (metadata == null || stillValid == null || string.IsNullOrEmpty(resultEndpoint)) return;
            // Metadata consists of values copied at camera readback, not current head pose.
            var snapshot = JsonUtility.FromJson<QuestFrameCapture.CaptureMetadata>(JsonUtility.ToJson(metadata));
            WaitingForResult = true;
            polling = StartCoroutine(Poll(snapshot, stillValid, generation));
        }

        IEnumerator Poll(QuestFrameCapture.CaptureMetadata metadata, Func<bool> stillValid, int token)
        {
            int lastRevision = -1;
            string uri = resultEndpoint + "?sessionId=" + Uri.EscapeDataString(metadata.sessionId) +
                "&frameId=" + Uri.EscapeDataString(metadata.frameId);
            while (token == generation && IsValid(stillValid))
            {
                activeRequest = UnityWebRequest.Get(uri);
                activeRequest.timeout = 8;
                yield return activeRequest.SendWebRequest();
                if (token != generation) yield break;
                if (!IsValid(stillValid)) break;
                if (activeRequest.responseCode == 409)
                {
                    Status = "This image was replaced. Capture again for new labels.";
                    break;
                }
                if (activeRequest.result == UnityWebRequest.Result.Success)
                {
                    Result response = null;
                    try { response = JsonUtility.FromJson<Result>(activeRequest.downloadHandler.text); }
                    catch (ArgumentException) { }
                    if (response == null || response.sessionId != metadata.sessionId || response.frameId != metadata.frameId ||
                        response.imageWidth != metadata.imageWidth || response.imageHeight != metadata.imageHeight ||
                        response.coordinateSystem != "image_pixels_top_left")
                    {
                        Status = "Recognition metadata did not match this image. Capture again.";
                        break;
                    }
                    if (response.ready && response.analysisRevision != lastRevision)
                    {
                        if (response.status != "recognized" && response.status != "needs_review")
                        {
                            Status = "Result is not independent recognition.";
                            break;
                        }
                        lastRevision = response.analysisRevision;
                        WaitingForResult = false;
                        Draw(response, metadata);
                    }
                    else if (!response.ready)
                    {
                        DestroyLabels();
                        WaitingForResult = true;
                        Status = "Waiting for recognition: select faces on the Mac.";
                    }
                }
                else Status = "Recognition receiver unavailable. Waiting to reconnect.";
                activeRequest.Dispose();
                activeRequest = null;
                yield return new WaitForSecondsRealtime(.35f);
            }
            if (token == generation)
            {
                if (activeRequest != null) { activeRequest.Dispose(); activeRequest = null; }
                DestroyLabels();
                WaitingForResult = false;
                polling = null;
                if (!IsValid(stillValid)) Status = "Tracking or surface reference changed. Capture again.";
            }
        }

        void Draw(Result result, QuestFrameCapture.CaptureMetadata metadata)
        {
            DestroyLabels();
            int known = 0, unknown = 0, skipped = 0;
            var up = metadata.board.rotation * Vector3.up;
            foreach (var detection in result.detections ?? Array.Empty<Detection>())
            {
                if (detection == null || detection.quad == null || detection.quad.Length != 4)
                { skipped++; continue; }
                var pixels = new Vector2[4];
                bool valid = true;
                for (int index = 0; index < detection.quad.Length; index++)
                {
                    var corner = detection.quad[index];
                    if (corner == null || !float.IsFinite(corner.x) || !float.IsFinite(corner.y)) { valid = false; break; }
                    pixels[index] = new Vector2(corner.x, corner.y);
                }
                if (!valid || !CapturePlaneProjection.TryQuadCenterToPlane(metadata, pixels, out var surfacePoint))
                { skipped++; continue; }
                bool accepted = detection.accepted && detection.letter != null && detection.letter.Length == 1 &&
                    detection.letter[0] >= 'A' && detection.letter[0] <= 'Z';
                string glyph = accepted ? detection.letter : "?";
                if (accepted) known++; else unknown++;
                CreateLabel(glyph, accepted, surfacePoint, up);
            }
            Status = "Labels: " + known + " recognized, " + unknown + " unknown" +
                (skipped > 0 ? "; " + skipped + " could not be positioned." : ".") +
                " Positions use the selected surface height.";
            Debug.Log("[LetterOverlay] " + metadata.frameId + " | " + Status);
        }

        void CreateLabel(string glyph, bool accepted, Vector3 surfacePoint, Vector3 up)
        {
            var root = new GameObject("Recognized letter " + glyph);
            root.transform.position = surfacePoint + up * .045f;
            var plate = GameObject.CreatePrimitive(PrimitiveType.Quad);
            plate.name = "Readable letter background";
            Destroy(plate.GetComponent<Collider>());
            plate.transform.SetParent(root.transform, false);
            plate.transform.localPosition = new Vector3(0, 0, .001f);
            plate.transform.localScale = new Vector3(.040f, .048f, 1);
            plate.GetComponent<MeshRenderer>().sharedMaterial = panelMaterial;
            var glyphObject = new GameObject("Glyph");
            glyphObject.transform.SetParent(root.transform, false);
            var text = glyphObject.AddComponent<TextMesh>();
            text.text = glyph;
            text.fontSize = 80;
            text.characterSize = .004f;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.color = accepted ? Color.white : new Color(.70f, .70f, .70f);
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.GetComponent<MeshRenderer>().sharedMaterial = text.font.material;
            var line = root.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.widthMultiplier = .0015f;
            line.sharedMaterial = accepted ? acceptedMaterial : unknownMaterial;
            line.SetPositions(new[] { surfacePoint, root.transform.position - up * .025f });
            labels.Add(root);
            FaceViewer(root.transform);
        }

        void LateUpdate()
        {
            foreach (var label in labels) if (label != null) FaceViewer(label.transform);
        }

        void FaceViewer(Transform label)
        {
            if (eye == null) return;
            var away = label.position - eye.position;
            if (away.sqrMagnitude > .0001f) label.rotation = Quaternion.LookRotation(away, Vector3.up);
        }

        static bool IsValid(Func<bool> condition)
        {
            try { return condition(); }
            catch (Exception) { return false; }
        }

        public void Clear(string message)
        {
            generation++;
            if (activeRequest != null) { activeRequest.Abort(); activeRequest.Dispose(); activeRequest = null; }
            if (polling != null) { StopCoroutine(polling); polling = null; }
            DestroyLabels();
            WaitingForResult = false;
            Status = message;
        }

        void DestroyLabels()
        {
            foreach (var label in labels) if (label != null) Destroy(label);
            labels.Clear();
        }

        void OnDisable() => Clear("Letter overlay paused.");
        void OnDestroy()
        {
            Clear("Letter overlay stopped.");
            if (acceptedMaterial != null) Destroy(acceptedMaterial);
            if (unknownMaterial != null) Destroy(unknownMaterial);
            if (panelMaterial != null) Destroy(panelMaterial);
        }

        [Serializable] sealed class Result
        {
            public bool ready;
            public string sessionId, frameId, status, message, coordinateSystem;
            public int analysisRevision, imageWidth, imageHeight;
            public Detection[] detections;
        }
        [Serializable] sealed class Detection { public string letter; public bool accepted; public Corner[] quad; }
        [Serializable] sealed class Corner { public float x, y; }
    }
}
