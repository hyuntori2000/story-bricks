using System;
using System.Collections.Generic;
using BrailleSoundDemo.CameraTest;
using UnityEngine;

namespace BrailleSoundDemo.PhysicalStory
{
    [Serializable] public sealed class PhysicalPixelPoint { public float x, y; }
    [Serializable] public sealed class PhysicalColorDetection
    {
        public string letter, kind;
        public bool accepted;
        public PhysicalPixelPoint[] quad;
    }
    [Serializable] public sealed class PhysicalColorResult
    {
        public string sessionId, frameId, coordinateSystem, status, captureTimestampUtc;
        public int imageWidth, imageHeight, analysisRevision;
        public bool ready;
        public bool automaticBlockDetection;
        public PhysicalPixelPoint groupCenterPixel;
        public PhysicalColorDetection[] detections;
    }
    public sealed class PhysicalWordGeometry
    {
        public string Word, GroupId, FrameId;
        public string[] ComponentIds;
        public Vector3 CenterWorld;
        public Quaternion Rotation;
        public Vector2 Size;
        public Vector3[] Corners;
    }

    /// <summary>
    /// Places a frozen word footprint from the selected faces in the exact captured image.
    /// Uses the measured tabletop plane: this is not a brick-height estimate or tracker.
    /// </summary>
    public static class PhysicalWordAnchorProjection
    {
        public static readonly Vector2 DefaultCenterBoxSize = new Vector2(.20f, .12f);
        public static bool TryCreate(QuestFrameCapture.CaptureMetadata metadata, PhysicalColorResult result,
            int currentReferenceRevision, out PhysicalWordGeometry geometry, out string reason)
        {
            geometry = null;
            reason = "Recognition does not match the current captured frame.";
            if (metadata == null || result == null || metadata.board == null || metadata.recognitionRegion == null ||
                string.IsNullOrWhiteSpace(metadata.sessionId) || string.IsNullOrWhiteSpace(metadata.frameId) ||
                string.IsNullOrWhiteSpace(metadata.captureTimestampUtc) || metadata.frameRevision < 0 ||
                metadata.imageWidth < 1 || metadata.imageWidth > 4096 || metadata.imageHeight < 1 || metadata.imageHeight > 4096 ||
                !ShortId(metadata.sessionId) || !ShortId(metadata.frameId) ||
                result.sessionId != metadata.sessionId || result.frameId != metadata.frameId ||
                result.captureTimestampUtc != metadata.captureTimestampUtc ||
                result.imageWidth != metadata.imageWidth || result.imageHeight != metadata.imageHeight ||
                result.coordinateSystem != "image_pixels_top_left" || metadata.frameRevision != currentReferenceRevision)
                return false;
            reason = "Every visible block must be confidently recognized in the current photo.";
            if (!result.ready || result.status != "recognized" || result.analysisRevision < 1 ||
                result.detections == null || result.detections.Length < 1 || result.detections.Length > 32)
                return false;

            var region = metadata.recognitionRegion;
            if (region.pixelOrigin != "top-left" || region.source != "recent_hand_approach" ||
                region.x < 0 || region.y < 0 || region.width <= 0 || region.height <= 0 ||
                (long)region.x + region.width > metadata.imageWidth || (long)region.y + region.height > metadata.imageHeight)
            { reason = "The saved hand-selected image region is invalid."; return false; }

            Quaternion rotation = metadata.board.rotation.normalized;
            Quaternion inverse = Quaternion.Inverse(rotation);
            Vector3 origin = metadata.board.position + rotation * Vector3.up * metadata.board.blockHeight;
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
            var letters = new char[result.detections.Length];
            var ids = new string[result.detections.Length];
            var selectedFaces = new List<Vector2[]>();
            for (int i = 0; i < result.detections.Length; i++)
            {
                var detection = result.detections[i];
                if (detection == null || !PhysicalWordInputPolicy.IsAcceptedSymbol(detection.letter, detection.kind, detection.accepted) ||
                    detection.quad == null || detection.quad.Length != 4)
                { reason = "At least one detected block was not accepted as a letter or supported punctuation."; return false; }
                letters[i] = detection.letter[0];
                ids[i] = "capture-" + metadata.frameId + "-letter-" + i;
                var pixels = new Vector2[4];
                for (int corner = 0; corner < 4; corner++)
                {
                    var pixel = detection.quad[corner];
                    if (pixel == null || !float.IsFinite(pixel.x) || !float.IsFinite(pixel.y))
                    { reason = "A selected face has invalid image coordinates."; return false; }
                    pixels[corner] = new Vector2(pixel.x, pixel.y);
                    // The ROI is a search window, not a detection; selected faces must belong to it.
                    if (pixel.x < region.x - 2 || pixel.x > region.x + region.width + 2 ||
                        pixel.y < region.y - 2 || pixel.y > region.y + region.height + 2)
                    { reason = "Select faces inside the saved hand region, or capture again around the word."; return false; }
                    if (!CapturePlaneProjection.TryPixelToPlane(metadata, pixels[corner], out var point))
                    { reason = "A selected face could not be projected onto the captured tabletop plane."; return false; }
                    var local = inverse * (point - origin);
                    minX = Mathf.Min(minX, local.x); maxX = Mathf.Max(maxX, local.x);
                    minZ = Mathf.Min(minZ, local.z); maxZ = Mathf.Max(maxZ, local.z);
                }
                if (!ConvexQuad(pixels))
                { reason = "Selected face corners must describe a nonzero convex quadrilateral."; return false; }
                foreach (var previous in selectedFaces)
                    if (SameFace(previous, pixels))
                    { reason = "The same face was selected twice. Select each physical block only once."; return false; }
                selectedFaces.Add(pixels);
            }
            string word = new string(letters);
            if (!PhysicalWordInputPolicy.IsRecognizedWord(word))
            { reason = "A physical word must include at least one letter A-Z."; return false; }
            float width = maxX - minX, depth = maxZ - minZ;
            if (!float.IsFinite(width) || !float.IsFinite(depth) || width < .001f || depth < .001f || width > 2 || depth > 2)
            { reason = "The projected word footprint is degenerate or too large."; return false; }
            // Small visible border; the sound and future image share its center.
            const float padding = .005f;
            minX -= padding; maxX += padding; minZ -= padding; maxZ += padding;
            geometry = new PhysicalWordGeometry {
                Word = word, GroupId = "capture-" + metadata.frameId, FrameId = metadata.frameId,
                ComponentIds = ids, Rotation = rotation, Size = new Vector2(maxX - minX, maxZ - minZ),
                CenterWorld = origin + rotation * new Vector3((minX + maxX) * .5f, 0, (minZ + maxZ) * .5f),
                Corners = new[] {
                    origin + rotation * new Vector3(minX, 0, minZ),
                    origin + rotation * new Vector3(maxX, 0, minZ),
                    origin + rotation * new Vector3(maxX, 0, maxZ),
                    origin + rotation * new Vector3(minX, 0, maxZ)
                }
            };
            if (result.automaticBlockDetection)
            {
                var center = result.groupCenterPixel;
                if (center == null || !float.IsFinite(center.x) || !float.IsFinite(center.y) ||
                    center.x < region.x || center.x > region.x + region.width ||
                    center.y < region.y || center.y > region.y + region.height ||
                    !CapturePlaneProjection.TryPixelToPlane(metadata, new Vector2(center.x, center.y), out var worldCenter))
                { geometry = null; reason = "The detected block center is outside the captured region or cannot be projected."; return false; }
                geometry.CenterWorld = worldCenter;
                geometry.Size = DefaultCenterBoxSize;
                Vector3 across = rotation * Vector3.right * (geometry.Size.x * .5f);
                Vector3 along = rotation * Vector3.forward * (geometry.Size.y * .5f);
                geometry.Corners = new[] { worldCenter-across-along, worldCenter+across-along,
                    worldCenter+across+along, worldCenter-across+along };
            }
            reason = "Recognized word anchored to the captured tabletop projection.";
            return true;
        }

        static bool ConvexQuad(Vector2[] points)
        {
            float sign = 0;
            for (int i = 0; i < 4; i++)
            {
                var a = points[(i + 1) % 4] - points[i];
                var b = points[(i + 2) % 4] - points[(i + 1) % 4];
                float cross = a.x * b.y - a.y * b.x;
                if (Mathf.Abs(cross) < .5f || (sign != 0 && Mathf.Sign(cross) != sign)) return false;
                sign = Mathf.Sign(cross);
            }
            return true;
        }
        static bool SameFace(Vector2[] a, Vector2[] b)
        {
            foreach (var point in a)
            {
                bool found = false;
                foreach (var other in b) if ((point - other).sqrMagnitude <= .25f) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }
        static bool ShortId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
            foreach (char c in value)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') &&
                    c != '-' && c != '_' && c != '.' && c != ':') return false;
            return true;
        }
    }
}
