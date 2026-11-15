using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BrailleSoundDemo.PhysicalStory;
using UnityEngine;

namespace BrailleSoundDemo
{
    public enum ConversationState
    {
        Ready, Assembling, PlayingWord, RequestingQuestion, Asking, PreparingAnswer, Listening,
        ReceivingAnswer, Silent, RequestingStory, Story, Finished, Error, PromptingNextWord
    }

    [Serializable] public sealed class WordRecord
    {
        public int index;
        public string groupId;
        public string word;
        public bool finishMarker;
        public string[] componentIds;
        // Latest accepted pose in calibrated Unity world metres.
        public Vector3 worldPosition;
        public string completedAt;
    }
    [Serializable] public sealed class ConversationTurn
    {
        public int wordIndex;
        public string groupId;
        public string[] componentIds;
        public string word;
        public string question;
        public string answer;
        public string answeredAt;
    }
    [Serializable] public sealed class StoryContext
    {
        public string currentWord;
        public string completionMode;
        public List<WordRecord> words = new List<WordRecord>();
        public List<ConversationTurn> turns = new List<ConversationTurn>();
    }
    [Serializable] public sealed class MockRequestRecord
    {
        public string operation;
        public string createdAt;
        public string payloadJson;
        public bool simulated = true;
        public int attempt;
    }
    [Serializable] public sealed class ConversationRecord
    {
        public string sessionId;
        public string workspaceId, experienceSessionId;
        public string startedAt;
        public bool fullyMocked = true;
        public List<WordRecord> words = new List<WordRecord>();
        public List<ConversationTurn> turns = new List<ConversationTurn>();
        public List<MockRequestRecord> requests = new List<MockRequestRecord>();
        public List<ParallelJobMetric> parallelJobs = new List<ParallelJobMetric>();
        public List<ConversationFailureRecord> failures = new List<ConversationFailureRecord>();
        public string finalStory;
        public string finalStoryAudioFile;
        public string finalStoryAudioPath;
        public bool completed;
    }
    [Serializable] public sealed class ConversationFailureRecord
    {
        public string operation, error, errorCode, failedAt;
        public int generationAttempts;
        public WordRecord word;
        public ConversationTurn answer;
        public bool recaptureRequested;
    }
    [Serializable] public sealed class ParallelJobMetric
    {
        public string operation, status, error;
        public int wordIndex;
        public double startedAt, completedAt = -1;
        public double ElapsedSeconds => (completedAt >= 0 ? completedAt : Time.realtimeSinceStartupAsDouble) - startedAt;
    }
    public sealed class StoryReply
    {
        public string text;
        public string audioResource;
        public AudioClip audioClip;
        public string audioFile;
        public string audioPath;
        public string error;
        public string errorCode;
        public int generationAttempts;
    }
    /// <summary>Local snapshot for a later animation adapter. Exporting it performs no network request.</summary>
    [Serializable] public sealed class StoryOutput
    {
        public int schemaVersion = 1;
        public string sessionId;
        public string workspaceId, experienceSessionId;
        public string storyText;
        public string storyAudioResource;
        public string storyAudioFile;
        public string storyAudioPath;
        public List<WordRecord> words = new List<WordRecord>();
        public List<ConversationTurn> turns = new List<ConversationTurn>();
    }

    /// <summary>Replace this provider later with a backend client. Never put a permanent API key in a Unity build.</summary>
    public interface IStoryProvider
    {
        IEnumerator Question(StoryContext context, Action<StoryReply> completed);
        IEnumerator Story(StoryContext context, Action<StoryReply> completed);
    }
    /// <summary>Optional cancellation seam for injected providers with work outside the owning coroutine.</summary>
    public interface ICancellableStoryProvider
    {
        void CancelRequests();
    }

    /// <summary>Deterministic fixtures: no HTTP, microphone, transcription or generation calls.</summary>
    public sealed class MockStoryProvider : IStoryProvider
    {
        public const string StoryText = "A little pink cat watched the rain from inside her cozy house. She was safe and dry, listening to the drops tap on the window. Soon she curled up on a soft blanket, closed her eyes, and fell asleep. Outside, the rain kept singing its gentle song.";
        public IEnumerator Question(StoryContext context, Action<StoryReply> completed)
        {
            yield return null;
            if (context.currentWord == "CAT")
                completed(new StoryReply { text = "What color is the cat?", audioResource = "StoryAudio/question_cat" });
            else if (context.currentWord == "RAIN")
                completed(new StoryReply { text = "Is the cat outside?", audioResource = "StoryAudio/question_rain" });
            else if (context.currentWord == "SLEEP")
                completed(new StoryReply { text = "Where does the cat fall asleep?", audioResource = "StoryAudio/question_sleep" });
            else throw new InvalidOperationException("This mocked provider supports CAT, RAIN and SLEEP questions only.");
        }
        public IEnumerator Story(StoryContext context, Action<StoryReply> completed)
        {
            yield return null;
            // The complete ordered context reaches the provider even though this first demo uses a fixed fixture.
            if (context.words.Count != 3 || context.turns.Count != 3 || context.currentWord != "SLEEP" || !context.words[2].finishMarker)
                throw new InvalidOperationException("The mock story needs all three words, all three saved answers and the finish marker.");
            completed(new StoryReply { text = StoryText, audioResource = "StoryAudio/story" });
        }
    }

    /// <summary>Owns turn timing and storage. Letter animation/vision only supplies completed word observations.</summary>
    public sealed class ConversationFlow : MonoBehaviour
    {
        public ConversationState State { get; private set; } = ConversationState.Ready;
        public string CurrentPrompt { get; private set; } = "";
        public string Status { get; private set; } = "Complete the first word.";
        public string SessionPath { get; private set; } = "";
        public string StoryOutputPath { get; private set; } = "";
        /// <summary>Returns a copy so later adapters cannot mutate the saved output or the session.</summary>
        public StoryOutput LastStoryOutput => lastStoryOutput == null ? null : CopyStoryOutput(lastStoryOutput);
        public ConversationRecord Record { get; private set; }
        public IReadOnlyList<string> Logs => logs;
        public bool CanAnswer => State == ConversationState.Listening;
        public bool IsLive { get; private set; }
        public bool PhysicalWordsEnabled { get; private set; }
        public int StoryWordLimit => PhysicalWordsEnabled ? int.MaxValue : 3;
        public int ExpectedWordIndex => Record?.words.Count ?? 0;
        public bool StoryFinishRequested => storyRequested;
        /// <summary>Optional generic spatial confirmation for unmapped physical words; never presented as a semantic or SATIE sound.</summary>
        public bool UseNeutralCueForUnknownPhysicalWords { get; set; }
        public bool IsRecording => microphone != null && microphone.IsRecording;
        public bool CanFinishAnswer => IsLive && CanAnswer && microphone != null && microphone.CanFinishAnswer;
        public string RecorderStatus => microphone != null ? microphone.Status : "Microphone idle";
        public bool IsAnswerCapturePending => liveAnswerPending;
        public bool IsAudioPlaying => (wordSource != null && wordSource.isPlaying) || (voiceSource != null && voiceSource.isPlaying) ||
            (activeSpatialSource != null && activeSpatialSource.isPlaying);
        public AudioSource WordSource => wordSource;
        public AudioSource VoiceSource => voiceSource;
        public int ActiveIndex => activeIndex;
        public string ActiveGroupId { get; private set; } = "";
        public string ActiveWord { get; private set; } = "";
        public Vector3 ActiveWorldPosition => activeSpatialSource != null ? activeSpatialSource.transform.position :
            wordSource != null ? wordSource.transform.position : Vector3.zero;
        public Func<string,AudioSource> ResolveWordAudioSource { get; set; }
        public bool SpatializerConfigured { get; private set; }
        public bool SpatialReady => spatialTrackingReady && (Application.platform != RuntimePlatform.Android || SpatializerConfigured);
        public bool IsWordAudioPaused => wordPlaybackPaused;
        public bool IsSpatialWaiting => wordPlaybackActive && !SpatialReady;
        public SoundPalettePipeline Palette { get; private set; }
        public IReadOnlyList<ParallelJobMetric> ParallelJobs => Record != null ? Record.parallelJobs : Array.Empty<ParallelJobMetric>();
        public string QuestionJobStatus => JobStatus("question");
        public string StoryJobStatus => JobStatus("story");
        public string WordSoundJobStatus => JobStatus("word sound");
        public string StoryMediaJobStatus => JobStatus("story sound preparation");
        public string PipelineSummary => State == ConversationState.RequestingStory || State == ConversationState.Story || State == ConversationState.Finished
            ? "Story: " + StoryJobStatus + " | Sound preparation: " + StoryMediaJobStatus
            : "Question: " + QuestionJobStatus + " | Word sound: " + WordSoundJobStatus;
        /// <summary>For automated logic tests only. No playback occurs; each phase still yields a frame.</summary>
        public bool TestFastMode;
        public event Action Changed;
        public event Action<StoryOutput> StoryOutputReady;
        public event Action<StoryOutput> StoryNarrationCompleted;
        public event Action<string> WordRecaptureRequested;
        public event Action<string,AudioClip> WordSoundPrepared;
        public event Action<WordRecord> QuestionStarted;
        public event Action<ConversationTurn> AnswerSaved;

        readonly List<string> logs = new List<string>();
        readonly string[] words = { "CAT", "RAIN", "SLEEP" };
        readonly string[] answers = { "Pink.", "No, she is inside.", "On a soft blanket." };
        AudioSource wordSource, voiceSource, activeSpatialSource;
        AudioClip neutralWordCue, readyInputCue, retryInputCue;
        IStoryProvider provider;
        LiveStoryProvider liveProvider;
        MicrophoneAnswerInput microphone;
        bool liveAnswerPending;
        bool applicationPaused;
        string interruptedAnswerSession;
        int interruptedAnswerGeneration = -1, interruptedAnswerIndex = -1;
        int[] automaticAnswerRetries = new int[4];
        int lastAnswerFailureIndex = -1;
        double lastAnswerActionAt = double.NegativeInfinity;
        int activeIndex = -1, generation;
        string pendingQuestion;
        ExperienceIdentity experience = new ExperienceIdentity();
        ExperienceIdentity RecordExperience => Record == null ? experience :
            new ExperienceIdentity(Record.workspaceId, Record.experienceSessionId);
        StoryOutput lastStoryOutput;
        bool storyRequested;
        bool physicalFinishRequested;
        bool manualStoryFinish;
        bool spatialTrackingReady = true;
        bool wordPlaybackActive, wordPlaybackStarted, wordPlaybackPaused;
        sealed class BackgroundJob
        {
            public ParallelJobMetric metric;
            public bool done;
            public string error;
        }
        readonly List<BackgroundJob> backgroundJobs = new List<BackgroundJob>();
        BackgroundJob[] liveWordSoundJobs = new BackgroundJob[4];
        AudioClip[] preparedLiveWordClips = new AudioClip[4];

        void Awake() { Initialize(); }
        public void Initialize()
        {
            if (provider != null) return;
            provider = new MockStoryProvider();
            Palette = GetComponent<SoundPalettePipeline>();
            if (Palette == null) Palette = gameObject.AddComponent<SoundPalettePipeline>();
            Palette.ExperienceContext = () => RecordExperience;
            var source = new GameObject("Word sound source"); source.transform.SetParent(transform, false);
            wordSource = source.AddComponent<AudioSource>(); wordSource.playOnAwake = false;
            SpatializerConfigured = AudioSettings.GetSpatializerPluginName() == "Meta XR Audio";
            wordSource.spatialBlend = 1; wordSource.spatialize = SpatializerConfigured;
            wordSource.rolloffMode = AudioRolloffMode.Linear; wordSource.minDistance = .25f; wordSource.maxDistance = 8;
            wordSource.dopplerLevel = 0; wordSource.volume = .80f;
            var meta = source.AddComponent<MetaXRAudioSource>(); meta.EnableSpatialization = SpatializerConfigured; meta.EnableAcoustics = false;
            var voice = new GameObject("Clear narration source"); voice.transform.SetParent(transform, false);
            voiceSource = voice.AddComponent<AudioSource>(); voiceSource.playOnAwake = false;
            voiceSource.spatialBlend = 0; voiceSource.volume = .8f;
            ResetSession();
        }
        /// <summary>Sets the owner of the next session. The coordinator performs its normal reset afterwards.</summary>
        public void SetExperienceContext(string workspaceId, string experienceSessionId)
        {
            experience = new ExperienceIdentity(workspaceId, experienceSessionId);
        }
        /// <summary>Explicitly selects real backend/microphone operation or deterministic fixtures. Switching starts a new session.</summary>
        public void ConfigureLive(bool enabled)
        {
            Initialize();
            if (IsLive == enabled) return;
            generation++; CancelJobMetrics(); StopAllCoroutines();
            CancelProviderWork(true);
            IsLive = enabled;
            if (enabled)
            {
                if (microphone == null)
                {
                    microphone = gameObject.AddComponent<MicrophoneAnswerInput>();
                    microphone.StateChanged += OnMicrophoneStateChanged;
                }
                liveProvider = new LiveStoryProvider(() => Record?.sessionId, () => RecordExperience) {
                    GenerationAttemptStarted = OnGenerationAttemptStarted
                };
                provider = liveProvider;
            }
            else provider = new MockStoryProvider();
            ResetSession();
            if (!enabled) { liveProvider?.Dispose(); liveProvider = null; }
        }
        /// <summary>Deterministic integration-test seam. It resets the session and never opens the microphone.</summary>
        public void SetProviderForTesting(IStoryProvider testProvider)
        {
            if (testProvider == null) throw new ArgumentNullException(nameof(testProvider));
            Initialize();
            generation++; CancelJobMetrics(); StopAllCoroutines();
            CancelProviderWork(true);
            liveProvider = null;
            provider = testProvider;
            IsLive = false;
            ResetSession();
        }
        /// <summary>
        /// Enables accepted physical words instead of the three CAT/RAIN/SLEEP fixtures.
        /// Physical stories finish when a period is recognized, rather than at a fixed word count.
        /// Changing input policy starts a new session, while ordinary resets preserve the policy.
        /// </summary>
        public void ConfigurePhysicalWords(bool enabled)
        {
            Initialize();
            if (PhysicalWordsEnabled == enabled) return;
            PhysicalWordsEnabled = enabled;
            ResetSession();
        }
        public bool CanComplete(int index)
        {
            if (Record == null || index < 0 || index >= StoryWordLimit || !SpatializerAvailableOnPlatform()) return false;
            if (index == 0) return State == ConversationState.Ready && Record.words.Count == 0;
            return State == ConversationState.Silent && Record.words.Count == index && Record.turns.Count == index;
        }
        public bool BeginAssembly(int index)
        {
            if (!CanComplete(index)) { Log("[Input ignored] Finish the current output and answer before completing the next word."); return false; }
            activeIndex = index; ActiveGroupId = ""; ActiveWord = "";
            SetState(ConversationState.Assembling, PhysicalWordsEnabled
                ? "Receiving physical word " + (index + 1) + ". Capture a period when your story is ready."
                : "Assembling " + words[index] + (index == 2 ? "#" : "") + " from tracked letter components.");
            return true;
        }
        /// <summary>Recognition handoff. The adapter must provide a stable group identity and a calibrated Unity world pose.</summary>
        public bool WordReady(int index, string groupId, string recognizedWord, bool finishMarker,
            Vector3 worldPosition, string[] componentIds = null)
        {
            if (State != ConversationState.Assembling || index != activeIndex) return false;
            if (!SpatializerAvailableOnPlatform()) return RejectRecognition("Select Meta XR Audio in Project Settings > Audio > Spatializer Plugin before testing positional audio on Quest.");
            if (string.IsNullOrWhiteSpace(groupId)) return RejectRecognition("A stable word group ID is required.");
            if (!Finite(worldPosition)) return RejectRecognition("Recognition position is not finite.");
            string normalizedWord = (recognizedWord ?? "").Trim().ToUpperInvariant();
            if (PhysicalWordsEnabled)
            {
                if (!PhysicalWordInputPolicy.IsRecognizedWord(normalizedWord))
                    return RejectRecognition("Physical words must contain 1 to 32 symbols, including a letter A-Z; the only supported punctuation is a period.");
                if (finishMarker != PhysicalWordInputPolicy.EndsStory(normalizedWord))
                    return RejectRecognition("Only a recognized trailing period can finish this word's story.");
                if (!PhysicalIdentifier(groupId)) return RejectRecognition("Physical group ID is missing or invalid.");
                if (componentIds == null || componentIds.Length != normalizedWord.Length)
                    return RejectRecognition("Provide one capture-scoped component ID per recognized block.");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (string id in componentIds)
                    if (!PhysicalIdentifier(id) || !ids.Add(id)) return RejectRecognition("Physical component IDs must be valid and distinct within the word.");
                foreach (var saved in Record.words)
                    if (saved.groupId == groupId) return RejectRecognition("This physical word group was already accepted.");
            }
            else if (normalizedWord != words[index] || finishMarker != (index == 2))
                return RejectRecognition("Mock step " + (index + 1) + " expects " + words[index] +
                    (index == 2 ? " with the finish marker." : " without a finish marker.") +
                    " Received " + normalizedWord + (finishMarker ? "#" : "") + ".");
            ActiveGroupId = groupId; ActiveWord = normalizedWord;
            wordSource.transform.position = worldPosition;
            EnsureRoundStorage(index);
            if (PhysicalWordsEnabled && finishMarker) physicalFinishRequested = true;
            Record.words.Add(new WordRecord { index = index, groupId = groupId, word = normalizedWord,
                finishMarker = PhysicalWordsEnabled ? physicalFinishRequested : finishMarker,
                componentIds = componentIds != null ? (string[])componentIds.Clone() : new string[0],
                worldPosition = worldPosition, completedAt = UtcNow() });
            int token = generation;
            Log("word - " + normalizedWord + (finishMarker ? (PhysicalWordsEnabled ? " [final round]" : "#") : ""));
            if (!Valid(token)) return false;
            if (!Save()) return false;
            StartCoroutine(WordSequence(index, token));
            return true;
        }
        /// <summary>Convenience fixture API. Real recognition adapters must use the explicit identity/text overload.</summary>
        public bool WordReady(int index, Vector3 worldPosition) => index >= 0 && index < words.Length &&
            WordReady(index, "mock-group-" + index, words[index], index == 2, worldPosition);
        /// <summary>Direct adapter seam for a future recognizer when there is no mock assembly animation.</summary>
        public bool BeginWord(int index, string groupId, string recognizedWord, bool finishMarker,
            Vector3 worldPosition, string[] componentIds = null)
        {
            int token = generation, previousIndex = activeIndex;
            var previousState = State;
            string previousStatus = Status, previousGroup = ActiveGroupId, previousWord = ActiveWord;
            if (!BeginAssembly(index)) return false;
            bool accepted = WordReady(index, groupId, recognizedWord, finishMarker, worldPosition, componentIds);
            // A rejected camera observation must not leave physical input stuck in Assembling.
            if (!accepted && PhysicalWordsEnabled && Valid(token) && State == ConversationState.Assembling)
            {
                activeIndex = previousIndex; ActiveGroupId = previousGroup; ActiveWord = previousWord;
                SetState(previousState, previousStatus);
            }
            return accepted;
        }
        public bool BeginWord(int index, Vector3 worldPosition) => BeginAssembly(index) && WordReady(index, worldPosition);
        void EnsureRoundStorage(int index)
        {
            if (index < automaticAnswerRetries.Length) return;
            int capacity = Math.Max(index + 1, automaticAnswerRetries.Length * 2);
            Array.Resize(ref automaticAnswerRetries, capacity);
            Array.Resize(ref liveWordSoundJobs, capacity);
            Array.Resize(ref preparedLiveWordClips, capacity);
        }
        /// <summary>A separate period ends the answered queue without becoming a question or sound word.</summary>
        public bool RequestStoryFinish()
        {
            if (!PhysicalWordsEnabled || storyRequested || Record == null || Record.words.Count == 0 ||
                Record.turns.Count != Record.words.Count || State != ConversationState.Silent) return false;
            physicalFinishRequested = true;
            var finalWord = Record.words[Record.words.Count - 1];
            finalWord.finishMarker = true;
            if (!Save()) return false;
            StartCoroutine(StorySequence(finalWord.word, generation));
            return true;
        }
        /// <summary>Finish immediately with captured words and only answers already saved. No unfinished answer is invented.</summary>
        public bool CreateStoryFromSavedProgress(IReadOnlyList<WordRecord> additionalWords = null, bool useLiveProvider = true)
        {
            if (!PhysicalWordsEnabled || Record == null || storyRequested || State == ConversationState.RequestingStory ||
                State == ConversationState.Story || State == ConversationState.Finished) return false;
            var combined = new List<WordRecord>();
            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var word in Record.words) { combined.Add(CopyWord(word)); groups.Add(word.groupId); }
            if (additionalWords != null)
                foreach (var word in additionalWords)
                {
                    if (word == null || PhysicalWordInputPolicy.IsStoryFinishMarker(word.word)) continue;
                    if (groups.Add(word.groupId)) combined.Add(CopyWord(word));
                }
            if (combined.Count == 0) return false;
            for (int i = 0; i < combined.Count; i++)
            {
                var word = combined[i];
                if (!PhysicalWordInputPolicy.IsRecognizedWord(word.word) || !PhysicalIdentifier(word.groupId) ||
                    !Finite(word.worldPosition) || word.componentIds.Length != word.word.Length) return false;
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in word.componentIds) if (!PhysicalIdentifier(id) || !ids.Add(id)) return false;
                word.index = i;
                word.finishMarker = i == combined.Count - 1;
            }

            generation++; CancelJobMetrics(); StopAllCoroutines();
            wordSource.Stop(); voiceSource.Stop();
            if (activeSpatialSource != null) activeSpatialSource.Stop();
            Palette?.CancelPending(); Palette?.StopAllPlayback();
            microphone?.Cancel(); CancelProviderWork(false); liveAnswerPending = false;
            ClearWordPlayback(); backgroundJobs.Clear();
            pendingQuestion = ""; CurrentPrompt = "";
            if (useLiveProvider && !IsLive)
            {
                // ConfigureLive resets the record; manual finish must retain it.
                CancelProviderWork(true);
                liveProvider = new LiveStoryProvider(() => Record?.sessionId, () => RecordExperience) {
                    GenerationAttemptStarted = OnGenerationAttemptStarted
                };
                provider = liveProvider; IsLive = true; Record.fullyMocked = false;
                SessionPath = Path.Combine(RecordExperience.DirectoryUnder(Application.persistentDataPath), "LiveSessions", Record.sessionId + ".json");
            }
            Record.words = combined;
            activeIndex = combined.Count - 1;
            EnsureRoundStorage(activeIndex);
            var finalWord = combined[activeIndex];
            ActiveGroupId = finalWord.groupId; ActiveWord = finalWord.word;
            wordSource.transform.position = finalWord.worldPosition;
            manualStoryFinish = true; physicalFinishRequested = true;
            if (!Save()) return false;
            // A failed manual request can be retried with the same saved progress.
            int token = generation;
            SetState(ConversationState.RequestingStory, "Creating a story from your saved progress.");
            if (!Valid(token)) return false;
            StartCoroutine(StorySequence(finalWord.word, token));
            return true;
        }
        /// <summary>Only the active group's tracker can move the active spatial source. Call with each accepted pose.</summary>
        public bool UpdateSourcePosition(string groupId, Vector3 worldPosition)
        {
            if (wordSource == null || string.IsNullOrEmpty(ActiveGroupId) || groupId != ActiveGroupId || !Finite(worldPosition))
                return false;
            wordSource.transform.position = worldPosition;
            if (Record != null && activeIndex >= 0 && activeIndex < Record.words.Count)
                Record.words[activeIndex].worldPosition = worldPosition;
            return true;
        }
        /// <summary>Head/anchor tracking gate. It never pauses the listener or centered question/story speech.</summary>
        public void SetSpatialReady(bool ready)
        {
            bool wasReady = SpatialReady;
            spatialTrackingReady = ready;
            Palette?.SetSpatialReady(SpatialReady);
            if (SpatialReady == wasReady) return;
            if (!SpatialReady && wordPlaybackActive && wordPlaybackStarted && activeSpatialSource != null && activeSpatialSource.isPlaying)
            {
                activeSpatialSource.Pause(); wordPlaybackPaused = true;
            }
            else if (SpatialReady && wordPlaybackActive && wordPlaybackPaused && activeSpatialSource != null)
            {
                activeSpatialSource.UnPause(); wordPlaybackPaused = false;
            }
            Changed?.Invoke();
        }
        IEnumerator WordSequence(int index, int token)
        {
            if (!Valid(token)) yield break;
            var context = CaptureContext(Record.words[index].word);
            StoryReply reply = null;
            bool receivedReply = false;
            bool generatedSound = IsLive && PhysicalWordsEnabled && Palette.LiveEffectsEnabled;
            SetState(ConversationState.PlayingWord, generatedSound
                ? "Preparing your question. The word sound is being generated in the background."
                : "Preparing the question while the word sound plays. Input is closed.");
            if (!Valid(token)) yield break;
            RecordRequest("question", context);
            if (!Valid(token)) yield break;
            var requestProvider = provider;
            var questionJob = StartJob("question", index, () => requestProvider.Question(context, result => {
                if (Valid(token) && !receivedReply) { receivedReply = true; reply = result; }
            }), token);
            if (!Valid(token)) yield break;
            var soundJob = StartJob("word sound", index, () => generatedSound
                ? PrepareGeneratedWordSound(index, token) : PrepareAndPlayWord(index, token), token);
            if (generatedSound) liveWordSoundJobs[index] = soundJob;
            while (Valid(token) && (!questionJob.done || (!generatedSound && !soundJob.done)))
            {
                if (questionJob.error != null || (!generatedSound && soundJob.error != null))
                { RecoverRequestFailure(index, "question", reply, questionJob.error ?? soundJob.error); yield break; }
                if ((generatedSound || soundJob.done) && !questionJob.done && State != ConversationState.RequestingQuestion)
                    SetState(ConversationState.RequestingQuestion, "Preparing the question and its spoken audio.");
                yield return null;
            }
            if (!Valid(token)) yield break;
            if (questionJob.error != null || (!generatedSound && soundJob.error != null))
            { RecoverRequestFailure(index, "question", reply, questionJob.error ?? soundJob.error); yield break; }
            if (reply == null || !string.IsNullOrEmpty(reply.error))
            { RecoverRequestFailure(index, "question", reply, reply?.error ?? "Question provider returned no result."); yield break; }
            CurrentPrompt = pendingQuestion = reply.text;
            Log("question - " + reply.text);
            if (!Valid(token)) yield break;
            Palette.StopAllPlayback();
            SetState(ConversationState.Asking, "Question audio is playing. Input is closed.");
            if (!Valid(token)) yield break;
            NotifyPresentation(QuestionStarted, CopyWord(Record.words[index]));
            if (!Valid(token)) yield break;
            yield return PlayReply(reply, token);
            if (!Valid(token)) yield break;
            if (IsLive)
            {
                // Let the question tail settle; the ready cue then precedes microphone capture.
                yield return new WaitForSecondsRealtime(.1f);
                if (!Valid(token)) yield break;
                SetState(ConversationState.Listening, "Your turn. Preparing the microphone.");
                yield return LiveAnswerSequence(index, token);
            }
            else
            {
                Log("[Mock input] Listening. Click SIMULATE ANSWER; no microphone is accessed.");
                if (!Valid(token)) yield break;
                SetState(ConversationState.Listening, "Waiting for your simulated spoken answer.");
            }
        }
        IEnumerator PrepareAndPlayWord(int index, int token)
        {
            string word = Record.words[index].word;
            if (PhysicalWordsEnabled && !SoundPalettePipeline.TryGetResource(word, out _))
            {
                if (UseNeutralCueForUnknownPhysicalWords)
                {
                    Log("[Word sound] Neutral spatial word-accepted cue for " + word + "; locally synthesized, not a semantic effect or SATIE asset.");
                    if (!Valid(token)) yield break;
                    wordSource.volume = .8f * Palette.WordGainMultiplier;
                    yield return PlayPreparedClip(wordSource, GetNeutralWordCue(), .25f, token);
                }
                else
                {
                    Log("[Word sound] No bundled sound for " + word + "; skipping the optional word cue. The question still uses the recognized word.");
                    yield return null;
                }
                yield break;
            }
            AudioClip clip = null;
            yield return Palette.PrepareWord(word, value => { if (Valid(token)) clip = value; });
            if (!Valid(token)) yield break;
            if (clip == null) throw new InvalidOperationException("Word sound unavailable. " + Palette.Status);
            wordSource.volume = .8f * Palette.WordGainMultiplier;
            Log("sound output - " + word);
            if (!Valid(token)) yield break;
            yield return PlayPreparedClip(wordSource, clip, 2f, token);
        }
        IEnumerator PrepareGeneratedWordSound(int index, int token)
        {
            string word = Record.words[index].word, groupId = Record.words[index].groupId;
            AudioClip clip = null;
            yield return Palette.PrepareWord(word, value => { if (Valid(token)) clip = value; });
            if (!Valid(token)) yield break;
            if (clip == null) throw new InvalidOperationException("Generated word sound unavailable. " + Palette.Status);
            preparedLiveWordClips[index] = clip;
            WordSoundPrepared?.Invoke(groupId, clip);
            Log("[Word sound] Generated sound attached to " + word + "; playback waits for the saved answer.");
        }
        IEnumerator PlayGeneratedWordAfterAnswer(int index, int token)
        {
            var job = liveWordSoundJobs[index];
            if (job == null) yield break;
            if (!job.done) SetState(ConversationState.PlayingWord, "Your answer is saved. Finishing the word sound.");
            while (Valid(token) && !job.done) yield return null;
            if (!Valid(token)) yield break;
            var clip = preparedLiveWordClips[index];
            if (job.error != null || clip == null)
            {
                Log("[Word sound] Optional effect failed; keeping the saved answer. " + (job.error ?? Palette.Status));
                yield break;
            }
            var word = Record.words[index];
            AudioSource source = null;
            try { source = ResolveWordAudioSource?.Invoke(word.groupId); }
            catch (Exception error) { Log("[Word sound] Anchor source unavailable: " + error.Message); }
            if (source == null) { source = wordSource; source.transform.position = word.worldPosition; }
            source.volume = .8f * Palette.WordGainMultiplier;
            SetState(ConversationState.PlayingWord, "Playing the sound from your word box.");
            yield return PlayPreparedClip(source, clip, 0, token);
        }
        AudioClip GetNeutralWordCue()
        {
            if (neutralWordCue != null) return neutralWordCue;
            const int sampleRate = 24000;
            const float duration = .25f;
            var samples = new float[Mathf.RoundToInt(sampleRate * duration)];
            for (int i = 0; i < samples.Length; i++)
            {
                float phase = i / (float)(samples.Length - 1);
                float envelope = Mathf.Sin(Mathf.PI * phase);
                samples[i] = .12f * envelope * envelope * Mathf.Sin(2 * Mathf.PI * 660 * i / sampleRate);
            }
            neutralWordCue = AudioClip.Create("Neutral physical word accepted cue (local)", samples.Length, 1, sampleRate, false);
            if (!neutralWordCue.SetData(samples, 0))
            {
                Destroy(neutralWordCue); neutralWordCue = null;
                throw new InvalidOperationException("Could not prepare the neutral spatial confirmation cue.");
            }
            return neutralWordCue;
        }
        public void SimulateAnswer()
        {
            if (IsLive) { AnswerAction(); return; }
            if (!CanAnswer) { Log("[Input ignored] Mock input opens only after the question finishes."); return; }
            StartCoroutine(AnswerSequence(activeIndex, generation));
        }
        public void SubmitMockAnswer() => SimulateAnswer();
        /// <summary>In live mode: finish the current recording, or retry an answer after no speech/permission/transcription failure.</summary>
        public void AnswerAction()
        {
            if (!IsLive) { SimulateAnswer(); return; }
            if (!CanAnswer) { Log("[Input ignored] Answer input opens only after the question finishes."); return; }
            double now = Time.realtimeSinceStartupAsDouble;
            if (now - lastAnswerActionAt < .6) { Log("[Microphone] Repeated answer-button press ignored."); return; }
            lastAnswerActionAt = now;
            if (microphone.IsRecording) { microphone.TryFinishAnswer(); return; }
            if (!liveAnswerPending) StartCoroutine(LiveAnswerSequence(activeIndex, generation));
        }
        IEnumerator LiveAnswerSequence(int index, int token)
        {
            if (!Valid(token) || liveAnswerPending || !IsLive || !CanAnswer) yield break;
            liveAnswerPending = true;
            bool retry = lastAnswerFailureIndex == index;
            while (Valid(token))
            {
                if (InterruptedAnswerMatches(index, token))
                {
                    // Resume the same unanswered round with a fresh recording.
                    // A canceled partial recording is never sent for transcription.
                    while (Valid(token) && (applicationPaused || !SpatialReady || IsAudioPlaying)) yield return null;
                    if (!Valid(token) || !InterruptedAnswerMatches(index, token)) yield break;
                    ClearInterruptedAnswer(); retry = true;
                    Log("[Voice input] Resuming the interrupted answer. Listen for the ready tone, then speak again.");
                    if (!Valid(token)) yield break;
                }
                else if (applicationPaused) { yield return null; continue; }
                SetState(ConversationState.PreparingAnswer, retry
                    ? "Let's try your answer again. Listen for the ready tone, then speak."
                    : "Listen for the ready tone, then speak your answer.");
                if (!Valid(token)) yield break;
                Palette.StopAllPlayback();
                Log("[Voice input] " + (retry ? "Retry" : "Ready") + "; preparing the microphone before the ready cue. Answer detection remains closed during the cue.");
                if (!Valid(token)) yield break;
                if (retry)
                {
                    yield return PlayPreparedClip(voiceSource, GetInputCue(true), 0, token);
                    if (!Valid(token)) yield break;
                    yield return new WaitForSecondsRealtime(.12f);
                }
                if (InterruptedAnswerMatches(index, token)) { retry = true; continue; }
                MicrophoneCapture capture = null;
                yield return microphone.Capture(value => { if (Valid(token)) capture = value; },
                    () => MicrophoneReadyCue(token));
                if (!Valid(token)) yield break;
                if (InterruptedAnswerMatches(index, token)) { retry = true; continue; }
                if (capture == null || !string.IsNullOrEmpty(capture.error))
                {
                    lastAnswerFailureIndex = index;
                    string detail = capture?.error ?? "Recording was canceled. Earlier answers are kept.";
                    Log("[Microphone] " + detail + " | code=" + (capture?.code ?? "canceled"));
                    if (!Valid(token)) yield break;
                    if (capture != null && capture.retryable && TryAutomaticAnswerRetry(index, capture.code))
                    { retry = true; continue; }
                    yield return AwaitManualAnswerRetry(detail, token);
                    if (InterruptedAnswerMatches(index, token)) { liveAnswerPending = true; retry = true; continue; }
                    yield break;
                }

                SetState(ConversationState.ReceivingAnswer, "Transcribing your answer. Microphone is closed; please wait.");
                if (!Valid(token)) yield break;
                Log("[Microphone] Captured " + capture.seconds.ToString("F1") + " seconds; ended by " + capture.endedBy +
                    "; meanRms=" + capture.meanRms.ToString("F5") + "; peakRms=" + capture.peakRms.ToString("F5") + ".");
                if (!Valid(token)) yield break;
                RecordRequest("transcription", CaptureContext(Record.words[index].word));
                if (!Valid(token)) yield break;
                BackendReply transcript = null;
                yield return liveProvider.Transcribe(capture.wav, value => { if (Valid(token)) transcript = value; });
                if (!Valid(token)) yield break;
                if (transcript == null || !string.IsNullOrEmpty(transcript.error) || string.IsNullOrWhiteSpace(transcript.text) || transcript.text.Length > 4000)
                {
                    lastAnswerFailureIndex = index;
                    string detail = transcript?.error ?? "No usable speech transcript was returned. Earlier answers are kept.";
                    Log("[Transcription] " + detail);
                    if (!Valid(token)) yield break;
                    string code = transcript?.code;
                    if (transcript != null && string.IsNullOrEmpty(transcript.error) && string.IsNullOrWhiteSpace(transcript.text)) code = "no_speech";
                    if (TryAutomaticAnswerRetry(index, code)) { retry = true; continue; }
                    yield return AwaitManualAnswerRetry(detail, token);
                    if (InterruptedAnswerMatches(index, token)) { liveAnswerPending = true; retry = true; continue; }
                    yield break;
                }
                liveAnswerPending = false;
                lastAnswerFailureIndex = -1;
                yield return SaveAnswer(index, transcript.text.Trim(), token);
                yield break;
            }
        }
        IEnumerator MicrophoneReadyCue(int token)
        {
            if (!Valid(token)) yield break;
            yield return PlayPreparedClip(voiceSource, GetInputCue(false), 0, token);
            if (!Valid(token)) yield break;
            // This short speaker tail is discarded along with the cue samples.
            yield return new WaitForSecondsRealtime(.08f);
        }
        void OnApplicationPause(bool paused)
        {
            applicationPaused = paused;
            if (!paused || !IsLive || !liveAnswerPending || Record == null || activeIndex < 0 ||
                Record.words.Count <= activeIndex || Record.turns.Count != activeIndex ||
                (State != ConversationState.Listening && State != ConversationState.PreparingAnswer)) return;
            interruptedAnswerSession = Record.sessionId;
            interruptedAnswerGeneration = generation; interruptedAnswerIndex = activeIndex;
            microphone?.Cancel();
            // These states only use the ready/retry cue on the voice source.
            // Stop that cue so it cannot keep resume waiting for playback to end.
            if (voiceSource != null) voiceSource.Stop();
        }
        bool InterruptedAnswerMatches(int index, int token) => interruptedAnswerGeneration == token &&
            token == generation && interruptedAnswerIndex == index && activeIndex == index &&
            Record != null && Record.sessionId == interruptedAnswerSession && Record.turns.Count == index;
        void ClearInterruptedAnswer()
        {
            interruptedAnswerSession = null; interruptedAnswerGeneration = -1; interruptedAnswerIndex = -1;
        }
        void OnMicrophoneStateChanged()
        {
            if (!IsLive || !liveAnswerPending || microphone == null) return;
            if (microphone.IsRecording) SetState(ConversationState.Listening, microphone.Status);
            else if (microphone.IsBusy) SetState(ConversationState.PreparingAnswer, microphone.Status);
        }
        bool TryAutomaticAnswerRetry(int index, string code)
        {
            if (!ShouldAutomaticallyRetryAnswer(automaticAnswerRetries[index], code)) return false;
            automaticAnswerRetries[index]++;
            Log("[Voice input] One automatic retry for word " + (index + 1) + " after " + code + ". No answer was saved.");
            return true;
        }
        public static bool ShouldAutomaticallyRetryAnswer(int retriesUsed, string code) => retriesUsed == 0 && code == "no_speech";
        IEnumerator AwaitManualAnswerRetry(string detail, int token)
        {
            SetState(ConversationState.PreparingAnswer, detail);
            if (!Valid(token)) yield break;
            yield return PlayPreparedClip(voiceSource, GetInputCue(true), 0, token);
            if (!Valid(token)) yield break;
            liveAnswerPending = false;
            SetState(ConversationState.Listening, detail + " Press A once to retry; wait for the ready tone before speaking.");
        }
        AudioClip GetInputCue(bool retry)
        {
            var cached = retry ? retryInputCue : readyInputCue;
            if (cached != null) return cached;
            const int sampleRate = 24000;
            const float noteSeconds = .08f, gapSeconds = .04f;
            var samples = new float[Mathf.RoundToInt(sampleRate * (2 * noteSeconds + gapSeconds))];
            for (int i = 0; i < samples.Length; i++)
            {
                float time = i / (float)sampleRate;
                bool second = time >= noteSeconds + gapSeconds;
                float local = second ? time - noteSeconds - gapSeconds : time;
                if (local < 0 || local >= noteSeconds) continue;
                float frequency = retry ? (second ? 540 : 720) : (second ? 1100 : 850);
                float envelope = Mathf.Sin(Mathf.PI * local / noteSeconds);
                samples[i] = .12f * envelope * envelope * Mathf.Sin(2 * Mathf.PI * frequency * local);
            }
            cached = AudioClip.Create(retry ? "Local answer retry cue" : "Local answer ready cue", samples.Length, 1, sampleRate, false);
            if (!cached.SetData(samples, 0)) { Destroy(cached); Fail("Could not prepare the voice-input cue."); return null; }
            if (retry) retryInputCue = cached; else readyInputCue = cached;
            return cached;
        }
        IEnumerator AnswerSequence(int index, int token)
        {
            SetState(ConversationState.ReceivingAnswer, "Receiving simulated answer audio.");
            Log("[Mock input] Simulated speech started.");
            yield return PlayClip(voiceSource, "StoryAudio/answer_" + words[index].ToLowerInvariant(), 0, token);
            if (!Valid(token)) yield break;
            Log("[Mock input] Simulated end of speech. No real VAD or transcription was used.");
            yield return SaveAnswer(index, answers[index], token);
        }
        IEnumerator SaveAnswer(int index, string answer, int token)
        {
            if (!Valid(token) || Record.turns.Count != index || State != ConversationState.ReceivingAnswer) yield break;
            var word = Record.words[index];
            Record.turns.Add(new ConversationTurn { wordIndex = word.index, groupId = word.groupId,
                componentIds = CopyIds(word.componentIds), word = word.word,
                question = pendingQuestion, answer = answer, answeredAt = UtcNow() });
            Log("answer - " + answer);
            if (!Valid(token)) yield break;
            if (!Save()) yield break;
            NotifyPresentation(AnswerSaved, CopyTurn(Record.turns[Record.turns.Count - 1]));
            if (!Valid(token)) yield break;
            CurrentPrompt = "";
            if (IsLive && PhysicalWordsEnabled && Palette.LiveEffectsEnabled)
            {
                yield return PlayGeneratedWordAfterAnswer(index, token);
                if (!Valid(token)) yield break;
            }
            if (word.finishMarker)
            {
                Log("[Saved] " + word.word + " + final question + answer. " +
                    "The recognized finish marker now requests the story.");
                yield return StorySequence(word.word, token);
                yield break;
            }
            CurrentPrompt = "What comes next?";
            SetState(ConversationState.PromptingNextWord, "Answer saved. Asking what comes next; capture and microphone input are closed.");
            if (!Valid(token)) yield break;
            Log("[Next word] What comes next? (prerecorded local speech; no API request)");
            if (!Valid(token)) yield break;
            yield return PlayClip(voiceSource, "StoryAudio/next_word", 0, token);
            if (!Valid(token)) yield break;
            CurrentPrompt = "";
            SetState(ConversationState.Silent, "Answer saved. Capture the next word, or a period to create your story.");
            Log("[Saved] " + word.word + " + question + answer. Input closed; staying quiet.");
        }
        IEnumerator StorySequence(string finalWord, int token)
        {
            if (!Valid(token) || storyRequested) yield break;
            storyRequested = true;
            var context = CaptureContext(finalWord);
            SetState(ConversationState.RequestingStory, IsLive ? "Creating your story from all " + Record.words.Count + " words and saved answers." : "Mock API: combining the saved answers with the words.");
            if (!Valid(token)) yield break;
            Log("words - (" + string.Join(", ", context.words.ConvertAll(w => w.word)) + ")");
            if (!Valid(token)) yield break;
            RecordRequest("story", context);
            if (!Valid(token)) yield break;
            StoryReply reply = null;
            bool receivedReply = false;
            var requestProvider = provider;
            var storyJob = StartJob("story", activeIndex, () => requestProvider.Story(context, result => {
                if (Valid(token) && !receivedReply) { receivedReply = true; reply = result; }
            }), token);
            if (!Valid(token)) yield break;
            StartJob("story sound preparation", activeIndex, () => PrepareStorySounds(context, token), token);
            while (Valid(token) && !storyJob.done) yield return null;
            if (!Valid(token)) yield break;
            if (storyJob.error != null) { RecoverRequestFailure(activeIndex, "story", reply, storyJob.error); yield break; }
            if (reply == null || !string.IsNullOrEmpty(reply.error))
            { RecoverRequestFailure(activeIndex, "story", reply, reply?.error ?? "Story provider returned no result."); yield break; }
            Record.finalStory = reply.text; Record.finalStoryAudioFile = reply.audioFile;
            Record.finalStoryAudioPath = reply.audioPath; CurrentPrompt = reply.text;
            Log("STORY!");
            if (!Valid(token)) yield break;
            Log("story - " + reply.text);
            if (!Valid(token)) yield break;
            if (!Save() || !ExportStoryOutput(reply, context, token) || !Valid(token)) yield break;
            SetState(ConversationState.Story, IsLive ? "Narrating your story. Input is closed." : "Narrating the mock story. Input is closed.");
            if (!Valid(token)) yield break;
            // Optional ambience never delays narration; preparation can finish during speech.
            Palette.StartStoryAmbience(context);
            yield return PlayReply(reply, token);
            if (!Valid(token)) yield break;
            Palette.StopAllPlayback();
            Record.completed = true;
            if (!Save()) yield break;
            SetState(ConversationState.Finished, "Story finished. All " + Record.words.Count + " words and answers are saved.");
            if (!Valid(token)) yield break;
            if (lastStoryOutput != null) NotifyPresentation(StoryNarrationCompleted, CopyStoryOutput(lastStoryOutput));
            if (!Valid(token)) yield break;
            Log("[Session complete] Silent. Reset to start again.");
        }
        IEnumerator PrepareStorySounds(StoryContext context, int token)
        {
            bool prepared = false;
            yield return Palette.PrepareStory(context, () => { if (Valid(token)) prepared = true; });
            if (Valid(token) && prepared && State == ConversationState.Story && !Palette.IsAmbiencePlaying && !Palette.IsAmbiencePaused)
                Palette.StartStoryAmbience(context);
        }
        IEnumerator PlayReply(StoryReply reply, int token)
        {
            if (!Valid(token)) yield break;
            if (reply.audioClip == null)
            {
                if (IsLive) { Fail("The backend returned no playable generated speech."); yield break; }
                yield return PlayClip(voiceSource, reply.audioResource, 0, token);
                yield break;
            }
            voiceSource.Stop(); voiceSource.clip = reply.audioClip;
            if (TestFastMode) { yield return null; yield break; }
            voiceSource.Play();
            while (Valid(token) && voiceSource.isPlaying) yield return null;
            if (Valid(token)) voiceSource.Stop();
        }
        IEnumerator PlayClip(AudioSource source, string resource, float capSeconds, int token)
        {
            if (!Valid(token)) yield break;
            var clip = Resources.Load<AudioClip>(resource);
            if (clip == null) { Fail("Missing prepared audio: Resources/" + resource); yield break; }
            yield return PlayPreparedClip(source, clip, capSeconds, token);
        }
        IEnumerator PlayPreparedClip(AudioSource source, AudioClip clip, float capSeconds, int token)
        {
            if (!Valid(token)) yield break;
            source.Stop(); source.clip = clip;
            bool spatial = source.spatialBlend > 0;
            if (spatial)
            {
                activeSpatialSource = source;
                wordPlaybackActive = true; wordPlaybackStarted = false; wordPlaybackPaused = false;
                // Do not start a block cue at an invalid or stale world location.
                while (Valid(token) && !SpatialReady) yield return null;
                if (!Valid(token)) yield break;
            }
            if (TestFastMode)
            {
                yield return null;
                if (spatial)
                {
                    while (Valid(token) && !SpatialReady) yield return null;
                    if (Valid(token)) ClearWordPlayback();
                }
                yield break;
            }
            source.Play();
            if (spatial) wordPlaybackStarted = true;
            while (Valid(token))
            {
                if (spatial && !SpatialReady)
                {
                    if (!wordPlaybackPaused && source.isPlaying) { source.Pause(); wordPlaybackPaused = true; }
                    // Paused time must not consume the effect's two-second playback budget.
                    yield return null;
                    continue;
                }
                if (spatial && wordPlaybackPaused) { source.UnPause(); wordPlaybackPaused = false; }
                // Audio sample position excludes tracking suspension and application background time.
                float playedSeconds = source.clip.frequency > 0 ? (float)source.timeSamples / source.clip.frequency : source.time;
                if (!source.isPlaying || (capSeconds > 0 && playedSeconds >= capSeconds)) break;
                yield return null;
            }
            if (Valid(token))
            {
                source.Stop();
                if (spatial) ClearWordPlayback();
            }
        }
        BackgroundJob StartJob(string operation, int index, Func<IEnumerator> routine, int token)
        {
            var job = new BackgroundJob { metric = new ParallelJobMetric {
                operation = operation, wordIndex = index, status = "Running", startedAt = Time.realtimeSinceStartupAsDouble
            } };
            backgroundJobs.Add(job);
            Record.parallelJobs.Add(job.metric);
            Changed?.Invoke();
            if (Valid(token)) StartCoroutine(RunJob(job, routine, token));
            return job;
        }
        IEnumerator RunJob(BackgroundJob job, Func<IEnumerator> routine, int token)
        {
            // Flatten nested iterator work so a provider/preparation exception becomes a completed
            // failed job instead of leaving the main sequence waiting forever.
            var iterators = new Stack<IEnumerator>();
            IEnumerator first = null;
            try { first = routine(); }
            catch (Exception error) { job.error = error.Message; }
            if (first == null && job.error == null) job.error = "The " + job.metric.operation + " job returned no work.";
            if (first != null) iterators.Push(first);
            while (Valid(token) && job.error == null && iterators.Count > 0)
            {
                var current = iterators.Peek();
                bool moved = false;
                object yielded = null;
                try { moved = current.MoveNext(); if (moved) yielded = current.Current; }
                catch (Exception error) { job.error = error.Message; }
                if (job.error != null) break;
                if (!moved)
                {
                    iterators.Pop();
                    try { (current as IDisposable)?.Dispose(); }
                    catch (Exception error) { job.error = error.Message; }
                    continue;
                }
                if (yielded is IEnumerator nested) { iterators.Push(nested); continue; }
                yield return yielded;
            }
            while (iterators.Count > 0)
            {
                try { (iterators.Pop() as IDisposable)?.Dispose(); }
                catch (Exception error) { if (job.error == null) job.error = error.Message; }
            }
            if (!Valid(token)) yield break;
            job.done = true;
            job.metric.completedAt = Time.realtimeSinceStartupAsDouble;
            job.metric.error = job.error;
            job.metric.status = job.error == null ? "Complete" : "Failed";
            Changed?.Invoke();
        }
        string JobStatus(string operation)
        {
            for (int i = backgroundJobs.Count - 1; i >= 0; i--)
                if (backgroundJobs[i].metric.operation == operation) return backgroundJobs[i].metric.status;
            return "Idle";
        }
        void CancelJobMetrics()
        {
            foreach (var job in backgroundJobs)
            {
                if (job.done) continue;
                job.done = true;
                job.metric.status = "Cancelled";
                job.metric.completedAt = Time.realtimeSinceStartupAsDouble;
            }
        }
        void CancelProviderWork(bool releaseClips)
        {
            if (provider is LiveStoryProvider live)
            {
                if (releaseClips) live.Dispose(); else live.CancelRequests();
            }
            else if (provider is ICancellableStoryProvider cancellable) cancellable.CancelRequests();
        }
        StoryContext CaptureContext(string current)
        {
            var context = new StoryContext { currentWord = current,
                completionMode = manualStoryFinish ? "manual_saved_progress" : "" };
            foreach (var w in Record.words)
                context.words.Add(CopyWord(w));
            foreach (var t in Record.turns)
                context.turns.Add(CopyTurn(t));
            return context;
        }
        static string[] CopyIds(string[] ids) => ids != null ? (string[])ids.Clone() : new string[0];
        static WordRecord CopyWord(WordRecord word) => new WordRecord {
            index = word.index, groupId = word.groupId, word = word.word, finishMarker = word.finishMarker,
            componentIds = CopyIds(word.componentIds), worldPosition = word.worldPosition, completedAt = word.completedAt
        };
        static ConversationTurn CopyTurn(ConversationTurn turn) => new ConversationTurn {
            wordIndex = turn.wordIndex, groupId = turn.groupId, componentIds = CopyIds(turn.componentIds),
            word = turn.word, question = turn.question, answer = turn.answer, answeredAt = turn.answeredAt
        };
        static void NotifyPresentation<T>(Action<T> handlers, T value)
        {
            if (handlers == null) return;
            foreach (Action<T> handler in handlers.GetInvocationList())
                try { handler(value); } catch (Exception error) { Debug.LogWarning("[Presentation event] " + error.GetType().Name); }
        }
        static StoryOutput CopyStoryOutput(StoryOutput output)
        {
            var snapshot = new StoryOutput { schemaVersion = output.schemaVersion, sessionId = output.sessionId,
                workspaceId = output.workspaceId, experienceSessionId = output.experienceSessionId,
                storyText = output.storyText, storyAudioResource = output.storyAudioResource, storyAudioFile = output.storyAudioFile,
                storyAudioPath = output.storyAudioPath };
            foreach (var word in output.words) snapshot.words.Add(CopyWord(word));
            foreach (var turn in output.turns) snapshot.turns.Add(CopyTurn(turn));
            return snapshot;
        }
        bool ExportStoryOutput(StoryReply reply, StoryContext context, int token)
        {
            if (!Valid(token) || lastStoryOutput != null) return false;
            var output = new StoryOutput { schemaVersion = IsLive ? 2 : 1, sessionId = Record.sessionId, storyText = reply.text,
                workspaceId = Record.workspaceId, experienceSessionId = Record.experienceSessionId,
                storyAudioResource = reply.audioResource, storyAudioFile = reply.audioFile, storyAudioPath = reply.audioPath };
            foreach (var word in context.words) output.words.Add(CopyWord(word));
            foreach (var turn in context.turns) output.turns.Add(CopyTurn(turn));
            string path = Path.Combine(RecordExperience.DirectoryUnder(Application.persistentDataPath), "StoryOutputs", output.sessionId + ".json");
            try { WriteJson(path, JsonUtility.ToJson(output, true)); }
            catch (Exception e) { Fail("Could not export story output: " + e.Message); return false; }
            lastStoryOutput = output; StoryOutputPath = path;
            Log("[Story output] Saved local animation handoff: " + path + ". No animation API was called.");
            // Each adapter receives its own copy; a reset inside a callback cancels the remaining old-session work.
            var handlers = StoryOutputReady;
            if (handlers != null)
            {
                foreach (Action<StoryOutput> handler in handlers.GetInvocationList())
                {
                    if (!Valid(token)) return false;
                    try { handler(CopyStoryOutput(output)); }
                    catch (Exception e) { Log("[Story output adapter error] " + e.Message); }
                }
            }
            return Valid(token);
        }
        void RecordRequest(string operation, StoryContext context)
        {
            string json = JsonUtility.ToJson(context);
            Record.requests.Add(new MockRequestRecord { operation = operation, createdAt = UtcNow(), payloadJson = json, simulated = !IsLive });
            Log((IsLive ? "[API " : "[Mock API ") + operation + "] " + json);
            Save();
        }
        void OnGenerationAttemptStarted(string operation, int attempt)
        {
            if (Record == null || !IsLive || State == ConversationState.Error) return;
            Record.requests.Add(new MockRequestRecord { operation = operation + " attempt", createdAt = UtcNow(),
                attempt = attempt, payloadJson = "", simulated = false });
            SetState(operation == "story" ? ConversationState.RequestingStory : ConversationState.RequestingQuestion,
                (attempt == 1 ? "Preparing " : "Trying again: ") + (operation == "story" ? "your story" : "your question") +
                " (" + attempt + " of " + LiveStoryProvider.MaximumGenerationAttempts + ").");
            Log("[Generation attempt] " + operation + " " + attempt + "/" + LiveStoryProvider.MaximumGenerationAttempts);
            Save();
        }
        void RecoverRequestFailure(int index, string operation, StoryReply reply, string error)
        {
            if (operation == "story" && (manualStoryFinish || PhysicalWordsEnabled))
            {
                Record.failures.Add(new ConversationFailureRecord { operation = operation, error = error,
                    errorCode = reply?.errorCode ?? "provider_failure", generationAttempts = reply?.generationAttempts ?? 0,
                    failedAt = UtcNow(), recaptureRequested = false });
                storyRequested = false;
                Fail("Story creation failed. Your words and answers are saved. Open Finish to try again or end the session.");
                Save();
                return;
            }
            bool finalRound = operation == "story";
            bool recoverableRound = PhysicalWordsEnabled && Record != null && index >= 0 &&
                index == Record.words.Count - 1 && index == activeIndex &&
                Record.turns.Count == index + (finalRound ? 1 : 0);
            if (!recoverableRound) { Fail(error); return; }

            var failedWord = CopyWord(Record.words[index]);
            var failedAnswer = finalRound ? CopyTurn(Record.turns[index]) : null;
            Record.failures.Add(new ConversationFailureRecord { operation = operation, error = error,
                errorCode = reply?.errorCode ?? "provider_failure", generationAttempts = reply?.generationAttempts ?? 0,
                failedAt = UtcNow(), word = failedWord, answer = failedAnswer, recaptureRequested = true });
            generation++; CancelJobMetrics(); StopAllCoroutines();
            wordSource.Stop(); voiceSource.Stop();
            if (activeSpatialSource != null) activeSpatialSource.Stop();
            Palette?.CancelPending(); Palette?.StopAllPlayback();
            microphone?.Cancel(); CancelProviderWork(false); liveAnswerPending = false;
            ClearWordPlayback();
            Record.words.RemoveAt(index);
            if (finalRound) Record.turns.RemoveAt(index);
            backgroundJobs.RemoveAll(job => job.metric.wordIndex == index);
            liveWordSoundJobs[index] = null; preparedLiveWordClips[index] = null;
            automaticAnswerRetries[index] = 0;
            lastAnswerFailureIndex = -1; lastAnswerActionAt = double.NegativeInfinity;
            activeIndex = -1; ActiveGroupId = ""; ActiveWord = ""; pendingQuestion = ""; CurrentPrompt = "";
            storyRequested = false; Record.completed = false;
            Record.finalStory = null; Record.finalStoryAudioFile = null; Record.finalStoryAudioPath = null;
            lastStoryOutput = null; StoryOutputPath = "";
            if (!Save()) return;
            SetState(index == 0 ? ConversationState.Ready : ConversationState.Silent,
                "Could not finish " + (finalRound ? "the story" : "the question") +
                ". Double pinch to photograph this word again. Earlier answers are kept.");
            Log("[Recapture requested] " + operation + " attempts=" + (reply?.generationAttempts ?? 0) +
                " code=" + (reply?.errorCode ?? "provider_failure") + "; earlier rounds retained.");
            WordRecaptureRequested?.Invoke(failedWord.groupId);
        }
        public void ResetSession()
        {
            ClearInterruptedAnswer();
            generation++; CancelJobMetrics(); StopAllCoroutines();
            if (wordSource != null) wordSource.Stop(); if (voiceSource != null) voiceSource.Stop();
            if (activeSpatialSource != null) activeSpatialSource.Stop();
            if (wordSource != null) wordSource.clip = null;
            if (voiceSource != null) voiceSource.clip = null;
            microphone?.Cancel(); CancelProviderWork(true); liveAnswerPending = false;
            Array.Clear(automaticAnswerRetries, 0, automaticAnswerRetries.Length);
            lastAnswerFailureIndex = -1; lastAnswerActionAt = double.NegativeInfinity;
            Palette?.ResetSession(); Palette?.SetSpatialReady(SpatialReady);
            ClearWordPlayback();
            backgroundJobs.Clear();
            Array.Clear(liveWordSoundJobs, 0, liveWordSoundJobs.Length);
            Array.Clear(preparedLiveWordClips, 0, preparedLiveWordClips.Length);
            activeIndex = -1; ActiveGroupId = ""; ActiveWord = ""; pendingQuestion = ""; CurrentPrompt = ""; logs.Clear();
            storyRequested = false; physicalFinishRequested = false; manualStoryFinish = false; lastStoryOutput = null; StoryOutputPath = "";
            Record = new ConversationRecord { sessionId = Guid.NewGuid().ToString("N"), startedAt = UtcNow(), fullyMocked = !IsLive,
                workspaceId = experience.WorkspaceId, experienceSessionId = experience.ExperienceSessionId };
            SessionPath = Path.Combine(experience.DirectoryUnder(Application.persistentDataPath), IsLive ? "LiveSessions" : "MockSessions", Record.sessionId + ".json");
            SetState(ConversationState.Ready, "Complete the first word.");
            Log(IsLive ? "[Live voice] Microphone, transcription, questions, narration and story use the local Python backend. " +
                (PhysicalWordsEnabled ? "Word observations come from the physical input coordinator. " : "Word detection is simulated. ") + "Speech is AI-generated."
                : "[Fully mocked] API, speech recognition and microphone are simulated.");
            Log("[Session file] " + SessionPath);
            if (!SpatializerAvailableOnPlatform())
            {
                Status = "Quest spatial audio unavailable. Select Meta XR Audio in Project Settings > Audio > Spatializer Plugin, then rebuild.";
                Log("[AUDIO SETUP ERROR] " + Status + " Word cues are blocked.");
            }
            else Log(SpatializerConfigured
                ? "[Spatial audio] Meta XR Audio is selected. Native HRTF remains unverified on physical Quest hardware."
                : "[Spatial audio] Desktop 3D panning only. Meta XR Audio must be selected before a Quest build; native HRTF remains hardware-unverified.");
        }
        bool Save()
        {
            try
            {
                WriteJson(SessionPath, JsonUtility.ToJson(Record, true));
                return true;
            }
            catch (Exception e) { Fail("Could not save session: " + e.Message); return false; }
        }
        static void WriteJson(string path, string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        void SetState(ConversationState state, string status) { State = state; Status = status; Changed?.Invoke(); }
        void Fail(string message)
        {
            if (manualStoryFinish) storyRequested = false;
            generation++; CancelJobMetrics(); StopAllCoroutines();
            wordSource.Stop(); voiceSource.Stop();
            if (activeSpatialSource != null) activeSpatialSource.Stop();
            Palette?.CancelPending(); Palette?.StopAllPlayback();
            microphone?.Cancel(); CancelProviderWork(false); liveAnswerPending = false;
            ClearWordPlayback();
            SetState(ConversationState.Error, message); Log("[ERROR] " + message);
        }
        bool RejectRecognition(string message) { Log("[Recognition rejected] " + message); return false; }
        void ClearWordPlayback() { wordPlaybackActive = false; wordPlaybackStarted = false; wordPlaybackPaused = false; activeSpatialSource = null; }
        void Log(string message)
        {
            logs.Add(message); Debug.Log(message); Changed?.Invoke();
        }
        bool Valid(int token) => token == generation && State != ConversationState.Error;
        bool SpatializerAvailableOnPlatform() => Application.platform != RuntimePlatform.Android || SpatializerConfigured;
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        static bool PhysicalIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 80) return false;
            foreach (char c in value)
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == ':' || c == '-')) return false;
            return true;
        }
        static string UtcNow() => DateTime.UtcNow.ToString("o");
        void OnDisable()
        {
            ClearInterruptedAnswer();
            generation++; CancelJobMetrics(); StopAllCoroutines(); if (wordSource != null) wordSource.Stop(); if (voiceSource != null) voiceSource.Stop();
            if (activeSpatialSource != null) activeSpatialSource.Stop();
            Palette?.CancelPending(); Palette?.StopAllPlayback();
            microphone?.Cancel(); CancelProviderWork(true); liveAnswerPending = false; ClearWordPlayback();
        }
        void OnDestroy()
        {
            if (microphone != null) microphone.StateChanged -= OnMicrophoneStateChanged;
            if (neutralWordCue != null)
            {
                if (wordSource != null && wordSource.clip == neutralWordCue) { wordSource.Stop(); wordSource.clip = null; }
                Destroy(neutralWordCue); neutralWordCue = null;
            }
            if (voiceSource != null && (voiceSource.clip == readyInputCue || voiceSource.clip == retryInputCue))
            { voiceSource.Stop(); voiceSource.clip = null; }
            if (readyInputCue != null) { Destroy(readyInputCue); readyInputCue = null; }
            if (retryInputCue != null) { Destroy(retryInputCue); retryInputCue = null; }
        }
    }
}
