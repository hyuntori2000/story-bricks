using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace BrailleSoundDemo
{
    [Serializable] sealed class SoundEffectRequest
    {
        public string sessionId, requestId, word;
    }

    /// <summary>Uses the Mac backend; the provider credential never enters the APK.</summary>
    public sealed class LiveSoundEffectClient
    {
        readonly Func<string> session;
        readonly List<UnityWebRequest> requests = new List<UnityWebRequest>();
        int revision;
        public LiveSoundEffectClient(Func<string> currentSession) { session=currentSession; }

        public IEnumerator Generate(string word,Action<AudioClip,string> completed)
        {
            string identity=session?.Invoke(); int token=revision;
            if(string.IsNullOrEmpty(identity)) { completed(null,"No active story session."); yield break; }
            string error="Sound generation did not complete.";
            for(int attempt=1;attempt<=2;attempt++)
            {
                if(!Valid(token,identity)) yield break;
                string requestId=Guid.NewGuid().ToString("N");
                var payload=new SoundEffectRequest {sessionId=identity,requestId=requestId,word=word};
                BackendReply reply=null;
                Debug.Log("[Sound generation] request attempt="+attempt+" word="+word);
                using(var request=new UnityWebRequest(LiveStoryProvider.BaseUrl+"/sound-effect","POST"))
                {
                    request.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload)));
                    request.downloadHandler=new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type","application/json");
                    request.timeout=85; request.redirectLimit=0; requests.Add(request);
                    yield return request.SendWebRequest(); requests.Remove(request);
                    if(!Valid(token,identity)) yield break;
                    var body=request.downloadHandler.text;
                    try { if(!string.IsNullOrEmpty(body)&&body.Length<64000) reply=JsonUtility.FromJson<BackendReply>(body); }
                    catch(ArgumentException) { }
                    if(request.result!=UnityWebRequest.Result.Success && string.IsNullOrEmpty(reply?.error))
                        reply=new BackendReply {code="transport",error="Sound backend HTTP "+request.responseCode};
                }
                if(reply!=null && string.IsNullOrEmpty(reply.error) && reply.sessionId==identity &&
                   reply.requestId==requestId && SafeAudioPath(reply.audioPath))
                {
                    byte[] bytes=null;
                    using(var request=UnityWebRequest.Get(LiveStoryProvider.BaseUrl+reply.audioPath))
                    {
                        request.timeout=20;request.redirectLimit=0;requests.Add(request);
                        yield return request.SendWebRequest();requests.Remove(request);
                        if(!Valid(token,identity)) yield break;
                        if(request.result==UnityWebRequest.Result.Success) bytes=request.downloadHandler.data;
                    }
                    AudioClip clip=null;
                    try { if(bytes!=null) clip=WavCodec.CreateClip(bytes,"Generated effect "+word); }
                    catch(Exception ex) when(ex is IOException || ex is ArgumentException || ex is InvalidOperationException || ex is OverflowException)
                    { error="Generated effect could not be decoded."; }
                    if(clip!=null) { completed(clip,null);yield break; }
                    error="Generated effect could not be downloaded or decoded.";
                }
                else error=reply?.error ?? "Invalid sound-generation response.";
                Debug.LogWarning("[Sound generation] failed attempt="+attempt+" code="+(reply?.code ?? "invalid_response")+" detail="+error);
                if(attempt<2) yield return new WaitForSecondsRealtime(.5f);
            }
            if(Valid(token,identity)) completed(null,error);
        }

        bool Valid(int token,string identity)=>token==revision && session?.Invoke()==identity;
        static bool SafeAudioPath(string path)=>path!=null && path.Length==43 && path.StartsWith("/audio/",StringComparison.Ordinal) &&
            path.EndsWith(".wav",StringComparison.Ordinal) && Guid.TryParseExact(path.Substring(7,32),"N",out _);

        public void Cancel()
        {
            ++revision;
            foreach(var request in requests)
            {
                try {request.Abort();request.Dispose();} catch(ObjectDisposedException) { }
            }
            requests.Clear();
        }
    }
}
