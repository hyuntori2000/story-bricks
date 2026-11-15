using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BrailleSoundDemo
{
    public enum SoundProfile { Standard, Gentle }

    /// <summary>
    /// Prepares live word effects through the Mac backend for physical stories.
    /// The older sample scenes can still use their bundled sound palette.
    /// </summary>
    public sealed class SoundPalettePipeline : MonoBehaviour
    {
        readonly Dictionary<string,AudioClip> clips = new Dictionary<string,AudioClip>();
        readonly Dictionary<string,ResourceRequest> pending = new Dictionary<string,ResourceRequest>();
        readonly Dictionary<string,AudioClip> generatedClips = new Dictionary<string,AudioClip>();
        LiveSoundEffectClient liveClient;
        public bool LiveEffectsEnabled { get; set; }
        public Func<string> SessionId { get; set; }
        public Func<ExperienceIdentity> ExperienceContext { get; set; }
        int generation;
        bool ambienceEnabled, spatialReady, ambienceRequested, ambiencePaused;
        string ambienceGroupId;
        AudioSource ambienceSource;
        MetaXRAudioSource metaSource;
        const float PreparationTimeoutSeconds = 10;
        const float NarrationAmbienceVolume = .10f;

        public SoundProfile Profile { get; private set; } = SoundProfile.Standard;
        public float WordGainMultiplier => Profile == SoundProfile.Gentle ? .6f : 1f;
        public float WordGain => WordGainMultiplier;
        public string Status { get; private set; } = "Existing sound palette ready";
        /// <summary>Loaded assets retained across sessions; this is not a generation quota.</summary>
        public int PreparedCount => clips.Count+generatedClips.Count;
        public AudioSource AmbienceSource => ambienceSource;
        public bool IsAmbiencePlaying => ambienceSource != null && ambienceSource.isPlaying;
        public bool IsAmbiencePaused => ambienceRequested && ambiencePaused;
        /// <summary>Optional current registered pose for a previously accepted group; never mutates the recognition record.</summary>
        public Func<string,Vector3?> ResolveWorldPosition { get; set; }
        public bool StoryAmbienceEnabled
        {
            get => ambienceEnabled;
            set { ambienceEnabled=value; if(!value) StopAllPlayback(); }
        }

        public void SetSoundProfile(SoundProfile profile)
        {
            if(profile != SoundProfile.Standard && profile != SoundProfile.Gentle)
                throw new ArgumentOutOfRangeException(nameof(profile));
            Profile=profile;
            if(ambienceSource != null) ambienceSource.volume=NarrationAmbienceVolume*WordGainMultiplier;
        }

        public IEnumerator PrepareWord(string word,Action<AudioClip> completed)
        {
            int token=generation;
            if(LiveEffectsEnabled)
            {
                string key=(ExperienceContext?.Invoke()?.Key ?? "")+":"+(SessionId?.Invoke() ?? "")+":"+Normalize(word);
                if(generatedClips.TryGetValue(key,out var cached) && cached!=null)
                { yield return null;if(Valid(token)) completed?.Invoke(cached);yield break; }
                if(liveClient==null) liveClient=new LiveSoundEffectClient(()=>SessionId?.Invoke(),()=>ExperienceContext?.Invoke() ?? new ExperienceIdentity());
                Status="Generating sound for "+Normalize(word);
                AudioClip generated=null;string error=null;
                yield return liveClient.Generate(word,(clip,detail)=>{generated=clip;error=detail;});
                if(!Valid(token)) {if(generated!=null) Destroy(generated);yield break;}
                if(generated!=null) generatedClips[key]=generated;
                Status=generated!=null ? "Generated sound for "+Normalize(word) : "Sound unavailable: "+error;
                completed?.Invoke(generated);
                yield break;
            }
            if(!TryGetResource(word,out var resource))
            {
                Status="No bundled sound for "+(word ?? "an empty word");
                if(Valid(token)) completed?.Invoke(null);
                yield break;
            }
            ResourceRequest request=BeginLoad(resource);
            Status="Preparing existing "+Normalize(word)+" sound";
            // Always provide a cancellation boundary, including an already cached asset.
            yield return null;
            if(!Valid(token)) yield break;
            AudioClip prepared=null;
            yield return CompleteLoad(resource,request,token,clip=>prepared=clip);
            if(!Valid(token)) yield break;
            Status=prepared != null ? "Prepared "+Normalize(word)+" sound" : "Could not prepare bundled "+Normalize(word)+" sound";
            completed?.Invoke(prepared);
        }

        /// <summary>
        /// Starts all unique resource requests before waiting. Missing optional effects
        /// do not prevent story narration; completion still fires for the current session.
        /// </summary>
        public IEnumerator PrepareStory(StoryContext context,Action completed)
        {
            int token=generation;
            if(LiveEffectsEnabled) {yield return null;if(Valid(token)) completed?.Invoke();yield break;}
            var requests=new Dictionary<string,ResourceRequest>();
            if(context?.words != null)
                foreach(var word in context.words)
                    if(word != null && TryGetResource(word.word,out var resource) && !requests.ContainsKey(resource))
                        requests.Add(resource,BeginLoad(resource));
            Status="Preparing existing story sounds";
            yield return null;
            int prepared=0;
            foreach(var entry in requests)
            {
                if(!Valid(token)) yield break;
                AudioClip clip=null;
                yield return CompleteLoad(entry.Key,entry.Value,token,result=>clip=result);
                if(!Valid(token)) yield break;
                if(clip != null) ++prepared;
            }
            if(!Valid(token)) yield break;
            Status="Prepared "+prepared+" of "+requests.Count+" bundled story sounds";
            completed?.Invoke();
        }

        ResourceRequest BeginLoad(string resource)
        {
            if(clips.TryGetValue(resource,out var cached) && cached != null) return null;
            if(pending.TryGetValue(resource,out var request)) return request;
            request=Resources.LoadAsync<AudioClip>(resource);
            pending.Add(resource,request);
            return request;
        }

        IEnumerator CompleteLoad(string resource,ResourceRequest request,int token,Action<AudioClip> completed)
        {
            if(!Valid(token)) yield break;
            if(clips.TryGetValue(resource,out var cached) && cached != null)
            { completed(cached); yield break; }
            float deadline=Time.realtimeSinceStartup+PreparationTimeoutSeconds;
            while(request != null && !request.isDone && Time.realtimeSinceStartup<deadline)
            { if(!Valid(token)) yield break; yield return null; }
            if(!Valid(token)) yield break;
            AudioClip clip=request != null && request.isDone ? request.asset as AudioClip : null;
            if(clip != null && clip.loadState == AudioDataLoadState.Unloaded && !clip.LoadAudioData()) clip=null;
            while(clip != null && clip.loadState == AudioDataLoadState.Loading && Time.realtimeSinceStartup<deadline)
            { if(!Valid(token)) yield break; yield return null; }
            if(!Valid(token)) yield break;
            if(clip != null && clip.loadState != AudioDataLoadState.Loaded) clip=null;
            if(pending.TryGetValue(resource,out var current) && current == request) pending.Remove(resource);
            if(clip != null) clips[resource]=clip;
            completed(clip);
        }

        /// <summary>
        /// Call only while final narration is playing, after PrepareStory completes.
        /// The optional rain bed stays at the saved RAIN word position and uses a
        /// quiet fixed accompaniment level; there is no automatic sidechain compressor.
        /// </summary>
        public void StartStoryAmbience(StoryContext context)
        {
            StopAllPlayback();
            if(LiveEffectsEnabled) return;
            if(!ambienceEnabled || !isActiveAndEnabled || context?.words == null) return;
            WordRecord rain=null;
            foreach(var word in context.words)
                if(word != null && Normalize(word.word)=="RAIN" && Finite(word.worldPosition)) { rain=word; break; }
            if(rain == null || !clips.TryGetValue("SatieAudio/rain",out var clip) || clip == null)
            { Status="Optional rain ambience is unavailable"; return; }
            EnsureAmbienceSource();
            ambienceGroupId=rain.groupId;
            ambienceSource.transform.position=rain.worldPosition;
            ambienceSource.clip=clip;
            ambienceSource.volume=NarrationAmbienceVolume*WordGainMultiplier;
            ambienceRequested=true;
            ResumeWhenReady();
        }

        void EnsureAmbienceSource()
        {
            if(ambienceSource != null) return;
            var source=new GameObject("Optional spatial rain under narration");
            source.transform.SetParent(transform,false);
            ambienceSource=source.AddComponent<AudioSource>();
            ambienceSource.playOnAwake=false;
            ambienceSource.loop=true;
            ambienceSource.spatialBlend=1;
            ambienceSource.dopplerLevel=0;
            ambienceSource.rolloffMode=AudioRolloffMode.Linear;
            ambienceSource.minDistance=.25f;
            ambienceSource.maxDistance=8;
            ambienceSource.priority=220;
            metaSource=source.AddComponent<MetaXRAudioSource>();
            metaSource.EnableAcoustics=false;
            metaSource.GainBoostDb=0;
        }

        public void SetSpatialReady(bool ready)
        {
            spatialReady=ready;
            if(!ready)
            {
                if(ambienceSource != null && ambienceSource.isPlaying)
                { ambienceSource.Pause(); ambiencePaused=true; }
                if(ambienceRequested) RefreshAmbiencePosition();
                return;
            }
            ResumeWhenReady();
        }

        void ResumeWhenReady()
        {
            if(!ambienceRequested || !ambienceEnabled || !isActiveAndEnabled || ambienceSource == null) return;
            if(!RefreshAmbiencePosition() || !spatialReady) return;
            bool configured=string.Equals(AudioSettings.GetSpatializerPluginName(),"Meta XR Audio",StringComparison.Ordinal);
            ambienceSource.spatialize=configured;
            metaSource.EnableSpatialization=configured;
            if(Application.platform==RuntimePlatform.Android && !configured)
            {
                if(ambienceSource.isPlaying) { ambienceSource.Pause(); ambiencePaused=true; }
                Status="Spatial ambience waiting for Meta XR Audio";
                return;
            }
            if(ambiencePaused) ambienceSource.UnPause();
            else if(!ambienceSource.isPlaying) ambienceSource.Play();
            ambiencePaused=false;
        }

        bool RefreshAmbiencePosition()
        {
            if(ResolveWorldPosition == null) return true;
            Vector3? resolved=null;
            try { resolved=ResolveWorldPosition(ambienceGroupId); }
            catch(Exception ex) { Debug.LogWarning("[SoundPalette] Word position resolver failed: "+ex.Message); }
            if(!resolved.HasValue || !Finite(resolved.Value))
            {
                if(ambienceSource != null && ambienceSource.isPlaying)
                { ambienceSource.Pause(); ambiencePaused=true; }
                Status="Spatial ambience waiting for a registered word position";
                return false;
            }
            ambienceSource.transform.position=resolved.Value;
            return true;
        }

        public void StopAllPlayback()
        {
            ambienceRequested=false;
            ambiencePaused=false;
            ambienceGroupId=null;
            if(ambienceSource != null) { ambienceSource.Stop(); ambienceSource.clip=null; }
        }

        public void CancelPending()
        {
            ++generation;
            liveClient?.Cancel();
            pending.Clear();
            StopAllPlayback();
            Status="Sound preparation canceled";
        }

        public void ResetSession()
        {
            CancelPending();
            foreach(var clip in generatedClips.Values) if(clip!=null) Destroy(clip);
            generatedClips.Clear();
            Status=LiveEffectsEnabled ? "Live sound generation ready" : "Existing sound palette ready";
        }

        void OnDisable() { CancelPending(); }
        void OnDestroy() { foreach(var clip in generatedClips.Values) if(clip!=null) Destroy(clip);generatedClips.Clear(); }
        void OnApplicationPause(bool paused) { if(paused) StopAllPlayback(); }
        void Update() { if(ambienceRequested) ResumeWhenReady(); }
        bool Valid(int token)=>token==generation && isActiveAndEnabled;

        public static bool TryGetResource(string word,out string resource)
        {
            switch(Normalize(word))
            {
                case "CAT": resource="SatieAudio/cat"; return true;
                case "RAIN": resource="SatieAudio/rain"; return true;
                case "SLEEP": resource="SatieAudio/sleep"; return true;
                default: resource=null; return false;
            }
        }

        public static string AssetProvenance(string word)
        {
            switch(Normalize(word))
            {
                case "CAT": case "RAIN": return "Existing SATIE library asset reused locally; no new generation request.";
                case "SLEEP": return "Local synthesized test cue; not a SATIE-generated asset.";
                default: return "No bundled sound asset.";
            }
        }

        static string Normalize(string word)=>(word ?? "").Trim().TrimEnd('#', '.').ToUpperInvariant();
        static bool Finite(Vector3 p)=>float.IsFinite(p.x)&&float.IsFinite(p.y)&&float.IsFinite(p.z);
    }
}
