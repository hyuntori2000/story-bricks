using System;
using System.Collections.Generic;
using System.IO;
using BrailleSoundDemo.CameraTest;
using BrailleSoundDemo.PhysicalStory;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    /// <summary>Offline geometry/identity checks; synthetic faces are not physical recognition ground truth.</summary>
    public static class PhysicalWordAnchorVerification
    {
        [Serializable] sealed class Envelope { public QuestFrameCapture.CaptureMetadata metadata; }
        [Serializable] sealed class Report
        {
            public bool passed;
            public bool usesRecordedCameraMetadata = true;
            public bool physicalAlignmentVerified = false;
            public List<string> checks = new List<string>();
        }
        static QuestFrameCapture.CaptureMetadata Fixture()
        {
            return new QuestFrameCapture.CaptureMetadata {
                sessionId="physical-fixture", frameId="fixture-frame", frameRevision=7,
                captureTimestampUtc="2026-11-14T20:20:26.0474660Z", imageWidth=640, imageHeight=480,
                cameraPositionWorld=new Vector3(0,1.75f,0), cameraRotationWorld=Quaternion.Euler(90,0,0),
                intrinsics=new QuestFrameCapture.IntrinsicsMetadata {
                    focalLength=new Vector2(320,320), principalPoint=new Vector2(320,240),
                    sensorResolution=new QuestFrameCapture.PixelSize { x=640,y=480 },
                    sensorCrop=new QuestFrameCapture.SensorCropMetadata { x=0,y=0,width=640,height=480 }
                },
                board=new QuestFrameCapture.BoardMetadata {
                    position=new Vector3(0,.75f,0), rotation=Quaternion.identity, width=.6f,height=.4f,blockHeight=0
                },
                recognitionRegion=new QuestFrameCapture.RecognitionRegionMetadata { x=160,y=120,width=320,height=240 }
            };
        }
        static PhysicalColorDetection Face(string letter,float x,float y,float width=40,float height=40,string kind=null)
        {
            return new PhysicalColorDetection { letter=letter,kind=kind,accepted=true,quad=new[] {
                new PhysicalPixelPoint {x=x,y=y}, new PhysicalPixelPoint {x=x+width,y=y},
                new PhysicalPixelPoint {x=x+width,y=y+height}, new PhysicalPixelPoint {x=x,y=y+height}
            }};
        }
        static PhysicalColorResult Result(QuestFrameCapture.CaptureMetadata m,params PhysicalColorDetection[] faces)
        {
            return new PhysicalColorResult { sessionId=m.sessionId,frameId=m.frameId,
                captureTimestampUtc=m.captureTimestampUtc,imageWidth=m.imageWidth,imageHeight=m.imageHeight,
                coordinateSystem="image_pixels_top_left",status="recognized",ready=true,analysisRevision=1,detections=faces };
        }
        static bool Build(QuestFrameCapture.CaptureMetadata m,PhysicalColorResult r,out PhysicalWordGeometry g)
        { return PhysicalWordAnchorProjection.TryCreate(m,r,m.frameRevision,out g,out _); }
        static bool Reject(Action<QuestFrameCapture.CaptureMetadata,PhysicalColorResult> mutate)
        {
            var m=Fixture(); var r=Result(m,Face("F",240,180)); mutate(m,r);
            return !Build(m,r,out var g) && g==null;
        }
        static Vector3 CornerCenter(PhysicalWordGeometry g)
        { return (g.Corners[0]+g.Corners[1]+g.Corners[2]+g.Corners[3])*.25f; }

        public static void Run()
        {
            var report=new Report();
            Action<bool,string> check=(ok,message)=>{
                if(!ok) throw new InvalidOperationException("Physical word anchor: "+message);
                report.checks.Add(message);
            };
            var acceptedWords=new List<WordRecord>{new WordRecord{word="FOX"},new WordRecord{word="JUDY"}};
            check(PhysicalWordInputPolicy.IsRepeatedWord("fox",acceptedWords) &&
                PhysicalWordInputPolicy.IsRepeatedWord(" JUDY ",acceptedWords),
                "Duplicate words are detected across frames and positions using the current story, ignoring case and edge spaces.");
            check(!PhysicalWordInputPolicy.IsRepeatedWord("OFX",acceptedWords) &&
                !PhysicalWordInputPolicy.IsRepeatedWord("FOXY",acceptedWords) &&
                !PhysicalWordInputPolicy.IsRepeatedWord("ZORP",acceptedWords),
                "New names and invented words stay eligible; no dictionary or fuzzy autocorrection rejects them.");
            check(!PhysicalWordInputPolicy.IsRepeatedWord("FOX",new List<WordRecord>()),
                "A reset story may use the same word again; duplicate history is session-local.");
            check(PhysicalWordInputPolicy.IsRecognizedWord("TREE.") &&
                PhysicalWordInputPolicy.IsRecognizedWord("JUDY") &&
                PhysicalWordInputPolicy.IsRecognizedWord("A"+new string('.',31)),
                "The shared conversation policy preserves letters, periods and the 32-symbol limit.");
            check(!PhysicalWordInputPolicy.IsRecognizedWord(".") &&
                !PhysicalWordInputPolicy.IsRecognizedWord("TREE#") &&
                !PhysicalWordInputPolicy.IsRecognizedWord("A1") &&
                !PhysicalWordInputPolicy.IsRecognizedWord("A<CAP>") &&
                !PhysicalWordInputPolicy.IsRecognizedWord("A<GRADE1>") &&
                !PhysicalWordInputPolicy.IsRecognizedWord("A B") &&
                !PhysicalWordInputPolicy.IsRecognizedWord("A"+new string('.',32)),
                "Punctuation alone, digits, indicators, spaces and oversized text cannot enter the story.");
            var m=Fixture(); var r=Result(m,Face("F",240,180));
            check(Build(m,r,out var one) && one.Word=="F" &&
                Vector3.Distance(one.CenterWorld,new Vector3(-.1875f,.75f,.125f))<.00001f &&
                Vector2.Distance(one.Size,new Vector2(.135f,.135f))<.00001f,
                "One face has the independently calculated metric center and padded footprint.");
            check(Build(m,Result(m,Face("F",240,180),Face("X",320,180)),out var two) && two.Word=="FX" &&
                Vector3.Distance(two.CenterWorld,new Vector3(-.0625f,.75f,.125f))<.00001f &&
                Vector2.Distance(two.Size,new Vector2(.385f,.135f))<.00001f,
                "Two faces form one word with a common footprint rather than separate drifting anchors.");
            check(Vector3.Distance(CornerCenter(two),two.CenterWorld)<.00001f &&
                Array.TrueForAll(two.Corners,p=>Mathf.Abs(p.y-.75f)<.00001f),
                "The sound/image center is exactly the footprint center on the captured tabletop plane.");
            check(Build(m,Result(m,Face("X",320,180),Face("F",240,180)),out var reversed) && reversed.Word=="XF" &&
                Vector3.Distance(reversed.CenterWorld,two.CenterWorld)<.00001f,
                "Selection order is preserved without altering physical geometry or autocorrecting letters.");
            check(Build(m,Result(m,Face("O",240,180),Face("F",300,180),Face("X",360,180)),out var ofx) && ofx.Word=="OFX",
                "OFX stays OFX; no dictionary rearrangement silently produces FOX.");
            var punctuated=Result(m,Face("T",200,180),Face("R",250,180),Face("E",300,180),
                Face("E",350,180),Face(".",400,180,kind:"punctuation"));
            string punctuatedJson=JsonUtility.ToJson(punctuated);
            punctuatedJson="{\"algorithm_id\":\"six_dot_radial_v1\",\"decoder_id\":\"lego_english_a16_punctuation_v2\","+
                punctuatedJson.Substring(1);
            var decoded=JsonUtility.FromJson<PhysicalColorResult>(punctuatedJson);
            check(Build(m,decoded,out var treePeriod) && treePeriod.Word=="TREE." &&
                treePeriod.ComponentIds.Length==5 &&
                treePeriod.ComponentIds[4]=="capture-"+m.frameId+"-letter-4" &&
                Vector3.Distance(treePeriod.CenterWorld,new Vector3(0,.75f,.125f))<.00001f &&
                Vector2.Distance(treePeriod.Size,new Vector2(.76f,.135f))<.00001f,
                "JSON provenance fields are harmless; TREE. retains all five block identities and includes the period in its footprint.");
            check(Build(m,Result(m,Face("A",240,180),Face(".",320,180,kind:"punctuation")),out var punctuation) &&
                punctuation.Word=="A." && punctuation.ComponentIds.Length==2,
                "An explicitly accepted period remains part of the physical word with its own block identity.");
            foreach(char symbol in "?!',-")
                check(!PhysicalWordInputPolicy.IsRecognizedWord("A"+symbol) &&
                    !Build(m,Result(m,Face("A",240,180),Face(symbol.ToString(),320,180,kind:"punctuation")),out var unsupported) &&
                    unsupported==null,
                    "Unsupported punctuation '"+symbol+"' rejects the entire observation instead of being silently removed.");
            var unknown=Face("?",320,180,kind:"uncertain");
            check(!Build(m,Result(m,Face("A",240,180),unknown),out _) &&
                !Build(m,Result(m,Face("A",240,180),Face("?",320,180)),out _),
                "An uncertain or untyped question mark remains rejected.");
            var rejectedPeriod=Face(".",320,180,kind:"punctuation");rejectedPeriod.accepted=false;
            check(!Build(m,Result(m,Face("A",240,180),rejectedPeriod),out _) &&
                !Build(m,Result(m,Face("A",240,180),Face(".",320,180)),out _) &&
                !Build(m,Result(m,Face(".",240,180,kind:"punctuation")),out _),
                "A period requires explicit punctuation kind, accepted=true and at least one alphabetic block in the word.");
            check(!Build(m,Result(m,Face("A",240,180),Face("#",320,180,kind:"indicator")),out _) &&
                !Build(m,Result(m,Face("A",240,180),Face("1",320,180,kind:"number")),out _) &&
                !Build(m,Result(m,Face("A",240,180,kind:"indicator")),out _) &&
                !Build(m,Result(m,Face("A",240,180),Face(".",320,180,kind:"letter")),out _),
                "Digits, indicators and mismatched kinds cannot create a physical word or a finish marker.");
            check(Build(m,Result(m,Face("F",240,180),Face("F",320,180)),out var repeated) && repeated.Word=="FF" &&
                repeated.ComponentIds[0]!=repeated.ComponentIds[1],"Repeated letters at distinct physical faces are allowed with distinct component identities.");
            check(!Build(m,Result(m,Face("F",240,180),Face("X",240,180)),out _),"A duplicated physical face cannot produce two letters.");
            var duplicate=Face("F",240,180); Array.Reverse(duplicate.quad);
            check(!Build(m,Result(m,Face("F",240,180),duplicate),out _),"Duplicate faces are rejected even when corner order is reversed.");
            check(Build(m,r,out var same) && same.GroupId==one.GroupId && same.ComponentIds[0]==one.ComponentIds[0],
                "The same frozen frame produces stable group and component identifiers.");
            m.frameId="another-frame";
            check(Build(m,Result(m,Face("F",240,180)),out var another) && another.GroupId!=one.GroupId,
                "A new frame has a different group identity.");
            r.detections[0].letter="Z"; r.detections[0].quad[0].x=0;
            check(one.Word=="F" && Vector3.Distance(one.CenterWorld,new Vector3(-.1875f,.75f,.125f))<.00001f,
                "Returned geometry is a snapshot and does not alias mutable recognition input.");

            m=Fixture(); var turn=Quaternion.Euler(0,35,0); var shift=new Vector3(2,.1f,-3);
            m.cameraPositionWorld=turn*m.cameraPositionWorld+shift; m.cameraRotationWorld=turn*m.cameraRotationWorld;
            m.board.position=turn*m.board.position+shift; m.board.rotation=turn;
            check(Build(m,Result(m,Face("F",240,180)),out var moved) &&
                Vector3.Distance(moved.CenterWorld,turn*one.CenterWorld+shift)<.00001f,
                "World translation and rotation use the frozen capture pose, not the current headset pose.");
            m.board.rotation=new Quaternion(turn.x*1.005f,turn.y*1.005f,turn.z*1.005f,turn.w*1.005f);
            check(Build(m,Result(m,Face("F",240,180)),out var normalized) &&
                Vector3.Distance(normalized.CenterWorld,moved.CenterWorld)<.00001f &&
                Mathf.Abs(Quaternion.Dot(normalized.Rotation,normalized.Rotation)-1)<.00001f,
                "Accepted near-unit board rotation is normalized consistently with camera projection.");

            check(Reject((a,b)=>b.sessionId="stale-session") && Reject((a,b)=>b.frameId="stale-frame"),"Session and frame mismatches fail closed.");
            check(Reject((a,b)=>b.captureTimestampUtc="2026-11-14T20:20:26.0474661Z") &&
                Reject((a,b)=>{a.captureTimestampUtc="";b.captureTimestampUtc="";}),"Capture timestamps must be present and match exactly, including the seventh fractional digit.");
            m=Fixture();
            check(!PhysicalWordAnchorProjection.TryCreate(m,Result(m,Face("F",240,180)),8,out _,out _) &&
                Reject((a,b)=>a.frameRevision=-1),"Stale or negative reference revisions cannot create a world anchor.");
            check(Reject((a,b)=>b.imageWidth++) && Reject((a,b)=>b.coordinateSystem="image_pixels_bottom_left") &&
                Reject((a,b)=>{a.imageWidth=4097;b.imageWidth=4097;}),"Image dimensions and full-image top-left coordinate convention are checked.");
            check(Reject((a,b)=>{a.sessionId="../../escape";b.sessionId=a.sessionId;}) &&
                Reject((a,b)=>{a.frameId=new string('x',65);b.frameId=a.frameId;}),"Unsafe or oversized frame identifiers are rejected.");
            check(Reject((a,b)=>b.ready=false) && Reject((a,b)=>b.status="needs_review") && Reject((a,b)=>b.analysisRevision=0),
                "Ready alone is insufficient: recognized status and a completed analysis revision are required.");
            check(Reject((a,b)=>b.detections[0].accepted=false) && Reject((a,b)=>b.detections[0].letter="?") &&
                Reject((a,b)=>b.detections[0].letter="f") && Reject((a,b)=>b.detections[0].letter="FX"),
                "Unaccepted, unknown, lowercase, and multi-character detections do not become words.");
            check(Reject((a,b)=>b.detections=null) && Reject((a,b)=>b.detections=Array.Empty<PhysicalColorDetection>()) &&
                Reject((a,b)=>b.detections=new PhysicalColorDetection[33]) && Reject((a,b)=>b.detections[0]=null),
                "Missing, empty, excessive, and partial detection arrays are rejected.");
            check(Reject((a,b)=>b.detections[0].quad=null) && Reject((a,b)=>b.detections[0].quad[0]=null) &&
                Reject((a,b)=>b.detections[0].quad[0].x=float.NaN) && Reject((a,b)=>b.detections[0].quad[0].y=float.PositiveInfinity),
                "Incomplete and non-finite face coordinates fail closed.");
            check(Reject((a,b)=>{var q=b.detections[0].quad;var p=q[1];q[1]=q[2];q[2]=p;}) &&
                Reject((a,b)=>{b.detections[0].quad[2].x=250;b.detections[0].quad[2].y=190;}) &&
                Reject((a,b)=>b.detections[0].quad[1]=b.detections[0].quad[0]),
                "Self-crossing, concave, and zero-area quadrilaterals are rejected.");
            check(Reject((a,b)=>b.detections[0]=Face("F",150,180)) && Reject((a,b)=>a.recognitionRegion.source="other") &&
                Reject((a,b)=>a.recognitionRegion.pixelOrigin="bottom-left"),"Faces outside the saved hand ROI or unsupported ROI conventions are rejected.");
            check(Reject((a,b)=>a.recognitionRegion.width=0) && Reject((a,b)=>a.recognitionRegion.x=-1) &&
                Reject((a,b)=>a.recognitionRegion.width=int.MaxValue),"Empty, negative, and overflow-sized ROI bounds are rejected.");
            check(Reject((a,b)=>a.cameraRotationWorld=new Quaternion()) && Reject((a,b)=>a.board.rotation=new Quaternion()) &&
                Reject((a,b)=>a.intrinsics.focalLength=Vector2.zero) && Reject((a,b)=>a.intrinsics.sensorCrop.width=1000),
                "Invalid camera/table orientation or sensor calibration cannot create an anchor.");

            const string fixture="Documentation/DeviceTests/table-depth/frame-8c7246df95fe45b29997e93a6054587e.json";
            var recorded=JsonUtility.FromJson<Envelope>(File.ReadAllText(fixture)).metadata;
            check(recorded.imageWidth==1280 && recorded.imageHeight==960 && recorded.intrinsics.sensorResolution.x==1728,
                "Saved Quest frame retains its 1728-pixel sensor calibration for the 1280-by-960 image.");
            check(Build(recorded,Result(recorded,Face("F",500,600,80,110)),out var capturedOne) &&
                Build(recorded,Result(recorded,Face("F",500,600,80,110),Face("X",620,620,80,110)),out var capturedTwo) &&
                capturedOne.Word=="F" && capturedTwo.Word=="FX" &&
                Mathf.Abs(capturedTwo.CenterWorld.y-recorded.board.position.y)<.00001f &&
                Vector3.Distance(CornerCenter(capturedTwo),capturedTwo.CenterWorld)<.00001f,
                "Synthetic faces inside the real captured ROI produce one- and two-letter footprints on its frozen measured plane.");
            m=Fixture(); r=Result(m,Face("R",240,180),Face("A",300,180));
            r.automaticBlockDetection=true; r.groupCenterPixel=new PhysicalPixelPoint {x=300,y=220};
            check(Build(m,r,out var centered) && centered.Word=="RA" &&
                Vector3.Distance(centered.CenterWorld,new Vector3(-.0625f,.75f,.0625f))<.00001f &&
                centered.Size==PhysicalWordAnchorProjection.DefaultCenterBoxSize &&
                Vector3.Distance(CornerCenter(centered),centered.CenterWorld)<.00001f,
                "Automatic input anchors a fixed 20-by-12 cm box at the captured block center without manual corners.");
            check(Reject((a,b)=>{b.automaticBlockDetection=true;b.groupCenterPixel=null;}) &&
                Reject((a,b)=>{b.automaticBlockDetection=true;b.groupCenterPixel=new PhysicalPixelPoint {x=0,y=0};}) &&
                Reject((a,b)=>{b.automaticBlockDetection=true;b.groupCenterPixel=new PhysicalPixelPoint {x=float.NaN,y=200};}),
                "Missing, nonfinite or out-of-capture automatic centers cannot create a sound anchor.");
            report.passed=true;
            Directory.CreateDirectory("Documentation");
            File.WriteAllText("Documentation/PHYSICAL_WORD_ANCHOR_TEST_RESULTS.json",JsonUtility.ToJson(report,true));
            Debug.Log("PHYSICAL_WORD_ANCHOR_TESTS_PASSED "+report.checks.Count);
        }
    }
}
