using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace BrailleSoundDemo
{
    [Serializable] public sealed class BackendMetrics
    {
        public float textMs, speechMs, audioStoreMs, totalMs, transcriptionMs;
        public float speechConnectMs, speechSetupMs, speechFirstAudioMs;
    }
    [Serializable] public sealed class BackendReply
    {
        public string sessionId, requestId, text, audioPath, error, code;
        public string nextInput, turnId, spokenText;
        public string workspaceId, experienceSessionId;
        public BackendMetrics metrics;
    }
    [Serializable] sealed class BackendStoryRequest
    {
        public string sessionId, requestId;
        public string workspaceId, experienceSessionId;
        public StoryContext context;
    }

    /// <summary>The local Python process owns the API key. No secret is sent to or stored by Unity.</summary>
    public sealed class LiveStoryProvider : IStoryProvider, IDialogueStoryProvider, IDisposable
    {
        public const string BaseUrl = "http://127.0.0.1:8766";
        readonly Func<string> currentSession;
        readonly Func<ExperienceIdentity> currentExperience;
        readonly List<UnityWebRequest> activeRequests = new List<UnityWebRequest>();
        readonly List<AudioClip> clips = new List<AudioClip>();
        int revision;
        public Action<string,int> GenerationAttemptStarted { get; set; }
        public const int MaximumGenerationAttempts = 2;
        public LiveStoryProvider(Func<string> session, Func<ExperienceIdentity> experience = null)
        {
            currentSession = session ?? throw new ArgumentNullException(nameof(session));
            currentExperience = experience ?? (() => new ExperienceIdentity());
        }
        public IEnumerator Question(StoryContext context, Action<StoryReply> completed) => Generate("question", context, completed);
        public IEnumerator Story(StoryContext context, Action<StoryReply> completed) => Generate("story", context, completed);
        public IEnumerator Dialogue(StoryContext context, Action<StoryReply> completed) => Generate("dialogue", context, completed);

        IEnumerator Generate(string operation, StoryContext context, Action<StoryReply> completed)
        {
            string session = currentSession(); int token = revision;
            var owner = currentExperience();
            StoryReply result = null;
            for (int attempt = 1; attempt <= MaximumGenerationAttempts; attempt++)
            {
                if (!Valid(token, session, owner)) yield break;
                GenerationAttemptStarted?.Invoke(operation, attempt);
                if (!Valid(token, session, owner)) yield break;
                result = null;
                yield return GenerateAttempt(operation, context, session, owner, token, value => result = value);
                if (!Valid(token, session, owner)) yield break;
                if (result == null) result = new StoryReply { error = "The Python backend returned no response.", errorCode = "no_response" };
                result.generationAttempts = attempt;
                if (string.IsNullOrEmpty(result.error) || attempt == MaximumGenerationAttempts || !RetryableGenerationError(result.errorCode)) break;
                Debug.LogWarning("[Voice retry] operation=" + operation + " attempt=" + attempt + " code=" + result.errorCode + "; retrying once.");
                yield return new WaitForSecondsRealtime(.5f);
            }
            if (Valid(token, session, owner)) completed?.Invoke(result);
        }

        // A fresh request ID intentionally starts a new generation attempt.
        // Reusing the failed ID would only return the backend's cached failure.
        IEnumerator GenerateAttempt(string operation, StoryContext context, string session, ExperienceIdentity owner, int token, Action<StoryReply> completed)
        {
            string requestId = Guid.NewGuid().ToString("N");
            var payload = new BackendStoryRequest { sessionId = session, requestId = requestId, context = context,
                workspaceId = owner.WorkspaceId, experienceSessionId = owner.ExperienceSessionId };
            BackendReply reply = null;
            double beganAt=Time.realtimeSinceStartupAsDouble;
            yield return Post("/" + operation, Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload)), "application/json", session, requestId, 150, owner, token, value => reply = value);
            double repliedAt=Time.realtimeSinceStartupAsDouble;
            if (!Valid(token, session, owner)) yield break;
            if (reply == null || !string.IsNullOrEmpty(reply.error))
            {
                completed(new StoryReply { error = reply?.error ?? "The Python backend returned no response.", errorCode = reply?.code ?? "no_response" }); yield break;
            }
            if (string.IsNullOrWhiteSpace(reply.text) || reply.text.Length > 12000 || !SafeAudioPath(reply.audioPath))
            {
                completed(new StoryReply { error = "The backend returned invalid speech text or an invalid audio path.", errorCode = "invalid_response" }); yield break;
            }
            if (operation == "dialogue" && (reply.turnId != context.turnId ||
                (reply.nextInput != "build_word" && reply.nextInput != "speak_answer")))
            {
                completed(new StoryReply { error = "Rejected a dialogue response with a different turn or unsupported input mode.", errorCode = "invalid_dialogue_response" });
                yield break;
            }
            byte[] audio = null; string error = null;
            using (var request = UnityWebRequest.Get(BaseUrl + reply.audioPath))
            {
                request.timeout = 30; request.redirectLimit = 0; activeRequests.Add(request);
                yield return request.SendWebRequest();
                activeRequests.Remove(request);
                if (!Valid(token, session, owner)) yield break;
                if (request.result != UnityWebRequest.Result.Success) error = "Could not download generated speech (HTTP " + request.responseCode + ").";
                else audio = request.downloadHandler.data;
            }
            if (error != null) { completed(new StoryReply { error = error, errorCode = "audio_download" }); yield break; }
            double downloadedAt=Time.realtimeSinceStartupAsDouble;
            AudioClip clip = null; string path = null;
            try
            {
                clip = WavCodec.CreateClip(audio, "Generated " + operation + " " + requestId);
                path = Path.Combine(owner.DirectoryUnder(Application.persistentDataPath), "GeneratedSpeech", session, operation + "-" + requestId + ".wav");
                Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, audio);
                clips.Add(clip);
            }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is InvalidOperationException || e is OverflowException)
            {
                if (clip != null) UnityEngine.Object.Destroy(clip);
                error = "Could not load or save generated speech: " + e.Message;
            }
            if (!Valid(token, session, owner)) yield break;
            Debug.Log("[VoiceTiming] operation="+operation+
                " postMs="+((repliedAt-beganAt)*1000).ToString("F0")+
                " downloadMs="+((downloadedAt-repliedAt)*1000).ToString("F0")+
                " decodeAndSaveMs="+((Time.realtimeSinceStartupAsDouble-downloadedAt)*1000).ToString("F0")+
                " backend="+(reply.metrics != null ? JsonUtility.ToJson(reply.metrics) : "unavailable"));
            completed(error == null ? new StoryReply { text = reply.text, audioClip = clip, audioFile = path,
                audioPath = reply.audioPath, nextInput = reply.nextInput, turnId = reply.turnId,
                spokenText = reply.spokenText } : new StoryReply { error = error, errorCode = "audio_local_storage" });
        }

        static bool RetryableGenerationError(string code)
        {
            // Configuration/authorization and local storage cannot be repaired
            // by issuing the same paid generation request a second time.
            switch (code)
            {
                case "api_access": case "key_missing": case "dependency_missing":
                case "invalid_context": case "invalid_id": case "answer_order":
                case "invalid_turn_id": case "invalid_dialogue_event": case "invalid_completion_mode":
                case "finish_required": case "request_conflict": case "audio_local_storage":
                case "request_interrupted": case "request_pending": case "result_save_failed":
                case "request_save_failed": case "audio_save_failed": case "invalid_workspace_owner":
                    return false;
                default: return true;
            }
        }

        public IEnumerator Transcribe(byte[] wav, Action<BackendReply> completed)
        {
            string session = currentSession(), requestId = Guid.NewGuid().ToString("N"); int token = revision;
            var owner = currentExperience();
            string path = "/transcribe?sessionId=" + Uri.EscapeDataString(session) + "&requestId=" + requestId + owner.Query;
            yield return Post(path, wav, "audio/wav", session, requestId, 60, owner, token, completed);
        }
        IEnumerator Post(string path, byte[] body, string contentType, string session, string requestId, int timeout, ExperienceIdentity owner, int token, Action<BackendReply> completed)
        {
            BackendReply reply = null;
            using (var request = new UnityWebRequest(BaseUrl + path, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(body); request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", contentType); request.timeout = timeout; request.redirectLimit = 0;
                activeRequests.Add(request);
                yield return request.SendWebRequest();
                activeRequests.Remove(request);
                if (!Valid(token, session, owner)) yield break;
                string data = request.downloadHandler.text;
                try { if (!string.IsNullOrEmpty(data) && data.Length < 64000) reply = JsonUtility.FromJson<BackendReply>(data); }
                catch (ArgumentException) { }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    string detail = reply != null && !string.IsNullOrEmpty(reply.error) ? reply.error :
                        "Python backend unavailable (HTTP " + request.responseCode + "). Start the backend and check USB forwarding.";
                    reply = new BackendReply { error = detail, code = reply?.code ?? "transport" };
                }
                else if (reply == null || reply.sessionId != session || reply.requestId != requestId ||
                    !owner.Matches(reply.workspaceId, reply.experienceSessionId))
                    reply = new BackendReply { error = "Rejected a backend response with missing or mismatched session/request IDs.", code = "invalid_response" };
            }
            if (Valid(token, session, owner)) completed(reply);
        }
        bool Valid(int token, string session, ExperienceIdentity owner) =>
            token == revision && currentSession() == session && owner.Matches(currentExperience());
        static bool SafeAudioPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("/audio/", StringComparison.Ordinal) || !path.EndsWith(".wav", StringComparison.Ordinal)) return false;
            string id = path.Substring(7, path.Length - 11);
            return Guid.TryParse(id, out _);
        }
        public void CancelRequests()
        {
            revision++;
            foreach (var request in activeRequests)
            {
                // Reset stops the owning coroutine first. Dispose explicitly because
                // Unity does not guarantee iterator finally blocks on StopAllCoroutines.
                try { request.Abort(); } catch (ObjectDisposedException) { }
                request.Dispose();
            }
            activeRequests.Clear();
        }
        public void Dispose()
        {
            CancelRequests();
            foreach (var clip in clips) if (clip != null) UnityEngine.Object.Destroy(clip);
            clips.Clear();
        }
    }
}
