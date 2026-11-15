using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Video;

namespace BrailleSoundDemo.Presentation
{
    /// <summary>Designer-owned media child. Uses local assets or the workspace catalog; no AI/provider credentials.</summary>
    [DisallowMultipleComponent]
    public sealed class WordMediaPresenter : MonoBehaviour
    {
        public bool PresentationEnabled = true;
        public bool UseDashboardCatalog = false;
        public string DashboardBaseUrl = "http://127.0.0.1:8770";
        public Vector3 VisualOffset = new Vector3(0, .12f, 0);
        public Vector2 VisualSizeMeters = new Vector2(.24f, .16f);
        public bool FaceViewer = true;
        public LocalMediaBinding[] LocalBindings = Array.Empty<LocalMediaBinding>();
        public string Status { get; private set; } = "Presentation ready";
        public string WorkspaceId { get; private set; } = "judy";
        public string Target { get; private set; } = "box";
        public string SessionId { get; private set; }
        public string GroupId { get; private set; }
        public string Word { get; private set; } = "";
        Transform boundAnchor;
        bool voiceAllowed, loadingCatalog;
        int generation, visualRevision;
        DesignerManifest catalog;
        readonly List<UnityWebRequest> requests = new List<UnityWebRequest>();
        readonly List<GameObject> mediaObjects = new List<GameObject>();
        readonly List<UnityEngine.Object> ownedAssets = new List<UnityEngine.Object>();
        GameObject visual;
        const long MaxAudioBytes = 32 * 1024 * 1024;

        sealed class Cue
        {
            public string id, action, url;
            public double due;
            public int order, ordinal;
            public float volume;
            public bool loop;
            public Texture2D image;
            public AudioClip audio;
            public VideoClip video;
        }

        public static WordMediaPresenter Attach(WordMediaPresenter prefab, Transform anchor,
            string sessionId, string groupId, int index, string word, string workspaceId, bool allowVoice, string target = "box")
        {
            if (anchor == null) return null;
            WordMediaPresenter presenter = null;
            foreach (var candidate in anchor.GetComponentsInChildren<WordMediaPresenter>(true))
                if (candidate.boundAnchor == anchor && candidate.Target == target) { presenter = candidate; break; }
            if (presenter == null)
            {
                if (prefab != null) presenter = Instantiate(prefab, anchor, false);
                else
                {
                    var child = new GameObject(target == "stage" ? "Story stage media" : "Word box media");
                    child.transform.SetParent(anchor, false);
                    presenter = child.AddComponent<WordMediaPresenter>();
                }
            }
            presenter.Bind(sessionId, groupId, index, word, anchor, workspaceId, allowVoice, target);
            return presenter;
        }

        public void Bind(string sessionId, string groupId, int index, string word, Transform anchor,
            string workspaceId, bool allowVoice, string target = "box")
        {
            string workspace = workspaceId == "demo" ? "demo" : "judy";
            if (SessionId != sessionId || WorkspaceId != workspace || boundAnchor != anchor)
            { CancelPlayback(); catalog = null; }
            SessionId = sessionId; GroupId = groupId; Word = word ?? "";
            WorkspaceId = workspace; boundAnchor = anchor; voiceAllowed = allowVoice;
            Target = target == "stage" ? "stage" : "box";
        }

        void OnEnable() { PresentationEvents.Published += Receive; PresentationEvents.SessionReset += ResetForSession; }
        void OnDisable() { PresentationEvents.Published -= Receive; PresentationEvents.SessionReset -= ResetForSession; CancelPlayback(); }
        void OnDestroy() { CancelPlayback(); }
        void ResetForSession(string sessionId) { if (sessionId == SessionId) { CancelPlayback(); catalog = null; SessionId = null; } }
        public void ReloadCatalog() { catalog = null; if (isActiveAndEnabled && UseDashboardCatalog) StartCoroutine(LoadCatalog(generation)); }

        public void Receive(PresentationEventContext context)
        {
            if (!PresentationEnabled || !isActiveAndEnabled || context == null || SessionId != context.sessionId ||
                WorkspaceId != (context.workspaceId == "demo" ? "demo" : "judy") ||
                (Target == "box" && (context.anchor != boundAnchor || context.groupId != GroupId))) return;
            StartCoroutine(Schedule(context, generation, Time.realtimeSinceStartupAsDouble));
        }

        IEnumerator Schedule(PresentationEventContext context, int token, double started)
        {
            // Refresh at event boundaries so uploaded cue edits are available
            // without recreating a recognized box. Failed refresh keeps the last catalog.
            if (UseDashboardCatalog) yield return LoadCatalog(token);
            if (!Valid(token)) yield break;
            var cues = new List<Cue>();
            int ordinal = 0;
            foreach (var binding in LocalBindings ?? Array.Empty<LocalMediaBinding>())
            {
                if (!MatchesLocalBinding(binding, context)) continue;
                cues.Add(new Cue { id = binding.id, action = binding.action.ToString().ToLowerInvariant(),
                    due = started + Mathf.Max(0, binding.delaySeconds), order = binding.order, ordinal = ordinal++,
                    volume = Mathf.Clamp01(binding.volume), loop = binding.loop, image = binding.image,
                    audio = binding.audio, video = binding.video, url = binding.url });
            }
            if (UseDashboardCatalog && catalog?.cues != null)
                foreach (var binding in catalog.cues)
                {
                    if (binding == null || !binding.enabled || binding.target != Target || !Matches(binding.wordFilter, context.word) ||
                        !MatchesTrigger(context.eventName, context.cueId, binding.@event, binding.cueKey, binding.id)) continue;
                    var asset = Array.Find(catalog.assets ?? Array.Empty<DesignerAsset>(), a => a != null && a.id == binding.assetId);
                    string kind = binding.action == "sound" || binding.action == "voice" ? "audio" : binding.action;
                    if (asset == null || asset.kind != kind) { Status = "Cue asset is missing or has the wrong type: " + binding.id; continue; }
                    cues.Add(new Cue { id = binding.id, action = binding.action, due = started + Math.Max(0, binding.delayMs) / 1000.0,
                        order = binding.order, ordinal = ordinal++, volume = Mathf.Clamp01(binding.volume), loop = binding.loop, url = asset.url });
                }
            cues.Sort((a, b) => { int n = a.due.CompareTo(b.due); if (n != 0) return n; n = a.order.CompareTo(b.order); return n != 0 ? n : a.ordinal.CompareTo(b.ordinal); });
            foreach (var cue in cues)
            {
                if (!Valid(token)) yield break;
                // Create World has no animation provider yet. Authored videos must never
                // masquerade as a generated result, including manually emitted video.ready.
                if(WorkspaceId=="demo" && cue.action=="video") continue;
                if (cue.action == "voice" && !(voiceAllowed && context.allowVoice || context.preview))
                { Status = "Voice cue skipped: Demo Lab conversation owns speech."; continue; }
                StartCoroutine(PlayAt(cue, context, token));
            }
            if (cues.Count == 0) Status = "No matching " + context.eventName + " cue in " + WorkspaceId;
        }

        IEnumerator LoadCatalog(int token)
        {
            if (loadingCatalog) { while (Valid(token) && loadingCatalog) yield return null; yield break; }
            if (!Valid(token) || !UseDashboardCatalog) yield break;
            loadingCatalog = true;
            using (var request = UnityWebRequest.Get(DashboardBaseUrl.TrimEnd('/') + "/api/manifest?workspace=" + WorkspaceId))
            {
                request.timeout = 8; request.redirectLimit = 0; requests.Add(request);
                yield return request.SendWebRequest(); requests.Remove(request);
                if (!Valid(token)) yield break;
                if (request.result == UnityWebRequest.Result.Success && request.downloadHandler.data.Length <= 1024 * 1024)
                {
                    try
                    {
                        var parsed = JsonUtility.FromJson<DesignerManifest>(request.downloadHandler.text);
                        if (parsed == null || parsed.schemaVersion != 1 || parsed.assets == null || parsed.cues == null || parsed.cues.Length > 512)
                            throw new ArgumentException("Unsupported catalog");
                        catalog = parsed; Status = "Loaded " + WorkspaceId + " catalog revision " + parsed.revision;
                    }
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                    { Status = "Catalog is invalid. Local Inspector bindings remain available."; }
                }
                else Status = "Dashboard unavailable. Local Inspector bindings remain available.";
            }
            loadingCatalog = false;
        }

        IEnumerator PlayAt(Cue cue, PresentationEventContext context, int token)
        {
            while (Valid(token) && Time.realtimeSinceStartupAsDouble < cue.due) yield return null;
            if (!Valid(token)) yield break;
            if (cue.action == "image") yield return ShowImage(cue, token);
            else if (cue.action == "video") yield return ShowVideo(cue, context, token);
            else if (cue.action == "sound" || cue.action == "voice")
            {
                while (Valid(token) && !AudioReady(context)) { Status = "Audio cue waiting for conversation: " + cue.id; yield return null; }
                if (Valid(token)) yield return PlayAudio(cue, context, token);
            }
        }

        IEnumerator ShowImage(Cue cue, int token)
        {
            int version = ++visualRevision;
            Texture2D texture = cue.image;
            if (texture == null)
            {
                if (!TryUrl(cue.url, out var url)) { Status = "Image URL must use the dashboard origin."; yield break; }
                using (var request = UnityWebRequest.Get(url))
                {
                    request.timeout = 20; request.redirectLimit = 0; requests.Add(request);
                    var operation = request.SendWebRequest();
                    while (!operation.isDone && ValidVisual(token, version))
                    { if (request.downloadedBytes > StoryImagePipeline.MaximumImageBytes) { request.Abort(); break; } yield return null; }
                    requests.Remove(request);
                    if (!ValidVisual(token, version)) { request.Abort(); yield break; }
                    if (request.result != UnityWebRequest.Result.Success || !StoryImagePipeline.TryReadImageHeader(request.downloadHandler.data, out _, out _, out _, out _))
                    { Status = "Image download failed or exceeds 4096 pixels / 8 MB."; yield break; }
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!texture.LoadImage(request.downloadHandler.data)) { Destroy(texture); Status = "Image could not be decoded."; yield break; }
                    ownedAssets.Add(texture);
                }
            }
            if (!ValidVisual(token, version)) yield break;
            ReplaceVisual();
            visual = CreateSurface(cue.id, texture, texture.width / (float)texture.height, true);
            Status = "Showing image: " + cue.id;
        }

        IEnumerator PlayAudio(Cue cue, PresentationEventContext context, int token)
        {
            AudioClip clip = cue.audio;
            if (clip == null)
            {
                if (!TryUrl(cue.url, out var url)) { Status = "Audio URL must use the dashboard origin."; yield break; }
                string extension = System.IO.Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
                AudioType type = extension == ".wav" ? AudioType.WAV : extension == ".mp3" ? AudioType.MPEG : extension == ".ogg" ? AudioType.OGGVORBIS : AudioType.UNKNOWN;
                if (type == AudioType.UNKNOWN) { Status = "Audio preview supports WAV, MP3 or OGG."; yield break; }
                using (var request = UnityWebRequestMultimedia.GetAudioClip(url, type))
                {
                    request.timeout = 25; request.redirectLimit = 0; requests.Add(request);
                    var operation = request.SendWebRequest();
                    while (!operation.isDone && Valid(token))
                    { if (request.downloadedBytes > MaxAudioBytes) { request.Abort(); break; } yield return null; }
                    requests.Remove(request);
                    if (!Valid(token)) { request.Abort(); yield break; }
                    if (request.result != UnityWebRequest.Result.Success) { Status = "Audio download failed: " + cue.id; yield break; }
                    clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip == null) { Status = "Audio could not be decoded."; yield break; }
                    ownedAssets.Add(clip);
                }
            }
            while (Valid(token) && !AudioReady(context)) yield return null;
            if (!Valid(token)) yield break;
            var node = new GameObject("Cue audio " + cue.id); node.transform.SetParent(transform, false); mediaObjects.Add(node);
            var source = node.AddComponent<AudioSource>(); source.playOnAwake = false; source.loop = cue.loop;
            source.spatialBlend = cue.action == "voice" || Target == "stage" ? 0 : 1;
            source.minDistance = .4f; source.maxDistance = 8; source.dopplerLevel = 0;
            source.clip = clip; source.volume = cue.volume; source.Play(); Status = "Playing " + cue.action + ": " + cue.id;
            bool paused = false;
            while (Valid(token) && node != null)
            {
                if (!AudioReady(context)) { if (!paused && source.isPlaying) { source.Pause(); paused = true; } yield return null; continue; }
                if (paused) { source.UnPause(); paused = false; }
                if (!source.isPlaying) break;
                yield return null;
            }
            if (node != null) { source.Stop(); mediaObjects.Remove(node); Destroy(node); }
        }

        IEnumerator ShowVideo(Cue cue, PresentationEventContext context, int token)
        {
            string url = null;
            if (cue.video == null && !TryUrl(cue.url, out url)) { Status = "Video URL must use the dashboard origin."; yield break; }
            int version = ++visualRevision; ReplaceVisual();
            var node = CreateSurface(cue.id, Texture2D.blackTexture, 16f / 9f); visual = node;
            var player = node.AddComponent<VideoPlayer>(); player.playOnAwake = false; player.isLooping = cue.loop;
            player.renderMode = VideoRenderMode.RenderTexture;
            if (cue.video != null) { player.source = VideoSource.VideoClip; player.clip = cue.video; }
            else { player.source = VideoSource.Url; player.url = url; }
            var output = node.AddComponent<AudioSource>(); output.playOnAwake = false; output.volume = cue.volume;
            bool audioAllowed = voiceAllowed && context.allowVoice || context.preview;
            player.audioOutputMode = audioAllowed ? VideoAudioOutputMode.AudioSource : VideoAudioOutputMode.None;
            string failure = null; player.errorReceived += (_, message) => failure = message;
            player.Prepare(); double deadline = Time.realtimeSinceStartupAsDouble + 25;
            while (ValidVisual(token, version) && node != null && !player.isPrepared && failure == null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            if (!ValidVisual(token, version) || node == null) yield break;
            if (!player.isPrepared || failure != null) { Status = "Video preparation failed: " + cue.id; ReplaceVisual(); yield break; }
            float aspect = player.height > 0 ? player.width / (float)player.height : 16f / 9f;
            int width = Mathf.Clamp((int)player.width, 16, 1920), height = Mathf.Clamp(Mathf.RoundToInt(width / aspect), 16, 1920);
            var target = new RenderTexture(width, height, 0); target.Create(); ownedAssets.Add(target);
            player.targetTexture = target; node.GetComponent<MeshRenderer>().sharedMaterial.mainTexture = target;
            SizeSurface(node.transform, aspect);
            if (audioAllowed && player.audioTrackCount > 0) { player.EnableAudioTrack(0, true); player.SetTargetAudioSource(0, output); }
            player.Play(); Status = "Playing video: " + cue.id + (audioAllowed ? "" : " (Demo Lab video audio muted)");
        }

        GameObject CreateSurface(string label, Texture texture, float aspect, bool transparent = false)
        {
            var node = GameObject.CreatePrimitive(PrimitiveType.Quad); node.name = "Cue visual " + label;
            var collider = node.GetComponent<Collider>(); if (collider != null) Destroy(collider);
            node.transform.SetParent(transform, false); node.transform.localPosition = VisualOffset; SizeSurface(node.transform, aspect);
            var shader = Shader.Find(transparent ? "Sprites/Default" : "Unlit/Texture"); if (shader == null) shader = Shader.Find("Sprites/Default");
            var material = new Material(shader); material.mainTexture = texture; ownedAssets.Add(material);
            node.GetComponent<MeshRenderer>().sharedMaterial = material; mediaObjects.Add(node); return node;
        }
        void SizeSurface(Transform surface, float aspect)
        {
            float width = Mathf.Max(.01f, VisualSizeMeters.x), height = Mathf.Max(.01f, VisualSizeMeters.y);
            if (width / height > aspect) width = height * aspect; else height = width / Mathf.Max(.01f, aspect);
            surface.localScale = new Vector3(width, height, 1);
        }
        void ReplaceVisual()
        {
            if (visual == null) return;
            var video = visual.GetComponent<VideoPlayer>(); if (video != null) video.Stop();
            mediaObjects.Remove(visual); Destroy(visual); visual = null;
        }
        void LateUpdate()
        {
            if (!FaceViewer || visual == null || Camera.main == null) return;
            var direction = visual.transform.position - Camera.main.transform.position;
            if (direction.sqrMagnitude > .00001f) visual.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
        }
        public void CancelPlayback()
        {
            ++generation; ++visualRevision; StopAllCoroutines(); loadingCatalog = false;
            foreach (var request in requests) { try { request.Abort(); request.Dispose(); } catch (ObjectDisposedException) { } }
            requests.Clear();
            foreach (var node in mediaObjects) if (node != null) { var video = node.GetComponent<VideoPlayer>(); if (video != null) video.Stop(); Destroy(node); }
            mediaObjects.Clear(); visual = null;
            foreach (var asset in ownedAssets) if (asset != null) { if (asset is RenderTexture target) target.Release(); Destroy(asset); }
            ownedAssets.Clear(); Status = "Presentation stopped";
        }
        bool Valid(int token) => token == generation && isActiveAndEnabled && PresentationEnabled;
        bool ValidVisual(int token, int version) => Valid(token) && version == visualRevision;
        static bool AudioReady(PresentationEventContext context)
        {
            if (context.audioReady == null) return true;
            try { return context.audioReady(); } catch (Exception) { return false; }
        }
        bool TryUrl(string value, out string url)
        {
            url = null;
            if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(DashboardBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var root) ||
                !Uri.TryCreate(root, value, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") ||
                uri.Scheme != root.Scheme || uri.Host != root.Host || uri.Port != root.Port || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            url = uri.AbsoluteUri; return true;
        }
        static bool Matches(string filter, string word) => string.IsNullOrWhiteSpace(filter) || string.Equals(filter.Trim(), (word ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        bool MatchesLocalBinding(LocalMediaBinding binding, PresentationEventContext context) =>
            binding != null && binding.enabled &&
            (string.IsNullOrWhiteSpace(binding.target) ? "box" : binding.target) == Target &&
            Matches(binding.wordFilter, context.word) &&
            MatchesTrigger(context.eventName, context.cueId, EventName(binding.trigger), binding.cueKey, binding.id);
        static bool MatchesCue(string requested, string key, string id) => string.IsNullOrWhiteSpace(requested)
            ? string.IsNullOrWhiteSpace(key) : string.Equals(requested, key, StringComparison.Ordinal) || string.Equals(requested, id, StringComparison.Ordinal);
        static bool MatchesTrigger(string eventName, string requested, string bindingEvent, string key, string id) =>
            (!string.IsNullOrWhiteSpace(requested) || eventName == bindingEvent) && MatchesCue(requested, key, id);
        public static string EventName(MediaEvent value)
        {
            switch (value) { case MediaEvent.BoxCreated: return "box.created"; case MediaEvent.WordAccepted: return "word.accepted";
                case MediaEvent.QuestionStarted: return "question.started"; case MediaEvent.AnswerSaved: return "answer.saved";
                case MediaEvent.StoryReady: return "story.ready"; case MediaEvent.VideoReady: return "video.ready"; default: return "manual"; }
        }
    }
}
