using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BrailleSoundDemo
{
    /// <summary>Explicit command-line acceptance test only. Never auto-answers in a normal session.</summary>
    public sealed class PythonInputSmokeCheck : MonoBehaviour
    {
        public DemoExperience experience;
        [Serializable] sealed class Report
        {
            public bool passed;
            public bool actualTcpInput = true;
            public bool actualAudioPlayback;
            public bool microphoneUsed = false;
            public bool apiUsed = false;
            public bool headsetTested = false;
            public bool disconnectObserved;
            public bool pausedAudioObserved;
            public bool pausedSamplesStayedFixed;
            public bool freshPoseResumeObserved;
            public bool activePoseAndVisualMatched;
            public bool movedGroupSaved;
            public int recognizedWords;
            public int savedAnswers;
            public int requests;
            public int storyOutputs;
            public string[] groupIds;
            public string[] failures;
            public string sessionFile;
            public string storyOutputFile;
        }
        readonly List<string> failures = new List<string>();
        bool active, connectedOnce, sawWord, sawVoice, disconnect, paused, frozen, resumed, matched = true;
        int pauseSample, storyOutputs;
        float pauseStarted = -1;

        IEnumerator Start()
        {
            string reportPath = Argument("--python-input-smoke");
            if (string.IsNullOrEmpty(reportPath)) yield break;
            while (experience == null || !experience.IsReady) yield return null;
            if (experience.Flow.IsLive)
            {
                Check(false, "Python input smoke must use the explicit offline voice fixture mode.");
                Finish(reportPath); yield break;
            }
            experience.Flow.TestFastMode = false;
            experience.InputPipeline.SetPythonMode(true);
            experience.InputPipeline.Bridge.ConnectionChanged += connected => { if (connected) connectedOnce = true; else if (connectedOnce) disconnect = true; };
            experience.Flow.StoryOutputReady += output => storyOutputs++;
            active = true;
            float deadline = Time.realtimeSinceStartup + 120;
            while (experience.Flow.State != ConversationState.Finished && experience.Flow.State != ConversationState.Error && Time.realtimeSinceStartup < deadline)
            {
                // Only this opt-in test flag submits prepared answers. Normal live sessions record the user.
                if (experience.Flow.CanAnswer) experience.SimulateAnswer();
                yield return null;
            }
            Check(experience.Flow.State == ConversationState.Finished, "Conversation did not reach Finished: " + experience.Flow.Status);
            yield return null;
            Finish(reportPath);
        }

        void Update()
        {
            if (!active || experience == null || !experience.IsReady) return;
            var flow = experience.Flow;
            sawWord |= flow.WordSource.isPlaying; sawVoice |= flow.VoiceSource.isPlaying;
            Check(!flow.IsLive, "Live API/microphone mode became enabled during the offline input smoke.");
            Check(!(flow.CanAnswer && flow.IsAudioPlaying), "Answer input opened during output playback.");
            if (flow.ActiveIndex >= 0 && flow.ActiveIndex < flow.Record.words.Count)
            {
                var word = flow.Record.words[flow.ActiveIndex];
                bool same = Vector3.Distance(flow.ActiveWorldPosition, word.worldPosition) < .001f &&
                    Vector3.Distance(flow.ActiveWorldPosition, experience.Layout.GroupWorldPosition(flow.ActiveIndex)) < .001f;
                matched &= same;
                Check(same, "The active group's visual, audio source, and record positions diverged.");
            }
            if (flow.State == ConversationState.PlayingWord && !experience.InputPipeline.AudioReady && flow.IsWordAudioPaused)
            {
                paused = true;
                if (pauseStarted < 0) { pauseStarted = Time.realtimeSinceStartup; pauseSample = flow.WordSource.timeSamples; }
                if (Time.realtimeSinceStartup - pauseStarted >= .3f)
                {
                    bool still = !flow.WordSource.isPlaying && Math.Abs(flow.WordSource.timeSamples - pauseSample) <= 2048;
                    frozen |= still;
                    Check(still, "Disconnected spatial audio continued advancing instead of holding its samples.");
                }
            }
            if (paused && experience.InputPipeline.AudioReady && flow.State == ConversationState.PlayingWord && flow.WordSource.isPlaying && !flow.IsWordAudioPaused)
                resumed = true;
        }

        void Finish(string reportPath)
        {
            active = false;
            var flow = experience.Flow;
            Check(flow.Record.words.Count == 3 && flow.Record.turns.Count == 3, "Expected exactly three words and three saved answers.");
            Check(flow.Record.requests.Count == 4 && storyOutputs == 1, "Expected three questions, one story request, and one story output.");
            var expected = new[] { "smoke-cat", "smoke-rain", "smoke-sleep" };
            var groups = new List<string>();
            for (int i = 0; i < flow.Record.words.Count; i++)
            {
                var word = flow.Record.words[i]; groups.Add(word.groupId);
                Check(i < expected.Length && word.groupId == expected[i], "A word lost its external group identity.");
                if (i < flow.Record.turns.Count)
                    Check(flow.Record.turns[i].groupId == word.groupId && string.Join("|", flow.Record.turns[i].componentIds) == string.Join("|", word.componentIds), "An answer lost its word/component link.");
            }
            bool moved = flow.Record.words.Count > 0 && Vector3.Distance(flow.Record.words[0].worldPosition, experience.DetectionOrigin.TransformPoint(new Vector3(-.55f, .09f, -.2f))) < .001f;
            Check(moved, "The final CAT pose update was not saved in the word record.");
            Check(sawWord && sawVoice, "Actual word and voice playback were not both observed.");
            Check(disconnect && paused && frozen && resumed, "Disconnect/pause/frozen samples/fresh-pose resume were not all observed.");
            Check(!flow.IsAudioPlaying && flow.Record.completed, "Completed narration did not end in silence.");
            var report = new Report {
                passed = failures.Count == 0, actualAudioPlayback = sawWord && sawVoice,
                disconnectObserved = disconnect, pausedAudioObserved = paused, pausedSamplesStayedFixed = frozen,
                freshPoseResumeObserved = resumed, activePoseAndVisualMatched = matched, movedGroupSaved = moved,
                recognizedWords = flow.Record.words.Count, savedAnswers = flow.Record.turns.Count, requests = flow.Record.requests.Count,
                storyOutputs = storyOutputs, groupIds = groups.ToArray(), failures = failures.ToArray(),
                sessionFile = flow.SessionPath, storyOutputFile = flow.StoryOutputPath
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)));
            File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
            Debug.Log("[Python input smoke] " + (report.passed ? "PASS" : "FAIL") + " / " + reportPath);
            Application.Quit(report.passed ? 0 : 2);
        }
        void Check(bool condition, string message)
        {
            if (!condition && !failures.Contains(message)) { failures.Add(message); Debug.LogError("[Python input smoke] " + message); }
        }
        static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return "";
        }
    }
}
