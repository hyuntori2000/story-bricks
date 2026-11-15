using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    /// <summary>Offline manual-finish contract checks; never opens a microphone or selects the live provider.</summary>
    public static class ManualFinishVerification
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        const string ManualMode = "manual_saved_progress";
        static readonly List<string> checks = new List<string>();

        sealed class HeldProvider : IStoryProvider, ICancellableStoryProvider
        {
            public int QuestionCalls, StoryCalls, Cancellations;
            public StoryContext LastQuestionContext, LastStoryContext;
            public Action<StoryReply> LastQuestionCallback, LastStoryCallback;

            public IEnumerator Question(StoryContext context, Action<StoryReply> completed)
            {
                QuestionCalls++; LastQuestionContext = context; LastQuestionCallback = completed;
                while (true) yield return null;
            }
            public IEnumerator Story(StoryContext context, Action<StoryReply> completed)
            {
                StoryCalls++; LastStoryContext = context; LastStoryCallback = completed;
                while (true) yield return null;
            }
            public void CancelRequests() { Cancellations++; }
        }

        sealed class Fixture : IDisposable
        {
            public readonly GameObject Root = new GameObject("Offline manual finish verification");
            public readonly HeldProvider Provider = new HeldProvider();
            public readonly ConversationFlow Flow;

            public Fixture()
            {
                Flow = Root.AddComponent<ConversationFlow>();
                Flow.TestFastMode = true;
                Flow.SetProviderForTesting(Provider);
                Flow.ConfigurePhysicalWords(true);
                Flow.SetSpatialReady(true);
            }
            public void Dispose() { UnityEngine.Object.DestroyImmediate(Root); }
        }

        public static void Run()
        {
            checks.Clear();
            CheckEmptyAndTerminalGuards();
            CheckSavedAndQueuedSnapshot();
            CheckOnlyQueuedWords();
            CheckResetRejectsLateReply();
            CheckFailurePreservesProgressAndRetries();
            CheckResetDuringFinishTransition();
            Debug.Log("MANUAL_FINISH_VERIFICATION_PASSED " + checks.Count);
        }

        static void CheckEmptyAndTerminalGuards()
        {
            using (var fixture = new Fixture())
            {
                var flow = fixture.Flow;
                int cancellations = fixture.Provider.Cancellations;
                Require(!flow.CreateStoryFromSavedProgress(useLiveProvider: false) &&
                    !flow.CreateStoryFromSavedProgress(new WordRecord[0], false),
                    "Empty saved progress and an empty capture queue cannot create a story.");
                Require(fixture.Provider.StoryCalls == 0 && fixture.Provider.QuestionCalls == 0 &&
                    fixture.Provider.Cancellations == cancellations && flow.State == ConversationState.Ready &&
                    flow.Record.words.Count == 0 && flow.Record.turns.Count == 0 && flow.Record.requests.Count == 0,
                    "Rejecting empty input leaves the session and provider untouched.");

                flow.Record.words.Add(Word(0, "guard-fox", "FOX"));
                foreach (var state in new[] { ConversationState.RequestingStory, ConversationState.Story, ConversationState.Finished })
                {
                    SetState(flow, state);
                    Require(!flow.CreateStoryFromSavedProgress(new[] { Word(99, "guard-extra", "MOON") }, false),
                        "Manual finish is rejected while the flow is " + state + ".");
                    Require(flow.Record.words.Count == 1 && !flow.Record.words[0].finishMarker &&
                        fixture.Provider.StoryCalls == 0 && flow.State == state,
                        "A rejected " + state + " request cannot append queued words or contact the provider.");
                }
                RequireOffline(fixture);
            }
        }

        static void CheckSavedAndQueuedSnapshot()
        {
            using (var fixture = new Fixture())
            {
                var flow = fixture.Flow;
                var answered = Word(0, "saved-judy", "JUDY");
                answered.finishMarker = true; // Only the final combined word may retain this marker.
                flow.Record.words.Add(answered);
                var savedTurn = new ConversationTurn {
                    wordIndex = 0, groupId = answered.groupId, word = answered.word,
                    componentIds = (string[])answered.componentIds.Clone(),
                    question = "Where is Judy going?", answer = "To her grandmother's house.", answeredAt = "saved-answer-time"
                };
                flow.Record.turns.Add(savedTurn);
                SetState(flow, ConversationState.Silent);
                Require(flow.BeginWord(1, "pending-fox", "FOX", false, new Vector3(.4f, .7f, .6f), Ids("pending-fox", "FOX")),
                    "The setup accepts a second captured word whose question remains in flight.");
                Require(fixture.Provider.QuestionCalls == 1 && fixture.Provider.LastQuestionCallback != null &&
                    flow.Record.words.Count == 2 && flow.Record.turns.Count == 1,
                    "An unfinished question has a captured word but no saved answer.");
                var lateQuestion = fixture.Provider.LastQuestionCallback;
                int oldGeneration = Generation(flow);
                int cancellations = fixture.Provider.Cancellations;
                var queuedRain = Word(37, "queued-rain", "RAIN");
                queuedRain.finishMarker = true;
                var queuedMoon = Word(84, "queued-moon", "MOON");
                var duplicateSaved = Word(90, "pending-fox", "FOX");
                var duplicateQueued = Word(91, "queued-rain", "RAIN");
                var additional = new[] { duplicateSaved, queuedRain, duplicateQueued, queuedMoon };

                Require(flow.CreateStoryFromSavedProgress(additional, false),
                    "Manual finish accepts saved answers, the pending captured word, and queued captures together.");
                var context = fixture.Provider.LastStoryContext;
                Require(context != null && fixture.Provider.StoryCalls == 1 && context.completionMode == ManualMode &&
                    context.currentWord == "MOON" && flow.State == ConversationState.RequestingStory,
                    "The injected provider receives one manual_saved_progress story request for the final captured word.");
                Require(Generation(flow) != oldGeneration && fixture.Provider.Cancellations > cancellations &&
                    flow.QuestionJobStatus != "Running" && !flow.CanAnswer && !flow.IsAnswerCapturePending,
                    "Manual finish invalidates the prior generation and cancels the pending question and answer input.");
                Require(context.words.Count == 4 && flow.Record.words.Count == 4 &&
                    context.words[0].word == "JUDY" && context.words[1].word == "FOX" &&
                    context.words[2].word == "RAIN" && context.words[3].word == "MOON",
                    "All captured words reach the story in order while repeated group IDs are omitted.");
                for (int index = 0; index < context.words.Count; index++)
                {
                    var word = context.words[index];
                    Require(word.index == index && flow.Record.words[index].index == index &&
                        word.finishMarker == (index == context.words.Count - 1) &&
                        flow.Record.words[index].finishMarker == (index == context.words.Count - 1),
                        "Combined word " + index + " has its ordered index and only the final word is finish-marked.");
                }
                Require(context.words[2].groupId == queuedRain.groupId &&
                    string.Join(",", context.words[2].componentIds) == string.Join(",", queuedRain.componentIds) &&
                    context.words[2].worldPosition == queuedRain.worldPosition && context.words[2].completedAt == queuedRain.completedAt &&
                    context.words[3].groupId == queuedMoon.groupId &&
                    string.Join(",", context.words[3].componentIds) == string.Join(",", queuedMoon.componentIds),
                    "Queue reindexing preserves capture group IDs, component IDs, positions, and capture times.");
                Require(context.turns.Count == 1 && flow.Record.turns.Count == 1 &&
                    context.turns[0].wordIndex == 0 && context.turns[0].groupId == "saved-judy" &&
                    context.turns[0].question == savedTurn.question && context.turns[0].answer == savedTurn.answer &&
                    context.turns[0].answeredAt == savedTurn.answeredAt,
                    "Only the already saved question and answer are included; unanswered captures receive no fabricated turns.");
                var persisted = JsonUtility.FromJson<ConversationRecord>(File.ReadAllText(flow.SessionPath));
                var request = flow.Record.requests.FindLast(value => value.operation == "story");
                var payload = request == null ? null : JsonUtility.FromJson<StoryContext>(request.payloadJson);
                Require(persisted.words.Count == 4 && persisted.turns.Count == 1 &&
                    payload != null && payload.completionMode == ManualMode && payload.words.Count == 4 && payload.turns.Count == 1,
                    "The persisted session and recorded story payload include every capture and only the saved answer.");
                Require(queuedRain.index == 37 && queuedRain.finishMarker && queuedMoon.index == 84 && !queuedMoon.finishMarker,
                    "Appending captures does not mutate caller-owned word indices or finish flags.");

                string pendingPrompt = flow.CurrentPrompt;
                lateQuestion(new StoryReply { text = "Stale question must be discarded." });
                Require(flow.State == ConversationState.RequestingStory && flow.CurrentPrompt == pendingPrompt && flow.Record.turns.Count == 1,
                    "A canceled question callback cannot reopen input or create an answer after manual finish.");
                Require(!flow.CreateStoryFromSavedProgress(new[] { Word(500, "too-late", "TREE") }, false) &&
                    fixture.Provider.StoryCalls == 1 && flow.Record.words.Count == 4 &&
                    flow.Record.requests.FindAll(value => value.operation == "story").Count == 1,
                    "A repeated finish request cannot issue another story or append later captures.");

                queuedRain.word = "CHANGED"; queuedRain.componentIds[0] = "changed-caller-id";
                flow.Record.words[0].word = "CHANGED"; flow.Record.words[0].componentIds[0] = "changed-record-id";
                flow.Record.turns[0].answer = "Changed after submission.";
                flow.Record.turns[0].componentIds[0] = "changed-answer-id";
                Require(context.words[0].word == "JUDY" && context.words[0].componentIds[0] == "saved-judy-letter-0" &&
                    context.words[2].word == "RAIN" && context.words[2].componentIds[0] == "queued-rain-letter-0" &&
                    context.turns[0].answer == "To her grandmother's house." &&
                    context.turns[0].componentIds[0] == "saved-judy-letter-0",
                    "The provider holds an independent snapshot of words, saved turns, and nested component ID arrays.");
                RequireOffline(fixture);
            }
        }

        static void CheckOnlyQueuedWords()
        {
            using (var fixture = new Fixture())
            {
                var flow = fixture.Flow;
                Require(flow.CreateStoryFromSavedProgress(new[] { Word(12, "queue-only", "TREE") }, false),
                    "A captured queued word can create a story before any conversation turn is saved.");
                var context = fixture.Provider.LastStoryContext;
                Require(context != null && context.completionMode == ManualMode && context.words.Count == 1 &&
                    context.words[0].index == 0 && context.words[0].finishMarker && context.turns.Count == 0 &&
                    fixture.Provider.QuestionCalls == 0 && fixture.Provider.StoryCalls == 1 && flow.Record.turns.Count == 0,
                    "Queue-only finish requests a story directly with zero invented questions or answers.");
                RequireOffline(fixture);
            }
        }

        static void CheckResetRejectsLateReply()
        {
            using (var fixture = new Fixture())
            {
                var flow = fixture.Flow;
                int outputs = 0;
                flow.StoryOutputReady += _ => outputs++;
                Require(flow.CreateStoryFromSavedProgress(new[] { Word(3, "reset-moon", "MOON") }, false),
                    "Reset coverage starts a held manual story request.");
                string oldSession = flow.Record.sessionId;
                var lateStory = fixture.Provider.LastStoryCallback;
                Require(lateStory != null, "The held provider exposes the actual pending story callback.");
                int cancellations = fixture.Provider.Cancellations;
                flow.ResetSession();
                lateStory(new StoryReply { text = "Old story must be discarded.", audioResource = "StoryAudio/story" });
                var resetContext = (StoryContext)Method("CaptureContext").Invoke(flow, new object[] { "" });
                Require(flow.Record.sessionId != oldSession && flow.State == ConversationState.Ready &&
                    flow.Record.words.Count == 0 && flow.Record.turns.Count == 0 && flow.Record.requests.Count == 0 &&
                    !flow.Record.completed && string.IsNullOrEmpty(flow.Record.finalStory) &&
                    resetContext.completionMode != ManualMode,
                    "Reset clears progress, story completion, and the manual completion mode.");
                Require(fixture.Provider.Cancellations > cancellations && outputs == 0 && flow.LastStoryOutput == null &&
                    string.IsNullOrEmpty(flow.StoryOutputPath) && flow.CurrentPrompt == "" && !flow.IsAudioPlaying,
                    "Reset cancels provider work and rejects late story output, narration, and export callbacks.");
                Require(flow.BeginWord(0, "new-fox", "FOX", false, Vector3.forward, Ids("new-fox", "FOX")) &&
                    fixture.Provider.LastQuestionContext != null && fixture.Provider.LastQuestionContext.completionMode != ManualMode,
                    "The new session accepts a normal question without leaking the prior manual completion mode.");
                Require(flow.CreateStoryFromSavedProgress(useLiveProvider: false) && fixture.Provider.StoryCalls == 2 &&
                    fixture.Provider.LastStoryContext.words.Count == 1 && fixture.Provider.LastStoryContext.words[0].groupId == "new-fox" &&
                    fixture.Provider.LastStoryContext.turns.Count == 0,
                    "Reset clears the finish latch so a new session can request its own story exactly once.");
                RequireOffline(fixture);
            }
        }

        static void CheckFailurePreservesProgressAndRetries()
        {
            using (var fixture = new Fixture())
            {
                var flow = fixture.Flow;
                foreach (string name in new[] { "FOX", "RAIN" })
                {
                    var word = Word(flow.Record.words.Count, "answered-" + name.ToLowerInvariant(), name);
                    flow.Record.words.Add(word);
                    flow.Record.turns.Add(new ConversationTurn {
                        wordIndex = word.index, groupId = word.groupId, word = word.word,
                        componentIds = (string[])word.componentIds.Clone(),
                        question = "Tell me about " + name + ".", answer = "Saved answer for " + name + ".",
                        answeredAt = "saved-answer-" + name
                    });
                }
                Require(flow.CreateStoryFromSavedProgress(useLiveProvider: false),
                    "Failure coverage starts a manual story with two completely saved question-and-answer turns.");
                string submitted = JsonUtility.ToJson(fixture.Provider.LastStoryContext);
                var lateFailedStory = fixture.Provider.LastStoryCallback;
                int cancellations = fixture.Provider.Cancellations;
                int recaptures = 0, outputs = 0;
                flow.WordRecaptureRequested += _ => recaptures++;
                flow.StoryOutputReady += _ => outputs++;
                var failure = new StoryReply { error = "Offline simulated provider failure.", errorCode = "offline_failure", generationAttempts = 2 };
                Method("RecoverRequestFailure").Invoke(flow, new object[] { 1, "story", failure, failure.error });

                var retained = (StoryContext)Method("CaptureContext").Invoke(flow, new object[] { "RAIN" });
                Require(flow.State == ConversationState.Error && !flow.StoryFinishRequested &&
                    fixture.Provider.Cancellations > cancellations && JsonUtility.ToJson(retained) == submitted &&
                    flow.Record.words.Count == 2 && flow.Record.turns.Count == 2 && recaptures == 0,
                    "Manual story failure preserves every word and saved answer, cancels provider work, and permits retry without recapture.");
                var persisted = JsonUtility.FromJson<ConversationRecord>(File.ReadAllText(flow.SessionPath));
                Require(persisted.words.Count == 2 && persisted.turns.Count == 2 && persisted.failures.Count == 1 &&
                    persisted.failures[0].operation == "story" && persisted.failures[0].errorCode == "offline_failure" &&
                    persisted.failures[0].generationAttempts == 2 && !persisted.failures[0].recaptureRequested,
                    "The failed manual attempt and unchanged saved progress are persisted without requesting recapture.");
                Require(flow.CreateStoryFromSavedProgress(useLiveProvider: false) &&
                    flow.State == ConversationState.RequestingStory && flow.StoryFinishRequested &&
                    fixture.Provider.StoryCalls == 2 && JsonUtility.ToJson(fixture.Provider.LastStoryContext) == submitted &&
                    flow.Record.requests.FindAll(request => request.operation == "story").Count == 2,
                    "Retry submits the same complete manual snapshot exactly once after the failed attempt.");
                lateFailedStory(new StoryReply { text = "Stale success from the failed attempt.", audioResource = "StoryAudio/story" });
                Require(outputs == 0 && flow.LastStoryOutput == null && string.IsNullOrEmpty(flow.Record.finalStory) &&
                    flow.State == ConversationState.RequestingStory && flow.Record.words.Count == 2 && flow.Record.turns.Count == 2 &&
                    !flow.CreateStoryFromSavedProgress(useLiveProvider: false) && fixture.Provider.StoryCalls == 2,
                    "A late reply from the failed attempt cannot complete the retry or allow a duplicate story request.");
                RequireOffline(fixture);
            }
        }

        static void CheckResetDuringFinishTransition()
        {
            using (var fixture = new Fixture())
            {
                var flow = fixture.Flow;
                bool reset = false;
                string originalSession = flow.Record.sessionId;
                flow.Changed += () => {
                    if (reset || flow.State != ConversationState.RequestingStory) return;
                    reset = true;
                    flow.ResetSession();
                };
                Require(!flow.CreateStoryFromSavedProgress(new[] { Word(9, "reset-transition", "MOON") }, false),
                    "A reset inside the finish-state notification cancels the initiating finish operation.");
                Require(reset && flow.Record.sessionId != originalSession && flow.State == ConversationState.Ready &&
                    flow.Record.words.Count == 0 && flow.Record.turns.Count == 0 && flow.Record.requests.Count == 0 &&
                    !flow.StoryFinishRequested && fixture.Provider.StoryCalls == 0 && flow.LastStoryOutput == null,
                    "The canceled finish cannot start an old-word story against the fresh empty session.");
                Require(flow.CreateStoryFromSavedProgress(new[] { Word(10, "fresh-transition", "TREE") }, false) &&
                    fixture.Provider.StoryCalls == 1 && fixture.Provider.LastStoryContext.words.Count == 1 &&
                    fixture.Provider.LastStoryContext.words[0].groupId == "fresh-transition",
                    "A finish canceled by its notification leaves the fresh session able to accept its own captures.");
                RequireOffline(fixture);
            }
        }

        static WordRecord Word(int index, string group, string word) => new WordRecord {
            index = index, groupId = group, word = word, componentIds = Ids(group, word),
            worldPosition = new Vector3(.2f, .7f, .6f), completedAt = "capture-time-" + group
        };
        static string[] Ids(string group, string word)
        {
            var ids = new string[word.Length];
            for (int index = 0; index < ids.Length; index++) ids[index] = group + "-letter-" + index;
            return ids;
        }
        static int Generation(ConversationFlow flow) => (int)typeof(ConversationFlow).GetField("generation", Private).GetValue(flow);
        static MethodInfo Method(string name) => typeof(ConversationFlow).GetMethod(name, Private);
        static void SetState(ConversationFlow flow, ConversationState state) =>
            Method("SetState").Invoke(flow, new object[] { state, "Offline manual finish setup" });
        static void RequireOffline(Fixture fixture)
        {
            Require(fixture.Root.GetComponent<MicrophoneAnswerInput>() == null && !fixture.Flow.IsLive &&
                !fixture.Flow.IsRecording && fixture.Flow.Record.requests.TrueForAll(request => request.simulated),
                "Verification uses only the injected offline provider and never creates microphone or live API input.");
        }
        static void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException("Manual finish: " + detail);
            checks.Add(detail);
        }
    }
}
