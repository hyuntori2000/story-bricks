using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    /// <summary>Offline checks only. Synthetic 2x2 fixtures are never shown in the user app.</summary>
    public static class ImagePipelineVerification
    {
        [Serializable] sealed class Report { public bool passed; public string[] checks; }
        sealed class CountingProvider : IStoryImageProvider
        {
            public int calls;
            public IEnumerator Generate(ImageGenerationRequest request, Action<StoryImageResult> completed)
            { calls++; yield break; }
        }
        public static void Run()
        {
            var checks = new List<string>();
            var go = new GameObject("Offline image-pipeline verification");
            var pipeline = go.AddComponent<StoryImagePipeline>();
            pipeline.SendMessage("Awake");
            var provider = new CountingProvider(); pipeline.Provider = provider;
            Texture2D fixture = null;
            try
            {
                var story = new StoryOutput { sessionId = "offline-image-check", storyText = "The pink cat found a quiet rainbow." };
                story.words.Add(new WordRecord { index = 0, word = "CAT", componentIds = new[] { "C", "A", "T" } });
                story.turns.Add(new ConversationTurn { word = "CAT", question = "Where does the cat go?", answer = "She is pink!", componentIds = new[] { "C", "A", "T" } });
                Check(!pipeline.EnableImagePreparation && pipeline.State == StoryImageJobState.Disabled, "Image preparation defaults OFF", checks);
                Check(!pipeline.Prepare(story, false) && pipeline.LastRequestJson == "" && pipeline.CurrentRequestId == "" && provider.calls == 0,
                    "OFF prepares no request and makes zero provider calls", checks);
                pipeline.ResetSession(story.sessionId); pipeline.EnableImagePreparation = true;
                Check(pipeline.Prepare(story, false) && pipeline.State == StoryImageJobState.Prepared && provider.calls == 0,
                    "Explicit preparation is local and still makes zero provider calls", checks);
                var request = JsonUtility.FromJson<ImageGenerationRequest>(pipeline.LastRequestJson);
                Check(request.preparationOnly && request.storyText == story.storyText && request.verbatimTurns[0].answer == "She is pink!" && request.verbatimTurns[0].question == story.turns[0].question,
                    "Request preserves final story and off-topic answers verbatim", checks);
                story.words[0].componentIds[0] = "MUTATED"; story.turns[0].answer = "MUTATED";
                request = JsonUtility.FromJson<ImageGenerationRequest>(pipeline.LastRequestJson);
                Check(request.words[0].componentIds[0] == "C" && request.verbatimTurns[0].answer == "She is pink!", "Request owns an independent context snapshot", checks);
                fixture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                fixture.SetPixels(new[] { Color.white, Color.black, Color.gray, Color.white }); fixture.Apply();
                byte[] png = fixture.EncodeToPNG(), jpg = fixture.EncodeToJPG();
                Check(StoryImagePipeline.TryReadImageHeader(png, out int w, out int h, out string format, out _) && w == 2 && h == 2 && format == "png", "PNG dimensions validated before decoding", checks);
                Check(StoryImagePipeline.TryReadImageHeader(jpg, out w, out h, out format, out _) && w == 2 && h == 2 && format == "jpeg", "JPEG dimensions validated before decoding", checks);
                var huge = (byte[])png.Clone(); huge[16] = 0; huge[17] = 0; huge[18] = 16; huge[19] = 1;
                Check(!StoryImagePipeline.TryReadImageHeader(huge, out _, out _, out _, out _), "4097-pixel PNG rejected before texture allocation", checks);
                Check(!StoryImagePipeline.TryReadImageHeader(new byte[StoryImagePipeline.MaximumImageBytes + 1], out _, out _, out _, out _), "Encoded images over 8 MB rejected", checks);
                Check(!StoryImagePipeline.TryReadImageHeader(new byte[] { 255, 216, 255, 224, 255, 255, 0, 0, 0, 0 }, out _, out _, out _, out _), "Truncated JPEG segments rejected", checks);
                string session = pipeline.CurrentSessionId, id = pipeline.CurrentRequestId;
                Check(!pipeline.LoadResult("old-session", id, png) && !pipeline.LoadResult(session, "old-request", png), "Wrong session and request results rejected", checks);
                Check(pipeline.LoadResult(session, id, png) && pipeline.State == StoryImageJobState.Loading && pipeline.Texture == null, "Image decoding is deferred until Unity Update", checks);
                png[0] = 0;
                pipeline.SendMessage("Update");
                Check(pipeline.State == StoryImageJobState.Ready && pipeline.Texture != null && pipeline.Texture.width == 2,
                    "Runtime PNG decoding succeeds and input bytes are privately copied", checks);
                Check(pipeline.LoadResult(session, id, jpg), "Runtime JPEG bytes accepted for existing request", checks);
                pipeline.SendMessage("Update");
                Check(pipeline.State == StoryImageJobState.Ready && pipeline.Texture.width == 2 && pipeline.Texture.height == 2, "Runtime JPEG decoding succeeds without rebuild", checks);
                Check(pipeline.LoadResult(session, id, jpg), "Second image can be queued", checks);
                pipeline.ResetSession("new-session"); pipeline.SendMessage("Update");
                Check(pipeline.Texture == null && pipeline.CurrentRequestId == "" && !pipeline.LoadResult(session, id, jpg), "Reset cancels queued decoding and rejects old-session results", checks);
                var fresh = new StoryOutput { sessionId = "new-session", storyText = "Another story." };
                Check(pipeline.Prepare(fresh, false), "New session can prepare independently", checks);
                id = pipeline.CurrentRequestId; pipeline.LoadResult(fresh.sessionId, id, jpg); pipeline.EnableImagePreparation = false; pipeline.SendMessage("Update");
                Check(pipeline.State == StoryImageJobState.Disabled && pipeline.Texture == null && !pipeline.LoadResult(fresh.sessionId, id, jpg), "Turning OFF cancels pending images", checks);
                bool workerRejected = Task.Run(() => { try { pipeline.ResetSession("worker"); return false; } catch (InvalidOperationException) { return true; } }).GetAwaiter().GetResult();
                Check(workerRejected && pipeline.CurrentSessionId == fresh.sessionId, "Background mutations are rejected before Unity access", checks);
                Check(provider.calls == 0, "No verification path invokes an image provider or external API", checks);
                Directory.CreateDirectory("Documentation");
                File.WriteAllText("Documentation/IMAGE_PIPELINE_TEST_RESULTS.json", JsonUtility.ToJson(new Report { passed = true, checks = checks.ToArray() }, true));
                Debug.Log("IMAGE_PIPELINE_VERIFICATION_OK " + checks.Count + " offline checks");
            }
            finally
            {
                if (fixture != null) UnityEngine.Object.DestroyImmediate(fixture);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
        static void Check(bool condition, string message, List<string> checks)
        { if (!condition) throw new InvalidOperationException("Image pipeline verification failed: " + message); checks.Add(message); }
    }
}
