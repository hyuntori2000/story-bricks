using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace BrailleSoundDemo.PhysicalStory
{
    /// <summary>Session-owned, independent image jobs. Only the local backend knows the generation provider.</summary>
    [DisallowMultipleComponent]
    public sealed class WordImagePipeline : MonoBehaviour
    {
        public const int MaximumPngBytes=8*1024*1024;
        public const int MaximumImageDimension=4096;
        const int MaximumStatusBytes=64*1024, MaximumConcurrentHttp=2;
        const double JobTimeoutSeconds=600;

        [Serializable] sealed class Request
        {
            public string workspaceId,experienceSessionId,sessionId,requestId,groupId,word;
        }
        [Serializable] sealed class Response
        {
            public string workspaceId,experienceSessionId,sessionId,requestId,groupId,word;
            public string status,imagePath,error,code;
        }
        sealed class Job
        {
            public Request identity;
            public PinchWordBox box;
            public WordSoundAttachment sound;
            public Func<bool> stillCurrent;
            public Action<WordSoundAttachment> displayed;
            public double deadline;
            public bool cancelled;
            public UnityWebRequest request;
        }
        sealed class HttpReply
        {
            public long status;
            public bool succeeded,tooLarge;
            public byte[] data;
        }
        readonly Dictionary<string,Job> jobs=new Dictionary<string,Job>(StringComparer.Ordinal);
        readonly HashSet<UnityWebRequest> activeHttp=new HashSet<UnityWebRequest>();

        public bool Begin(string workspaceId,string experienceSessionId,string sessionId,string groupId,string word,
            PinchWordBox box,WordSoundAttachment sound,Func<bool> stillCurrent,Action<WordSoundAttachment> displayed)
        {
            if(!isActiveAndEnabled || workspaceId!="demo" || string.IsNullOrEmpty(experienceSessionId) ||
                string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(groupId) ||
                !PhysicalWordInputPolicy.IsRecognizedWord(word) || box==null || sound==null ||
                stillCurrent==null || jobs.ContainsKey(groupId)) return false;
            var job=new Job {identity=new Request {workspaceId=workspaceId,experienceSessionId=experienceSessionId,
                sessionId=sessionId,requestId=Guid.NewGuid().ToString("N"),groupId=groupId,word=word},
                box=box,sound=sound,stillCurrent=stillCurrent,displayed=displayed,
                deadline=Time.realtimeSinceStartupAsDouble+JobTimeoutSeconds};
            jobs.Add(groupId,job);
            StartCoroutine(Prepare(job));
            return true;
        }

        public void Cancel(string groupId)
        {
            if(!jobs.TryGetValue(groupId,out var job)) return;
            job.cancelled=true;
            if(job.sound!=null) job.sound.CancelImageReplay();
            job.request?.Abort();
        }

        public void CancelAll()
        {
            foreach(var job in jobs.Values)
            {
                job.cancelled=true;
                if(job.sound!=null) job.sound.CancelImageReplay();
                job.request?.Abort();
            }
            StopAllCoroutines();
            foreach(var job in jobs.Values) { job.request?.Dispose();job.request=null; }
            jobs.Clear();activeHttp.Clear();
        }

        bool Current(Job job) => !job.cancelled && isActiveAndEnabled && job.box!=null &&
            job.sound!=null && job.box.isActiveAndEnabled && job.sound.isActiveAndEnabled && job.stillCurrent();
        bool WithinDeadline(Job job) => Current(job) && Time.realtimeSinceStartupAsDouble<job.deadline;

        IEnumerator Prepare(Job job)
        {
            // Every ambiguous POST retry carries exactly the same immutable request ID/body.
            byte[] body=Encoding.UTF8.GetBytes(JsonUtility.ToJson(job.identity));
            Response status=null;
            for(int attempt=0;attempt<3 && WithinDeadline(job);attempt++)
            {
                HttpReply reply=null;
                yield return Send(job,"POST",LiveStoryProvider.BaseUrl+"/word-image",body,MaximumStatusBytes,value=>reply=value);
                if(!WithinDeadline(job)) yield break;
                if(reply!=null && reply.succeeded)
                {
                    if(!TryReadStatus(job,reply.data,out status)) { Failed(job,"invalid_status_identity");yield break; }
                    break;
                }
                if(!Retryable(reply)) { Failed(job,"request_rejected");yield break; }
                yield return Delay(job,.5*(attempt+1));
            }
            if(status==null) { Failed(job,"request_unavailable");yield break; }
            string query="?workspaceId="+Uri.EscapeDataString(job.identity.workspaceId)+
                "&experienceSessionId="+Uri.EscapeDataString(job.identity.experienceSessionId)+
                "&sessionId="+Uri.EscapeDataString(job.identity.sessionId)+
                "&requestId="+Uri.EscapeDataString(job.identity.requestId);
            int consecutiveErrors=0;
            while(WithinDeadline(job))
            {
                if(status.status=="failed") { Failed(job,string.IsNullOrEmpty(status.code)?"generation_failed":status.code);yield break; }
                if(status.status=="ready")
                {
                    if(!IsImagePath(status.imagePath)) { Failed(job,"invalid_image_path");yield break; }
                    yield return DownloadImage(job,status.imagePath);
                    yield break;
                }
                yield return Delay(job,1);
                if(!WithinDeadline(job)) break;
                HttpReply reply=null;
                yield return Send(job,"GET",LiveStoryProvider.BaseUrl+"/word-image"+query,null,MaximumStatusBytes,value=>reply=value);
                if(!WithinDeadline(job)) break;
                if(reply==null || !reply.succeeded)
                {
                    if(!Retryable(reply) || ++consecutiveErrors>=10) { Failed(job,"poll_unavailable");yield break; }
                    continue;
                }
                consecutiveErrors=0;
                if(!TryReadStatus(job,reply.data,out status)) { Failed(job,"invalid_status_identity");yield break; }
            }
            if(Current(job)) Failed(job,"timeout");
        }

        IEnumerator DownloadImage(Job job,string path)
        {
            HttpReply reply=null;
            for(int attempt=0;attempt<3 && WithinDeadline(job);attempt++)
            {
                yield return Send(job,"GET",LiveStoryProvider.BaseUrl+path,null,MaximumPngBytes,value=>reply=value);
                if(!WithinDeadline(job)) yield break;
                if(reply!=null && reply.succeeded) break;
                if(!Retryable(reply)) { Failed(job,"image_download_rejected");yield break; }
                yield return Delay(job,1);
            }
            if(reply==null || !reply.succeeded || !TryReadPngDimensions(reply.data,out int width,out int height))
            { Failed(job,"invalid_png");yield break; }
            var texture=new Texture2D(2,2,TextureFormat.RGBA32,false) {
                name="Word image "+job.identity.groupId,wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear};
            bool decoded=false;
            try { decoded=ImageConversion.LoadImage(texture,reply.data,true) && texture.width==width && texture.height==height; }
            catch(Exception) { decoded=false; }
            if(!decoded || !Current(job) || !job.box.SetWordImage(texture))
            {
                Destroy(texture);
                if(Current(job)) Failed(job,"image_display_failed");
                yield break;
            }
            job.sound.MarkImageDisplayed();
            job.displayed?.Invoke(job.sound);
            Debug.Log("[Word image] Displayed word="+job.identity.word+" request="+job.identity.requestId);
        }

        IEnumerator Send(Job job,string method,string url,byte[] body,int maximumBytes,Action<HttpReply> completed)
        {
            var download=new BoundedDownload(maximumBytes);
            var request=new UnityWebRequest(url,method) {downloadHandler=download,timeout=30,redirectLimit=0};
            if(body!=null)
            {
                request.uploadHandler=new UploadHandlerRaw(body);
                request.SetRequestHeader("Content-Type","application/json");
            }
            request.SetRequestHeader("Accept",maximumBytes==MaximumPngBytes?"image/png":"application/json");
            job.request=request;
            try
            {
                while(WithinDeadline(job) && activeHttp.Count>=MaximumConcurrentHttp) yield return null;
                if(!WithinDeadline(job)) yield break;
                activeHttp.Add(request);
                var operation=request.SendWebRequest();
                while(!operation.isDone && WithinDeadline(job)) yield return null;
                if(!WithinDeadline(job)) { request.Abort();yield break; }
                completed(new HttpReply {status=request.responseCode,tooLarge=download.TooLarge,
                    succeeded=request.result==UnityWebRequest.Result.Success && !download.TooLarge,
                    data=download.TooLarge?null:download.Bytes});
            }
            finally
            {
                activeHttp.Remove(request);
                if(job.request==request) job.request=null;
                request.Dispose();
            }
        }

        IEnumerator Delay(Job job,double seconds)
        {
            double until=Time.realtimeSinceStartupAsDouble+seconds;
            while(WithinDeadline(job) && Time.realtimeSinceStartupAsDouble<until) yield return null;
        }

        static bool Retryable(HttpReply reply) => reply==null || (!reply.tooLarge &&
            (reply.status==0 || reply.status==408 || reply.status==429 || reply.status>=500));

        static bool TryReadStatus(Job job,byte[] json,out Response response)
        {
            response=null;
            try { response=JsonUtility.FromJson<Response>(Encoding.UTF8.GetString(json)); }
            catch(Exception) { return false; }
            var id=job.identity;
            return response!=null && response.workspaceId==id.workspaceId && response.experienceSessionId==id.experienceSessionId &&
                response.sessionId==id.sessionId && response.requestId==id.requestId && response.groupId==id.groupId && response.word==id.word &&
                (response.status=="queued" || response.status=="running" || response.status=="ready" || response.status=="failed");
        }

        public static bool IsImagePath(string path)
        {
            const string prefix="/word-images/";
            if(path==null || path.Length!=prefix.Length+32+4 || !path.StartsWith(prefix,StringComparison.Ordinal) ||
                !path.EndsWith(".png",StringComparison.Ordinal)) return false;
            for(int i=prefix.Length;i<prefix.Length+32;i++)
                if(!((path[i]>='0' && path[i]<='9') || (path[i]>='a' && path[i]<='f'))) return false;
            return true;
        }

        public static bool TryReadPngDimensions(byte[] bytes,out int width,out int height)
        {
            width=height=0;
            if(bytes==null || bytes.Length<33 || bytes.Length>MaximumPngBytes) return false;
            byte[] signature={137,80,78,71,13,10,26,10};
            for(int i=0;i<signature.Length;i++) if(bytes[i]!=signature[i]) return false;
            if(bytes[8]!=0 || bytes[9]!=0 || bytes[10]!=0 || bytes[11]!=13 ||
                bytes[12]!='I' || bytes[13]!='H' || bytes[14]!='D' || bytes[15]!='R') return false;
            uint w=ReadUInt32(bytes,16),h=ReadUInt32(bytes,20);
            if(w==0 || h==0 || w>MaximumImageDimension || h>MaximumImageDimension) return false;
            width=(int)w;height=(int)h;return true;
        }

        static uint ReadUInt32(byte[] value,int offset) => ((uint)value[offset]<<24)|((uint)value[offset+1]<<16)|
            ((uint)value[offset+2]<<8)|value[offset+3];

        static void Failed(Job job,string code)
        {
            if(job.sound!=null) job.sound.CancelImageReplay();
            if(!job.cancelled) Debug.LogWarning("[Word image] Unavailable word="+job.identity.word+" code="+code);
        }

        sealed class BoundedDownload : DownloadHandlerScript
        {
            readonly int maximumBytes;
            readonly MemoryStream buffer=new MemoryStream();
            public bool TooLarge { get; private set; }
            public byte[] Bytes => buffer.ToArray();
            public BoundedDownload(int maximumBytes) : base(new byte[8192]) { this.maximumBytes=maximumBytes; }
            protected override void ReceiveContentLengthHeader(ulong contentLength)
            { if(contentLength>(ulong)maximumBytes) TooLarge=true; }
            protected override bool ReceiveData(byte[] data,int dataLength)
            {
                if(TooLarge) return false;
                if(data==null || dataLength<=0) return true;
                if(buffer.Length+dataLength>maximumBytes) { TooLarge=true;return false; }
                buffer.Write(data,0,dataLength);return true;
            }
        }

        void OnDisable() => CancelAll();
        void OnDestroy() => CancelAll();
    }
}
