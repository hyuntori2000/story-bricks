using System;
using UnityEngine;

namespace BrailleSoundDemo.Presentation
{
    public sealed class PresentationEventContext
    {
        public string eventName, sessionId, groupId, word = "", workspaceId = "judy", storyText = "", cueId = "";
        public int wordIndex;
        public Transform anchor;
        public bool allowVoice, preview;
        public Func<bool> audioReady;
    }

    /// <summary>Presentation observers cannot change or interrupt the capture/conversation state machine.</summary>
    public static class PresentationEvents
    {
        public static event Action<PresentationEventContext> Published;
        public static event Action<string> SessionReset;
        public static void Publish(PresentationEventContext value)
        {
            if (value == null || string.IsNullOrEmpty(value.sessionId) || string.IsNullOrEmpty(value.eventName)) return;
            var handlers = Published;
            if (handlers == null) return;
            foreach (Action<PresentationEventContext> handler in handlers.GetInvocationList())
                try { handler(value); } catch (Exception e) { Debug.LogWarning("[Presentation] Observer failed: " + e.GetType().Name); }
        }
        public static void ResetSession(string sessionId)
        {
            var handlers = SessionReset;
            if (handlers == null) return;
            foreach (Action<string> handler in handlers.GetInvocationList())
                try { handler(sessionId); } catch (Exception e) { Debug.LogWarning("[Presentation] Reset observer failed: " + e.GetType().Name); }
        }
    }

    public enum MediaEvent { BoxCreated, WordAccepted, QuestionStarted, AnswerSaved, StoryReady, VideoReady, Manual }
    public enum MediaAction { Image, Sound, Voice, Video }
    [Serializable] public sealed class LocalMediaBinding
    {
        public string id = "local-cue", cueKey = "", wordFilter = "";
        [Tooltip("box or stage. Empty legacy values behave as box.")]
        public string target = "box";
        public bool enabled = true;
        public MediaEvent trigger = MediaEvent.WordAccepted;
        public MediaAction action = MediaAction.Image;
        public Texture2D image;
        public AudioClip audio;
        public UnityEngine.Video.VideoClip video;
        [Tooltip("Optional media URL on this presenter's dashboard origin. Local assets take priority.")]
        public string url;
        [Min(0)] public float delaySeconds;
        public int order;
        [Range(0, 1)] public float volume = .8f;
        public bool loop;
    }
    [Serializable] public sealed class DesignerManifest
    {
        public int schemaVersion, revision;
        public string name;
        public DesignerAsset[] assets;
        public DesignerCue[] cues;
    }
    [Serializable] public sealed class DesignerAsset { public string id, name, kind, mimeType, url; }
    [Serializable] public sealed class DesignerCue
    {
        public string id, cueKey, @event, wordFilter, action, target, assetId;
        public int delayMs, order;
        public float volume = 1;
        public bool loop, enabled = true;
    }
}
