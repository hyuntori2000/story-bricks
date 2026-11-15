using System;
using System.IO;
using BrailleSoundDemo.CameraTest;
using UnityEditor;
using UnityEngine;

namespace BrailleSoundDemo.Editor
{
    /// <summary>
    /// CPU-only transport fixture. This does not test the Quest sensor, GPU
    /// readback, native camera texture orientation, hand tracking, or networking.
    /// Decode fixture.jpg with Python/OpenCV and compare expected.json.
    /// </summary>
    public static class CameraFrameCodecVerification
    {
        [MenuItem("Word Story Demo/Camera Test/Write CPU Codec Fixture")]
        public static void Run()
        {
            const int width = 64, height = 48;
            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool top = y >= height / 2;
                    bool left = x < width / 2;
                    // y=0 is the bottom row in Unity's raw texture layout.
                    Color32 color = top
                        ? (left ? new Color32(255, 0, 0, 255) : new Color32(0, 255, 0, 255))
                        : (left ? new Color32(0, 0, 255, 255) : new Color32(255, 255, 0, 255));
                    int offset = (y * width + x) * 4;
                    rgba[offset] = color.r;
                    rgba[offset + 1] = color.g;
                    rgba[offset + 2] = color.b;
                    rgba[offset + 3] = color.a;
                }
            }

            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../work/camera-codec-check"));
            var arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < arguments.Length; i++)
                if (arguments[i] == "-cameraCodecOutput") directory = Path.GetFullPath(arguments[i + 1]);
            Directory.CreateDirectory(directory);

            // This is the same method used for a received AsyncGPUReadback buffer.
            byte[] jpeg = QuestFrameCapture.EncodeRgbaSnapshot(rgba, width, height, 92);
            if (jpeg == null || jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8)
                throw new InvalidOperationException("Unity encoder did not return a JPEG.");
            File.WriteAllBytes(Path.Combine(directory, "fixture.jpg"), jpeg);
            File.WriteAllBytes(Path.Combine(directory, "fixture.rgba"), rgba);

            var expected = new FixtureExpectation
            {
                width = width,
                height = height,
                samples = new[]
                {
                    new ColorSample { name = "top-left", x = 8, y = 8, rgb = new[] { 255, 0, 0 } },
                    new ColorSample { name = "top-right", x = 55, y = 8, rgb = new[] { 0, 255, 0 } },
                    new ColorSample { name = "bottom-left", x = 8, y = 39, rgb = new[] { 0, 0, 255 } },
                    new ColorSample { name = "bottom-right", x = 55, y = 39, rgb = new[] { 255, 255, 0 } }
                }
            };
            File.WriteAllText(Path.Combine(directory, "expected.json"), JsonUtility.ToJson(expected, true));

            var metadata = new QuestFrameCapture.CaptureMetadata
            {
                sessionId = "synthetic-codec-preflight",
                frameId = "codec-fixture-001",
                frameRevision = 3,
                cameraEye = "left",
                imageWidth = width,
                imageHeight = height,
                captureTimestampUtc = "2026-11-14T00:00:00.0000000Z",
                cameraPositionWorld = new Vector3(0.1f, 1.2f, -0.3f),
                cameraRotationWorld = Quaternion.identity,
                intrinsics = new QuestFrameCapture.IntrinsicsMetadata
                {
                    focalLength = new Vector2(40, 40),
                    principalPoint = new Vector2(32, 24),
                    sensorResolution = new QuestFrameCapture.PixelSize { x = width, y = height },
                    sensorCrop = new QuestFrameCapture.SensorCropMetadata { x = 0, y = 0, width = width, height = height }
                },
                board = new QuestFrameCapture.BoardMetadata
                {
                    position = Vector3.zero,
                    rotation = Quaternion.identity,
                    width = 0.6f,
                    height = 0.4f,
                    blockHeight = 0.02f
                },
                trigger = new QuestFrameCapture.TriggerMetadata { clearSeconds = 1f }
            };
            string metadataJson = JsonUtility.ToJson(metadata, true);
            File.WriteAllText(Path.Combine(directory, "metadata.json"), metadataJson);
            Debug.Log("CAMERA_CODEC_FIXTURE_WRITTEN " + directory +
                " | CPU fixture only; Python decode checks and Quest camera checks remain separate.");
        }

        [Serializable]
        private sealed class FixtureExpectation
        {
            public string fixture = "synthetic-cpu-codec-preflight";
            public string rawRowOrigin = "bottom-left";
            public string decodedRowOrigin = "top-left";
            public int width;
            public int height;
            public int channelTolerance = 12;
            public ColorSample[] samples;
        }

        [Serializable]
        private sealed class ColorSample
        {
            public string name;
            public int x;
            public int y;
            public int[] rgb;
        }
    }
}
