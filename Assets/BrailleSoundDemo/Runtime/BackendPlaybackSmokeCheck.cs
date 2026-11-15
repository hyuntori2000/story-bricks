using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace BrailleSoundDemo
{
    /// <summary>Opt-in test of an already-generated backend WAV through the actual Unity speech source.</summary>
    public sealed class BackendPlaybackSmokeCheck : MonoBehaviour
    {
        public DemoExperience experience;
        [Serializable] sealed class Report
        {
            public bool passed, observedPlayback;
            public bool generatedAudio = true, microphoneTested = false, headsetTested = false, newApiCall = false;
            public string audioPath, failure;
            public int channels, sampleRate;
            public float seconds, spatialBlend;
        }
        IEnumerator Start()
        {
            string path=Argument("--backend-audio-smoke");
            if(string.IsNullOrEmpty(path)) yield break;
            string reportPath=Argument("--backend-audio-report");
            if(string.IsNullOrEmpty(reportPath)) yield break;
            while(experience==null || !experience.IsReady) yield return null;
            var report=new Report { audioPath=path };
            AudioClip clip=null;
            if(!path.StartsWith("/audio/",StringComparison.Ordinal) || !path.EndsWith(".wav",StringComparison.Ordinal)) report.failure="Invalid local audio route.";
            else
            {
                using(var request=UnityWebRequest.Get(LiveStoryProvider.BaseUrl+path))
                {
                    request.timeout=15;
                    yield return request.SendWebRequest();
                    if(request.result!=UnityWebRequest.Result.Success) report.failure="Download failed: HTTP "+request.responseCode;
                    else
                    {
                        try { clip=WavCodec.CreateClip(request.downloadHandler.data,"Live backend playback check"); }
                        catch(Exception e) { report.failure="WAV decode failed: "+e.GetType().Name; }
                    }
                }
            }
            if(clip!=null)
            {
                var source=experience.Flow.VoiceSource;
                source.Stop(); source.clip=clip; source.Play();
                report.channels=clip.channels; report.sampleRate=clip.frequency; report.seconds=clip.length; report.spatialBlend=source.spatialBlend;
                float deadline=Time.realtimeSinceStartup+clip.length+5;
                yield return null;
                report.observedPlayback=source.isPlaying;
                while(source.isPlaying && Time.realtimeSinceStartup<deadline) yield return null;
                report.passed=report.observedPlayback && !source.isPlaying && report.spatialBlend==0 && report.channels==1;
                if(!report.passed) report.failure="Generated narration did not complete on the centered voice source.";
                source.Stop(); source.clip=null; Destroy(clip);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)));
            File.WriteAllText(reportPath,JsonUtility.ToJson(report,true));
            Debug.Log("[Backend playback smoke] "+(report.passed?"PASS":"FAIL"));
            Application.Quit(report.passed?0:2);
        }
        static string Argument(string key)
        {
            var args=Environment.GetCommandLineArgs();
            for(int i=0;i<args.Length-1;i++) if(args[i]==key) return args[i+1];
            return "";
        }
    }
}
