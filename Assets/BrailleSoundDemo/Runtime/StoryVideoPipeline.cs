using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Video;
using UnityEngine.UI;

namespace BrailleSoundDemo
{
    /// <summary>One session-owned animation after narration. Provider credentials stay on the Mac.</summary>
    [DisallowMultipleComponent]
    public sealed class StoryVideoPipeline : MonoBehaviour
    {
        public const long MaximumVideoBytes=128L*1024*1024;
        const int MaximumStatusBytes=64*1024;
        const double JobTimeoutSeconds=2400;

        [Serializable] sealed class Request
        {
            public string workspaceId,experienceSessionId,sessionId,requestId,storyText,storyAudioPath;
        }
        [Serializable] sealed class Response
        {
            public string workspaceId,experienceSessionId,sessionId,requestId,status,videoPath,error,code;
            public int shotsTotal,shotsReady;
            public float elapsedSeconds;
            public bool previewOpened;
        }
        sealed class Job
        {
            public Request identity;
            public Func<bool> stillCurrent;
            public Action<string> status,stage;
            public Transform head;
            public double deadline;
            public int generation;
            public string temporaryPath;
            public UnityWebRequest request;
        }
        sealed class HttpReply
        {
            public long status;
            public bool succeeded,tooLarge;
            public byte[] data;
        }

        Job active;
        int generation;
        bool applicationPaused,applicationFocused=true,spatialReady=true,recenter=true;
        bool voicePaused,videoPaused,videoVisible;
        GameObject presentation,screen;
        AudioSource narration,videoAudio;
        VideoPlayer video;
        RenderTexture target;
        Material material;
        RawImage display;
        string savedVideoPath;
        bool playbackRunning;
        public string Stage { get; private set; } = "idle";
        public string Status { get; private set; }="Animation idle.";
        public event Action<string> VideoReady;
        public event Action<string,string> PresentationChanged;

        /// <summary>The authored UX may host the video; the existing world screen remains a fallback.</summary>
        public void SetPresentationTarget(RawImage image)
        {
            if(display!=null && display!=image) { display.texture=null;display.enabled=false; }
            display=image;
            if(display!=null) { display.texture=target;display.enabled=videoVisible && CanPresent; }
        }

        /// <summary>Replay only the downloaded video belonging to this still-current experience.</summary>
        public bool Replay()
        {
            if(!Current(active) || playbackRunning || string.IsNullOrEmpty(savedVideoPath) || !File.Exists(savedVideoPath)) return false;
            ClearVideoSurface();
            SetStatus(active,"Loading your saved animation...","animation_ready");
            StartCoroutine(PlayVideo(active,savedVideoPath));
            return true;
        }

        void Awake() => applicationFocused=Application.isFocused;
        void Update() { if(active!=null && !Current(active)) CancelAll(); }

        public bool Begin(StoryOutput output,Transform head,Func<bool> stillCurrent,
            Action<string> status,Action<string> stage)
        {
            if(!isActiveAndEnabled || active!=null || output==null || output.workspaceId!="demo" ||
                !IsId(output.experienceSessionId) || !IsId(output.sessionId) ||
                string.IsNullOrWhiteSpace(output.storyText) || output.storyText.Length>12000 ||
                head==null || stillCurrent==null || !stillCurrent()) return false;
            active=new Job {identity=new Request {workspaceId=output.workspaceId,
                experienceSessionId=output.experienceSessionId,sessionId=output.sessionId,
                requestId=Guid.NewGuid().ToString("N"),storyText=output.storyText,
                storyAudioPath=output.storyAudioPath ?? ""},head=head,stillCurrent=stillCurrent,
                status=status,stage=stage,generation=generation,
                deadline=Time.realtimeSinceStartupAsDouble+JobTimeoutSeconds};
            StartCoroutine(Generate(active));
            return true;
        }

        /// <summary>Play an indexed Mac movie without creating a session or contacting a generator.</summary>
        public bool BeginSaved(StoryLibraryItem item,Transform head,Func<bool> stillCurrent)
        {
            if(!isActiveAndEnabled || item==null || !item.Valid || head==null || stillCurrent==null || !stillCurrent()) return false;
            CancelAll();
            active=new Job {identity=new Request {workspaceId="demo",experienceSessionId=item.experienceSessionId,
                sessionId=item.sessionId,requestId=Guid.NewGuid().ToString("N")},head=head,
                stillCurrent=stillCurrent,generation=generation,deadline=Time.realtimeSinceStartupAsDouble+600};
            SetStatus(active,"Loading your saved animation...","animation_ready");
            StartCoroutine(DownloadAndPlay(active,item.videoPath));
            return true;
        }

        public void SetSpatialReady(bool ready)
        {
            if(ready && !spatialReady) recenter=true;
            spatialReady=ready;
            RefreshPlayback();
        }

        bool Current(Job job)
        {
            if(job==null || active!=job || job.generation!=generation || !isActiveAndEnabled || job.head==null) return false;
            try { return job.stillCurrent(); } catch(Exception) { return false; }
        }
        bool Waiting(Job job) => Current(job) && Time.realtimeSinceStartupAsDouble<job.deadline;
        bool CanPresent => !applicationPaused && applicationFocused && spatialReady;

        IEnumerator Generate(Job job)
        {
            SetStatus(job,"Let's turn your story into an animation.","animation_requested");
            yield return Announce(job);
            if(!Waiting(job)) { TimeoutIfNeeded(job);yield break; }
            SetStatus(job,"Making your animation...","animation_generating");
            byte[] body=Encoding.UTF8.GetBytes(JsonUtility.ToJson(job.identity));
            Response response=null;
            for(int attempt=0;attempt<3 && Waiting(job);attempt++)
            {
                HttpReply reply=null;
                yield return SendStatus(job,"POST",LiveStoryProvider.BaseUrl+"/story-video",body,value=>reply=value);
                if(!Waiting(job)) { TimeoutIfNeeded(job);yield break; }
                if(reply!=null && reply.succeeded)
                {
                    if(!TryReadStatus(job,reply.data,out response)) { Failed(job,"invalid_status_identity");yield break; }
                    break;
                }
                if(!Retryable(reply)) { Failed(job,"request_rejected");yield break; }
                yield return Delay(job,.5*(attempt+1));
            }
            if(response==null) { Failed(job,"request_unavailable");yield break; }
            string query="?workspaceId="+Uri.EscapeDataString(job.identity.workspaceId)+
                "&experienceSessionId="+Uri.EscapeDataString(job.identity.experienceSessionId)+
                "&sessionId="+Uri.EscapeDataString(job.identity.sessionId)+
                "&requestId="+Uri.EscapeDataString(job.identity.requestId);
            int errors=0;
            while(Waiting(job))
            {
                if(response.status=="failed") { Failed(job,string.IsNullOrEmpty(response.code)?"generation_failed":response.code);yield break; }
                if(response.status=="ready")
                {
                    if(!IsVideoPath(response.videoPath)) { Failed(job,"invalid_video_path");yield break; }
                    SetStatus(job,"Your animation is ready. Loading the video...","animation_ready");
                    yield return DownloadAndPlay(job,response.videoPath);
                    yield break;
                }
                SetStatus(job,response.shotsTotal>0 ? "Making your animation... "+response.shotsReady+" of "+response.shotsTotal+" scenes ready." : "Making your animation...");
                yield return Delay(job,2);
                if(!Waiting(job)) break;
                HttpReply reply=null;
                yield return SendStatus(job,"GET",LiveStoryProvider.BaseUrl+"/story-video"+query,null,value=>reply=value);
                if(!Waiting(job)) break;
                if(reply==null || !reply.succeeded)
                {
                    if(!Retryable(reply) || ++errors>=15) { Failed(job,"poll_unavailable");yield break; }
                    continue;
                }
                errors=0;
                if(!TryReadStatus(job,reply.data,out response)) { Failed(job,"invalid_status_identity");yield break; }
            }
            if(Current(job)) Failed(job,"timeout");
        }

        IEnumerator Announce(Job job)
        {
            while(Waiting(job) && !CanPresent) yield return null;
            if(!Waiting(job)) yield break;
            EnsurePresentation();
            var clip=Resources.Load<AudioClip>("Audio/AnimationCreating");
            if(clip==null) { Debug.LogWarning("[Story video] AnimationCreating announcement is unavailable.");yield break; }
            narration.clip=clip; narration.Play();voicePaused=false;
            while(Waiting(job))
            {
                RefreshPlayback();
                if(!CanPresent || voicePaused) { yield return null;continue; }
                if(!narration.isPlaying) break;
                yield return null;
            }
            if(narration!=null) narration.Stop();voicePaused=false;
        }

        IEnumerator DownloadAndPlay(Job job,string path)
        {
            string directory=Path.Combine(new ExperienceIdentity(job.identity.workspaceId,job.identity.experienceSessionId)
                .DirectoryUnder(Application.persistentDataPath),"Videos");
            string file=Path.Combine(directory,path.Substring("/story-videos/".Length));
            bool directoryReady=true;
            try { Directory.CreateDirectory(directory); } catch(IOException) { directoryReady=false; } catch(UnauthorizedAccessException) { directoryReady=false; }
            if(!directoryReady) { Failed(job,"video_storage_unavailable");yield break; }
            if(ValidMp4File(file))
            {
                savedVideoPath=file;
                Debug.Log("[Story video] Reusing the downloaded movie for session="+job.identity.sessionId);
                yield return PlayVideo(job,file);
                yield break;
            }
            job.temporaryPath=file+"."+job.identity.requestId+".partial";
            bool downloaded=false;
            for(int attempt=0;attempt<3 && Waiting(job);attempt++)
            {
                DeleteTemporary(job);
                using(var request=new UnityWebRequest(LiveStoryProvider.BaseUrl+path,UnityWebRequest.kHttpVerbGET))
                {
                    request.downloadHandler=new DownloadHandlerFile(job.temporaryPath) {removeFileOnAbort=true};
                    request.timeout=180;request.redirectLimit=0;request.SetRequestHeader("Accept","video/mp4");
                    job.request=request;
                    var operation=request.SendWebRequest();
                    bool oversized=false;
                    while(!operation.isDone && Waiting(job))
                    {
                        string length=request.GetResponseHeader("Content-Length");
                        oversized=request.downloadedBytes>(ulong)MaximumVideoBytes ||
                            (long.TryParse(length,out long declared) && declared>MaximumVideoBytes);
                        if(oversized) { request.Abort();break; }
                        yield return null;
                    }
                    if(!Waiting(job)) { request.Abort();job.request=null;DeleteTemporary(job);TimeoutIfNeeded(job);yield break; }
                    long status=request.responseCode;
                    downloaded=!oversized && request.result==UnityWebRequest.Result.Success &&
                        request.downloadedBytes<=(ulong)MaximumVideoBytes;
                    job.request=null;
                    if(oversized) { DeleteTemporary(job);Failed(job,"video_too_large");yield break; }
                    if(downloaded) break;
                    if(status!=0 && status!=408 && status!=429 && status<500)
                    { DeleteTemporary(job);Failed(job,"video_download_rejected");yield break; }
                }
                yield return Delay(job,1);
            }
            if(!downloaded || !ValidMp4File(job.temporaryPath))
            { DeleteTemporary(job);Failed(job,"invalid_video_file");yield break; }
            bool saved=true;
            try
            {
                if(File.Exists(file)) File.Delete(file);
                File.Move(job.temporaryPath,file);job.temporaryPath=null;
            }
            catch(IOException) { saved=false; } catch(UnauthorizedAccessException) { saved=false; }
            if(!saved) { DeleteTemporary(job);Failed(job,"video_storage_unavailable");yield break; }
            if(!Current(job)) yield break;
            savedVideoPath=file;
            Debug.Log("[Story video] Downloaded session="+job.identity.sessionId);
            try { VideoReady?.Invoke(file); } catch(Exception) { Debug.LogWarning("[Story video] A ready listener could not finish."); }
            if(!Current(job)) yield break;
            yield return PlayVideo(job,file);
        }

        IEnumerator PlayVideo(Job job,string file)
        {
            playbackRunning=true;
            while(Current(job) && !CanPresent) yield return null;
            if(!Current(job)) yield break;
            EnsurePresentation();
            videoVisible=false;
            if(display==null)
            {
                var shader=Resources.Load<Shader>("WordImageAlpha");
                if(shader==null) { Failed(job,"video_shader_unavailable");yield break; }
                screen=GameObject.CreatePrimitive(PrimitiveType.Quad);screen.name="Generated story animation";
                var collider=screen.GetComponent<Collider>();if(collider!=null) DestroyOwned(collider);
                screen.transform.SetParent(presentation.transform,false);
                screen.transform.localScale=new Vector3(1.2f,.675f,1);
                screen.SetActive(false);
                material=new Material(shader) {name="Story animation screen",color=Color.white,mainTexture=Texture2D.blackTexture};
                screen.GetComponent<MeshRenderer>().sharedMaterial=material;
            }
            video=presentation.AddComponent<VideoPlayer>();video.playOnAwake=false;video.isLooping=false;
            video.source=VideoSource.Url;video.url=new Uri(file).AbsoluteUri;
            video.renderMode=VideoRenderMode.RenderTexture;video.audioOutputMode=VideoAudioOutputMode.AudioSource;
            video.controlledAudioTrackCount=1;video.EnableAudioTrack(0,true);video.SetTargetAudioSource(0,videoAudio);
            video.waitForFirstFrame=true;
            string failure=null;bool ended=false;
            video.errorReceived+=(_,message)=>failure=message;
            video.loopPointReached+=_=>ended=true;
            video.Prepare();double prepareDeadline=Time.realtimeSinceStartupAsDouble+45;
            while(Current(job) && !video.isPrepared && failure==null && Time.realtimeSinceStartupAsDouble<prepareDeadline) yield return null;
            if(!Current(job)) yield break;
            if(!video.isPrepared || failure!=null || video.width==0 || video.height==0 ||
                video.width>4096 || video.height>4096 || !double.IsFinite(video.length) || video.length<=0 || video.length>300)
            { Failed(job,"video_prepare_failed");yield break; }
            float aspect=video.width/(float)video.height;
            int width=Mathf.Min((int)video.width,1920),height=Mathf.Clamp(Mathf.RoundToInt(width/aspect),16,1920);
            target=new RenderTexture(width,height,0);target.Create();video.targetTexture=target;
            if(material!=null) material.mainTexture=target;
            if(screen!=null) screen.transform.localScale=aspect>=1 ? new Vector3(1.2f,1.2f/aspect,1) : new Vector3(.9f*aspect,.9f,1);
            if(display!=null)
            {
                display.texture=target;
                var fitter=display.GetComponent<AspectRatioFitter>();
                if(fitter==null) fitter=display.gameObject.AddComponent<AspectRatioFitter>();
                fitter.aspectMode=AspectRatioFitter.AspectMode.FitInParent;fitter.aspectRatio=aspect;
            }
            while(Current(job) && !CanPresent) yield return null;
            if(!Current(job)) yield break;
            videoVisible=true;
            if(screen!=null) screen.SetActive(true);
            if(display!=null) display.enabled=true;
            Recenter(job);video.Play();videoPaused=false;
            SetStatus(job,"Playing your animation.","animation_playing");
            Debug.Log("[Story video] Playing session="+job.identity.sessionId);
            double activePlaybackSeconds=0,previousTick=Time.realtimeSinceStartupAsDouble;
            while(Current(job) && !ended && failure==null)
            {
                RefreshPlayback();
                double now=Time.realtimeSinceStartupAsDouble;
                if(CanPresent) activePlaybackSeconds+=Math.Min(1,now-previousTick);
                previousTick=now;
                if(activePlaybackSeconds>video.length+45) { failure="Playback stalled.";break; }
                yield return null;
            }
            if(!Current(job)) yield break;
            if(failure!=null) { Failed(job,"video_playback_failed");yield break; }
            playbackRunning=false;
            SetStatus(job,"Your animation is saved on the Mac. Share your story in video!","animation_complete");
            Debug.Log("[Story video] Playback complete session="+job.identity.sessionId);
        }

        void EnsurePresentation()
        {
            if(presentation!=null) return;
            presentation=new GameObject("Story animation presentation");presentation.transform.SetParent(transform,false);
            narration=presentation.AddComponent<AudioSource>();narration.playOnAwake=false;narration.spatialBlend=0;narration.volume=.8f;
            videoAudio=presentation.AddComponent<AudioSource>();videoAudio.playOnAwake=false;videoAudio.spatialBlend=0;videoAudio.volume=.8f;
        }
        void Recenter(Job job)
        {
            if(!Current(job) || presentation==null) return;
            presentation.transform.position=job.head.position+job.head.forward*1.7f;
            presentation.transform.rotation=Quaternion.LookRotation(job.head.forward,job.head.up);
            recenter=false;
        }
        void RefreshPlayback()
        {
            if(!CanPresent)
            {
                if(narration!=null && narration.isPlaying) { narration.Pause();voicePaused=true; }
                if(video!=null && video.isPlaying) { video.Pause();videoPaused=true; }
                if(screen!=null) screen.SetActive(false);
                if(display!=null) display.enabled=false;
                return;
            }
            if(screen!=null) { screen.SetActive(videoVisible);if(recenter && videoVisible) Recenter(active); }
            if(display!=null) display.enabled=videoVisible;
            if(voicePaused && narration!=null) { narration.UnPause();voicePaused=false; }
            if(videoPaused && video!=null) { video.Play();videoPaused=false; }
        }
        void OnApplicationPause(bool paused) { applicationPaused=paused;if(!paused) recenter=true;RefreshPlayback(); }
        void OnApplicationFocus(bool focused) { applicationFocused=focused;if(focused) recenter=true;RefreshPlayback(); }

        IEnumerator SendStatus(Job job,string method,string url,byte[] body,Action<HttpReply> complete)
        {
            var download=new BoundedDownload();
            using(var request=new UnityWebRequest(url,method) {downloadHandler=download,timeout=30,redirectLimit=0})
            {
                if(body!=null) { request.uploadHandler=new UploadHandlerRaw(body);request.SetRequestHeader("Content-Type","application/json"); }
                request.SetRequestHeader("Accept","application/json");job.request=request;
                var operation=request.SendWebRequest();
                while(!operation.isDone && Waiting(job)) yield return null;
                if(!Waiting(job)) { request.Abort();job.request=null;yield break; }
                complete(new HttpReply {status=request.responseCode,tooLarge=download.TooLarge,
                    succeeded=request.result==UnityWebRequest.Result.Success && !download.TooLarge,
                    data=download.TooLarge?null:download.Bytes});
                job.request=null;
            }
        }
        IEnumerator Delay(Job job,double seconds)
        { double end=Time.realtimeSinceStartupAsDouble+seconds;while(Waiting(job) && Time.realtimeSinceStartupAsDouble<end) yield return null; }
        static bool Retryable(HttpReply reply) => reply==null || (!reply.tooLarge &&
            (reply.status==0 || reply.status==408 || reply.status==429 || reply.status>=500));
        static bool IsId(string value) => Guid.TryParseExact(value,"N",out var id) && id.ToString("N")==value;
        static bool TryReadStatus(Job job,byte[] bytes,out Response response)
        {
            response=null;if(bytes==null || bytes.Length>MaximumStatusBytes) return false;
            try { response=JsonUtility.FromJson<Response>(Encoding.UTF8.GetString(bytes)); } catch(Exception) { return false; }
            var id=job.identity;
            return response!=null && response.workspaceId==id.workspaceId && response.experienceSessionId==id.experienceSessionId &&
                response.sessionId==id.sessionId && response.requestId==id.requestId &&
                (response.status=="queued" || response.status=="running" || response.status=="ready" || response.status=="failed") &&
                response.shotsTotal>=0 && response.shotsTotal<=7 && response.shotsReady>=0 && response.shotsReady<=response.shotsTotal;
        }
        public static bool IsVideoPath(string path)
        {
            const string prefix="/story-videos/";
            if(path==null || path.Length!=prefix.Length+36 || !path.StartsWith(prefix,StringComparison.Ordinal) ||
                !path.EndsWith(".mp4",StringComparison.Ordinal)) return false;
            return IsId(path.Substring(prefix.Length,32));
        }
        static bool ValidMp4File(string path)
        {
            try
            {
                var info=new FileInfo(path);if(!info.Exists || info.Length<16 || info.Length>MaximumVideoBytes) return false;
                var header=new byte[12];using(var stream=File.OpenRead(path)) if(stream.Read(header,0,header.Length)!=header.Length) return false;
                uint size=((uint)header[0]<<24)|((uint)header[1]<<16)|((uint)header[2]<<8)|header[3];
                return size>=16 && size<=info.Length && header[4]=='f' && header[5]=='t' && header[6]=='y' && header[7]=='p';
            }
            catch(IOException) { return false; } catch(UnauthorizedAccessException) { return false; }
        }
        void SetStatus(Job job,string text,string stage=null)
        {
            if(!Current(job)) return;
            bool changed=Status!=text || (stage!=null && Stage!=stage);
            if(Status!=text) { Status=text;job.status?.Invoke(text); }
            if(stage!=null && Current(job)) { Stage=stage;job.stage?.Invoke(stage); }
            if(changed && Current(job)) PresentationChanged?.Invoke(Stage,Status);
        }
        void Failed(Job job,string code)
        {
            if(!Current(job)) return;
            if(video!=null) video.Stop();if(narration!=null) narration.Stop();
            voicePaused=false;videoPaused=false;videoVisible=false;playbackRunning=false;
            if(screen!=null) screen.SetActive(false);
            if(display!=null) display.enabled=false;
            SetStatus(job,"The animation could not finish. Your story is still saved.","animation_failed");
            Debug.LogWarning("[Story video] Unavailable code="+code);
        }
        void TimeoutIfNeeded(Job job)
        { if(Current(job) && Time.realtimeSinceStartupAsDouble>=job.deadline) Failed(job,"timeout"); }
        static void DeleteTemporary(Job job)
        {
            if(string.IsNullOrEmpty(job?.temporaryPath)) return;
            try { if(File.Exists(job.temporaryPath)) File.Delete(job.temporaryPath); } catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
        public void CancelAll()
        {
            ++generation;
            var old=active;active=null;
            if(old!=null) { old.request?.Abort(); }
            StopAllCoroutines();
            if(old!=null) { old.request?.Dispose();old.request=null;DeleteTemporary(old); }
            if(narration!=null) narration.Stop();
            ClearVideoSurface();
            DestroyOwned(presentation);
            presentation=null;narration=null;videoAudio=null;
            savedVideoPath=null;playbackRunning=false;
            voicePaused=false;videoPaused=false;videoVisible=false;recenter=true;Status="Animation idle.";Stage="idle";
        }
        void ClearVideoSurface()
        {
            if(video!=null) { video.Stop();video.targetTexture=null; }
            if(display!=null) { display.texture=null;display.enabled=false; }
            if(target!=null) { target.Release();DestroyOwned(target); }
            DestroyOwned(video);DestroyOwned(material);DestroyOwned(screen);
            target=null;material=null;screen=null;video=null;videoVisible=false;videoPaused=false;
        }
        static void DestroyOwned(UnityEngine.Object item)
        { if(item==null) return;if(Application.isPlaying) Destroy(item);else DestroyImmediate(item); }
        sealed class BoundedDownload : DownloadHandlerScript
        {
            readonly MemoryStream buffer=new MemoryStream();
            public bool TooLarge { get; private set; }
            public byte[] Bytes => buffer.ToArray();
            public BoundedDownload() : base(new byte[8192]) { }
            protected override void ReceiveContentLengthHeader(ulong length) { if(length>MaximumStatusBytes) TooLarge=true; }
            protected override bool ReceiveData(byte[] bytes,int count)
            {
                if(TooLarge) return false;if(bytes==null || count<=0) return true;
                if(buffer.Length+count>MaximumStatusBytes) { TooLarge=true;return false; }
                buffer.Write(bytes,0,count);return true;
            }
        }
        void OnDisable() => CancelAll();
        void OnDestroy() => CancelAll();
    }
}
