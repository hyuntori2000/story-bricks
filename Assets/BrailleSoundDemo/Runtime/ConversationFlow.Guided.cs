using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BrailleSoundDemo
{
    public sealed partial class ConversationFlow
    {
        const string BuildWordInput = "build_word", SpeakAnswerInput = "speak_answer";
        bool guidedEnabled, guidedRequestPending, guidedFinishPending, guidedFinalQuestionActive;
        string guidedNextInput = "", guidedPromptTurnId = "", guidedRequestTurnId = "";
        readonly HashSet<string> guidedSoundWords = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<AudioSource> guidedEffectSources = new HashSet<AudioSource>();
        readonly HashSet<AudioSource> guidedPausedEffectSources = new HashSet<AudioSource>();
        readonly HashSet<PhysicalStory.WordSoundAttachment> guidedImageReplays = new HashSet<PhysicalStory.WordSoundAttachment>();
        int guidedEffectGeneration;

        public bool GuidedDialogueEnabled => guidedEnabled && PhysicalWordsEnabled;
        public bool PeriodQuestionPending => GuidedDialogueEnabled && guidedFinalQuestionActive;
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

        /// <summary>A queue delimiter suppresses the next build invitation; its queue position starts the final question.</summary>
        public void SetFinishPending(bool pending = true)
        {
            if (!GuidedDialogueEnabled || storyRequested) return;
            guidedFinishPending = pending || guidedFinalQuestionActive || physicalFinishRequested || (Record?.words.Count > 0 && Record.words[Record.words.Count - 1].finishMarker);
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
            guidedRequestPending = true; guidedNextInput = "";
            bool finalQuestion = dialogueEvent == "finish" ||
                (dialogueEvent == "word" && index >= 0 && Record.words[index].finishMarker);
            if (finalQuestion) { guidedFinalQuestionActive = true; guidedFinishPending = true; }
            string requiredInput = dialogueEvent == "start" || dialogueEvent == "answer" ||
                (dialogueEvent == "word" && !finalQuestion && index % 2 == 1) ? BuildWordInput : SpeakAnswerInput;
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
            // A period can arrive while the related-word invitation is in flight. Keep
            // the saved answer and let the queue boundary request the final question.
            if (dialogueEvent == "answer" && guidedFinishPending)
            {
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
                reply.nextInput != requiredInput || string.IsNullOrWhiteSpace(reply.text))
            {
                RecoverGuidedDialogueFailure("dialogue", reply, "The dialogue reply has an invalid turn or input instruction.");
                yield break;
            }
            guidedPromptTurnId = reply.turnId; guidedNextInput = reply.nextInput;
            pendingQuestion = CurrentPrompt = reply.text;
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
                ? "Your answer is saved. One final question follows the earlier captured words."
                : string.IsNullOrEmpty(pendingQuestion) ? "Build another word, or press Create Story." : pendingQuestion);
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
            if (guidedFinalQuestionActive)
            {
                guidedFinalQuestionActive = false; guidedFinishPending = true;
                if (!Valid(token)) yield break;
                yield return StorySequence(word.word, token);
                yield break;
            }
            if (guidedFinishPending)
            {
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
            if (!ReadyForPhysicalWord || Record.words.Count == 0 || guidedFinalQuestionActive) return false;
            guidedFinishPending = true; guidedFinalQuestionActive = true; physicalFinishRequested = true;
            ClearGuidedPrompt();
            // A standalone period asks one final question; only its saved answer
            // starts the story. The period itself is never a word or sound source.
            if (!Save()) return false;
            StartCoroutine(GuidedDialogueSequence("finish", Record.words.Count - 1, generation));
            return true;
        }

        void StartGuidedWordEffect(int index)
        {
            if (!GuidedDialogueEnabled || !IsLive || !Palette.LiveEffectsEnabled ||
                index < 0 || index >= Record.words.Count) return;
            var word = Record.words[index];
            if (!guidedSoundWords.Add(word.groupId)) return;
            int token = guidedEffectGeneration;
            // Sound belongs to the accepted word/session, not to a spoken-answer turn.
            // Palette hosts this job so manual Create Story can cancel dialogue without canceling sound.
            liveWordSoundJobs[index] = StartJob("word sound", index,
                () => PrepareGuidedWordEffect(index, token), generation,
                Palette, () => ValidGuidedEffect(token));
        }

        bool ValidGuidedEffect(int token) => token == guidedEffectGeneration && isActiveAndEnabled &&
            GuidedDialogueEnabled && State != ConversationState.Error;

        IEnumerator PrepareGuidedWordEffect(int index, int token)
        {
            if (!ValidGuidedEffect(token)) yield break;
            var word = Record.words[index];
            AudioClip clip = null;
            // Empty context: never send the child's spoken sentences to sound generation.
            yield return Palette.PrepareWord(word.word, value => { if (ValidGuidedEffect(token)) clip = value; });
            if (!ValidGuidedEffect(token)) yield break;
            if (clip == null) { Log("[Word sound] Effect unavailable; dialogue continues."); yield break; }
            preparedLiveWordClips[index] = clip;
            WordSoundPrepared?.Invoke(word.groupId, clip);
            // Speech, recording, story generation and narration do not gate this effect.
            // Wait only while the headset/anchor cannot play positioned audio safely.
            while (ValidGuidedEffect(token) && (applicationPaused || !SpatialReady)) yield return null;
            if (!ValidGuidedEffect(token)) yield break;
            AudioSource source = null;
            try { source = ResolveWordAudioSource?.Invoke(word.groupId); }
            catch (Exception error) { Log("[Word sound] Anchor source unavailable: " + error.Message); }
            if (source == null) { Log("[Word sound] No box source for " + word.word + "."); yield break; }
            guidedEffectSources.Add(source);
            source.clip = clip; source.volume = .8f * .7f * Palette.WordGainMultiplier;
            var attachment = source.GetComponent<PhysicalStory.WordSoundAttachment>();
            attachment?.MarkPrimaryStarted(clip);
            if (!TestFastMode) source.Play();
            Log("[Word sound] Playing " + word.word + " at 70% of the previous gain.");
            while (ValidGuidedEffect(token) && source != null && source.clip == clip &&
                (source.isPlaying || guidedPausedEffectSources.Contains(source))) yield return null;
            bool completed = ValidGuidedEffect(token) && source != null && source.clip == clip;
            if (source != null && source.clip == clip) source.Stop();
            guidedEffectSources.Remove(source); guidedPausedEffectSources.Remove(source);
            if (completed) attachment?.MarkPrimaryCompleted(clip);
            else attachment?.CancelImageReplay();
        }

        /// <summary>Replay the existing box effect once its image and original playback are both complete.</summary>
        public void RequestWordImageReplay(PhysicalStory.WordSoundAttachment attachment)
        {
            if (attachment == null || !attachment.ImageReplayPending || !GuidedDialogueEnabled ||
                !IsLive || Palette == null || !guidedImageReplays.Add(attachment)) return;
            Palette.StartCoroutine(ReplayWordImageEffect(attachment, guidedEffectGeneration));
        }

        IEnumerator ReplayWordImageEffect(PhysicalStory.WordSoundAttachment attachment, int token)
        {
            AudioSource source = null;
            AudioClip clip = null;
            try
            {
                while (ValidGuidedEffect(token) && attachment != null && attachment.isActiveAndEnabled && attachment.ImageReplayPending)
                {
                    // These are tracking/lifecycle gates only. Speech and story state never gate word effects.
                    if (!applicationPaused && SpatialReady && attachment.TryTakeImageReplay(out clip)) break;
                    yield return null;
                }
                if (!ValidGuidedEffect(token) || attachment == null || clip == null) yield break;
                source = attachment.Source;
                if (source == null || source.clip != clip) yield break;
                guidedEffectSources.Add(source);
                source.volume = .8f * .7f * Palette.WordGainMultiplier;
                if (!TestFastMode) source.Play();
                Log("[Word sound] Image displayed; replaying the existing effect for " + attachment.Word + ".");
                while (ValidGuidedEffect(token) && attachment != null && attachment.isActiveAndEnabled &&
                    source != null && source.clip == clip &&
                    (source.isPlaying || guidedPausedEffectSources.Contains(source))) yield return null;
            }
            finally
            {
                if (token == guidedEffectGeneration)
                {
                    if (source != null && source.clip == clip) source.Stop();
                    if (source != null) { guidedEffectSources.Remove(source); guidedPausedEffectSources.Remove(source); }
                    guidedImageReplays.Remove(attachment);
                }
            }
        }
        void RefreshGuidedEffectPlayback()
        {
            foreach (var source in guidedEffectSources)
            {
                if (source == null) continue;
                if (applicationPaused || !SpatialReady)
                {
                    if (source.isPlaying) { source.Pause(); guidedPausedEffectSources.Add(source); }
                }
                else if (guidedPausedEffectSources.Remove(source)) source.UnPause();
            }
        }
        void StopGuidedEffectPlayback()
        {
            ++guidedEffectGeneration;
            foreach (var attachment in guidedImageReplays) if (attachment != null) attachment.CancelImageReplay();
            guidedImageReplays.Clear();
            foreach (var source in guidedEffectSources) if (source != null) source.Stop();
            guidedEffectSources.Clear(); guidedPausedEffectSources.Clear();
        }
        void ClearGuidedPrompt() { guidedPromptTurnId = ""; guidedNextInput = ""; pendingQuestion = ""; CurrentPrompt = ""; }
        void CancelGuidedPromptForStory()
        {
            guidedRequestPending = false; guidedRequestTurnId = ""; guidedFinishPending = true; guidedFinalQuestionActive = false;
            ClearGuidedPrompt();
        }
        void RecoverGuidedDialogueFailure(string operation, StoryReply reply, string error)
        {
            guidedRequestPending = false; guidedRequestTurnId = "";
            Record.failures.Add(new ConversationFailureRecord { operation = operation, error = error,
                errorCode = reply?.errorCode ?? "dialogue_failure", generationAttempts = reply?.generationAttempts ?? 0,
                failedAt = UtcNow(), recaptureRequested = false });
            Fail(Record.words.Count == 0 ? "Could not start the story guidance. End this session and select Create World again."
                : "Could not continue the question. Your words and answers are saved; press Create Story to use your saved progress, or Home to return to the start.");
            Save();
        }
        void ResetGuidedDialogue()
        {
            StopGuidedEffectPlayback(); guidedEnabled = false; guidedRequestPending = false; guidedFinishPending = false; guidedFinalQuestionActive = false;
            guidedNextInput = guidedPromptTurnId = guidedRequestTurnId = "";
            guidedSoundWords.Clear();
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
