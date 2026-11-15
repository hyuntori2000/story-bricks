using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BrailleSoundDemo
{
    public sealed partial class ConversationFlow
    {
        const string BuildWordInput = "build_word", SpeakAnswerInput = "speak_answer";
        bool guidedEnabled, guidedRequestPending, guidedFinishPending;
        string guidedNextInput = "", guidedPromptTurnId = "", guidedRequestTurnId = "";
        readonly HashSet<string> guidedCompletedWords = new HashSet<string>(StringComparer.Ordinal);
        AudioSource guidedEffectSource;

        public bool GuidedDialogueEnabled => guidedEnabled && PhysicalWordsEnabled;
        public string ExpectedInput => GuidedDialogueEnabled ? guidedNextInput : CanAnswer ? SpeakAnswerInput : BuildWordInput;
        public bool ReadyForPhysicalWord => GuidedDialogueEnabled && !guidedRequestPending && !liveAnswerPending &&
            !storyRequested && guidedNextInput == BuildWordInput &&
            (State == ConversationState.Silent || State == ConversationState.Ready);

        /// <summary>Explicit route entry. Legacy/mock fixtures keep their original one-answer flow unless selected.</summary>
        public bool BeginGuidedExperience(bool playIntroduction = true)
        {
            Initialize();
            if (!PhysicalWordsEnabled || provider is not IDialogueStoryProvider || Record == null ||
                Record.words.Count != 0 || guidedEnabled || State != ConversationState.Ready) return false;
            guidedEnabled = true; Record.schemaVersion = 2;
            if (!playIntroduction)
            {
                // Captures may already be queued. Do not invent an assistant turn
                // merely because the UI invites the next build without spoken guidance.
                ClearGuidedPrompt();
                EnterGuidedBuildWait();
                return true;
            }
            StartCoroutine(GuidedDialogueSequence("start", -1, generation));
            return true;
        }

        /// <summary>A queue delimiter suppresses new follow-ups; only its queue position requests the story.</summary>
        public void SetFinishPending(bool pending = true)
        {
            if (!GuidedDialogueEnabled || storyRequested) return;
            guidedFinishPending = pending || (Record?.words.Count > 0 && Record.words[Record.words.Count - 1].finishMarker);
        }

        void SaveGuidedBuildResponse(WordRecord word)
        {
            // Captures made ahead of a finish delimiter may be unprompted. Keep the
            // word itself without inventing a question or reusing a spoken-answer turn.
            if (guidedNextInput == BuildWordInput && !string.IsNullOrEmpty(guidedPromptTurnId) &&
                !string.IsNullOrWhiteSpace(pendingQuestion))
                Record.turns.Add(GuidedTurn(word, guidedPromptTurnId, BuildWordInput, pendingQuestion, word.word));
            guidedPromptTurnId = ""; guidedNextInput = ""; pendingQuestion = ""; CurrentPrompt = "";
        }

        IEnumerator GuidedDialogueSequence(string dialogueEvent, int index, int token)
        {
            if (!Valid(token) || !GuidedDialogueEnabled || provider is not IDialogueStoryProvider dialogue) yield break;
            StopGuidedEffectPlayback();
            guidedRequestPending = true; guidedNextInput = "";
            var context = CaptureContext(index >= 0 ? Record.words[index].word : "");
            context.dialogueEvent = dialogueEvent;
            guidedRequestTurnId = context.turnId;
            SetState(ConversationState.RequestingQuestion, dialogueEvent == "start" ? "Preparing your story introduction." : "Preparing the next story question.");
            if (!Valid(token)) yield break;
            RecordRequest("dialogue", context);
            if (!Valid(token)) yield break;
            StoryReply reply = null;
            bool received = false;
            var job = StartJob("question", index, () => dialogue.Dialogue(context, value => {
                if (Valid(token) && guidedRequestTurnId == context.turnId && !received)
                { received = true; reply = value; }
            }), token);
            while (Valid(token) && !job.done) yield return null;
            if (!Valid(token) || guidedRequestTurnId != context.turnId) yield break;
            guidedRequestPending = false; guidedRequestTurnId = "";
            // A period can arrive while the next follow-up is in flight. The saved
            // answer is sufficient, even if that now-obsolete request fails.
            if (dialogueEvent == "answer" && guidedFinishPending)
            {
                CompleteGuidedWord(index, token);
                if (!Valid(token)) yield break;
                ClearGuidedPrompt(); EnterGuidedBuildWait();
                yield break;
            }
            if (job.error != null || reply == null || !string.IsNullOrEmpty(reply.error))
            {
                RecoverGuidedDialogueFailure("dialogue", reply, job.error ?? reply?.error ?? "The dialogue provider returned no reply.");
                yield break;
            }
            if (reply.turnId != context.turnId ||
                (reply.nextInput != BuildWordInput && reply.nextInput != SpeakAnswerInput) ||
                string.IsNullOrWhiteSpace(reply.text) || (dialogueEvent == "start" && reply.nextInput != BuildWordInput) ||
                (dialogueEvent == "word" && reply.nextInput !=
                    (index == 0 && !Record.words[index].finishMarker ? BuildWordInput : SpeakAnswerInput)))
            {
                RecoverGuidedDialogueFailure("dialogue", reply, "The dialogue reply has an invalid turn or input instruction.");
                yield break;
            }
            guidedPromptTurnId = reply.turnId; guidedNextInput = reply.nextInput;
            pendingQuestion = CurrentPrompt = reply.text;
            if (index >= 0 && reply.nextInput == BuildWordInput) CompleteGuidedWord(index, token);
            if (!Valid(token)) yield break;
            Palette.StopAllPlayback();
            SetState(ConversationState.Asking, "Story guidance is playing.");
            if (!Valid(token)) yield break;
            if (index >= 0) NotifyPresentation(QuestionStarted, CopyWord(Record.words[index]));
            if (!Valid(token)) yield break;
            yield return PlayReply(reply, token);
            if (!Valid(token)) yield break;
            if (reply.nextInput == BuildWordInput)
            {
                EnterGuidedBuildWait();
                yield break;
            }
            if (index < 0)
            {
                RecoverGuidedDialogueFailure("dialogue", reply, "A spoken answer needs an accepted word.");
                yield break;
            }
            if (!TestFastMode) yield return new WaitForSecondsRealtime(.1f);
            if (!Valid(token)) yield break;
            automaticAnswerRetries[index] = 0; lastAnswerFailureIndex = -1;
            SetState(ConversationState.Listening, "Listen for the ready tone, then say your answer.");
            if (!Valid(token)) yield break;
            if (IsLive) yield return LiveAnswerSequence(index, token);
        }

        void EnterGuidedBuildWait()
        {
            guidedNextInput = BuildWordInput; guidedRequestPending = false;
            if (!Save()) return;
            SetState(ConversationState.Silent, guidedFinishPending
                ? "Your answer is saved. Finishing the captured word queue."
                : string.IsNullOrEmpty(pendingQuestion) ? "Build the next word, then capture it." : pendingQuestion);
        }

        IEnumerator SaveGuidedAnswer(int index, string answer, int token)
        {
            if (!Valid(token) || State != ConversationState.ReceivingAnswer || !GuidedAnswerPending(index) ||
                string.IsNullOrWhiteSpace(answer)) yield break;
            var word = Record.words[index];
            var turn = GuidedTurn(word, guidedPromptTurnId, SpeakAnswerInput, pendingQuestion, answer.Trim());
            Record.turns.Add(turn);
            ClearGuidedPrompt();
            if (!Save()) yield break;
            NotifyPresentation(AnswerSaved, CopyTurn(turn));
            if (!Valid(token)) yield break;
            if (word.finishMarker)
            {
                guidedFinishPending = true;
                CompleteGuidedWord(index, token);
                if (!Valid(token)) yield break;
                yield return StorySequence(word.word, token);
                yield break;
            }
            if (guidedFinishPending)
            {
                CompleteGuidedWord(index, token);
                if (Valid(token)) EnterGuidedBuildWait();
                yield break;
            }
            yield return GuidedDialogueSequence("answer", index, token);
        }

        static ConversationTurn GuidedTurn(WordRecord word, string turnId, string inputType, string question, string answer) =>
            new ConversationTurn { turnId = turnId, inputType = inputType, wordIndex = word.index,
                groupId = word.groupId, componentIds = CopyIds(word.componentIds), word = word.word,
                question = question, answer = answer, answeredAt = UtcNow() };

        bool GuidedAnswerPending(int index) => GuidedDialogueEnabled && Record != null && index >= 0 &&
            index < Record.words.Count && activeIndex == index && guidedNextInput == SpeakAnswerInput &&
            !string.IsNullOrEmpty(guidedPromptTurnId) && !Record.turns.Exists(turn => turn.turnId == guidedPromptTurnId);
        bool AnswerRoundPending(int index) => GuidedDialogueEnabled ? GuidedAnswerPending(index) :
            Record != null && Record.turns.Count == index;

        bool RequestGuidedStoryFinish()
        {
            if (!ReadyForPhysicalWord || Record.words.Count == 0) return false;
            guidedFinishPending = true;
            ClearGuidedPrompt();
            // A standalone period is a session signal, not a fabricated WORD. marker.
            if (!Save()) return false;
            StartCoroutine(StorySequence(Record.words[Record.words.Count - 1].word, generation));
            return true;
        }

        void CompleteGuidedWord(int index, int token)
        {
            if (!Valid(token) || index < 0 || index >= Record.words.Count) return;
            var word = Record.words[index];
            if (!guidedCompletedWords.Add(word.groupId)) return;
            var descriptions = new List<string>();
            foreach (var turn in Record.turns)
                if (turn.wordIndex == index && turn.inputType == SpeakAnswerInput) descriptions.Add(turn.answer);
            if (!IsLive || !Palette.LiveEffectsEnabled || descriptions.Count == 0) return;
            string context = string.Join(" ", descriptions);
            var job = StartJob("word sound", index, () => PrepareGuidedWordEffect(index, context, token), token);
            liveWordSoundJobs[index] = job;
        }

        IEnumerator PrepareGuidedWordEffect(int index, string context, int token)
        {
            var word = Record.words[index];
            AudioClip clip = null;
            yield return Palette.PrepareWord(word.word, context, value => { if (Valid(token)) clip = value; });
            if (!Valid(token)) yield break;
            if (clip == null) { Log("[Word sound] Optional contextual effect unavailable; dialogue continues."); yield break; }
            preparedLiveWordClips[index] = clip;
            WordSoundPrepared?.Invoke(word.groupId, clip);
            if (!Valid(token) || !CanPlayGuidedEffect()) yield break;
            AudioSource source = null;
            try { source = ResolveWordAudioSource?.Invoke(word.groupId); }
            catch (Exception error) { Log("[Word sound] Anchor source unavailable: " + error.Message); }
            if (source == null) yield break; // Retain the clip rather than invent a stale spatial position.
            guidedEffectSource = activeSpatialSource = source;
            source.clip = clip; source.volume = .8f * Palette.WordGainMultiplier;
            if (!TestFastMode) source.Play();
            while (Valid(token) && source != null && source.isPlaying && CanPlayGuidedEffect()) yield return null;
            if (source != null && source.clip == clip) source.Stop();
            if (guidedEffectSource == source) guidedEffectSource = null;
            if (activeSpatialSource == source) activeSpatialSource = null;
        }
        bool CanPlayGuidedEffect() => GuidedDialogueEnabled && State == ConversationState.Silent &&
            guidedNextInput == BuildWordInput && !guidedRequestPending && !liveAnswerPending && !IsRecording &&
            !storyRequested && SpatialReady && (voiceSource == null || !voiceSource.isPlaying);
        void StopGuidedEffectPlayback()
        {
            if (guidedEffectSource != null) guidedEffectSource.Stop();
            if (activeSpatialSource == guidedEffectSource) activeSpatialSource = null;
            guidedEffectSource = null;
        }
        void ClearGuidedPrompt() { guidedPromptTurnId = ""; guidedNextInput = ""; pendingQuestion = ""; CurrentPrompt = ""; }
        void CancelGuidedPromptForStory()
        {
            guidedRequestPending = false; guidedRequestTurnId = ""; guidedFinishPending = true;
            ClearGuidedPrompt(); StopGuidedEffectPlayback();
        }
        void RecoverGuidedDialogueFailure(string operation, StoryReply reply, string error)
        {
            guidedRequestPending = false; guidedRequestTurnId = "";
            Record.failures.Add(new ConversationFailureRecord { operation = operation, error = error,
                errorCode = reply?.errorCode ?? "dialogue_failure", generationAttempts = reply?.generationAttempts ?? 0,
                failedAt = UtcNow(), recaptureRequested = false });
            Fail(Record.words.Count == 0 ? "Could not start the story guidance. End this session and select Create World again."
                : "Could not continue the question. Your words and answers are saved; use Finish to create the story or end the session.");
            Save();
        }
        void ResetGuidedDialogue()
        {
            StopGuidedEffectPlayback(); guidedEnabled = false; guidedRequestPending = false; guidedFinishPending = false;
            guidedNextInput = guidedPromptTurnId = guidedRequestTurnId = "";
            guidedCompletedWords.Clear();
        }

        /// <summary>Offline verification only: exercise multi-turn input without a microphone or live provider.</summary>
        public bool SubmitGuidedAnswerForTesting(string answer)
        {
            if (!TestFastMode || IsLive || !CanAnswer || !GuidedAnswerPending(activeIndex) || string.IsNullOrWhiteSpace(answer)) return false;
            SetState(ConversationState.ReceivingAnswer, "Receiving an offline guided answer fixture.");
            StartCoroutine(SaveGuidedAnswer(activeIndex, answer, generation));
            return true;
        }
    }
}
