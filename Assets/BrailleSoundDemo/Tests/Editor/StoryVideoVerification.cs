using System;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    /// <summary>Offline protocol and ownership checks; never submits a render or starts a decoder.</summary>
    public static class StoryVideoVerification
    {
        const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        const BindingFlags StaticPrivate=BindingFlags.Static|BindingFlags.NonPublic;
        public static void Run()
        {
            int checks=0;
            Action<bool,string> check=(ok,message)=>{
                if(!ok) throw new InvalidOperationException("Story video: "+message);
                checks++;
            };
            string jobId=new string('a',32),ownerId=new string('b',32),sessionId=new string('c',32),requestId=new string('d',32);
            string validPath="/story-videos/"+jobId+".mp4";
            check(StoryVideoPipeline.IsVideoPath(validPath),"A local immutable MP4 path is accepted.");
            foreach(string invalid in new[] {null,"",validPath+"?redirect=1",validPath+"#fragment",
                "https://example.invalid"+validPath,"//example.invalid"+validPath,
                "/story-videos/../"+jobId+".mp4","/story-videos/"+jobId.ToUpperInvariant()+".mp4",
                "/story-videos/"+jobId+".png","/story-videos/"+jobId+"%2emp4"})
                check(!StoryVideoPipeline.IsVideoPath(invalid),"Foreign origins, traversal, suffixes and malformed IDs are rejected.");

            Type type=typeof(StoryVideoPipeline),requestType=type.GetNestedType("Request",BindingFlags.NonPublic),
                jobType=type.GetNestedType("Job",BindingFlags.NonPublic);
            object request=Activator.CreateInstance(requestType,true),job=Activator.CreateInstance(jobType,true);
            requestType.GetField("workspaceId").SetValue(request,"demo");
            requestType.GetField("experienceSessionId").SetValue(request,ownerId);
            requestType.GetField("sessionId").SetValue(request,sessionId);
            requestType.GetField("requestId").SetValue(request,requestId);
            jobType.GetField("identity").SetValue(job,request);
            string json="{\"workspaceId\":\"demo\",\"experienceSessionId\":\""+ownerId+"\",\"sessionId\":\""+sessionId+
                "\",\"requestId\":\""+requestId+"\",\"status\":\"running\",\"shotsTotal\":4,\"shotsReady\":1}";
            var parse=type.GetMethod("TryReadStatus",StaticPrivate);
            Func<string,bool> accepts=value=>{
                object[] arguments={job,Encoding.UTF8.GetBytes(value),null};
                return (bool)parse.Invoke(null,arguments);
            };
            check(accepts(json),"The exact owner's running response is accepted.");
            foreach(var pair in new[] {new[]{"demo","judy"},new[]{ownerId,new string('e',32)},
                new[]{sessionId,new string('f',32)},new[]{requestId,new string('1',32)}})
                check(!accepts(json.Replace(pair[0],pair[1])),"Every response identity must match the active request.");
            check(!accepts(json.Replace("running","unexpected")),"An unknown job state cannot trigger playback.");
            check(!accepts(json.Replace("\"shotsReady\":1","\"shotsReady\":5")),"Scene progress cannot exceed the declared total.");
            check(!accepts(json.Replace("\"shotsTotal\":4","\"shotsTotal\":999")),"Unbounded scene counts are rejected.");
            check(!accepts("not json"),"Malformed status data is rejected.");

            var root=new GameObject("Temporary story video verification");
            try
            {
                var pipeline=root.AddComponent<StoryVideoPipeline>();
                bool currentOwner=true;
                jobType.GetField("head").SetValue(job,root.transform);
                jobType.GetField("stillCurrent").SetValue(job,(Func<bool>)(()=>currentOwner));
                jobType.GetField("generation").SetValue(job,type.GetField("generation",Private).GetValue(pipeline));
                type.GetField("active",Private).SetValue(pipeline,job);
                var current=type.GetMethod("Current",Private);
                check((bool)current.Invoke(pipeline,new[]{job}),"An active job retains its owner.");
                currentOwner=false;
                check(!(bool)current.Invoke(pipeline,new[]{job}),"Changing experience ownership prevents a late result.");
                currentOwner=true;
                type.GetMethod("EnsurePresentation",Private).Invoke(pipeline,null);
                var presentation=(GameObject)type.GetField("presentation",Private).GetValue(pipeline);
                check(presentation.GetComponents<AudioSource>().Length==2 && root.GetComponentsInChildren<AudioListener>().Length==0,
                    "Video and announcement use audio sources without creating another listener.");
                pipeline.SetSpatialReady(false);
                check(!(bool)type.GetProperty("CanPresent",Private).GetValue(pipeline),"Tracking loss gates playback without changing ownership.");
                check((bool)current.Invoke(pipeline,new[]{job}),"Tracking loss leaves the background job current.");
                pipeline.CancelAll();
                check(!(bool)current.Invoke(pipeline,new[]{job}) && type.GetField("active",Private).GetValue(pipeline)==null,
                    "Reset invalidates the old generation and job.");
                check(presentation==null && root.transform.childCount==0,
                    "Cancellation releases the owned presentation and audio sources in edit mode.");
                pipeline.CancelAll();
                check(root.transform.childCount==0,"Repeated cancellation is safe.");
                check(!pipeline.Begin(new StoryOutput {workspaceId="judy",experienceSessionId=ownerId,sessionId=sessionId,storyText="Story."},
                    root.transform,()=>true,null,null),"Judy cannot start live animation generation.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
            Debug.Log("STORY_VIDEO_VERIFICATION_PASSED "+checks);
        }
    }
}
