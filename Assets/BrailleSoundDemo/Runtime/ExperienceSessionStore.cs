using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BrailleSoundDemo.CameraTest;
using UnityEngine;
using UnityEngine.Networking;

namespace BrailleSoundDemo
{
    [Serializable] public sealed class ExperienceCaptureRecord
    {
        public string sessionId, frameId, captureTimestampUtc;
        public int imageWidth, imageHeight;
    }
    [Serializable] public sealed class ExperienceWordRecord
    {
        public string groupId, word, frameId;
        public int index;
    }
    [Serializable] public sealed class ExperienceCueRecord
    {
        public string cueId, status, word, timestampUtc;
    }
    [Serializable] public sealed class ExperienceState
    {
        public string startedAtUtc, updatedAtUtc, endedAtUtc, stage = "building", endReason;
        public bool ended;
        public List<ExperienceCaptureRecord> captures = new List<ExperienceCaptureRecord>();
        public List<ExperienceWordRecord> words = new List<ExperienceWordRecord>();
        // Retained only to read and synchronize archived snapshots from the former Judy cue mode.
        public List<ExperienceCueRecord> cues = new List<ExperienceCueRecord>();
        public ConversationRecord conversation;
        public StoryOutput story;
    }
    [Serializable] sealed class ExperienceSnapshot
    {
        public string workspaceId, experienceSessionId;
        public long revision;
        public ExperienceState state;
    }
    [Serializable] sealed class ExperienceSnapshotReply
    {
        public bool ok;
        public string workspaceId, experienceSessionId, code;
        public long revision, storedRevision;
    }

    /// <summary>Durable local snapshots with a mode-owned Mac mirror. Pending old runs survive route changes.</summary>
    public sealed class ExperienceSessionStore : MonoBehaviour
    {
        sealed class Pending
        {
            public string path, json;
            public ExperienceSnapshot snapshot;
            public int attempts;
            public float retryAt;
        }
        const int MaximumResidentOutbox = 32;
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        ExperienceSnapshot current;
        Coroutine upload;
        UnityWebRequest activeRequest;
        float nextScan;
        public string WorkspaceId => current?.workspaceId ?? "";
        public string ExperienceSessionId => current?.experienceSessionId ?? "";
        public long Revision => current?.revision ?? 0;
        public string SyncStatus { get; private set; } = "No active experience.";
        string Root => Path.Combine(Application.persistentDataPath, "ExperienceSessions");

        public void Begin(string workspaceId, string experienceSessionId)
        {
            var owner = new ExperienceIdentity(workspaceId, experienceSessionId);
            if (!owner.IsScoped) throw new ArgumentException("A saved experience needs a route and session.");
            if (current != null && owner.Matches(current.workspaceId, current.experienceSessionId)) return;
            current = new ExperienceSnapshot { workspaceId = owner.WorkspaceId,
                experienceSessionId = owner.ExperienceSessionId,
                state = new ExperienceState { startedAtUtc = DateTime.UtcNow.ToString("O") } };
            Persist();
        }
        public void SetStage(string stage)
        {
            if (!CanChange || string.IsNullOrWhiteSpace(stage) || current.state.stage == stage) return;
            current.state.stage = stage; Persist();
        }
        public void RecordCapture(QuestFrameCapture.CaptureMetadata capture)
        {
            if (!CanChange || capture == null || !Owns(capture.workspaceId, capture.experienceSessionId)) return;
            if (current.state.captures.Exists(value => value.frameId == capture.frameId)) return;
            current.state.captures.Add(new ExperienceCaptureRecord { sessionId = capture.sessionId,
                frameId = capture.frameId, captureTimestampUtc = capture.captureTimestampUtc,
                imageWidth = capture.imageWidth, imageHeight = capture.imageHeight });
            Persist();
        }
        public void RecordWord(string groupId, string word, int index, string frameId)
        {
            if (!CanChange || current.state.words.Exists(value => value.groupId == groupId)) return;
            current.state.words.Add(new ExperienceWordRecord { groupId = groupId, word = word, index = index, frameId = frameId });
            Persist();
        }
        public void RecordConversation(ConversationRecord conversation)
        {
            if (!CanChange || conversation == null || !Owns(conversation.workspaceId, conversation.experienceSessionId)) return;
            current.state.conversation = Clone(conversation);
            // Each original request already embeds every previous turn. Keep its
            // metadata here; full diagnostic payloads remain in the Flow session file.
            foreach (var request in current.state.conversation.requests) request.payloadJson = "";
            Persist();
        }
        public void RecordStory(StoryOutput story)
        {
            if (!CanChange || story == null || !Owns(story.workspaceId, story.experienceSessionId)) return;
            current.state.story = Clone(story); current.state.stage = "story_ready"; Persist();
        }
        public void End(string reason = "ended")
        {
            if (!CanChange) return;
            current.state.ended = true; current.state.endReason = reason;
            current.state.endedAtUtc = DateTime.UtcNow.ToString("O"); current.state.stage = "ended"; Persist();
        }
        bool CanChange => current != null && !current.state.ended;
        bool Owns(string workspaceId, string experienceSessionId) =>
            current != null && current.workspaceId == workspaceId && current.experienceSessionId == experienceSessionId;
        static T Clone<T>(T value) => JsonUtility.FromJson<T>(JsonUtility.ToJson(value));

        void Persist()
        {
            current.revision++; current.state.updatedAtUtc = DateTime.UtcNow.ToString("O");
            var owner = new ExperienceIdentity(current.workspaceId, current.experienceSessionId);
            string directory = owner.DirectoryUnder(Application.persistentDataPath);
            string json = JsonUtility.ToJson(current), path = Path.Combine(directory, "session.pending.json");
            try
            {
                AtomicWrite(Path.Combine(directory, "session.json"), json);
                AtomicWrite(path, json);
                if (pending.ContainsKey(owner.Key) || pending.Count < MaximumResidentOutbox)
                    pending[owner.Key] = new Pending { path = path, json = json, snapshot = Clone(current) };
                SyncStatus = "Saved on Quest; waiting for Mac sync.";
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            { SyncStatus = "Session storage unavailable."; Debug.LogWarning("[Experience store] " + error.GetType().Name); }
        }
        static void AtomicWrite(string path, string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        void Update()
        {
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 15;
                LoadOutbox();
            }
            if (upload != null) return;
            foreach (var item in pending)
                if (item.Value.retryAt <= Time.unscaledTime)
                { upload = StartCoroutine(Sync(item.Key, item.Value)); break; }
        }
        void LoadOutbox()
        {
            if (!Directory.Exists(Root) || pending.Count >= MaximumResidentOutbox) return;
            try
            {
                foreach (string path in Directory.EnumerateFiles(Root, "session.pending.json", SearchOption.AllDirectories))
                {
                    if (pending.Count >= MaximumResidentOutbox) break;
                    try
                    {
                        string json = File.ReadAllText(path);
                        var snapshot = JsonUtility.FromJson<ExperienceSnapshot>(json);
                        if (snapshot?.state == null || snapshot.revision <= 0) continue;
                        var owner = new ExperienceIdentity(snapshot.workspaceId, snapshot.experienceSessionId);
                        string expected = Path.Combine(owner.DirectoryUnder(Application.persistentDataPath), "session.pending.json");
                        if (!owner.IsScoped || Path.GetFullPath(path) != Path.GetFullPath(expected) || pending.ContainsKey(owner.Key)) continue;
                        pending.Add(owner.Key, new Pending { path = path, json = json, snapshot = snapshot });
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
                    { Debug.LogWarning("[Experience outbox entry] " + error.GetType().Name); }
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
            { Debug.LogWarning("[Experience outbox] " + error.GetType().Name); }
        }
        IEnumerator Sync(string key, Pending item)
        {
            using (var request = new UnityWebRequest(LiveStoryProvider.BaseUrl + "/session-state", "POST"))
            {
                activeRequest = request;
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(item.json));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 8; request.redirectLimit = 0;
                yield return request.SendWebRequest();
                activeRequest = null;
                ExperienceSnapshotReply reply = null;
                try { reply = JsonUtility.FromJson<ExperienceSnapshotReply>(request.downloadHandler.text); }
                catch (ArgumentException) { }
                bool matching = reply != null && reply.workspaceId == item.snapshot.workspaceId &&
                    reply.experienceSessionId == item.snapshot.experienceSessionId && reply.revision == item.snapshot.revision;
                bool acknowledged = matching && ((request.result == UnityWebRequest.Result.Success && reply.ok) ||
                    (request.responseCode == 409 && reply.code == "stale_revision" && reply.storedRevision > reply.revision));
                if (pending.TryGetValue(key, out var latest) && ReferenceEquals(latest, item))
                {
                    if (acknowledged)
                    {
                        pending.Remove(key);
                        try { if (File.Exists(item.path)) File.Delete(item.path); }
                        catch (IOException) { }
                        SyncStatus = "Saved on Quest and Mac.";
                    }
                    else
                    {
                        item.attempts++;
                        item.retryAt = Time.unscaledTime + Mathf.Min(60, Mathf.Pow(2, Mathf.Min(6, item.attempts)));
                        SyncStatus = "Saved on Quest; Mac sync will retry.";
                    }
                }
            }
            upload = null;
        }
        void OnDisable()
        {
            if (upload != null) StopCoroutine(upload);
            upload = null;
            if (activeRequest != null)
            {
                try { activeRequest.Abort(); activeRequest.Dispose(); } catch (ObjectDisposedException) { }
                activeRequest = null;
            }
            // Pending snapshots remain on disk, including runs that ended before the Mac returned.
        }
    }
}
