using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    public static class SoundPaletteVerification
    {
        [Serializable] sealed class Report
        {
            public bool passed;
            public bool usesBundledAssetsOnly=true;
            public bool onDeviceAudioVerified=false;
            public List<string> checks=new List<string>();
        }

        public static void Run()
        {
            var report=new Report();
            Action<bool,string> check=(ok,message)=>{
                if(!ok) throw new InvalidOperationException("Sound palette: "+message);
                report.checks.Add(message);
            };
            foreach(var word in new[]{"CAT","RAIN","SLEEP"})
            {
                check(SoundPalettePipeline.TryGetResource(word,out var resource) && Resources.Load<AudioClip>(resource)!=null,
                    word+" maps to an existing bundled audio asset.");
            }
            check(SoundPalettePipeline.TryGetResource(" sleep# ",out var sleep) && sleep=="SatieAudio/sleep",
                "The finish marker and input casing do not change the sound lookup.");
            check(!SoundPalettePipeline.TryGetResource("FOX",out _) && !SoundPalettePipeline.TryGetResource(null,out _),
                "Unsupported words do not silently reuse another word's sound.");
            check(SoundPalettePipeline.AssetProvenance("SLEEP").Contains("Local synthesized") &&
                SoundPalettePipeline.AssetProvenance("CAT").Contains("reused locally"),
                "Local synthesis and reused SATIE assets have distinct provenance.");

            var go=new GameObject("Sound palette verification");
            try
            {
                var palette=go.AddComponent<SoundPalettePipeline>();
                check(!palette.StoryAmbienceEnabled && palette.WordGainMultiplier==1 && palette.AmbienceSource==null,
                    "Ambience defaults off and initialization creates no playing sound source.");
                palette.SetSoundProfile(SoundProfile.Gentle);
                check(Mathf.Abs(palette.WordGainMultiplier-.6f)<.0001f && palette.WordGain==palette.WordGainMultiplier,
                    "Gentle changes only the gain multiplier, retaining the same sound palette.");
                palette.StoryAmbienceEnabled=true;
                check(palette.AmbienceSource==null && !palette.IsAmbiencePlaying,
                    "Enabling the preference alone does not start ambience during questions or microphone input.");
                bool wordCalled=false;
                var pendingWord=palette.PrepareWord("CAT",clip=>wordCalled=true);
                check(pendingWord.MoveNext() && !wordCalled,"Word preparation exposes a cancellation boundary even for cached Unity assets.");
                palette.ResetSession();
                check(!pendingWord.MoveNext() && !wordCalled && palette.PreparedCount==0,
                    "Reset suppresses an old word callback and prevents its result entering the palette cache.");
                var context=new StoryContext();
                context.words.Add(new WordRecord {word="RAIN",worldPosition=Vector3.zero});
                bool storyCalled=false;
                var pendingStory=palette.PrepareStory(context,()=>storyCalled=true);
                check(pendingStory.MoveNext() && !storyCalled,"Story preparation starts asynchronously without starting playback.");
                palette.CancelPending();
                check(!pendingStory.MoveNext() && !storyCalled && !palette.IsAmbiencePlaying,
                    "Cancel suppresses stale story preparation and keeps optional playback stopped.");
                check(palette.Profile==SoundProfile.Gentle && palette.StoryAmbienceEnabled,
                    "Session cancellation retains the user's sound preferences.");
                palette.SetSoundProfile(SoundProfile.Standard);
                check(palette.WordGainMultiplier==1,"Standard restores the original cue gain.");

                // Seed a borrowed bundled clip so this synchronous editor check does
                // not need to wait for the editor's asynchronous loading scheduler.
                var cache=(Dictionary<string,AudioClip>)typeof(SoundPalettePipeline)
                    .GetField("clips",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(palette);
                cache["SatieAudio/rain"]=Resources.Load<AudioClip>("SatieAudio/rain");
                context.words[0].groupId="saved-rain";
                context.words[0].worldPosition=new Vector3(9,9,9);
                Vector3? registeredPosition=new Vector3(.2f,.8f,1.2f);
                palette.ResolveWorldPosition=id=>id=="saved-rain" ? registeredPosition : null;
                palette.SetSpatialReady(false);
                palette.StartStoryAmbience(context);
                check(palette.AmbienceSource!=null && Vector3.Distance(palette.AmbienceSource.transform.position,registeredPosition.Value)<.0001f &&
                    !palette.IsAmbiencePlaying,
                    "Ambience resolves a saved group's current pose and remains silent while spatial tracking is unavailable.");
                registeredPosition=new Vector3(-.6f,1.1f,2);
                palette.SetSpatialReady(false);
                check(Vector3.Distance(palette.AmbienceSource.transform.position,registeredPosition.Value)<.0001f && !palette.IsAmbiencePlaying,
                    "A changed calibration updates the ambience position while its tracking gate remains closed.");
                registeredPosition=new Vector3(float.NaN,0,0);
                palette.SetSpatialReady(true);
                check(!palette.IsAmbiencePlaying && Vector3.Distance(palette.AmbienceSource.transform.position,new Vector3(-.6f,1.1f,2))<.0001f,
                    "A non-finite resolved pose cannot resume or relocate spatial ambience.");
                palette.ResolveWorldPosition=id=>throw new InvalidOperationException("test resolver failure");
                palette.SetSpatialReady(true);
                check(!palette.IsAmbiencePlaying,"A failed position resolver keeps ambience silent.");
                palette.StopAllPlayback();
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }

            report.passed=true;
            Directory.CreateDirectory("Documentation");
            File.WriteAllText("Documentation/SOUND_PALETTE_TEST_RESULTS.json",JsonUtility.ToJson(report,true));
            Debug.Log("SOUND_PALETTE_TESTS_PASSED "+report.checks.Count);
        }
    }
}
