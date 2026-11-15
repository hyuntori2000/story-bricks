using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    /// <summary>Simulated integration checks. Run without -quit; batch mode exits after the report is written.</summary>
    [InitializeOnLoad]
    public static class RuntimeVerification
    {
        const string Pending = "BrailleStoryDemo.RuntimeVerification.Pending";
        static double deadline;
        static IEnumerator suite;
        static Report report;
        static string invariantFailure;
        static readonly List<ConversationState> states = new List<ConversationState>();
        [Serializable] sealed class Report
        {
            public bool passed;
            public bool simulated = true;
            public bool headsetTested = false;
            public bool audiblePlaybackTested = false;
            public string completedAt;
            public string failure = "";
            public string sessionPath = "";
            public string storyOutputPath = "";
            public List<string> checks = new List<string>();
        }

        static RuntimeVerification() { if (SessionState.GetBool(Pending, false)) BeginPolling(); }
        public static void Run()
        {
            string scene = null;
            foreach (var candidate in EditorBuildSettings.scenes)
                if (candidate.enabled) { scene = candidate.path; break; }
            if (string.IsNullOrEmpty(scene)) throw new InvalidOperationException("Create the demo scene before running runtime verification.");
            EditorSceneManager.OpenScene(scene);
            SessionState.SetBool(Pending, true);
            BeginPolling();
            EditorApplication.isPlaying = true;
        }
        static void BeginPolling()
        {
            deadline = EditorApplication.timeSinceStartup + 90;
            suite = null; report = new Report(); invariantFailure = null; states.Clear();
            EditorApplication.update -= Poll; EditorApplication.update += Poll;
        }
        static void Poll()
        {
            try
            {
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Runtime verification timed out.");
                if (!EditorApplication.isPlaying) return;
                if (suite == null)
                {
                    var demo = UnityEngine.Object.FindFirstObjectByType<DemoExperience>();
                    if (demo == null || !demo.IsReady) return;
                    suite = Verify(demo);
                }
                if (invariantFailure != null) throw new InvalidOperationException(invariantFailure);
                if (!suite.MoveNext()) Finish(true, "");
            }
            catch (Exception e) { Finish(false, e.ToString()); }
        }
        static IEnumerator Verify(DemoExperience demo)
        {
            CheckTrackingPolicy();
            foreach (var message in LiveAudioVerification.CodecChecks()) Check(true, message);
            foreach (var wait in LiveAudioVerification.CancellationChecks(Check)) yield return wait;
            var flow = demo.Flow; var layout = demo.Layout;
            flow.TestFastMode = true; layout.AssemblySeconds = .08f;
            int storyEvents = 0; StoryOutput emittedStory = null; bool exportedBeforeEvent = false, emittedBeforeNarration = false;
            flow.StoryOutputReady += output => {
                storyEvents++; emittedStory = output;
                exportedBeforeEvent = File.Exists(flow.StoryOutputPath) && flow.Record.turns.Count == 3;
                emittedBeforeNarration = flow.State == ConversationState.RequestingStory;
            };
            flow.Changed += () => {
                states.Add(flow.State);
                if (flow.CanAnswer && flow.IsAudioPlaying) invariantFailure = "Answer input opened while output was playing.";
                if (flow.CanAnswer && flow.State != ConversationState.Listening) invariantFailure = "Input opened outside Listening.";
                foreach (var request in flow.Record.requests)
                    if (request.operation == "story" && flow.Record.turns.Count != 3) invariantFailure = "Story request started before all three answers were saved.";
            };
            Check(layout.ComponentCount == 13, "Thirteen individual letter/finish components exist.");
            var ids = new HashSet<string>(); int a = 0, e = 0, marker = 0;
            foreach (var c in layout.Components)
            {
                Check(!string.IsNullOrEmpty(c.id) && ids.Add(c.id), "Component identity is unique: " + c.id);
                if (c.letter == "A") a++; if (c.letter == "E") e++; if (c.letter == "#") marker++;
            }
            Check(a == 2 && e == 2 && marker == 1, "Repeated letters retain separate identities and # is separate.");
            Check(!layout.IsAssembled(0) && !layout.IsAssembled(1) && !layout.IsAssembled(2), "All word groups start scattered.");
            Check(!demo.Complete(2) && !demo.Complete(1), "Premature second completion and Finish are rejected.");
            demo.SimulateAnswer();
            Check(flow.Record.turns.Count == 0 && !flow.CanAnswer, "Mock input cannot be accepted before a question.");
            int listeners = 0;
            foreach (var listener in UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                if (listener.enabled) listeners++;
            Check(listeners == 1 && demo.ListenerCamera.GetComponent<AudioListener>().enabled, "Exactly one enabled listener is attached to the head camera.");
            Check(flow.WordSource.spatialBlend == 1 && !flow.WordSource.playOnAwake && flow.WordSource.dopplerLevel == 0, "Word cue is a 3D source without automatic playback or Doppler.");
            Check(flow.VoiceSource.spatialBlend == 0, "Question and story narration use a separate 2D source.");
            Check(AudioSettings.GetSpatializerPluginName() == "Meta XR Audio" && flow.WordSource.spatialize, "Meta XR Audio spatializer is selected for word cues (configuration check).");
            Check(flow.WordSource.GetComponent<MetaXRAudioSource>() != null, "Word source includes the Meta audio component.");
            foreach (var word in new[] { "cat", "rain", "sleep" })
            {
                var clip = Resources.Load<AudioClip>("SatieAudio/" + word);
                Check(clip != null && clip.channels == 1 && clip.length > 0, "Prepared word cue is nonempty mono: " + word);
            }
            foreach (var cue in new[] { "question_cat", "question_rain", "question_sleep", "answer_cat", "answer_rain", "answer_sleep", "story" })
                Check(Resources.Load<AudioClip>("StoryAudio/" + cue) != null, "Prepared speech asset exists: " + cue);

            // Test conversion using an explicit desktop board pose. This is not physical calibration.
            var tracking = demo.GetComponent<QuestAudioTracking>();
            Check(tracking != null && !tracking.IsXrSession, "Verification is running in desktop simulation without a headset.");
            var board = layout.DetectionOrigin;
            tracking.CalibrateBoardAt(new Vector3(2, 1, 3), Quaternion.Euler(0, 90, 0));
            Check(Close(board.TransformPoint(new Vector3(.5f, .1f, 0)), new Vector3(2, 1.1f, 2.5f)), "Board-local metres convert through explicit world translation and rotation.");
            tracking.InvalidateAlignment("Simulated invalidation for verification");
            Check(!tracking.IsSpatialReady, "Explicit alignment invalidation closes readiness.");
            Check(!demo.Complete(0), "The UI completion boundary rejects input while alignment is invalid.");
            tracking.CalibrateBoardAt(new Vector3(2, 1, 3), Quaternion.Euler(0, 90, 0));
            Check(tracking.IsSpatialReady, "Explicit desktop board placement restores readiness.");

            Check(demo.Complete(0), "First completion is accepted.");
            Check(!demo.Complete(0) && !demo.Complete(2), "Duplicate completion and Finish are rejected during assembly/output.");
            demo.SimulateAnswer();
            Check(flow.Record.turns.Count == 0, "Premature simulated answer is ignored during assembly/output.");
            foreach (var wait in Until(() => flow.CanAnswer, "CAT question completion")) yield return wait;
            Check(layout.Observations[0].word == "CAT" && layout.Observations[0].componentIds.Length == 3, "CAT is read from three placed letter components.");
            Check(flow.CurrentPrompt == "What color is the cat?", "CAT receives the intended English question.");
            Check(states.IndexOf(ConversationState.Asking) >= 0 && states.IndexOf(ConversationState.Asking) < states.IndexOf(ConversationState.Listening), "Question output precedes listening.");
            Check(!flow.IsAudioPlaying, "No output plays when mock input opens.");
            var first = FindLetter(layout, 0, "C"); var last = FindLetter(layout, 0, "T");
            var firstPosition = first.position; first.position = last.position; last.position = firstPosition;
            Check(layout.ReadGroup(0).rawText == "TAC", "Recognition reads spatial letter order rather than returning the button's fixture string.");
            last.position = first.position; first.position = firstPosition;
            var group = layout.GroupTransform(0); group.position += new Vector3(.5f, 0, .25f);
            foreach (var wait in Frames(2)) yield return wait;
            Check(Close(flow.WordSource.transform.position, group.position), "Active word source follows its moving group pose.");
            var localPose = group.localPosition + Vector3.right * .5f;
            Check(demo.UpdateRecognizedGroupPose(flow.ActiveGroupId, localPose, Quaternion.Euler(0, 20, 0)), "Active group accepts board-local adapter pose updates.");
            Check(Close(flow.WordSource.transform.position, board.TransformPoint(localPose)), "Adapter pose updates share the calibrated board-to-world conversion.");
            var anchoredPosition = flow.WordSource.transform.position;
            Check(!flow.UpdateSourcePosition("unrelated-group", anchoredPosition + Vector3.up), "An unrelated group cannot move the active source.");
            Check(!flow.UpdateSourcePosition(flow.ActiveGroupId, new Vector3(float.NaN, 0, 0)), "Nonfinite active-source coordinates are rejected.");
            Check(Close(flow.WordSource.transform.position, anchoredPosition), "Rejected pose updates leave the source unchanged.");
            demo.ListenerCamera.transform.position += Vector3.right * .5f;
            demo.ListenerCamera.transform.rotation = Quaternion.Euler(0, 90, 0);
            Check(Close(flow.WordSource.transform.position, anchoredPosition), "Moving or rotating the listener does not move the world-anchored source.");
            demo.SimulateAnswer(); demo.SimulateAnswer();
            Check(!flow.CanAnswer && !flow.CanComplete(1), "Input closes immediately; next word stays blocked while the answer is being received.");
            foreach (var wait in Until(() => flow.State == ConversationState.Silent, "CAT answer save")) yield return wait;
            Check(flow.Record.turns.Count == 1 && flow.Record.turns[0].answer == "Pink.", "CAT answer is saved exactly once.");
            Check(File.Exists(flow.SessionPath) && File.ReadAllText(flow.SessionPath).Contains("Pink."), "CAT answer is persisted before progression.");
            Check(!flow.IsAudioPlaying && !flow.CanAnswer && flow.CanComplete(1), "Saved answer leads to silence and enables the next word.");
            Check(demo.Complete(1), "Second completion is accepted after the first answer is saved.");
            foreach (var wait in Until(() => flow.CanAnswer, "RAIN question completion")) yield return wait;
            Check(layout.Observations[1].word == "RAIN" && layout.Observations[1].componentIds.Length == 4, "RAIN is read from four letter components.");
            Check(flow.CurrentPrompt == "Is the cat outside?", "RAIN question uses the intended earlier-cat context.");
            var rainContext = JsonUtility.FromJson<StoryContext>(flow.Record.requests[1].payloadJson);
            Check(rainContext.words.Count == 2 && rainContext.turns.Count == 1 && rainContext.turns[0].answer == "Pink.", "Second question request includes prior saved context.");
            demo.SimulateAnswer();
            foreach (var wait in Until(() => flow.State == ConversationState.Silent, "RAIN answer save")) yield return wait;
            Check(flow.Record.turns.Count == 2 && flow.Record.turns[1].answer == "No, she is inside.", "Second answer is saved with its word.");
            Check(demo.Complete(2), "Finish is accepted only after both answers are saved.");
            demo.SimulateAnswer();
            Check(!flow.CanAnswer && flow.Record.turns.Count == 2, "Final answer input remains closed during assembly and output.");
            foreach (var wait in Until(() => flow.CanAnswer, "final question completion")) yield return wait;
            var finish = layout.Observations[2];
            Check(finish.rawText == "SLEEP#" && finish.word == "SLEEP" && finish.isFinish && finish.componentIds.Length == 6, "Final recognition keeps # as a separate finish command.");
            Check(flow.CurrentPrompt == "Where does the cat fall asleep?" && !flow.IsAudioPlaying, "Final word asks its English question and finishes output before listening.");
            Check(flow.Record.words.Count == 3 && flow.Record.turns.Count == 2 && flow.Record.requests.Count == 3 && flow.Record.requests[2].operation == "question", "Finish asks a third question before any story request.");
            var finalQuestionContext = JsonUtility.FromJson<StoryContext>(flow.Record.requests[2].payloadJson);
            Check(finalQuestionContext.words.Count == 3 && finalQuestionContext.turns.Count == 2 && finalQuestionContext.currentWord == "SLEEP", "Final question receives the final word plus both earlier answers.");
            Check(storyEvents == 0 && flow.LastStoryOutput == null && string.IsNullOrEmpty(flow.StoryOutputPath), "No story output exists while waiting for the final answer.");
            Check(!demo.Complete(2), "Repeated Finish cannot bypass the final answer.");
            demo.SimulateAnswer(); demo.SimulateAnswer();
            Check(!flow.CanAnswer && flow.Record.requests.Count == 3 && !demo.Complete(2), "Final answer closes input; duplicate answers and Finish cannot start a story early.");
            foreach (var wait in Until(() => flow.State == ConversationState.Finished, "story completion after final answer")) yield return wait;
            Check(flow.Record.turns.Count == 3 && flow.Record.turns[2].answer == "On a soft blanket.", "Final answer is saved exactly once before story generation.");
            Check(flow.Record.requests.Count == 4 && flow.Record.requests[3].operation == "story", "Three question requests are followed by exactly one story request.");
            var context = JsonUtility.FromJson<StoryContext>(flow.Record.requests[3].payloadJson);
            Check(context.currentWord == "SLEEP" && context.words.Count == 3 && context.turns.Count == 3 && context.turns[2].answer == "On a soft blanket.", "Story request receives all ordered words and all three saved answers.");
            Check(context.words[0].word == "CAT" && context.words[1].word == "RAIN" && context.words[2].word == "SLEEP", "Story words preserve completion order.");
            Check(flow.Record.completed && flow.Record.finalStory == MockStoryProvider.StoryText && !flow.IsAudioPlaying && !flow.CanAnswer, "Narration completes, persists the prepared story, and remains silent.");
            Check(storyEvents == 1 && exportedBeforeEvent && emittedBeforeNarration && emittedStory != null, "Story output emits once after JSON and all answers are saved, before narration.");
            Check(emittedStory.schemaVersion == 1 && emittedStory.sessionId == flow.Record.sessionId && emittedStory.storyText == flow.Record.finalStory && emittedStory.storyAudioResource == "StoryAudio/story", "Story event provides a versioned text/audio snapshot for later animation.");
            Check(emittedStory.words.Count == 3 && emittedStory.turns.Count == 3 && emittedStory.turns[2].wordIndex == 2 && emittedStory.turns[2].groupId == finish.groupId && emittedStory.turns[2].componentIds.Length == 6, "Exported answers retain the exact word/group/component identity links.");
            var outputFile = JsonUtility.FromJson<StoryOutput>(File.ReadAllText(flow.StoryOutputPath));
            Check(outputFile.turns.Count == 3 && outputFile.turns[2].answer == "On a soft blanket." && outputFile.storyText == flow.Record.finalStory, "Story-output JSON contains the final answer and narration text.");
            emittedStory.words[0].word = "MUTATED BY CONSUMER";
            var outputCopy = flow.LastStoryOutput; outputCopy.turns[2].answer = "MUTATED COPY";
            Check(flow.LastStoryOutput.words[0].word == "CAT" && flow.LastStoryOutput.turns[2].answer == "On a soft blanket." && flow.Record.words[0].word == "CAT", "Story-output consumers cannot mutate the controller's snapshot or live record.");
            demo.SimulateAnswer(); Check(!demo.Complete(2), "Finished sessions reject repeated Finish.");
            foreach (var wait in Frames(3)) yield return wait;
            Check(storyEvents == 1 && flow.Record.requests.Count == 4 && flow.Record.turns.Count == 3, "Repeated controls do not duplicate the final answer, story request, or output event.");
            report.sessionPath = flow.SessionPath;
            report.storyOutputPath = flow.StoryOutputPath;
            var saved = JsonUtility.FromJson<ConversationRecord>(File.ReadAllText(flow.SessionPath));
            Check(saved.completed && saved.turns.Count == 3 && saved.words.Count == 3, "Completed session persists all three answers as structured JSON.");
            // Deliberate pose updates above may bring randomized groups closer together.
            // Initial random-placement bounds and separation are checked by InputPipelineVerification.
            foreach (var c in layout.Components) Check(ids.Contains(c.id), "Assembly preserves component identity: " + c.id);

            string previousSession = flow.Record.sessionId;
            demo.ResetDemo();
            Check(flow.Record.sessionId != previousSession && flow.Record.words.Count == 0 && flow.Record.turns.Count == 0, "Reset creates a fresh empty session.");
            Check(flow.LastStoryOutput == null && string.IsNullOrEmpty(flow.StoryOutputPath), "Reset clears the current story-output handle.");
            Check(demo.Complete(0), "New session can start an assembly.");
            demo.ResetDemo();
            foreach (var wait in Seconds(.3)) yield return wait;
            Check(flow.State == ConversationState.Ready && flow.Record.words.Count == 0 && !flow.IsAudioPlaying, "Reset cancels pending assembly and prevents stale recognition callbacks.");
            Check(!layout.IsAssembled(0) && !layout.IsAssembled(1) && !layout.IsAssembled(2), "Reset restores all groups to scattered components.");
            Check(demo.Complete(0), "Question cancellation test starts normally.");
            foreach (var wait in Until(() => flow.State == ConversationState.Asking || flow.CanAnswer, "question cancellation boundary")) yield return wait;
            demo.ResetDemo();
            foreach (var wait in Frames(8)) yield return wait;
            Check(flow.State == ConversationState.Ready && !flow.CanAnswer && flow.Record.turns.Count == 0, "Reset during question flow does not reopen old answer input.");
            Check(demo.Complete(0), "Answer cancellation test starts normally.");
            foreach (var wait in Until(() => flow.CanAnswer, "answer cancellation boundary")) yield return wait;
            demo.SimulateAnswer(); demo.ResetDemo();
            foreach (var wait in Frames(8)) yield return wait;
            Check(flow.State == ConversationState.Ready && flow.Record.turns.Count == 0, "Reset during simulated input discards its unsaved answer.");

            // Independent recognizer probe: no layout updater and no hardware data producer.
            var probeObject = new GameObject("Simulated recognizer verification");
            var probe = probeObject.AddComponent<ConversationFlow>(); probe.TestFastMode = true;
            Check(probe.BeginAssembly(0), "Recognizer probe starts its first completion.");
            Check(!probe.WordReady(0, "tracked-cat", "DOG", false, Vector3.zero), "Unexpected recognized text is rejected rather than silently replaced.");
            Check(!probe.WordReady(0, "tracked-cat", "CAT", true, Vector3.zero), "An early finish marker is rejected.");
            Check(!probe.WordReady(0, "", "CAT", false, Vector3.zero), "Recognition requires a stable group identity.");
            Check(!probe.WordReady(0, "tracked-cat", "CAT", false, new Vector3(float.PositiveInfinity, 0, 0)), "Recognition rejects nonfinite location data.");
            Check(probe.Record.words.Count == 0 && probe.State == ConversationState.Assembling, "Invalid recognition adds no word and can be corrected.");
            var trackedIds = new[] { "tracked-c", "tracked-a", "tracked-t" };
            probe.SetSpatialReady(false);
            Check(probe.WordReady(0, "tracked-cat", " cat ", false, new Vector3(1, 2, 3), trackedIds), "Corrected recognized text enters the same conversation pipeline.");
            trackedIds[0] = "mutated-after-submit";
            Check(probe.Record.words[0].componentIds[0] == "tracked-c", "Recognition snapshots component identity data.");
            foreach (var wait in Frames(4)) yield return wait;
            Check(probe.State == ConversationState.PlayingWord && probe.IsSpatialWaiting && probe.Record.requests.Count == 0 && !probe.CanAnswer, "Invalid spatial readiness holds the word cue before question or input.");
            Check(probe.UpdateSourcePosition("tracked-cat", new Vector3(1.5f, 2, 3)) && Close(probe.ActiveWorldPosition, new Vector3(1.5f, 2, 3)), "Same-group pose updates are accepted while spatial output waits.");
            probe.SetSpatialReady(true);
            foreach (var wait in Until(() => probe.CanAnswer, "spatial gate recovery")) yield return wait;
            Check(probe.Record.requests.Count == 1 && probe.CurrentPrompt == "What color is the cat?", "Restored readiness resumes the cue and question sequence once.");
            probe.ResetSession(); probe.SetSpatialReady(false);
            Check(probe.BeginWord(0, "tracked-cat", "CAT", false, Vector3.zero), "Cancellation probe can wait on spatial readiness.");
            foreach (var wait in Frames(3)) yield return wait;
            probe.ResetSession(); probe.SetSpatialReady(true);
            foreach (var wait in Frames(6)) yield return wait;
            Check(probe.State == ConversationState.Ready && probe.Record.words.Count == 0 && probe.Record.requests.Count == 0 && !probe.IsAudioPlaying, "Reset cancels a spatially suspended cue without reviving stale work.");
            int cancelledStoryEvents = 0;
            probe.StoryOutputReady += output => cancelledStoryEvents++;
            for (int cancellation = 0; cancellation < 2; cancellation++)
            {
                probe.ResetSession();
                foreach (var wait in ReachFinalQuestion(probe)) yield return wait;
                Check(probe.Record.requests.Count == 3 && probe.Record.turns.Count == 2 && probe.LastStoryOutput == null, "Final cancellation boundary has three questions, two answers, and no story.");
                if (cancellation == 1)
                {
                    probe.SimulateAnswer();
                    Check(probe.State == ConversationState.ReceivingAnswer && !probe.CanAnswer, "Final simulated-answer reception closes input before cancellation.");
                }
                probe.ResetSession();
                foreach (var wait in Frames(8)) yield return wait;
                Check(probe.State == ConversationState.Ready && probe.Record.words.Count == 0 && probe.Record.turns.Count == 0 && probe.Record.requests.Count == 0 && !probe.IsAudioPlaying,
                    cancellation == 0 ? "Reset while waiting for the final answer cancels the pending story." : "Reset during the final answer cancels its save and pending story.");
                Check(cancelledStoryEvents == 0 && probe.LastStoryOutput == null && string.IsNullOrEmpty(probe.StoryOutputPath), "Cancelled final input emits and exports no stale story.");
            }
            probe.ResetSession();
            probe.StoryOutputReady += output => probe.ResetSession();
            foreach (var wait in ReachFinalQuestion(probe)) yield return wait;
            probe.SimulateAnswer();
            foreach (var wait in Until(() => cancelledStoryEvents == 1 && probe.State == ConversationState.Ready, "reset inside story-output callback")) yield return wait;
            foreach (var wait in Frames(6)) yield return wait;
            Check(probe.State == ConversationState.Ready && !probe.IsAudioPlaying && probe.Record.words.Count == 0 && probe.Record.turns.Count == 0 && probe.LastStoryOutput == null && string.IsNullOrEmpty(probe.StoryOutputPath), "Reset from the story-output event prevents old narration and clears the current export handle.");
            UnityEngine.Object.Destroy(probeObject);
        }
        static IEnumerable<object> ReachFinalQuestion(ConversationFlow flow)
        {
            var words = new[] { "CAT", "RAIN", "SLEEP" };
            for (int index = 0; index < 3; index++)
            {
                Check(flow.BeginWord(index, "cancel-group-" + index, words[index], index == 2, Vector3.right * index), "Final cancellation setup accepts word " + words[index]);
                foreach (var wait in Until(() => flow.CanAnswer, words[index] + " cancellation setup question")) yield return wait;
                if (index == 2) break;
                flow.SimulateAnswer();
                foreach (var wait in Until(() => flow.State == ConversationState.Silent, words[index] + " cancellation setup save")) yield return wait;
            }
            Check(flow.CurrentPrompt == "Where does the cat fall asleep?", "Final cancellation setup reaches the SLEEP question.");
        }
        static void CheckTrackingPolicy()
        {
            var state = new SpatialTrackingState();
            state.Observe(false, false); Check(state.Ready && !state.IsXrSession, "Simulated desktop starts ready without XR.");
            state.Observe(true, true); Check(!state.Ready && state.IsXrSession, "First tracked XR observation requires explicit placement.");
            Check(state.Align() && state.Ready, "Tracked XR placement opens spatial readiness.");
            state.Observe(true, false); Check(!state.Ready && !state.AlignmentValid, "Head tracking loss invalidates alignment.");
            Check(!state.Align(), "Alignment is refused without tracked head pose.");
            state.Observe(true, true); Check(!state.Ready, "Tracking recovery does not silently restore alignment.");
            Check(state.Align() && state.Ready, "Explicit placement restores readiness after recovery.");
            state.Invalidate("Simulated origin change"); Check(!state.Ready, "Origin-change invalidation closes spatial readiness.");
            state.Observe(false, false); Check(state.IsXrSession && !state.Ready, "Lost XR never falls back to a ready desktop session.");
        }
        static Transform FindLetter(MockLetterLayout layout, int group, string glyph)
        {
            foreach (var c in layout.Components) if (c.groupIndex == group && c.letter == glyph) return c.transform;
            throw new InvalidOperationException("Missing expected letter " + glyph);
        }
        static bool Close(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < .001f;
        static IEnumerable<object> Until(Func<bool> ready, string label)
        {
            double limit = EditorApplication.timeSinceStartup + 15;
            while (!ready())
            {
                if (EditorApplication.timeSinceStartup > limit) throw new TimeoutException("Timed out waiting for " + label);
                yield return null;
            }
        }
        static IEnumerable<object> Frames(int count)
        {
            int target = Time.frameCount + count;
            while (Time.frameCount < target) yield return null;
        }
        static IEnumerable<object> Seconds(double duration)
        {
            double target = EditorApplication.timeSinceStartup + duration;
            while (EditorApplication.timeSinceStartup < target) yield return null;
        }
        static void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Runtime verification failed: " + label);
            report.checks.Add(label);
        }
        static void Finish(bool passed, string failure)
        {
            EditorApplication.update -= Poll; SessionState.SetBool(Pending, false);
            report.passed = passed; report.failure = failure; report.completedAt = DateTime.UtcNow.ToString("O");
            string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Documentation", "RUNTIME_VERIFICATION.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, JsonUtility.ToJson(report, true));
            if (passed) Debug.Log("BRAILLE_STORY_RUNTIME_VERIFY_OK " + report.checks.Count + " simulated checks; report=" + path);
            else Debug.LogError(failure + "\nReport: " + path);
            if (Application.isBatchMode) EditorApplication.Exit(passed ? 0 : 1);
            else EditorApplication.isPlaying = false;
        }
    }
}
