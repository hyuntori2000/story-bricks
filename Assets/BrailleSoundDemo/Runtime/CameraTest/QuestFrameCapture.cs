using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Meta.XR;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace BrailleSoundDemo.CameraTest
{
    /// <summary>
    /// Values fixed when a capture trigger fires. BoardPose is the floor-plane
    /// pose; BoardDepth is the footprint along its local Z axis, in meters.
    /// </summary>
    public sealed class QuestCaptureContext
    {
        public string SessionId;
        public string WorkspaceId, ExperienceSessionId;
        public int FrameRevision;
        public Pose BoardPose;
        public float BoardWidth;
        public float BoardDepth;
        public float BlockHeight;
        public float ClearSeconds;
        public string TriggerType = "hands_off";

        internal QuestCaptureContext Copy()
        {
            return (QuestCaptureContext)MemberwiseClone();
        }
    }

    /// <summary>
    /// Sends one fresh camera image per explicit RequestCapture call. It neither
    /// decides that a word is complete nor continuously streams camera images.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class QuestFrameCapture : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess cameraAccess;
        [SerializeField] private string endpoint = "http://127.0.0.1:8767/frames";
        [SerializeField, Range(50, 100)] private int jpegQuality = 92;
        [SerializeField] private float freshFrameTimeoutSeconds = 2f;
        [SerializeField] private float readbackTimeoutSeconds = 3f;
        [SerializeField] private float maximumCameraStallSeconds = 0.5f;
        [SerializeField] public bool IncludeUnityCrop;

        private DateTime lastObservedTimestamp;
        private float lastCameraAdvanceAt = float.NegativeInfinity;
        private int generation;
        private Coroutine operation;
        private UnityWebRequest activeUpload;
        private Vector2Int loggedResolution;
        private ExperienceIdentity experience = new ExperienceIdentity();

        public bool Busy { get; private set; }
        public string Status { get; private set; } = "Waiting for camera";
        public string LastFrameId { get; private set; } = "";
        public int SentCount { get; private set; }
        public int AcceptedCount { get; private set; }
        public string LastResponse { get; private set; } = "";
        public CaptureMetadata LastMetadata { get; private set; }
        public event Action<bool, string> Completed;
        /// <summary>Optional enrichment of this exact frame before encoding/upload. False cancels transmission.</summary>
        public Func<CaptureMetadata, bool> PrepareMetadata { get; set; }

        public bool IsReady => !Busy && CameraIsFresh;

        public void SetExperienceContext(string workspaceId, string experienceSessionId)
        {
            var next = new ExperienceIdentity(workspaceId, experienceSessionId);
            if (experience.Matches(next)) return;
            CancelPending("Experience changed");
            experience = next;
        }

        private bool CameraIsFresh => cameraAccess != null && cameraAccess.enabled &&
            cameraAccess.IsPlaying && cameraAccess.Timestamp != default &&
            cameraAccess.CurrentResolution.x > 0 && cameraAccess.CurrentResolution.y > 0 &&
            Time.realtimeSinceStartup - lastCameraAdvanceAt <= maximumCameraStallSeconds &&
            SystemInfo.supportsAsyncGPUReadback;

        public void Configure(PassthroughCameraAccess source, string receiverEndpoint)
        {
            CancelPending("Capture configuration changed");
            cameraAccess = source;
            endpoint = receiverEndpoint;
            lastObservedTimestamp = default;
            lastCameraAdvanceAt = float.NegativeInfinity;
            Status = "Waiting for camera";
        }

        private void Update()
        {
            if (cameraAccess != null && cameraAccess.IsPlaying &&
                cameraAccess.Timestamp != default && cameraAccess.Timestamp != lastObservedTimestamp)
            {
                lastObservedTimestamp = cameraAccess.Timestamp;
                lastCameraAdvanceAt = Time.realtimeSinceStartup;
            }
        }

        /// <param name="stillValid">
        /// Checks the same session/board revision and the caller's trigger state.
        /// A false result cancels before transmission where
        /// possible; an already transmitted HTTP body cannot be recalled.
        /// </param>
        /// <param name="frozenSnapshotStillValid">
        /// Optional session/reference gate after a private image is read, validated and encoded.
        /// When omitted, the live trigger and camera checks continue through the response.
        /// </param>
        public bool RequestCapture(QuestCaptureContext context, Func<bool> stillValid, Func<bool> frozenSnapshotStillValid = null)
        {
            if (Busy || !isActiveAndEnabled) return false;
            if (!IsReady)
            {
                Status = SystemInfo.supportsAsyncGPUReadback
                    ? "Camera not ready or frame stream stalled"
                    : "Async camera readback is unsupported";
                return false;
            }
            if (context == null || string.IsNullOrWhiteSpace(context.SessionId) ||
                context.BoardWidth <= 0f || context.BoardDepth <= 0f || context.BlockHeight < 0f ||
                !float.IsFinite(context.BoardWidth) || !float.IsFinite(context.BoardDepth) ||
                !float.IsFinite(context.BlockHeight) || !float.IsFinite(context.ClearSeconds) ||
                (context.TriggerType != "hands_off" && context.TriggerType != "manual") ||
                !ValidPose(context.BoardPose) ||
                stillValid == null || !CheckValidity(stillValid))
            {
                Status = "Capture context or trigger state is invalid";
                return false;
            }
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Status = "Receiver URL is invalid";
                return false;
            }

            Busy = true;
            Status = "Waiting for a new camera frame";
            int token = ++generation;
            DateTime triggerFrameTimestamp = cameraAccess.Timestamp;
            var validity = new CaptureValidityGuard(stillValid, frozenSnapshotStillValid);
            var ownedContext = context.Copy();
            ownedContext.WorkspaceId = experience.WorkspaceId;
            ownedContext.ExperienceSessionId = experience.ExperienceSessionId;
            operation = StartCoroutine(CaptureAndSend(ownedContext, validity, token, triggerFrameTimestamp,
                PrepareMetadata, Time.realtimeSinceStartupAsDouble));
            return true;
        }

        public void CancelPending(string reason = "Capture canceled")
        {
            ++generation;
            if (activeUpload != null)
            {
                activeUpload.Abort();
                activeUpload.Dispose();
                activeUpload = null;
            }
            if (operation != null)
            {
                StopCoroutine(operation);
                operation = null;
            }
            bool wasBusy = Busy;
            Busy = false;
            Status = reason;
            if (wasBusy) Completed?.Invoke(false, reason);
        }

        private void OnDisable()
        {
            CancelPending("Camera test paused");
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) CancelPending("Application paused; capture canceled");
        }

        private IEnumerator CaptureAndSend(QuestCaptureContext context, CaptureValidityGuard validity,
            int token, DateTime triggerFrameTimestamp, Func<CaptureMetadata,bool> prepareMetadata, double requestedAt)
        {
            // PCA queues its native texture update in Update(-100). This coroutine
            // resumes after Update and issues a readback after that GPU event.
            // A blocking Graphics.Blit/ReadPixels path would read the previous image.
            float deadline = Time.realtimeSinceStartup + freshFrameTimeoutSeconds;
            do
            {
                yield return null;
                if (!OperationIsValid(token, validity))
                {
                    Finish(token, false, "Capture canceled: trigger or calibration changed");
                    yield break;
                }
                if (Time.realtimeSinceStartup > deadline)
                {
                    Finish(token, false, "No fresh camera frame arrived after the trigger");
                    yield break;
                }
            }
            while (!CameraIsFresh || !cameraAccess.IsUpdatedThisFrame ||
                   cameraAccess.Timestamp <= triggerFrameTimestamp);

            if (!TryBeginReadback(context, out var metadata, out var readback, out var error))
            {
                Finish(token, false, error);
                yield break;
            }
            if (prepareMetadata != null)
                metadata.captureTimings = new CaptureTimingsMetadata {
                    requestMonotonicSeconds = requestedAt,
                    frameObservedMonotonicSeconds = Time.realtimeSinceStartupAsDouble
                };
            Status = "Reading one camera frame";
            deadline = Time.realtimeSinceStartup + readbackTimeoutSeconds;
            while (!readback.done)
            {
                if (!OperationIsValid(token, validity))
                {
                    Finish(token, false, "Capture canceled before upload");
                    yield break;
                }
                if (Time.realtimeSinceStartup > deadline)
                {
                    Finish(token, false, "Camera readback timed out; no image sent");
                    yield break;
                }
                yield return null;
            }
            if (!OperationIsValid(token, validity))
            {
                Finish(token, false, "Capture canceled before upload");
                yield break;
            }
            if (metadata.captureTimings != null)
                metadata.captureTimings.readbackCompletedMonotonicSeconds = Time.realtimeSinceStartupAsDouble;
            if (prepareMetadata != null)
            {
                bool prepared = false;
                string preparationError = "Capture region is invalid; no image sent";
                try { prepared = prepareMetadata(metadata); }
                catch (Exception ex) { preparationError = "Capture metadata preparation failed: " + ex.Message; }
                if (!prepared) { Finish(token, false, preparationError); yield break; }
            }
            if (!TryEncode(readback, metadata, out var jpeg, out var unityCropPng, out error))
            {
                Finish(token, false, error);
                yield break;
            }
            // The private JPEG and capture-time metadata are now fixed. Physical
            // story captures can stop requiring live hands/depth for network IO;
            // the default diagnostic continues applying its original live gate.
            if (!validity.TryFreeze(token == generation && isActiveAndEnabled, CameraIsFresh))
            {
                Finish(token, false, "Capture canceled before upload");
                yield break;
            }
            if (!TryBeginUpload(metadata, jpeg, unityCropPng, out var uploadOperation, out error))
            {
                Finish(token, false, error);
                yield break;
            }
            LastMetadata = metadata;
            LastFrameId = metadata.frameId;
            SentCount++;
            Status = "Sending one frame to Python";
            Debug.Log($"[CameraCapture] Sending {metadata.frameId}: {metadata.imageWidth}x{metadata.imageHeight}, {jpeg.Length} bytes");

            while (!uploadOperation.isDone)
            {
                if (!OperationIsValid(token, validity))
                {
                    activeUpload?.Abort();
                    Finish(token, false, "Capture invalidated during upload; response ignored");
                    yield break;
                }
                yield return null;
            }
            if (!OperationIsValid(token, validity))
            {
                Finish(token, false, "Late capture response ignored");
                yield break;
            }

            LastResponse = activeUpload.downloadHandler?.text ?? "";
            if (activeUpload.result != UnityWebRequest.Result.Success)
            {
                string responseCode = activeUpload.responseCode.ToString(CultureInfo.InvariantCulture);
                Finish(token, false, $"Python upload failed ({responseCode}): {activeUpload.error}");
                yield break;
            }
            CaptureAck ack = null;
            try { ack = JsonUtility.FromJson<CaptureAck>(LastResponse); }
            catch (ArgumentException) { /* Treat malformed responses as failure. */ }
            if (ack == null || !ack.accepted || ack.frameId != metadata.frameId || ack.sessionId != metadata.sessionId ||
                !new ExperienceIdentity(metadata.workspaceId, metadata.experienceSessionId).Matches(ack.workspaceId, ack.experienceSessionId))
            {
                Finish(token, false, "Python returned an invalid or mismatched frame acknowledgement");
                yield break;
            }

            AcceptedCount++;
            if (metadata.captureTimings != null)
            {
                metadata.captureTimings.acknowledgedMonotonicSeconds = Time.realtimeSinceStartupAsDouble;
                double latency = 1000*(metadata.captureTimings.acknowledgedMonotonicSeconds-metadata.captureTimings.requestMonotonicSeconds);
                Debug.Log("[CameraCaptureTiming] frame="+metadata.frameId+" request_to_ack_ms="+latency.ToString("F1",CultureInfo.InvariantCulture));
            }
            string resultMessage = ack.recognition?.message;
            if (string.IsNullOrWhiteSpace(resultMessage))
                resultMessage = "Frame received by Python: " + (ack.status ?? "processed");
            Finish(token, true, resultMessage);
        }

        private bool TryBeginReadback(QuestCaptureContext context, out CaptureMetadata metadata,
            out AsyncGPUReadbackRequest readback, out string error)
        {
            metadata = null;
            readback = default;
            error = "";
            try
            {
                Texture source = cameraAccess.GetTexture();
                Vector2Int size = cameraAccess.CurrentResolution;
                if (source == null || source.width != size.x || source.height != size.y)
                {
                    error = "Camera image dimensions are not ready";
                    return false;
                }
                if (loggedResolution != size)
                {
                    loggedResolution = size;
                    Debug.Log("[CameraResolution] requested=" + cameraAccess.RequestedResolution.x + "x" + cameraAccess.RequestedResolution.y +
                              " current=" + size.x + "x" + size.y);
                }
                // All metadata is copied before yielding. Never obtain a new pose
                // after the GPU or Python request completes.
                DateTime timestamp = cameraAccess.Timestamp;
                Pose sensorPose = cameraAccess.GetCameraPose();
                var intrinsics = cameraAccess.Intrinsics;
                if (!float.IsFinite(intrinsics.FocalLength.x) || !float.IsFinite(intrinsics.FocalLength.y) ||
                    !float.IsFinite(intrinsics.PrincipalPoint.x) || !float.IsFinite(intrinsics.PrincipalPoint.y) ||
                    intrinsics.FocalLength.x <= 0f || intrinsics.FocalLength.y <= 0f ||
                    intrinsics.SensorResolution.x <= 0 || intrinsics.SensorResolution.y <= 0 ||
                    !ValidPose(sensorPose))
                {
                    error = "Camera calibration or capture-time pose is invalid";
                    return false;
                }
                metadata = new CaptureMetadata
                {
                    sessionId = context.SessionId,
                    workspaceId = context.WorkspaceId,
                    experienceSessionId = context.ExperienceSessionId,
                    frameId = Guid.NewGuid().ToString("N"),
                    frameRevision = context.FrameRevision,
                    cameraEye = cameraAccess.CameraPosition.ToString().ToLowerInvariant(),
                    imageWidth = size.x,
                    imageHeight = size.y,
                    captureTimestampUtc = timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    cameraPositionWorld = sensorPose.position,
                    cameraRotationWorld = sensorPose.rotation,
                    intrinsics = new IntrinsicsMetadata
                    {
                        focalLength = intrinsics.FocalLength,
                        principalPoint = intrinsics.PrincipalPoint,
                        sensorResolution = new PixelSize { x = intrinsics.SensorResolution.x, y = intrinsics.SensorResolution.y },
                        sensorCrop = SensorCrop(intrinsics.SensorResolution, size)
                    },
                    board = new BoardMetadata
                    {
                        position = context.BoardPose.position,
                        rotation = context.BoardPose.rotation,
                        width = context.BoardWidth,
                        height = context.BoardDepth,
                        blockHeight = context.BlockHeight
                    },
                    trigger = new TriggerMetadata { type = context.TriggerType, clearSeconds = context.ClearSeconds }
                };
                readback = AsyncGPUReadback.Request(source, 0, TextureFormat.RGBA32);
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not read camera frame: " + ex.Message;
                return false;
            }
        }

        private bool TryEncode(AsyncGPUReadbackRequest readback, CaptureMetadata metadata,
            out byte[] jpeg, out byte[] unityCropPng, out string error)
        {
            jpeg = null;
            unityCropPng = null;
            error = "";
            int width = metadata.imageWidth, height = metadata.imageHeight;
            if (readback.hasError)
            {
                error = "GPU camera readback failed; no image sent";
                return false;
            }
            try
            {
                var pixels = readback.GetData<byte>();
                if (pixels.Length != width * height * 4)
                {
                    error = "Unexpected camera readback length";
                    return false;
                }
                // Encode the private CPU copy. The live camera may now advance
                // without changing this image or its capture-time metadata.
                byte[] snapshot = pixels.ToArray();
                jpeg = EncodeRgbaSnapshot(snapshot, width, height, jpegQuality);
                if (jpeg == null || jpeg.Length == 0)
                {
                    error = "JPEG encoding returned an empty image";
                    return false;
                }
                if (IncludeUnityCrop && metadata.recognitionRegion != null)
                {
                    metadata.unityCrop = PlanUnityCrop(width, height, metadata.recognitionRegion);
                    if (metadata.unityCrop != null)
                    {
                        unityCropPng = EncodeRgbaCropPng(snapshot, width, height, metadata.unityCrop);
                        Debug.Log("[CameraCrop] targetCrop=" + metadata.unityCrop.width + "x" + metadata.unityCrop.height +
                                  " cropStages=1 resized=false");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not encode camera frame: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Encodes packed RGBA pixels in Unity texture row order (bottom row first).
        /// Kept separate so the exact transport encoder can be checked using an
        /// asymmetric CPU fixture without opening a camera or transmitting data.
        /// </summary>
        public static byte[] EncodeRgbaSnapshot(byte[] pixels, int width, int height, int quality)
        {
            if (width <= 0 || height <= 0 || pixels == null || pixels.Length != checked(width * height * 4))
                throw new ArgumentException("Expected tightly packed RGBA pixels matching the image dimensions.");
            Texture2D snapshot = null;
            try
            {
                snapshot = new Texture2D(width, height, TextureFormat.RGBA32, false);
                snapshot.LoadRawTextureData(pixels);
                return snapshot.EncodeToJPG(Mathf.Clamp(quality, 1, 100));
            }
            finally
            {
                if (snapshot != null)
                {
                    if (Application.isPlaying) Destroy(snapshot);
                    else DestroyImmediate(snapshot);
                }
            }
        }

        // Send the final target rectangle directly, at native pixel resolution.
        // Python masks its projected yellow-box polygon without another resize
        // or rectangular crop. Coordinates stay in the original image space.
        public static UnityCropMetadata PlanUnityCrop(int width, int height, RecognitionRegionMetadata region)
        {
            if (width < 1 || height < 1 || region == null || region.pixelOrigin != "top-left" ||
                region.x < 0 || region.y < 0 || region.width < 1 || region.height < 1 ||
                region.x > width - region.width || region.y > height - region.height) return null;
            return new UnityCropMetadata {
                x = region.x, y = region.y, width = region.width, height = region.height
            };
        }

        public static byte[] EncodeRgbaCropPng(byte[] pixels, int width, int height, UnityCropMetadata crop)
        {
            if (pixels == null || pixels.Length != checked(width * height * 4) || crop == null ||
                crop.x < 0 || crop.y < 0 || crop.width < 1 || crop.height < 1 ||
                crop.x > width - crop.width || crop.y > height - crop.height)
                throw new ArgumentException("Invalid crop of the packed RGBA snapshot.");
            byte[] cropped = new byte[checked(crop.width * crop.height * 4)];
            // Unity's CPU bytes begin at the bottom; crop coordinates begin at
            // the top. Preserve bottom-first rows for Unity's PNG encoder.
            int bottom = height - crop.y - crop.height;
            for (int row = 0; row < crop.height; row++)
                Buffer.BlockCopy(pixels, ((bottom + row) * width + crop.x) * 4,
                                 cropped, row * crop.width * 4, crop.width * 4);
            Texture2D texture = null;
            try
            {
                texture = new Texture2D(crop.width, crop.height, TextureFormat.RGBA32, false);
                texture.LoadRawTextureData(cropped);
                return texture.EncodeToPNG();
            }
            finally
            {
                if (texture != null)
                {
                    if (Application.isPlaying) Destroy(texture);
                    else DestroyImmediate(texture);
                }
            }
        }

        private bool TryBeginUpload(CaptureMetadata metadata, byte[] jpeg, byte[] unityCropPng,
            out UnityWebRequestAsyncOperation uploadOperation, out string error)
        {
            uploadOperation = null;
            error = "";
            try
            {
                if (metadata.captureTimings != null)
                    metadata.captureTimings.uploadStartedMonotonicSeconds = Time.realtimeSinceStartupAsDouble;
                var form = new List<IMultipartFormSection>
                {
                    new MultipartFormDataSection("metadata", JsonUtility.ToJson(metadata), "application/json"),
                    new MultipartFormFileSection("image", jpeg, metadata.frameId + ".jpg", "image/jpeg")
                };
                if (unityCropPng != null)
                    form.Add(new MultipartFormFileSection("unityCrop", unityCropPng, metadata.frameId + "_stage1.png", "image/png"));
                activeUpload = UnityWebRequest.Post(endpoint, form);
                activeUpload.timeout = 12;
                uploadOperation = activeUpload.SendWebRequest();
                return true;
            }
            catch (Exception ex)
            {
                activeUpload?.Dispose();
                activeUpload = null;
                error = "Could not start Python upload: " + ex.Message;
                return false;
            }
        }

        private bool OperationIsValid(int token, CaptureValidityGuard validity)
        {
            return validity.CanContinue(token == generation && isActiveAndEnabled, CameraIsFresh);
        }

        private static bool CheckValidity(Func<bool> stillValid)
        {
            try { return stillValid != null && stillValid(); }
            catch (Exception ex)
            {
                Debug.LogWarning("[CameraCapture] Capture validity check failed: " + ex.Message);
                return false;
            }
        }

        private static bool ValidPose(Pose pose)
        {
            var p = pose.position;
            var q = pose.rotation;
            float norm = Quaternion.Dot(q, q);
            return float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z) &&
                   float.IsFinite(norm) && norm > 0.5f && norm < 1.5f;
        }

        private void Finish(int token, bool success, string message)
        {
            if (token != generation) return;
            activeUpload?.Dispose();
            activeUpload = null;
            operation = null;
            Busy = false;
            Status = message;
            Debug.Log("[CameraCapture] " + message);
            Completed?.Invoke(success, message);
        }

        private static SensorCropMetadata SensorCrop(Vector2Int sensor, Vector2Int image)
        {
            // Matches MRUK 209's centered sensor crop for CurrentResolution.
            Vector2 scale = new Vector2((float)image.x / sensor.x, (float)image.y / sensor.y);
            scale /= Mathf.Max(scale.x, scale.y);
            return new SensorCropMetadata
            {
                x = sensor.x * (1f - scale.x) * 0.5f,
                y = sensor.y * (1f - scale.y) * 0.5f,
                width = sensor.x * scale.x,
                height = sensor.y * scale.y
            };
        }

        [Serializable]
        public sealed class CaptureMetadata
        {
            public int schemaVersion = 1;
            public string sessionId;
            public string workspaceId, experienceSessionId;
            public string frameId;
            public int frameRevision;
            public string cameraEye;
            public int imageWidth;
            public int imageHeight;
            public string pixelOrigin = "top-left";
            public string orientation = "upright";
            public string encoding = "jpeg";
            public string coordinateSpace = "Unity world; meters; +X right, +Y up, +Z forward";
            public string imageToViewport = "u=x/width; v=1-y/height";
            public string projectionModel = "mruk-209-centered-sensor-crop";
            public string captureTimestampUtc;
            public Vector3 cameraPositionWorld;
            public Quaternion cameraRotationWorld;
            public IntrinsicsMetadata intrinsics;
            public BoardMetadata board;
            public TriggerMetadata trigger;
            public HandApproachMetadata handApproach;
            public RecognitionRegionMetadata recognitionRegion;
            public UnityCropMetadata unityCrop;
            public string recognitionRegionIssue;
            public CaptureTimingsMetadata captureTimings;
        }

        [Serializable]
        public sealed class UnityCropMetadata
        {
            public int x, y, width, height;
            public string pixelOrigin = "top-left";
            public string encoding = "png";
            public string source = "unity_rgba_snapshot";
        }

        [Serializable]
        public sealed class HandApproachMetadata
        {
            public string hand;
            public string interpretation;
            public Vector3 positionWorld;
            public Vector3 headPositionWorld;
            public float signedGapMeters;
            public float surfaceHeightMeters;
            public float headToPointDistanceMeters;
            public double sampleMonotonicSeconds;
            public double liftStartedMonotonicSeconds;
            public double confirmedMonotonicSeconds;
            public int sampleFrame;
            public int confirmedFrame;
            public double sampleAgeAtConfirmationSeconds;
            public int frameAgeAtConfirmation;
            public double sampleAgeAtCaptureObservationSeconds;
        }

        [Serializable]
        public sealed class RecognitionRegionMetadata
        {
            public int x, y, width, height;
            public string pixelOrigin = "top-left";
            public string source = "recent_hand_approach";
            public bool clipped;
            public float retainedFraction;
            public Vector3 centerWorld;
            public float physicalWidthMeters;
            public float physicalDepthMeters;
            // Explicit acquisition convention for the timed pinch box. Full-image top-left
            // pixels ordered TL, TR, BR, BL; these physical corners are not padded or clipped.
            public string readingOrientation;
            public Vector2[] readingQuad;
        }

        [Serializable]
        public sealed class CaptureTimingsMetadata
        {
            // Application observation times, not interchangeable with native exposure timestamps.
            public double requestMonotonicSeconds;
            public double frameObservedMonotonicSeconds;
            public double readbackCompletedMonotonicSeconds;
            public double uploadStartedMonotonicSeconds;
            // Filled locally after ACK; the uploaded metadata contains zero here.
            public double acknowledgedMonotonicSeconds;
        }

        [Serializable]
        public sealed class IntrinsicsMetadata
        {
            // PrincipalPoint is the native top-down sensor coordinate. Projection
            // flips its Y just like MRUK209.ViewportPointToRay. The world camera
            // pose already includes LensOffset; do not apply that offset twice.
            public Vector2 focalLength;
            public Vector2 principalPoint;
            public PixelSize sensorResolution;
            public SensorCropMetadata sensorCrop;
        }

        [Serializable]
        public sealed class PixelSize
        {
            public int x;
            public int y;
        }

        [Serializable]
        public sealed class SensorCropMetadata
        {
            public float x;
            public float y;
            public float width;
            public float height;
        }

        [Serializable]
        public sealed class BoardMetadata
        {
            public Vector3 position;
            public Quaternion rotation;
            public float width;
            public float height;
            public float blockHeight;
        }

        [Serializable]
        public sealed class TriggerMetadata
        {
            public string type = "hands_off";
            public string gesture;
            public bool savedTableReference, depthFreshAtCapture;
            public float delaySeconds;
            public float clearSeconds;
        }

        [Serializable]
        private sealed class CaptureAck
        {
            public bool accepted;
            public string frameId;
            public string sessionId;
            public string workspaceId, experienceSessionId;
            public string status;
            public RecognitionAck recognition;
        }

        [Serializable]
        private sealed class RecognitionAck
        {
            public string status;
            public string message;
            public string word;
        }
    }
}
