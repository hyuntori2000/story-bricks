using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BrailleSoundDemo
{
    /// <summary>
    /// Opt-in standalone acceptance check using an injected provider and prepared local assets.
    /// --conversation-parallel-smoke /absolute/report.json [--story-auto-quit]
    /// Never creates network requests or opens the microphone.
    /// </summary>
    public sealed class ConversationParallelVerification : MonoBehaviour
    {
        [Serializable] sealed class Report
        {
            public bool passed, questionStartedBeforeCueFinished, earlyQuestionWaitedForCue;
            public bool resetRejectedLateReply, finalAnswerPrecededStory, nestedFailureStoppedJobs;
            public bool physicalWordsPreserved, physicalResetRejectedLateReply, fixturePolicyPreserved;
            public bool neutralFallbackSpatialAndGated;
            public bool nextWordPromptGated, nextWordPromptSkippedForFinal, resetCanceledNextWordPrompt;
            public bool microphoneUsed = false, apiUsed = false;
            public int checks;
            public string[] failures;
        }

        readonly List<string> failures = new List<string>();
        readonly Report report = new Report();
        ConversationFlow flow;
        ControlledProvider provider;
        string outputPath;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void InstallWhenRequested()
        {
            string path = Argument("--conversation-parallel-smoke");
            if (string.IsNullOrEmpty(path)) return;
            var root = new GameObject("Conversation parallel verification");
            DontDestroyOnLoad(root);
            root.AddComponent<ConversationParallelVerification>().outputPath = path;
        }

        IEnumerator Start()
        {
            if (string.IsNullOrEmpty(outputPath)) yield break;
            flow = gameObject.AddComponent<ConversationFlow>();
            flow.TestFastMode = true;
            provider = new ControlledProvider(flow);
            flow.SetProviderForTesting(provider);
            var checks = RunChecks();
            while (true)
            {
                bool more = false;
                object current = null;
                try { more = checks.MoveNext(); if (more) current = checks.Current; }
                catch (Exception error) { failures.Add("Unexpected verification error: " + error.Message); }
                if (!more) break;
                yield return current;
            }
            flow.ResetSession();
            report.passed = failures.Count == 0;
            report.failures = failures.ToArray();
            string path = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            Debug.Log("[Conversation parallel verification] " + (report.passed ? "PASS" : "FAIL") + " / " + path);
            if (HasArgument("--story-auto-quit")) Application.Quit(report.passed ? 0 : 2);
        }

        IEnumerator RunChecks()
        {
            Check(MicrophoneAnswerInput.FailureCodeForEnd("device stopped", false, 24000) == "device_stopped" &&
                MicrophoneAnswerInput.FailureCodeForEnd("no speech", false, 24000) == "no_speech",
                "Native microphone stop must not be mislabeled as silence or missing speech.");
            Check(!MicrophoneAnswerInput.CanFinishManually(true, .5f, true) &&
                !MicrophoneAnswerInput.CanFinishManually(true, 1.7f, false) &&
                MicrophoneAnswerInput.CanFinishManually(true, 1.7f, true),
                "Manual finish needs the recording grace period and detected speech.");
            Check(ConversationFlow.ShouldAutomaticallyRetryAnswer(0, "no_speech") &&
                !ConversationFlow.ShouldAutomaticallyRetryAnswer(1, "no_speech") &&
                !ConversationFlow.ShouldAutomaticallyRetryAnswer(0, "device_stopped") &&
                !ConversationFlow.ShouldAutomaticallyRetryAnswer(0, "api_limit"),
                "Automatic answer retries must be limited to one no-speech recovery, never device or billing errors.");
            // Hold the spatial cue behind a real flow gate. The provider must still start;
            // this observes injected provider work, not a metric set by the implementation.
            flow.SetSpatialReady(false);
            Check(flow.BeginWord(0, new Vector3(0, 0, 1)), "First completed word was rejected.");
            yield return WaitFor(() => provider.QuestionCalls == 1 && flow.IsSpatialWaiting, "Question start and blocked cue");
            report.questionStartedBeforeCueFinished = provider.QuestionCalls == 1 && !flow.SpatialReady && flow.IsSpatialWaiting;
            Check(report.questionStartedBeforeCueFinished, "Question provider must start before the spatially blocked word cue can finish.");
            provider.ReleaseQuestions = true;
            yield return WaitFor(() => provider.QuestionReplies == 1, "Early question reply");
            for (int i = 0; i < 3; i++) yield return null;
            report.earlyQuestionWaitedForCue = flow.State == ConversationState.PlayingWord && !flow.CanAnswer && !flow.VoiceSource.isPlaying;
            Check(report.earlyQuestionWaitedForCue, "An early question reply must not speak or open answer input over the word cue.");
            flow.SetSpatialReady(true);
            yield return WaitFor(() => flow.State == ConversationState.Listening, "Listening after both outputs");
            Check(flow.CanAnswer && !flow.IsAudioPlaying && !flow.IsRecording, "Answer input must open only after output ends; test must not record.");

            flow.ResetSession();
            provider.ReleaseQuestions = false;
            flow.SetSpatialReady(false);
            int callsBefore = provider.QuestionCalls;
            Check(flow.BeginWord(0, Vector3.forward), "Second test session did not begin.");
            yield return WaitFor(() => provider.QuestionCalls == callsBefore + 1, "Pending request before reset");
            Action<StoryReply> staleCallback = provider.LastQuestionCallback;
            int repliesBefore = provider.QuestionReplies;
            int cancellationsBefore = provider.Cancellations;
            flow.ResetSession();
            string newSession = flow.Record.sessionId;
            staleCallback(new StoryReply { text = "Stale reply must be ignored.", audioResource = "StoryAudio/question_cat" });
            provider.ReleaseQuestions = true;
            for (int i = 0; i < 6; i++) yield return null;
            report.resetRejectedLateReply = flow.Record.sessionId == newSession && flow.Record.words.Count == 0 &&
                flow.CurrentPrompt == "" && flow.State == ConversationState.Ready && !flow.IsAudioPlaying &&
                provider.QuestionReplies == repliesBefore && provider.Cancellations > cancellationsBefore;
            Check(report.resetRejectedLateReply, "Reset must cancel child work and reject callbacks/audio from the old generation.");

            flow.SetSpatialReady(true);
            int storyOutputs = 0;
            flow.StoryOutputReady += _ => storyOutputs++;
            var nextWordClip = Resources.Load<AudioClip>("StoryAudio/next_word");
            Check(nextWordClip != null && nextWordClip.samples > 0,
                "The next-word prompt must be a nonempty prerecorded local audio resource.");
            int gatedNextPrompts = 0;
            for (int index = 0; index < 3; index++)
            {
                Check(flow.BeginWord(index, new Vector3(index, 0, 1)), "Word " + index + " was rejected.");
                yield return WaitFor(() => flow.State == ConversationState.Listening, "Question for word " + index);
                if (flow.State != ConversationState.Listening) yield break;
                Check(provider.StoryCalls == 0, "Story generation began before the final question was answered.");
                flow.SubmitMockAnswer();
                if (index < 2)
                {
                    yield return WaitFor(() => flow.State == ConversationState.PromptingNextWord, "Next-word prompt after saved answer " + index);
                    bool promptGated = flow.Record.turns.Count == index + 1 &&
                        !flow.CanAnswer && !flow.CanComplete(index + 1) && !flow.IsRecording &&
                        flow.VoiceSource.spatialBlend == 0 && flow.CurrentPrompt == "What comes next?";
                    if (promptGated) gatedNextPrompts++;
                    Check(promptGated, "The centered next-word prompt must keep capture and answer input closed after saving.");
                    var persisted = JsonUtility.FromJson<ConversationRecord>(File.ReadAllText(flow.SessionPath));
                    Check(persisted.turns.Count == index + 1,
                        "The child's answer must already be persisted when the next-word prompt starts.");
                    Check(flow.Record.requests.Count == index + 1,
                        "The prerecorded next-word prompt must not add an API/provider request.");
                    yield return WaitFor(() => flow.State == ConversationState.Silent, "Saved answer " + index);
                    Check(flow.Record.turns.Count == index + 1 && !flow.IsAudioPlaying && flow.CanComplete(index + 1),
                        "Each non-final answer must enter a quiet, capture-ready state after its next-word prompt.");
                }
                else yield return WaitFor(() => flow.State == ConversationState.Finished, "Final story playback");
            }
            report.finalAnswerPrecededStory = provider.StoryCalls == 1 && provider.StoryStartedWithThreeSavedAnswers &&
                provider.LastStoryContext != null && provider.LastStoryContext.turns.Count == 3 &&
                provider.LastStoryContext.turns[2].word == "SLEEP" && flow.Record.completed;
            Check(report.finalAnswerPrecededStory, "Story request must include all three ordered answers, including the finish-marked word.");
            Check(storyOutputs == 1 && !flow.IsAudioPlaying && !flow.CanAnswer, "Story output must occur once and finish quietly.");
            report.nextWordPromptGated = gatedNextPrompts == 2;
            int nextPromptLogs = 0;
            foreach (string log in flow.Logs) if (log.StartsWith("[Next word]", StringComparison.Ordinal)) nextPromptLogs++;
            report.nextWordPromptSkippedForFinal = nextPromptLogs == 2 && flow.Record.requests.Count == 4;
            Check(report.nextWordPromptGated && report.nextWordPromptSkippedForFinal,
                "Only the first two saved answers may prompt for another word; the final answer goes directly to its story.");

            flow.ResetSession();
            flow.SetSpatialReady(true);
            Check(flow.BeginWord(0, Vector3.forward), "Reset-during-next-prompt test word rejected.");
            yield return WaitFor(() => flow.State == ConversationState.Listening, "Answer before next-prompt reset");
            flow.SubmitMockAnswer();
            yield return WaitFor(() => flow.State == ConversationState.PromptingNextWord, "Next-word prompt before reset");
            string promptingSession = flow.Record.sessionId;
            int storiesBeforePromptReset = provider.StoryCalls;
            flow.ResetSession();
            for (int i = 0; i < 6; i++) yield return null;
            report.resetCanceledNextWordPrompt = flow.Record.sessionId != promptingSession &&
                flow.State == ConversationState.Ready && flow.Record.words.Count == 0 && flow.Record.turns.Count == 0 &&
                flow.CurrentPrompt == "" && !flow.IsAudioPlaying && !flow.IsRecording && flow.VoiceSource.clip == null &&
                provider.StoryCalls == storiesBeforePromptReset;
            Check(report.resetCanceledNextWordPrompt,
                "Reset during the next-word prompt must stop its audio and prevent old-session continuation into Silent or Story.");

            flow.ResetSession();
            provider.ThrowNestedQuestion = true;
            flow.SetSpatialReady(false);
            cancellationsBefore = provider.Cancellations;
            Check(flow.BeginWord(0, Vector3.forward), "Failure test word rejected.");
            yield return WaitFor(() => flow.State == ConversationState.Error, "Nested provider failure", allowError: true);
            report.nestedFailureStoppedJobs = flow.State == ConversationState.Error && !flow.IsAudioPlaying && !flow.CanAnswer &&
                provider.Cancellations > cancellationsBefore && flow.WordSoundJobStatus != "Running";
            Check(report.nestedFailureStoppedJobs, "Nested provider exceptions must stop sibling sound work without opening input.");

            flow.ResetSession();
            provider.ThrowNestedQuestion = false;
            provider.ReleaseQuestions = true;
            flow.SetSpatialReady(true);
            report.fixturePolicyPreserved = !flow.PhysicalWordsEnabled &&
                !flow.BeginWord(0, "fixture-reject", "FOX", false, Vector3.forward, LetterIds("fixture", "FOX"));
            Check(report.fixturePolicyPreserved, "Fixture input policy must still reject arbitrary recognized words.");
            flow.ConfigurePhysicalWords(true);
            provider.UseGenericQuestionFixture = true;
            Check(!flow.BeginWord(0, "capture-invalid", "FOX#", false, Vector3.forward, LetterIds("invalid", "FOX#")) &&
                flow.State == ConversationState.Ready && flow.ExpectedWordIndex == 0,
                "Rejected physical spelling must leave input ready for another capture; # is not a recognized letter.");
            int storiesBeforePhysical = provider.StoryCalls;
            string[] physicalWords = { "FOX", "JUDY", "OFX" };
            int questionWordStart = provider.QuestionWords.Count;
            for (int index = 0; index < physicalWords.Length; index++)
            {
                string word = physicalWords[index];
                string group = "capture-test-" + index;
                Vector3 position = new Vector3(.2f + index * .3f, .75f, .6f);
                Check(flow.ExpectedWordIndex == index && flow.CanComplete(index), "Physical next-word index must follow saved turns.");
                Check(flow.BeginWord(index, group, word, index == 2, position, LetterIds(group, word)), "Physical word " + word + " was rejected.");
                yield return WaitFor(() => flow.State == ConversationState.Listening, "Physical question for " + word);
                if (flow.State != ConversationState.Listening) yield break;
                Check(flow.Record.words[index].word == word && flow.Record.words[index].groupId == group &&
                    flow.ActiveWorldPosition == position && provider.QuestionWords[questionWordStart + index] == word,
                    "Physical word, identity and position must reach the question unchanged: " + word);
                Check(provider.StoryCalls == storiesBeforePhysical, "Physical story must wait for the final answer.");
                flow.SubmitMockAnswer();
                if (index < 2) yield return WaitFor(() => flow.State == ConversationState.Silent, "Physical answer saved");
                else yield return WaitFor(() => flow.State == ConversationState.Finished, "Physical three-word story");
            }
            report.physicalWordsPreserved = flow.Record.completed && flow.Record.words.Count == 3 &&
                provider.StoryCalls == storiesBeforePhysical + 1 && provider.LastStoryContext.turns.Count == 3 &&
                provider.LastStoryContext.words[0].word == "FOX" && provider.LastStoryContext.words[1].word == "JUDY" &&
                provider.LastStoryContext.currentWord == "OFX" && provider.LastStoryContext.words[2].finishMarker &&
                provider.LastStoryContext.turns[2].word == "OFX";
            Check(report.physicalWordsPreserved, "Physical story context must retain FOX/JUDY/OFX and the last saved answer without correction.");
            bool skippedUnknownSound = false;
            foreach (string log in flow.Logs) if (log.Contains("No bundled sound for OFX")) skippedUnknownSound = true;
            Check(skippedUnknownSound, "Missing physical word sounds must be explicitly skipped, not replaced or treated as an error.");

            flow.ResetSession();
            provider.ReleaseQuestions = false;
            int physicalCalls = provider.QuestionCalls;
            Check(flow.BeginWord(0, "capture-reset", "FOX", false, Vector3.forward, LetterIds("reset", "FOX")), "Physical reset test word rejected.");
            yield return WaitFor(() => provider.QuestionCalls == physicalCalls + 1, "Physical pending question");
            staleCallback = provider.LastQuestionCallback;
            flow.ResetSession();
            staleCallback(new StoryReply { text = "Old physical question.", audioResource = "StoryAudio/question_cat" });
            for (int i = 0; i < 3; i++) yield return null;
            report.physicalResetRejectedLateReply = flow.PhysicalWordsEnabled && flow.ExpectedWordIndex == 0 &&
                flow.State == ConversationState.Ready && flow.CurrentPrompt == "" && !flow.IsAudioPlaying;
            Check(report.physicalResetRejectedLateReply, "Physical reset must preserve input policy and reject an old question callback.");

            provider.ReleaseQuestions = true;
            flow.UseNeutralCueForUnknownPhysicalWords = true;
            flow.SetSpatialReady(false);
            Vector3 neutralPosition = new Vector3(.4f, .75f, .6f);
            Check(flow.BeginWord(0, "capture-neutral", "FOX", false, neutralPosition, LetterIds("neutral", "FOX")), "Neutral cue test word rejected.");
            yield return WaitFor(() => flow.IsSpatialWaiting, "Neutral spatial cue waiting for tracked position");
            AudioClip neutral = flow.WordSource.clip;
            report.neutralFallbackSpatialAndGated = neutral != null && neutral.samples > 0 && neutral.channels == 1 &&
                neutral.length >= .2f && neutral.length <= .3f && flow.WordSource.spatialBlend == 1 &&
                flow.ActiveWorldPosition == neutralPosition && !flow.CanAnswer && !flow.IsRecording && !flow.VoiceSource.isPlaying;
            Check(report.neutralFallbackSpatialAndGated, "Enabled neutral cue must be a short nonempty mono clip at the accepted 3D position, with question/answer input gated.");
            flow.SetSpatialReady(true);
            yield return WaitFor(() => flow.State == ConversationState.Listening, "Answer input after neutral cue and question");
            Check(flow.CanAnswer && !flow.IsAudioPlaying, "Question must complete before input opens after the neutral cue.");
            foreach (string firstWord in new[] { "RAIN", "MOON", "OFX" })
            {
                flow.ResetSession();
                flow.SetSpatialReady(true);
                string group = "first-" + firstWord;
                Check(flow.BeginWord(0, group, firstWord, false, Vector3.forward, LetterIds(group, firstWord)),
                    "Physical first word must not require CAT or FOX: " + firstWord);
                yield return WaitFor(() => flow.State == ConversationState.Listening, "First-word question for " + firstWord);
                Check(flow.Record.words.Count == 1 && flow.Record.words[0].word == firstWord &&
                    provider.QuestionWords[provider.QuestionWords.Count - 1] == firstWord &&
                    flow.CurrentPrompt == "Test question for " + firstWord,
                    "Event, place, or invented first word must reach question context unchanged: " + firstWord);
            }
            flow.ConfigurePhysicalWords(false);
            Check(!flow.PhysicalWordsEnabled && flow.ExpectedWordIndex == 0, "Returning to fixture policy must start a clean session.");
        }

        static string[] LetterIds(string prefix, string word)
        {
            var result = new string[word.Length];
            for (int i = 0; i < result.Length; i++) result[i] = prefix + "-letter-" + i;
            return result;
        }

        IEnumerator WaitFor(Func<bool> condition, string name, bool allowError = false)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 12;
            while (!condition() && Time.realtimeSinceStartupAsDouble < deadline && (allowError || flow.State != ConversationState.Error)) yield return null;
            Check(condition(), "Timed out waiting for " + name + ": " + flow.State + " / " + flow.Status);
        }
        void Check(bool condition, string message)
        {
            report.checks++;
            if (!condition) { failures.Add(message); Debug.LogError("[Conversation parallel verification] " + message); }
        }

        sealed class ControlledProvider : IStoryProvider, ICancellableStoryProvider
        {
            readonly ConversationFlow flow;
            public bool ReleaseQuestions, ThrowNestedQuestion, UseGenericQuestionFixture;
            public int QuestionCalls, QuestionReplies, StoryCalls, Cancellations;
            public bool StoryStartedWithThreeSavedAnswers;
            public Action<StoryReply> LastQuestionCallback;
            public StoryContext LastStoryContext;
            public readonly List<string> QuestionWords = new List<string>();
            public ControlledProvider(ConversationFlow flow) { this.flow = flow; }
            public IEnumerator Question(StoryContext context, Action<StoryReply> completed)
            {
                QuestionCalls++;
                QuestionWords.Add(context.currentWord);
                LastQuestionCallback = completed;
                if (ThrowNestedQuestion) { yield return FailInNestedIterator(); yield break; }
                while (!ReleaseQuestions) yield return null;
                QuestionReplies++;
                completed(new StoryReply { text = "Test question for " + context.currentWord,
                    audioResource = UseGenericQuestionFixture ? "StoryAudio/question_cat" : "StoryAudio/question_" + context.currentWord.ToLowerInvariant() });
            }
            public IEnumerator Story(StoryContext context, Action<StoryReply> completed)
            {
                StoryCalls++; LastStoryContext = context;
                StoryStartedWithThreeSavedAnswers = flow.Record.turns.Count == 3;
                yield return null;
                completed(new StoryReply { text = "A story using every saved answer.", audioResource = "StoryAudio/story" });
            }
            static IEnumerator FailInNestedIterator()
            {
                yield return null;
                throw new InvalidOperationException("Deliberate nested provider failure.");
            }
            public void CancelRequests() { Cancellations++; }
        }
        static bool HasArgument(string name) { foreach (string value in Environment.GetCommandLineArgs()) if (value == name) return true; return false; }
        static string Argument(string name)
        {
            string[] values = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < values.Length; i++) if (values[i] == name) return values[i + 1];
            return "";
        }
    }
}
