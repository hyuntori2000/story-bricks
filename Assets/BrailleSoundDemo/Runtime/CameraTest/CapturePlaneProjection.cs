using System;
using System.Collections.Generic;
using UnityEngine;

namespace BrailleSoundDemo.CameraTest
{
    /// <summary>
    /// Projects decoded JPEG pixel coordinates with the exact captured sensor pose.
    /// Formula matches MRUK 209 PassthroughCameraAccess.ViewportPointToLocalRay:
    /// a centered sensor crop and a vertically mirrored native principal point.
    /// A known surface plane is required; image coordinates alone have no depth.
    /// </summary>
    public static class CapturePlaneProjection
    {
        public const float MaximumDistanceMeters = 5f;

        public static bool TryPixelToRay(QuestFrameCapture.CaptureMetadata metadata,
            Vector2 pixelTopLeft, out Ray worldRay)
        {
            worldRay = default;
            if (metadata == null || metadata.imageWidth <= 0 || metadata.imageHeight <= 0 ||
                metadata.pixelOrigin != "top-left" || metadata.orientation != "upright" ||
                (!string.IsNullOrEmpty(metadata.projectionModel) && metadata.projectionModel != "mruk-209-centered-sensor-crop") ||
                !Finite(pixelTopLeft) || pixelTopLeft.x < 0 || pixelTopLeft.x > metadata.imageWidth ||
                pixelTopLeft.y < 0 || pixelTopLeft.y > metadata.imageHeight ||
                !Finite(metadata.cameraPositionWorld) || !TryRotation(metadata.cameraRotationWorld, out var rotation))
                return false;

            var calibration = metadata.intrinsics;
            if (calibration == null || calibration.sensorResolution == null ||
                calibration.sensorResolution.x <= 0 || calibration.sensorResolution.y <= 0 ||
                !Finite(calibration.focalLength) || calibration.focalLength.x <= 0 || calibration.focalLength.y <= 0 ||
                !Finite(calibration.principalPoint)) return false;

            float sensorWidth = calibration.sensorResolution.x;
            float sensorHeight = calibration.sensorResolution.y;
            var crop = calibration.sensorCrop;
            // Older captures had no explicit crop. Derive the same centered crop
            // from those captures' image/sensor sizes, never from the live camera.
            float cropX, cropY, cropWidth, cropHeight;
            if (crop == null)
            {
                float scaleX = metadata.imageWidth / sensorWidth;
                float scaleY = metadata.imageHeight / sensorHeight;
                float scale = Mathf.Max(scaleX, scaleY);
                cropWidth = sensorWidth * scaleX / scale;
                cropHeight = sensorHeight * scaleY / scale;
                cropX = (sensorWidth - cropWidth) * .5f;
                cropY = (sensorHeight - cropHeight) * .5f;
            }
            else
            {
                cropX = crop.x; cropY = crop.y; cropWidth = crop.width; cropHeight = crop.height;
                if (!Finite(cropX) || !Finite(cropY) || !Finite(cropWidth) || !Finite(cropHeight) ||
                    cropWidth <= 0 || cropHeight <= 0 || cropX < 0 || cropY < 0 ||
                    cropX + cropWidth > sensorWidth + .01f || cropY + cropHeight > sensorHeight + .01f)
                    return false;
            }

            float u = pixelTopLeft.x / metadata.imageWidth;
            float v = 1f - pixelTopLeft.y / metadata.imageHeight;
            var directionInCamera = new Vector3(
                (cropX + cropWidth * u - calibration.principalPoint.x) / calibration.focalLength.x,
                (cropY + cropHeight * v - (sensorHeight - calibration.principalPoint.y)) / calibration.focalLength.y,
                1f);
            var directionInWorld = rotation * directionInCamera;
            if (!Finite(directionInWorld) || directionInWorld.sqrMagnitude < .000001f) return false;
            worldRay = new Ray(metadata.cameraPositionWorld, directionInWorld.normalized);
            return true;
        }

        public static bool TryPixelToPlane(QuestFrameCapture.CaptureMetadata metadata,
            Vector2 pixelTopLeft, out Vector3 worldPoint)
        {
            worldPoint = default;
            if (!TryPixelToRay(metadata, pixelTopLeft, out var ray) || metadata.board == null ||
                !Finite(metadata.board.position) || !Finite(metadata.board.blockHeight) || metadata.board.blockHeight < 0 ||
                !TryRotation(metadata.board.rotation, out var rotation)) return false;
            var normal = rotation * Vector3.up;
            var pointOnPlane = metadata.board.position + normal * metadata.board.blockHeight;
            float denominator = Vector3.Dot(normal, ray.direction);
            if (Mathf.Abs(denominator) < .0001f) return false;
            float distance = Vector3.Dot(normal, pointOnPlane - ray.origin) / denominator;
            if (!Finite(distance) || distance <= .001f || distance > MaximumDistanceMeters) return false;
            worldPoint = ray.GetPoint(distance);
            return Finite(worldPoint);
        }

        /// <summary>Average projected corners, avoiding perspective bias from averaging pixels first.</summary>
        public static bool TryQuadCenterToPlane(QuestFrameCapture.CaptureMetadata metadata,
            IReadOnlyList<Vector2> quadTopLeft, out Vector3 worldPoint)
        {
            worldPoint = default;
            if (quadTopLeft == null || quadTopLeft.Count != 4) return false;
            for (int i = 0; i < 4; i++)
            {
                if (!TryPixelToPlane(metadata, quadTopLeft[i], out var corner))
                { worldPoint = default; return false; }
                worldPoint += corner * .25f;
            }
            return true;
        }

        static bool TryRotation(Quaternion q, out Quaternion normalized)
        {
            normalized = Quaternion.identity;
            float norm = Quaternion.Dot(q, q);
            if (!Finite(norm) || Mathf.Abs(norm - 1f) > .02f) return false;
            float inverseLength = 1f / Mathf.Sqrt(norm);
            normalized = new Quaternion(q.x * inverseLength, q.y * inverseLength, q.z * inverseLength, q.w * inverseLength);
            return true;
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);
        static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
