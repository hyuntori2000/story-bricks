using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    /// <summary>Codec and lifecycle checks only. No microphone or external API is used.</summary>
    public static class LiveAudioVerification
    {
        public static IEnumerable<string> CodecChecks()
        {
            var original = new[] { -1f, -.5f, 0f, .5f, 1f, float.NaN };
            byte[] wav = WavCodec.Encode(original, 1, 24000);
            var decoded = WavCodec.Decode(wav, out int channels, out int rate);
            Require(channels == 1 && rate == 24000 && decoded.Length == original.Length, "Mono PCM16 WAV preserves channel/rate/sample count.");
            yield return "Mono PCM16 WAV preserves channel/rate/sample count.";
            Require(Math.Abs(decoded[0] + 1) < .0001 && Math.Abs(decoded[3] - .5) < .0001 && decoded[5] == 0, "WAV roundtrip preserves amplitude and silences nonfinite samples.");
            yield return "WAV roundtrip preserves amplitude and silences nonfinite samples.";
            var stereo = WavCodec.Encode(new[] { .2f, -.3f, .4f, -.5f }, 2, 48000);
            var stereoSamples = WavCodec.Decode(stereo, out channels, out rate);
            Require(channels == 2 && rate == 48000 && stereoSamples.Length == 4, "Stereo WAV preserves interleaved sample count.");
            yield return "Stereo WAV preserves interleaved sample count.";
            Require(Throws(() => WavCodec.Encode(new float[0], 1, 24000)), "Empty microphone WAV is rejected.");
            yield return "Empty microphone WAV is rejected.";
            Require(Throws(() => WavCodec.Encode(new[] { .1f }, 3, 24000)), "Unsupported microphone channels are rejected.");
            yield return "Unsupported microphone channels are rejected.";
            Require(Throws(() => WavCodec.Encode(new[] { .1f }, 1, 96000)), "Unsupported microphone sample rates are rejected.");
            yield return "Unsupported microphone sample rates are rejected.";
            var truncated = new byte[wav.Length - 1]; Array.Copy(wav, truncated, truncated.Length);
            Require(Throws(() => WavCodec.Decode(truncated, out _, out _)), "Truncated downloaded WAV is rejected.");
            yield return "Truncated downloaded WAV is rejected.";
            var nonPcm = (byte[])wav.Clone(); nonPcm[20] = 3;
            Require(Throws(() => WavCodec.Decode(nonPcm, out _, out _)), "Non-PCM generated audio is rejected.");
            yield return "Non-PCM generated audio is rejected.";
            var wrongRate = (byte[])wav.Clone(); wrongRate[28] ^= 1;
            Require(Throws(() => WavCodec.Decode(wrongRate, out _, out _)), "Inconsistent WAV byte rate is rejected.");
            yield return "Inconsistent WAV byte rate is rejected.";
            var overflow = (byte[])wav.Clone(); overflow[40] = 255; overflow[41] = 255; overflow[42] = 255; overflow[43] = 127;
            Require(Throws(() => WavCodec.Decode(overflow, out _, out _)), "Oversized WAV chunk is rejected before reading samples.");
            yield return "Oversized WAV chunk is rejected before reading samples.";
        }

        public static IEnumerable<object> CancellationChecks(Action<bool, string> check)
        {
            var go = new GameObject("Live voice cancellation verification");
            var flow = go.AddComponent<ConversationFlow>(); flow.TestFastMode = true;
            flow.ConfigureLive(true);
            var pending = new PendingProvider();
            typeof(ConversationFlow).GetField("provider", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(flow, pending);
            check(flow.IsLive && !flow.Record.fullyMocked, "Live sessions are explicitly marked non-mocked.");
            check(flow.BeginWord(0, Vector3.zero), "Live cancellation check accepts a completed word.");
            float deadline = Time.realtimeSinceStartup + 5;
            while (pending.callback == null && Time.realtimeSinceStartup < deadline) yield return null;
            check(pending.callback != null && flow.State == ConversationState.RequestingQuestion, "Live pending-question boundary is reached without a network call.");
            check(flow.Record.requests.Count == 1 && !flow.Record.requests[0].simulated && !flow.IsRecording,
                "Live question request is marked real while microphone remains closed during output preparation.");
            string oldSession = flow.Record.sessionId;
            flow.ResetSession();
            pending.callback?.Invoke(new StoryReply { text = "Stale response", audioResource = "StoryAudio/question_cat" });
            for (int i = 0; i < 3; i++) yield return null;
            check(flow.Record.sessionId != oldSession && flow.Record.words.Count == 0 && flow.State == ConversationState.Ready && !flow.IsRecording && !flow.IsAudioPlaying,
                "Reset discards a pending live reply and stops microphone/output work.");
            flow.ConfigureLive(false);
            check(!flow.IsLive && flow.Record.fullyMocked && flow.State == ConversationState.Ready,
                "Returning to fixture mode begins a fresh explicitly mocked session.");
            UnityEngine.Object.Destroy(go);
        }
        sealed class PendingProvider : IStoryProvider
        {
            public Action<StoryReply> callback;
            public IEnumerator Question(StoryContext context, Action<StoryReply> completed)
            {
                callback = completed; while (true) yield return null;
            }
            public IEnumerator Story(StoryContext context, Action<StoryReply> completed) { throw new InvalidOperationException("No story is expected in this cancellation test."); }
        }
        static bool Throws(Action action)
        {
            try { action(); return false; }
            catch (ArgumentException) { return true; }
            catch (InvalidDataException) { return true; }
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
