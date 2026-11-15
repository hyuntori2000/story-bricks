using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    /// <summary>Offline dialogue/state contracts. Uses synthetic audio, never a microphone or network provider.</summary>
    public static class GuidedDialogueVerification
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static int checks;
        sealed class Provider : IStoryProvider, IDialogueStoryProvider, ICancellableStoryProvider
        {
            public string NextInput = "build_word";
            public bool Hold, WrongTurn, Fail;
            public Action DuringReply;
            public Action<StoryReply> Callback;
            public StoryContext LastContext, StoryContext;
            public int Stories;
            public readonly AudioClip Clip = AudioClip.Create("Offline guided voice", 240, 1, 24000, false);
            public IEnumerator Question(StoryContext context, Action<StoryReply> completed)
            { throw new InvalidOperationException("Guided flow must not use the legacy question endpoint."); }
            public IEnumerator Dialogue(StoryContext context, Action<StoryReply> completed)
            {
                LastContext = context; Callback = completed;
                while (Hold) yield return null;
                DuringReply?.Invoke();
                if (Fail) { completed(new StoryReply { error = "Offline obsolete follow-up failure." }); yield break; }
                completed(new StoryReply { text = NextInput == "build_word" ? "What could help with that idea? Build a word for that idea." : "What does it feel like? Say the sentence.",
                    nextInput = NextInput, turnId = WrongTurn ? Guid.NewGuid().ToString("N") : context.turnId, audioClip = Clip });
            }
            public IEnumerator Story(StoryContext context, Action<StoryReply> completed)
            {
                Stories++; StoryContext = context;
                completed(new StoryReply { text = "One. Two. Three. Four. Five. Six. Seven.", audioClip = Clip });
                yield break;
            }
            public void CancelRequests() { }
        }
        sealed class Fixture : IDisposable
        {
            public readonly GameObject Root = new GameObject("Offline guided dialogue verification");
            public readonly Provider Provider = new Provider();
            public readonly ConversationFlow Flow;
            public Fixture()
            {
                Flow = Root.AddComponent<ConversationFlow>(); Flow.TestFastMode = true;
                Flow.SetProviderForTesting(Provider); Flow.ConfigurePhysicalWords(true); Flow.SetSpatialReady(true);
                Require(Flow.BeginGuidedExperience(false), "Explicit guided fixture activation succeeds.");
                Dialogue("start", -1, "build_word");
            }
            public void Dialogue(string evt, int index, string next)
            {
                Flow.StopAllCoroutines(); Provider.Hold = false; Provider.NextInput = next;
                Pump((IEnumerator)Method("GuidedDialogueSequence").Invoke(Flow, new object[] { evt, index, Generation(Flow) }));
            }
            public void Word(string word, string next)
            {
                int index = Flow.Record.words.Count;
                Provider.Hold = true;
                string group = "guided-" + index;
                var ids = new string[word.Length];
                for (int i = 0; i < ids.Length; i++) ids[i] = group + "-" + i;
                Require(Flow.BeginWord(index, group, word, word.EndsWith(".", StringComparison.Ordinal), Vector3.zero, ids),
                    "A captured word enters the expected build input.");
                // Editor verification pumps the same coroutine explicitly after the held transport boundary.
                Dialogue("word", index, next);
            }
            public void Period()
            {
                Provider.Hold = true;
                Require(Flow.RequestStoryFinish(), "A period starts the final-question route at the queue boundary.");
                Dialogue("finish", Flow.Record.words.Count - 1, "speak_answer");
            }
            public void Answer(string answer, string next)
            {
                Provider.NextInput = next;
                Method("SetState").Invoke(Flow, new object[] { ConversationState.ReceivingAnswer, "Offline answer" });
                Pump((IEnumerator)Method("SaveGuidedAnswer").Invoke(Flow,
                    new object[] { Flow.ActiveIndex, answer, Generation(Flow) }));
            }
            public void Dispose()
            { UnityEngine.Object.DestroyImmediate(Root); UnityEngine.Object.DestroyImmediate(Provider.Clip); }
        }
        public static void Run()
        {
            checks = 0;
            using (var f = new Fixture())
            {
                string intro = f.Provider.LastContext.turnId;
                Require(f.Flow.ReadyForPhysicalWord && f.Flow.Record.words.Count == 0 && !f.Flow.CanAnswer && !f.Flow.RequestStoryFinish(),
                    "Intro opens build input, and a period cannot finish an empty story.");
                f.Word("CAT", "speak_answer");
                Require(f.Flow.Record.turns.Count == 1 && f.Flow.Record.turns[0].turnId == intro &&
                    f.Flow.Record.turns[0].inputType == "build_word" && f.Flow.CanAnswer && !f.Flow.CanComplete(1),
                    "The first word waits for its one real spoken answer.");
                f.Answer("She wants to visit the moon.", "build_word");
                Require(f.Flow.ReadyForPhysicalWord && f.Flow.Record.turns.Count == 2 &&
                    f.Flow.Record.turns[1].wordIndex == 0 && f.Flow.Record.turns[1].inputType == "speak_answer",
                    "One saved answer opens the related next-word invitation.");
                f.Word("ROCKET", "build_word");
                Require(f.Flow.ReadyForPhysicalWord && !f.Flow.CanAnswer && f.Provider.Stories == 0 &&
                    f.Flow.Record.turns.Count == 3 && f.Flow.CanComplete(2),
                    "The second word closes briefly, then waits for another word or Create Story without auto-finishing.");
                f.Word("WIND", "speak_answer");
                f.Provider.DuringReply = () => f.Flow.SetFinishPending();
                f.Answer("It carries my rocket.", "build_word");
                Require(f.Flow.ReadyForPhysicalWord && !f.Flow.CanAnswer && f.Flow.CurrentPrompt == "",
                    "A queued period suppresses an unplayed next-word invitation after the saved answer.");
                f.Provider.DuringReply = null;
                f.Word("MOON", "build_word");
                Require(f.Flow.Record.turns.FindAll(t => t.wordIndex == 3 && t.inputType == "build_word").Count == 0,
                    "An earlier queued unprompted word is retained without inventing a build exchange.");
                f.Period();
                Require(f.Flow.CanAnswer && f.Flow.PeriodQuestionPending && f.Provider.Stories == 0 && !f.Flow.RequestStoryFinish(),
                    "A standalone period asks exactly one final question instead of generating immediately or repeating itself.");
                f.Flow.SetFinishPending(false);
                Require(f.Flow.PeriodQuestionPending, "Consuming the delimiter cannot clear the final-question latch.");
                f.Answer("We bring moon flowers home.", "build_word");
                Require(f.Provider.Stories == 1 && f.Provider.StoryContext.finishRequested &&
                    !f.Flow.Record.words[3].finishMarker &&
                    f.Provider.StoryContext.turns[f.Provider.StoryContext.turns.Count - 1].answer == "We bring moon flowers home.",
                    "The standalone period preserves the real final answer and does not fabricate a trailing-period word.");
                Require(f.Root.GetComponent<MicrophoneAnswerInput>() == null && !f.Flow.IsLive,
                    "No microphone or live provider is used by guided verification.");
            }
            using (var f = new Fixture())
            {
                f.Word("CAT", "speak_answer");
                f.Provider.Fail = true;
                f.Provider.DuringReply = () => f.Flow.SetFinishPending();
                f.Answer("She wants to fly.", "build_word");
                Require(f.Flow.ReadyForPhysicalWord && f.Flow.Record.turns[f.Flow.Record.turns.Count - 1].answer == "She wants to fly.",
                    "A failed obsolete build invitation cannot block a pending period or lose the saved answer.");
                f.Provider.Fail = false; f.Provider.DuringReply = null;
                f.Period();
                Require(f.Flow.CanAnswer && f.Flow.Record.turns.Count == 2,
                    "A period after an already answered first word still opens one additional final question.");
                f.Answer("She lands safely at home.", "build_word");
                Require(f.Provider.Stories == 1 && f.Provider.StoryContext.turns.Count == 3,
                    "Both the original answer and the extra ending answer enter the final story.");
            }
            using (var f = new Fixture())
            {
                f.Word("MOON.", "speak_answer");
                Require(f.Provider.Stories == 0 && f.Flow.PeriodQuestionPending, "WORD. waits for its final spoken answer.");
                f.Answer("It is warm.", "build_word");
                Require(f.Provider.Stories == 1 && f.Provider.StoryContext.turns.Count == 2 &&
                    f.Provider.StoryContext.turns[1].answer == "It is warm.", "WORD. includes its real answer in the story.");
            }
            using (var f = new Fixture())
            {
                f.Word("CAT", "speak_answer"); f.Answer("She wants to travel.", "build_word");
                f.Word("ROCKET.", "speak_answer");
                Require(f.Flow.CanAnswer && f.Provider.Stories == 0,
                    "A period on the second word overrides its ordinary closing with the final spoken question.");
                f.Answer("They return to their friends.", "build_word");
                Require(f.Provider.Stories == 1, "The marked second word starts its story only after the ending answer.");
            }
            using (var f = new Fixture())
            {
                f.Word("CAT", "speak_answer"); f.Answer("She wants to travel.", "build_word");
                f.Word("CAT", "build_word"); f.Word("CAT", "speak_answer");
                Require(f.Flow.Record.words.Count == 3 && f.Flow.Record.words[1].groupId != f.Flow.Record.words[2].groupId,
                    "Later captures may repeat the same spelling for new story moments.");
                int saved = f.Flow.Record.turns.Count;
                Require(f.Flow.CreateStoryFromSavedProgress(useLiveProvider: false) &&
                    f.Provider.StoryContext.completionMode == "manual_saved_progress" &&
                    f.Provider.StoryContext.turns.Count == saved,
                    "Create Story preserves saved exchanges while canceling an unfinished question, without requiring a period.");
            }
            using (var f = new Fixture())
            {
                f.Provider.WrongTurn = true;
                f.Word("JUDY", "speak_answer");
                Require(f.Flow.State == ConversationState.Error && f.Flow.Record.words.Count == 1 && f.Flow.Record.turns.Count == 1,
                    "A mismatched reply turn cannot open input or discard saved words/exchanges.");
                var late = f.Provider.Callback;
                f.Flow.ResetSession();
                late(new StoryReply { text = "Stale", nextInput = "speak_answer", turnId = f.Provider.LastContext.turnId, audioClip = f.Provider.Clip });
                Require(!f.Flow.GuidedDialogueEnabled && f.Flow.Record.words.Count == 0 && f.Flow.State == ConversationState.Ready,
                    "A callback from a reset guided session cannot alter the new session.");
            }
            using (var f = new Fixture())
            {
                f.Word("JUDY", "build_word");
                Require(f.Flow.State == ConversationState.Error && !f.Flow.ReadyForPhysicalWord && !f.Flow.CanAnswer,
                    "A provider cannot skip the first word's question.");
            }
            VerifyImmediateWordSound();
            Debug.Log("GUIDED_DIALOGUE_VERIFICATION_PASSED " + checks);
        }
        static void VerifyImmediateWordSound()
        {
            using (var f = new Fixture())
            {
                f.Word("JUDY", "speak_answer");
                Require(!f.Flow.Record.turns.Exists(turn => turn.inputType == "speak_answer"),
                    "The first word sound fixture has no spoken-answer context.");
                var palette = f.Flow.Palette;
                palette.LiveEffectsEnabled = true; palette.SessionId = () => f.Flow.Record.sessionId;
                var cache = (Dictionary<string, AudioClip>)typeof(SoundPalettePipeline).GetField("generatedClips", Private).GetValue(palette);
                // A cache miss must fail locally, never contact a provider during this verification.
                typeof(SoundPalettePipeline).GetField("liveClient", Private).SetValue(palette, new LiveSoundEffectClient(() => null));
                var clip = AudioClip.Create("Offline recognized-word effect", 240, 1, 24000, false);
                string key = palette.ExperienceContext().Key + ":" + f.Flow.Record.sessionId + ":JUDY:";
                cache[key] = clip;
                var node = new GameObject("Offline word effect source"); node.transform.SetParent(f.Root.transform);
                var source = node.AddComponent<AudioSource>(); source.playOnAwake = false;
                f.Flow.ResolveWordAudioSource = _ => source;
                int attached = 0; f.Flow.WordSoundPrepared += (_, _) => attached++;
                Func<IEnumerator> prepare = () => (IEnumerator)Method("PrepareGuidedWordEffect").Invoke(f.Flow,
                    new object[] { 0, (int)typeof(ConversationFlow).GetField("guidedEffectGeneration", Private).GetValue(f.Flow) });
                try
                {
                    foreach (var state in new[] { ConversationState.Asking, ConversationState.Listening,
                        ConversationState.RequestingStory, ConversationState.Story, ConversationState.Finished })
                    {
                        Method("SetState").Invoke(f.Flow, new object[] { state, "Offline simultaneous sound" });
                        source.clip = null;
                        Pump(prepare());
                        Require(source.clip == clip && Mathf.Approximately(source.volume, .56f * palette.WordGainMultiplier),
                            "Word-only effect is prepared at 70 percent volume during " + state + ".");
                    }
                    Require(attached == 5, "Every busy-state sound result reaches its word box without an answer sentence.");

                    // Suspend the cached asynchronous result before its callback, then change only dialogue generation.
                    Method("SetState").Invoke(f.Flow, new object[] { ConversationState.Listening, "Offline manual finish" });
                    var pending = prepare(); Require(pending.MoveNext(), "Word effect reaches its preparation boundary.");
                    var result = (IEnumerator)pending.Current;
                    Require(result.MoveNext(), "Cached word effect yields before its callback.");
                    Require(f.Flow.CreateStoryFromSavedProgress(useLiveProvider: false), "Manual story can start with a word effect pending.");
                    source.clip = null; Pump(result); Pump(pending);
                    Require(attached == 6 && source.clip == clip,
                        "Manual Create Story preserves the pending word effect without regenerating it.");

                    var otherNode = new GameObject("Second offline effect source"); otherNode.transform.SetParent(f.Root.transform);
                    var other = otherNode.AddComponent<AudioSource>(); other.playOnAwake = false;
                    var sources = (HashSet<AudioSource>)typeof(ConversationFlow).GetField("guidedEffectSources", Private).GetValue(f.Flow);
                    var paused = (HashSet<AudioSource>)typeof(ConversationFlow).GetField("guidedPausedEffectSources", Private).GetValue(f.Flow);
                    sources.Add(source); sources.Add(other); paused.Add(source); paused.Add(other);
                    int oldSoundToken = (int)typeof(ConversationFlow).GetField("guidedEffectGeneration", Private).GetValue(f.Flow);
                    Method("StopGuidedEffectPlayback").Invoke(f.Flow, null);
                    Require(sources.Count == 0 && paused.Count == 0 &&
                        !(bool)Method("ValidGuidedEffect").Invoke(f.Flow, new object[] { oldSoundToken }),
                        "Sound cleanup releases every tracked source and invalidates prior completions.");

                    var late = prepare(); Require(late.MoveNext(), "Late word effect reaches its preparation boundary.");
                    var lateResult = (IEnumerator)late.Current;
                    Require(lateResult.MoveNext(), "Late cached effect is waiting before reset.");
                    cache.Clear(); // The fixture owns this synthetic clip and destroys it immediately below.
                    f.Flow.ResetSession(); Pump(lateResult); Pump(late);
                    Require(attached == 6 && f.Flow.Record.words.Count == 0,
                        "Reset rejects a late word effect before it can attach to the new session.");
                }
                finally
                {
                    cache.Clear();
                    if (clip != null) UnityEngine.Object.DestroyImmediate(clip);
                }
            }
        }
        static MethodInfo Method(string name) => typeof(ConversationFlow).GetMethod(name, Private) ?? throw new MissingMethodException(name);
        static int Generation(ConversationFlow flow) => (int)typeof(ConversationFlow).GetField("generation", Private).GetValue(flow);
        static void Pump(IEnumerator routine)
        {
            var stack = new Stack<IEnumerator>(); stack.Push(routine);
            for (int steps = 0; stack.Count > 0 && steps < 1000; steps++)
            {
                var top = stack.Peek();
                if (!top.MoveNext()) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                if (top.Current is IEnumerator nested) stack.Push(nested);
            }
            if (stack.Count != 0) throw new InvalidOperationException("Offline guided routine did not settle.");
        }
        static void Require(bool value, string detail)
        { if (!value) throw new InvalidOperationException("Guided dialogue: " + detail); checks++; }
    }
}
