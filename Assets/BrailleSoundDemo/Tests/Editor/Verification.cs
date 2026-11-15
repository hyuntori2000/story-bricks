using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    /// <summary>Batch smoke checks at the recognizer/presentation boundary. No audio files are played.</summary>
    public static class Verification
    {
        static int checks;
        static void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException("Verification failed: " + message);
        }

        public static void Run()
        {
            var fixture = new GameObject("Input contract verification");
            try
            {
                var hub = fixture.AddComponent<BlockInputHub>();
                // executeMethod is edit mode; explicitly supply the runtime Awake lifecycle.
                hub.SendMessage("Awake");
                var origin = new GameObject("Detection origin");
                origin.transform.SetParent(fixture.transform);
                origin.transform.SetPositionAndRotation(new Vector3(2, 1, 3), Quaternion.Euler(0, 90, 0));
                hub.detectionOrigin = origin.transform;
                var changes = new List<BlockObservation>();
                var removals = new List<string>();
                var activations = new List<string>();
                hub.Changed += value => changes.Add(value);
                hub.Removed += removals.Add;
                hub.Activated += activations.Add;

                var source = new BlockObservation { id = "C-1", word = " cat ", position = new Vector3(.5f, .08f, 0) };
                hub.Submit(source);
                source.position = Vector3.one * 99;
                Check(hub.Count == 1, "first recognized block is recorded");
                Check(hub.TryGet("C-1", out var first) && first.word == "CAT", "word normalized at input boundary");
                Check(Vector3.Distance(first.position, new Vector3(.5f, .08f, 0)) < .0001f, "producer mutation cannot move a recorded block");
                first.word = "DOG";
                Check(hub.TryGet("C-1", out first) && first.word == "CAT", "consumer gets a copy, not mutable registry state");
                hub.Submit(new BlockObservation { id = "C-1", word = "CAT", position = new Vector3(-.5f, .08f, 0) });
                hub.Submit(new BlockObservation { id = "C-2", word = "CAT", position = new Vector3(.5f, .08f, 0) });
                Check(hub.Count == 2 && changes.Count == 3, "moving a block updates its id; duplicate words retain distinct ids");
                hub.Activate("C-2"); hub.Activate("absent");
                Check(activations.Count == 1 && activations[0] == "C-2", "only tracked blocks can trigger sound");
                hub.Submit(new BlockObservation { id = "C-1", word = "CAT", position = new Vector3(float.NaN, 0, 0) });
                Check(hub.TryGet("C-1", out first) && first.position.x == -.5f, "invalid tracking coordinate does not replace last valid pose");
                Check(!string.IsNullOrEmpty(hub.LastError), "invalid tracking data supplies feedback");
                hub.Submit(new BlockObservation { id = "C-2", visible = false });
                Check(hub.Count == 1 && removals.Contains("C-2"), "tracking loss removes the sound-producing block");

                hub.SubmitJson("{\"id\":\"external\",\"word\":\"CAT\",\"position\":{\"x\":0,\"y\":0,\"z\":0},\"visible\":true}");
                Check(hub.Count == 1, "external JSON cannot overwrite mock mode");
                var mock = fixture.AddComponent<MockBlockInput>(); mock.hub = hub;
                mock.enabled = false;
                hub.Clear(); hub.acceptExternalInput = true;
                mock.Seed(); mock.Move("external", Vector3.one);
                Check(hub.Count == 0, "disabled mock producer remains silent in real mode");
                hub.SubmitJson("{\"id\":\"external\",\"word\":\"rain\",\"position\":{\"x\":0.25,\"y\":0.08,\"z\":-0.5}}");
                Check(hub.TryGet("external", out var external) && external.word == "RAIN", "real JSON input produces the same normalized observation");
                Check(Vector3.Distance(external.position, new Vector3(.25f, .08f, -.5f)) < .0001f, "recognizer coordinates are retained in detection-origin local metres");
                hub.SubmitJson("{\"operation\":\"activate\",\"id\":\"external\"}");
                Check(activations.Count == 2 && activations[1] == "external", "real touch message reaches shared activation path");
                int before = hub.Count;
                hub.SubmitJson("{broken json");
                Check(hub.Count == before && !string.IsNullOrEmpty(hub.LastError), "malformed JSON is rejected without replacing scene state");
                hub.SubmitJson("{\"operation\":\"remove\",\"id\":\"external\"}");
                Check(hub.Count == 0, "real tracking-loss message reaches shared removal path");
                bool queued = false;
                var thread = new Thread(() => queued = hub.EnqueueJson("{\"id\":\"worker\",\"word\":\"DOG\",\"position\":{\"x\":1,\"y\":0,\"z\":0},\"visible\":true}"));
                thread.Start(); thread.Join();
                Check(queued && hub.Count == 0, "background producer queues without touching Unity state");
                hub.SendMessage("Update");
                Check(hub.TryGet("worker", out _), "main-thread drain applies background recognizer data");
                hub.Clear();
                Check(hub.Count == 0, "reset removes all tracked blocks");
                Debug.Log("BRAILLE_DEMO_VERIFY_OK " + checks + " assertions; no audio playback.");
            }
            finally { UnityEngine.Object.DestroyImmediate(fixture); }
        }
    }
}
