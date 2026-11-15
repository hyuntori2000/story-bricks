using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace BrailleSoundDemo.Presentation
{
    /// <summary>Prepared Judy content only. Recognition and session ownership stay in the core router.</summary>
    public sealed class JudySequencePlayer : MonoBehaviour
    {
        public JudyCueSequence sequence;
        public event Action<string, string, string> CueChanged;
        public event Action<string> CaptionChanged;
        public event Action StoryStarted, StoryCompleted;
        public string FinalStoryText => sequence != null ? sequence.finalStoryText ?? "" : "";
        public bool StoryRequested { get; private set; }
        public bool Busy { get; private set; }
        public string Status { get; private set; } = "Judy is ready.";
        public string SessionId { get; private set; }

        sealed class Accepted { public JudyWordCue cue; public string word, group; public Transform anchor; }
        readonly Queue<Accepted> queued = new Queue<Accepted>();
        readonly HashSet<string> groups = new HashSet<string>(StringComparer.Ordinal);
        readonly List<GameObject> owned = new List<GameObject>();
        readonly List<GameObject> spatialVisuals = new List<GameObject>();
        readonly List<AudioSource> positionalAudio = new List<AudioSource>();
        readonly HashSet<Transform> capturedAnchors = new HashSet<Transform>();
        readonly HashSet<Transform> invalidAnchors = new HashSet<Transform>();
        readonly List<RenderTexture> textures = new List<RenderTexture>();
        Coroutine playback;
        int generation, accepted;
        Transform lastAnchor;

        public void Begin(string sessionId)
        {
            Cancel();
            if (sequence == null) sequence = Resources.Load<JudyCueSequence>("StoryBricks/JudySequence");
            SessionId = sessionId;
            Status = sequence == null ? "Judy cue sequence is missing." : "Build " + ExpectedWord() + " to begin its cue.";
        }

        public bool AcceptWord(string word, string groupId, Transform anchor)
        {
            if (string.IsNullOrEmpty(SessionId) || sequence == null || StoryRequested ||
                string.IsNullOrEmpty(groupId) || groups.Contains(groupId)) return false;
            if (accepted >= (sequence.words?.Length ?? 0))
            { Report("", "all_word_cues_accepted", word); return false; }
            var cue = sequence.words[accepted];
            if (cue == null || Normalize(word) != Normalize(cue.expectedWord))
            {
                Status = "This Judy cue expects " + ExpectedWord() + ". Your captured word is kept.";
                CueChanged?.Invoke(cue?.id ?? "", "unexpected_word", word ?? "");
                return false;
            }
            groups.Add(groupId); accepted++;
            if (anchor != null) capturedAnchors.Add(anchor);
            queued.Enqueue(new Accepted { cue = cue, word = word, group = groupId, anchor = anchor });
            Report(cue.id, "queued", word);
            StartPlayback();
            return true;
        }

        public bool CreateStory()
        {
            if (string.IsNullOrEmpty(SessionId) || sequence == null || accepted == 0 || StoryRequested) return false;
            StoryRequested = true;
            StartPlayback();
            return true;
        }

        void StartPlayback()
        {
            if (Busy) return;
            Busy = true;
            playback = StartCoroutine(Play(generation));
        }

        IEnumerator Play(int token)
        {
            while (Valid(token) && queued.Count > 0)
            {
                var next = queued.Dequeue();
                lastAnchor = next.anchor;
                Report(next.cue.id, "playing", next.word);
                yield return PlaySteps(next.cue.steps, next.anchor, next.cue.id, next.word, token);
                if (!Valid(token)) yield break;
                Report(next.cue.id, "completed", next.word);
            }
            if (Valid(token) && StoryRequested)
            {
                StoryStarted?.Invoke();
                if (!Valid(token)) yield break;
                bool hasNarration = Array.Exists(sequence.finalStory ?? Array.Empty<JudyMediaStep>(), step => step != null && step.voice != null);
                if (!string.IsNullOrWhiteSpace(FinalStoryText)) CaptionChanged?.Invoke(FinalStoryText);
                if (!hasNarration)
                {
                    Report("story", "missing_prepared_narration", "");
                    CaptionChanged?.Invoke("Judy preview: prepared narration is not assigned.\n" + FinalStoryText);
                }
                yield return PlaySteps(sequence.finalStory, lastAnchor, "story", "", token);
                if (!Valid(token)) yield break;
                Report("story", "completed", "");
                StoryCompleted?.Invoke();
                if (!Valid(token)) yield break;
            }
            Busy = false;
            playback = null;
        }

        IEnumerator PlaySteps(JudyMediaStep[] steps, Transform anchor, string cueId, string word, int token)
        {
            if (steps == null || steps.Length == 0)
            { Report(cueId, "missing_prepared_media", word); yield break; }
            foreach (var step in steps)
            {
                if (!Valid(token)) yield break;
                if (step == null) continue;
                if (!string.IsNullOrWhiteSpace(step.caption)) CaptionChanged?.Invoke(step.caption);
                GameObject prefab = null;
                JudyCueSignal signal = null;
                bool spatialReady = SpatialReady(anchor);
                if (!spatialReady && (step.prefab != null || step.image != null || step.video != null))
                    Report(cueId, "waiting_for_new_anchor", word);
                if (step.prefab != null && spatialReady)
                {
                    prefab = Instantiate(step.prefab, anchor, false);
                    prefab.transform.localPosition = step.offset;
                    owned.Add(prefab); spatialVisuals.Add(prefab);
                    signal = prefab.GetComponentInChildren<JudyCueSignal>(true);
                    if (step.waitForPrefabSignal && signal == null)
                        Report(cueId, "missing_prefab_completion_signal", word);
                }
                var voice = PlayAudio(step.voice, anchor, step.volume, false);
                var sound = PlayAudio(step.sound, anchor, step.volume, true);
                RawImage image = null;
                if (spatialReady && (step.image != null || step.video != null)) image = CreateVisual(anchor, step);
                if (image != null && step.image != null) image.texture = step.image;
                VideoPlayer video = null;
                bool videoEnded = false, videoError = false;
                if (step.video != null && image != null)
                {
                    video = image.gameObject.AddComponent<VideoPlayer>();
                    video.playOnAwake = false; video.isLooping = false;
                    video.source = VideoSource.VideoClip; video.clip = step.video;
                    video.renderMode = VideoRenderMode.RenderTexture;
                    video.audioOutputMode = VideoAudioOutputMode.AudioSource;
                    var audio = image.gameObject.AddComponent<AudioSource>();
                    audio.playOnAwake = false; audio.spatialBlend = 0; audio.volume = step.volume;
                    if (step.video.audioTrackCount > 0)
                    { video.controlledAudioTrackCount = 1; video.SetTargetAudioSource(0, audio); }
                    var target = new RenderTexture(1024, 576, 0);
                    target.Create(); textures.Add(target); image.texture = target; video.targetTexture = target;
                    video.loopPointReached += _ => videoEnded = true;
                    video.errorReceived += (_, error) => { videoError = true; Report(cueId, "video_failed", word); };
                    video.Prepare();
                    double prepareStarted = Time.realtimeSinceStartupAsDouble;
                    while (Valid(token) && video != null && !video.isPrepared && !videoError && Time.realtimeSinceStartupAsDouble - prepareStarted < 20) yield return null;
                    if (!Valid(token)) yield break;
                    if (video == null || !SpatialReady(anchor))
                    { videoError = true; Report(cueId, "waiting_for_new_anchor", word); }
                    else if (!video.isPrepared) { videoError = true; Report(cueId, "video_not_ready", word); }
                    else video.Play();
                }
                if (step.prefab == null && step.image == null && step.voice == null && step.sound == null && step.video == null)
                {
                    Report(cueId, "missing_prepared_media", word);
                    CaptionChanged?.Invoke("Judy preview: prepared media is not assigned.\n" + (step.caption ?? ""));
                }
                if (voice == null && sound == null && video == null && !(step.waitForPrefabSignal && signal != null) &&
                    !string.IsNullOrWhiteSpace(step.caption) && step.captionPreviewSeconds > 0)
                    yield return new WaitForSecondsRealtime(step.captionPreviewSeconds);
                while (Valid(token) && ((voice != null && voice.isPlaying) || (sound != null && sound.isPlaying) ||
                    (video != null && !videoEnded && !videoError) || (step.waitForPrefabSignal && signal != null && !signal.Completed)))
                    yield return null;
                if (!Valid(token)) yield break;
                // Visuals remain above the captured box. Finite media completion advances the cue.
            }
        }

        AudioSource PlayAudio(AudioClip clip, Transform anchor, float volume, bool spatial)
        {
            if (clip == null) return null;
            var go = new GameObject("Judy " + (spatial ? "sound" : "voice"));
            go.transform.SetParent(anchor != null ? anchor : transform, false);
            owned.Add(go);
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false; source.spatialBlend = spatial && SpatialReady(anchor) ? 1 : 0;
            source.clip = clip; source.volume = volume; source.loop = false; source.Play();
            if (spatial) positionalAudio.Add(source);
            return source;
        }

        RawImage CreateVisual(Transform anchor, JudyMediaStep step)
        {
            var go = new GameObject("Judy visual", typeof(RectTransform), typeof(Canvas));
            go.transform.SetParent(anchor != null ? anchor : transform, false);
            go.transform.localPosition = step.offset;
            var camera = Camera.main;
            if (camera != null) go.transform.rotation = Quaternion.LookRotation(go.transform.position - camera.transform.position, Vector3.up);
            go.transform.localScale = Vector3.one * .001f;
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            go.GetComponent<RectTransform>().sizeDelta = step.imageSizeMeters * 1000;
            var child = new GameObject("Image", typeof(RectTransform), typeof(RawImage));
            child.transform.SetParent(go.transform, false);
            var rect = child.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
            var result = child.GetComponent<RawImage>(); result.raycastTarget = false;
            owned.Add(go);
            spatialVisuals.Add(go);
            return result;
        }

        /// <summary>Tracking reacquisition invalidates poses, not accepted words or authored progress.</summary>
        public void InvalidateSpatialAnchors()
        {
            foreach (var anchor in capturedAnchors) if (anchor != null) invalidAnchors.Add(anchor);
            foreach (var visual in spatialVisuals)
                if (visual != null) { visual.SetActive(false); Destroy(visual); }
            spatialVisuals.Clear();
            foreach (var source in positionalAudio) if (source != null) source.spatialBlend = 0;
            foreach (var texture in textures) if (texture != null) { texture.Release(); Destroy(texture); }
            textures.Clear();
            Report("", "waiting_for_new_anchor", "");
            // Centered voice and the queue continue. New captures supply valid new anchors.
        }

        void Report(string id, string status, string word)
        {
            Status = status;
            CueChanged?.Invoke(id ?? "", status, word ?? "");
        }
        string ExpectedWord() => sequence?.words != null && accepted < sequence.words.Length ? sequence.words[accepted]?.expectedWord ?? "the next word" : "the next word";
        static string Normalize(string word) => (word ?? "").Trim().TrimEnd('.').ToUpperInvariant();
        bool Valid(int token) => token == generation && !string.IsNullOrEmpty(SessionId);
        bool SpatialReady(Transform anchor) => anchor != null && !invalidAnchors.Contains(anchor);

        public void Cancel()
        {
            generation++; StopAllCoroutines(); playback = null; queued.Clear(); groups.Clear();
            foreach (var go in owned) if (go != null) Destroy(go);
            owned.Clear();
            spatialVisuals.Clear(); positionalAudio.Clear(); capturedAnchors.Clear(); invalidAnchors.Clear();
            foreach (var texture in textures) if (texture != null) { texture.Release(); Destroy(texture); }
            textures.Clear();
            accepted = 0; Busy = StoryRequested = false; SessionId = null; lastAnchor = null;
            Status = "Judy stopped.";
        }
        void OnDisable() { Cancel(); }
    }
}
