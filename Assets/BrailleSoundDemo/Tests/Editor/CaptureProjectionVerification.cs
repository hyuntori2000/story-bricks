using System;
using System.Collections.Generic;
using System.IO;
using BrailleSoundDemo.CameraTest;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    public static class CaptureProjectionVerification
    {
        [Serializable] sealed class Report
        {
            public bool passed;
            public bool syntheticGeometry = true;
            public bool physicalOverlayAlignmentVerified = false;
            public List<string> checks = new List<string>();
        }

        static QuestFrameCapture.CaptureMetadata Fixture()
        {
            return new QuestFrameCapture.CaptureMetadata {
                imageWidth = 640, imageHeight = 480,
                cameraPositionWorld = new Vector3(0, 1, 0),
                cameraRotationWorld = Quaternion.Euler(90, 0, 0),
                intrinsics = new QuestFrameCapture.IntrinsicsMetadata {
                    focalLength = new Vector2(320, 320), principalPoint = new Vector2(320, 240),
                    sensorResolution = new QuestFrameCapture.PixelSize { x = 640, y = 480 },
                    sensorCrop = new QuestFrameCapture.SensorCropMetadata { x = 0, y = 0, width = 640, height = 480 }
                },
                board = new QuestFrameCapture.BoardMetadata {
                    position = Vector3.zero, rotation = Quaternion.identity, width = .6f, height = .4f, blockHeight = 0
                }
            };
        }

        static bool At(QuestFrameCapture.CaptureMetadata metadata, Vector2 pixel, Vector3 expected)
        {
            return CapturePlaneProjection.TryPixelToPlane(metadata, pixel, out var point) &&
                   Vector3.Distance(point, expected) < .0001f;
        }

        public static void Run()
        {
            var report = new Report();
            Action<bool, string> check = (ok, message) => {
                if (!ok) throw new InvalidOperationException("Capture projection: " + message);
                report.checks.Add(message);
            };
            var center = new Vector2(320, 240);
            var m = Fixture();
            check(At(m, center, Vector3.zero), "Center pixel intersects the captured horizontal surface.");
            check(At(m, new Vector2(480,240), new Vector3(.5f,0,0)), "Right image point maps right by the pinhole geometry.");
            check(At(m, new Vector2(320,120), new Vector3(0,0,.375f)), "Top-left image coordinates flip Y once, matching MRUK viewport orientation.");

            m.board.position = new Vector3(0,.75f,0);
            check(At(m, new Vector2(480,240), new Vector3(.125f,.75f,0)), "Table height changes intersection depth; floor height is not assumed.");
            m.board.blockHeight = .02f;
            check(At(m, center, new Vector3(0,.77f,0)), "An explicit surface offset is applied once along the plane normal.");

            m = Fixture();
            m.cameraPositionWorld = new Vector3(-.5f,1,2);
            m.cameraRotationWorld = Quaternion.Euler(0,90,0) * Quaternion.Euler(90,0,0);
            check(At(m, new Vector2(480,240), new Vector3(-.5f,0,1.5f)), "Captured camera translation and rotation produce world-fixed coordinates.");

            m = Fixture();
            m.intrinsics.principalPoint = new Vector2(300,220);
            check(At(m, center, new Vector3(.0625f,0,-.0625f)), "Native off-center principal point uses the sensor-height Y reflection.");

            m = Fixture(); m.imageHeight = 360;
            m.intrinsics.sensorCrop = new QuestFrameCapture.SensorCropMetadata { x=0,y=60,width=640,height=360 };
            check(At(m, new Vector2(320,0), new Vector3(0,0,.5625f)), "A 16:9 image uses its centered crop within a 4:3 sensor.");
            m.intrinsics.sensorCrop = null; m.projectionModel = null;
            check(At(m, new Vector2(320,0), new Vector3(0,0,.5625f)), "Legacy captures derive the same crop solely from stored dimensions.");

            m = Fixture();
            var quad = new[] {new Vector2(256,176),new Vector2(384,176),new Vector2(384,304),new Vector2(256,304)};
            check(CapturePlaneProjection.TryQuadCenterToPlane(m, quad, out var average) && average.magnitude < .0001f,
                "A four-corner surface center is calculated from projected world corners.");
            quad[3] = new Vector2(-1,304);
            check(!CapturePlaneProjection.TryQuadCenterToPlane(m,quad,out _), "One invalid face corner rejects the complete overlay position.");

            m = Fixture(); m.cameraRotationWorld = Quaternion.identity;
            check(!CapturePlaneProjection.TryPixelToPlane(m,center,out _), "A ray parallel to the surface is rejected.");
            m.cameraRotationWorld = Quaternion.Euler(-90,0,0);
            check(!CapturePlaneProjection.TryPixelToPlane(m,center,out _), "A surface behind the camera is rejected.");
            m = Fixture(); m.cameraPositionWorld = new Vector3(0,10,0);
            check(!CapturePlaneProjection.TryPixelToPlane(m,center,out _), "An implausibly distant intersection is rejected.");

            m = Fixture(); m.board.rotation = Quaternion.Euler(0,0,90);
            m.cameraPositionWorld = Vector3.right; m.cameraRotationWorld = Quaternion.Euler(0,-90,0);
            check(At(m,center,Vector3.zero), "The plane normal comes from captured surface rotation, not a hardcoded world-up normal.");

            m = Fixture(); m.cameraRotationWorld = new Quaternion(0,0,0,0);
            check(!CapturePlaneProjection.TryPixelToRay(m,center,out _), "An invalid camera quaternion cannot produce a label.");
            m = Fixture(); m.intrinsics.focalLength = new Vector2(0,320);
            check(!CapturePlaneProjection.TryPixelToRay(m,center,out _), "Zero focal length is rejected.");
            m = Fixture(); m.intrinsics.sensorCrop.x = 100;
            check(!CapturePlaneProjection.TryPixelToRay(m,center,out _), "An out-of-sensor crop is rejected.");
            m = Fixture(); m.cameraPositionWorld = new Vector3(float.NaN,1,0);
            check(!CapturePlaneProjection.TryPixelToRay(m,center,out _), "Non-finite captured geometry is rejected.");
            m = Fixture(); m.orientation = "rotated-90";
            check(!CapturePlaneProjection.TryPixelToRay(m,center,out _), "Unimplemented image rotations are rejected instead of silently misprojected.");
            m = Fixture(); m.pixelOrigin = "bottom-left";
            check(!CapturePlaneProjection.TryPixelToRay(m,center,out _), "An incompatible pixel-origin convention is rejected.");
            m = Fixture(); m.projectionModel = "unknown";
            check(!CapturePlaneProjection.TryPixelToRay(m,center,out _), "Unknown projection conventions fail closed.");

            report.passed = true;
            Directory.CreateDirectory("Documentation");
            File.WriteAllText("Documentation/CAPTURE_PROJECTION_TEST_RESULTS.json", JsonUtility.ToJson(report,true));
            Debug.Log("CAPTURE_PROJECTION_TESTS_PASSED " + report.checks.Count);
        }
    }
}
