using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BrailleSoundDemo
{
    /// <summary>Optional standalone acceptance run; it is inert in ordinary demos.</summary>
    public sealed class DemoSmokeCheck : MonoBehaviour
    {
        public DemoExperience experience;
        [Serializable] sealed class Report
        {
            public bool passed;
            public bool fastMode;
            public int letterComponents;
            public int savedAnswers;
            public int recognizedWords;
            public int requestCount;
            public string[] recognized;
            public string[] componentIds;
            public Vector3[] groupPositions;
            public string[] states;
            public string[] failures;
            public string spatializer;
            public bool observedWordAudio;
            public bool observedVoiceAudio;
            public bool microphoneUsed=false;
            public bool apiUsed=false;
            public string sessionFile;
            public string story;
            public string storyOutputFile;
            public int storyOutputEvents;
        }
        readonly List<string> failures=new List<string>();
        readonly List<string> states=new List<string>();
        bool wordAudio,voiceAudio,active;
        IEnumerator Start()
        {
            string reportPath=Argument("--story-smoke-test"); if(string.IsNullOrEmpty(reportPath)) yield break;
            active=true;
            while(experience==null || !experience.IsReady) yield return null;
            var flow=experience.Flow; var layout=experience.Layout;
            int storyEvents=0; StoryOutput storyOutput=null;
            flow.StoryOutputReady+=output=> { storyEvents++; storyOutput=output; Check(File.Exists(flow.StoryOutputPath),"Story event fired before its JSON export existed."); Check(flow.Record.turns.Count==3,"Story event fired without all three saved answers."); };
            bool fast=HasArgument("--story-fast-smoke"); flow.TestFastMode=fast; if(fast) layout.AssemblySeconds=.05f;
            yield return null;
            Check(layout.ComponentCount==13,"Expected 13 individual letter components.");
            var unique=new HashSet<string>(); foreach(var component in layout.Components) unique.Add(component.id);
            Check(unique.Count==13,"Component IDs must be unique and stable.");
            for(int i=0;i<3;i++) for(int j=i+1;j<3;j++) Check(Vector3.Distance(layout.GroupWorldPosition(i),layout.GroupWorldPosition(j))>1.2f,"Random word targets overlap or are too close.");
            Check(!experience.Complete(1) && !experience.Complete(2),"Later completion controls must be gated.");
            experience.SimulateAnswer(); Check(flow.Record.turns.Count==0,"Answer before output must be ignored.");
            foreach(var path in new[]{"SatieAudio/cat","SatieAudio/rain","SatieAudio/sleep","StoryAudio/question_cat","StoryAudio/question_rain","StoryAudio/question_sleep","StoryAudio/answer_cat","StoryAudio/answer_rain","StoryAudio/answer_sleep","StoryAudio/story"})
            { var clip=Resources.Load<AudioClip>(path); Check(clip!=null && clip.samples>0,"Missing playable audio: "+path); }
            Check(flow.WordSource.spatialBlend==1 && flow.VoiceSource.spatialBlend==0,"Word effect must be spatial and narration centered.");
            Check(experience.PlayProbe(0),"Spatial probe should start while ready."); yield return null;
            Check(experience.ProbeSource.isPlaying,"Spatial diagnostic source did not play."); experience.PlayProbe(0);
            var expectedWords=new[]{"CAT","RAIN","SLEEP"};
            var expectedQuestions=new[]{"What color is the cat?","Is the cat outside?","Where does the cat fall asleep?"};
            for(int index=0;index<3;index++)
            {
                Check(experience.Complete(index),"Completion "+index+" rejected unexpectedly.");
                Check(!experience.Complete(index),"Repeated completion must be gated.");
                Check(!flow.CanAnswer,"Answer must remain closed during assembly/output.");
                experience.SimulateAnswer();
                Check(flow.Record.turns.Count==index,"Premature answer changed the saved turns.");
                if(index==0 && !fast)
                {
                    yield return WaitFor(ConversationState.PlayingWord,15);
                    yield return new WaitForSecondsRealtime(.15f);
                    Check(flow.WordSource.isPlaying,"Word cue must play before suspension test.");
                    experience.Tracking.InvalidateAlignment("Simulated tracking loss during desktop audio check");
                    int pausedSample=flow.WordSource.timeSamples;
                    yield return new WaitForSecondsRealtime(.35f);
                    Check(!flow.WordSource.isPlaying && flow.IsWordAudioPaused,"Invalid tracking did not pause actual word playback.");
                    Check(Math.Abs(flow.WordSource.timeSamples-pausedSample)<=2048,"Paused cue consumed audio samples.");
                    Check(!experience.Complete(1) && !flow.CanAnswer,"Input opened while spatial output was suspended.");
                    var board=experience.DetectionOrigin;
                    experience.Tracking.CalibrateBoardAt(board.position+Vector3.right*.15f,board.rotation);
                    Check(Vector3.Distance(flow.ActiveWorldPosition,layout.GroupWorldPosition(0))<.001f,"Cue resumed before its recalibrated source pose was applied.");
                    yield return null;
                    Check(flow.WordSource.isPlaying && !flow.IsWordAudioPaused,"Actual word playback did not resume after valid placement.");
                }
                yield return WaitFor(ConversationState.Listening,30);
                if(flow.State!=ConversationState.Listening) break;
                var observation=layout.Observations[index];
                Check(observation!=null && observation.word==expectedWords[index],"Geometric letter readback was incorrect.");
                Check(flow.CurrentPrompt==expectedQuestions[index] && !flow.IsAudioPlaying,"Question must finish before answer input opens.");
                Check(Vector3.Distance(flow.WordSource.transform.position,layout.GroupWorldPosition(index))<.001f,"Audio source does not match word center.");
                Check(flow.Record.requests.Count==index+1 && CountStoryRequests(flow)==0,"Story request started before the final answer.");
                Check(storyEvents==0 && flow.LastStoryOutput==null,"Story output was produced before the final answer.");
                Check(!experience.Complete(index<2?index+1:2),"Completion must wait until the current answer has been saved.");
                experience.SimulateAnswer(); experience.SimulateAnswer();
                Check(!flow.CanAnswer && !experience.Complete(2),"Answer reception must close duplicate answer and Finish controls.");
                Check(flow.Record.requests.Count==index+1,"Story generation began before answer reception ended.");
                if(index<2)
                {
                    yield return WaitFor(ConversationState.Silent,20);
                    Check(flow.Record.turns.Count==index+1,"Mock answer was not saved exactly once.");
                    Check(!flow.IsAudioPlaying,"Output should stop after saved answer.");
                    yield return new WaitForSecondsRealtime(.2f);
                    Check(flow.State==ConversationState.Silent && !flow.IsAudioPlaying,"Silent gap changed without input.");
                    if(index==0)
                    {
                        var group=layout.GroupTransform(0); var moved=group.localPosition+Vector3.left*.2f;
                        Check(experience.UpdateRecognizedGroupPose(MockLetterLayout.GroupIds[0],moved,Quaternion.identity),"Active stable group pose update rejected.");
                        yield return null;
                        Check(Vector3.Distance(flow.ActiveWorldPosition,experience.DetectionOrigin.TransformPoint(moved))<.001f,"Source failed to follow moved word.");
                        Vector3 before=flow.ActiveWorldPosition;
                        Check(!flow.UpdateSourcePosition("unrelated",Vector3.one*99),"Unrelated ID moved active source.");
                        Check(flow.ActiveWorldPosition==before,"Unrelated source pose mutation.");
                    }
                }
                else yield return WaitFor(ConversationState.Finished,60);
            }
            Check(flow.Record.words.Count==3,"All three words must be recorded.");
            Check(flow.Record.turns.Count==3,"Exactly three mock answers expected.");
            Check(flow.Record.requests.Count==4 && CountStoryRequests(flow)==1,"Expected three question requests and one story request.");
            Check(flow.Record.completed && !string.IsNullOrEmpty(flow.Record.finalStory),"Story did not finish.");
            Check(!flow.IsAudioPlaying,"Final narration must finish in silence.");
            if(flow.Record.turns.Count==3) Check(flow.Record.turns[0].answer=="Pink." && flow.Record.turns[1].answer=="No, she is inside." && flow.Record.turns[2].answer=="On a soft blanket.","Saved answer fixtures differ from expected.");
            Check(storyEvents==1 && storyOutput!=null,"Story output must be emitted exactly once.");
            if(storyOutput!=null)
            {
                Check(storyOutput.words.Count==3 && storyOutput.turns.Count==3 && storyOutput.storyText==flow.Record.finalStory && storyOutput.storyAudioResource=="StoryAudio/story","Story output snapshot lacks the full context or prepared narration.");
                Check(storyOutput.turns.Count==3 && storyOutput.turns[2].wordIndex==2 && storyOutput.turns[2].groupId==MockLetterLayout.GroupIds[2],"Final answer lost its exact word/group link.");
            }
            if(flow.Record.requests.Count==4)
            {
                var finalContext=JsonUtility.FromJson<StoryContext>(flow.Record.requests[3].payloadJson);
                Check(finalContext.turns.Count==3 && finalContext.turns[2].answer=="On a soft blanket.","Story request omitted the final answer.");
            }
            Check(!string.IsNullOrEmpty(flow.StoryOutputPath) && File.Exists(flow.StoryOutputPath),"Story output JSON was not saved.");
            experience.SimulateAnswer(); Check(!experience.Complete(2),"Finished story accepted a duplicate Finish.");
            yield return null;
            Check(storyEvents==1 && CountStoryRequests(flow)==1 && flow.Record.turns.Count==3,"Duplicate controls produced another answer, story request or output event.");
            var words=new List<string>(); var positions=new List<Vector3>();
            foreach(var o in layout.Observations) if(o!=null) { words.Add(o.rawText); positions.Add(o.worldPosition); }
            if(layout.Observations[2]!=null) Check(layout.Observations[2].word=="SLEEP" && layout.Observations[2].isFinish && layout.Observations[2].rawText=="SLEEP#","Finish marker must be separate from semantic word.");
            if(!fast) { Check(wordAudio,"No actual word effect playback observed."); Check(voiceAudio,"No actual narration playback observed."); }
            var report=new Report { passed=failures.Count==0,fastMode=fast,letterComponents=layout.ComponentCount,savedAnswers=flow.Record.turns.Count,recognizedWords=flow.Record.words.Count,requestCount=flow.Record.requests.Count,recognized=words.ToArray(),componentIds=new List<string>(unique).ToArray(),groupPositions=positions.ToArray(),states=states.ToArray(),failures=failures.ToArray(),spatializer=AudioSettings.GetSpatializerPluginName(),observedWordAudio=wordAudio,observedVoiceAudio=voiceAudio,sessionFile=flow.SessionPath,story=flow.Record.finalStory,storyOutputFile=flow.StoryOutputPath,storyOutputEvents=storyEvents };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))); File.WriteAllText(reportPath,JsonUtility.ToJson(report,true));
            string screenshot=Argument("--story-screenshot");
            if(!string.IsNullOrEmpty(screenshot)) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))); ScreenCapture.CaptureScreenshot(screenshot); yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame(); yield return new WaitForSecondsRealtime(.3f); }
            Debug.Log("[Story smoke] "+(report.passed?"PASS":"FAIL")+" / "+reportPath);
            active=false;
            if(HasArgument("--story-auto-quit")) Application.Quit(report.passed?0:2);
        }
        IEnumerator WaitFor(ConversationState target,float timeout)
        {
            float start=Time.realtimeSinceStartup;
            while(experience.Flow.State!=target && experience.Flow.State!=ConversationState.Error && Time.realtimeSinceStartup-start<timeout) yield return null;
            Check(experience.Flow.State==target,"Expected "+target+", got "+experience.Flow.State+": "+experience.Flow.Status);
        }
        static int CountStoryRequests(ConversationFlow flow)
        {
            int count=0; foreach(var request in flow.Record.requests) if(request.operation=="story") count++; return count;
        }
        void Update()
        {
            if(!active || experience==null || experience.Flow==null) return;
            string state=experience.Flow.State.ToString(); if(states.Count==0 || states[states.Count-1]!=state) states.Add(state);
            wordAudio|=experience.Flow.WordSource.isPlaying; voiceAudio|=experience.Flow.VoiceSource.isPlaying;
            if(experience.Flow.CanAnswer && experience.Flow.IsAudioPlaying && !failures.Contains("Answer input opened while output was playing.")) Check(false,"Answer input opened while output was playing.");
            if(CountStoryRequests(experience.Flow)>0 && experience.Flow.Record.turns.Count!=3 && !failures.Contains("Story request began without all three saved answers.")) Check(false,"Story request began without all three saved answers.");
        }
        void Check(bool condition,string message) { if(!condition) { failures.Add(message); Debug.LogError("[Story smoke] "+message); } }
        static bool HasArgument(string name) { foreach(var arg in Environment.GetCommandLineArgs()) if(arg==name) return true; return false; }
        static string Argument(string name) { var args=Environment.GetCommandLineArgs(); for(int i=0;i<args.Length-1;i++) if(args[i]==name) return args[i+1]; return ""; }
    }
}
