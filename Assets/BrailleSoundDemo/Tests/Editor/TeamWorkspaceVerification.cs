using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BrailleSoundDemo.Presentation;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    /// <summary>Offline boundaries/protocol checks. Does not claim headset, audible or video-codec verification.</summary>
    public static class TeamWorkspaceVerification
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly List<string> checks = new List<string>();
        [Serializable] sealed class Report
        {
            public bool passed, simulated = true;
            public bool microphoneUsed, apiUsed, headsetTested, audiblePlaybackTested, fullConversationTested;
            public List<string> checks;
        }
        sealed class OfflineProvider : IStoryProvider
        {
            public int stories;
            public IEnumerator Question(StoryContext context, Action<StoryReply> completed)
            { completed(new StoryReply { text = "Offline question", audioResource = "StoryAudio/question_cat" }); yield break; }
            public IEnumerator Story(StoryContext context, Action<StoryReply> completed)
            { stories++; completed(new StoryReply { text = "Offline story", audioResource = "StoryAudio/story" }); yield break; }
        }

        public static void Run()
        {
            checks.Clear();
            CheckFourWordAcceptance();
            CheckCueProtocol();
            CheckLocalTargetRouting();
            CheckResetCancellation();
            Directory.CreateDirectory("work/team-workspaces");
            File.WriteAllText("work/team-workspaces/verification.json", JsonUtility.ToJson(new Report { passed = true, checks = new List<string>(checks) }, true));
            Debug.Log("TEAM_WORKSPACE_VERIFICATION_PASSED " + checks.Count);
        }

        static void CheckFourWordAcceptance()
        {
            var root = new GameObject("Offline four-word contract verification");
            var provider = new OfflineProvider();
            var flow = root.AddComponent<ConversationFlow>();
            try
            {
                flow.TestFastMode = true;
                flow.SetProviderForTesting(provider);
                Require(flow.StoryWordLimit == 3, "The legacy CAT/RAIN/SLEEP fixture remains a three-word flow.");
                flow.ConfigurePhysicalWords(true); flow.SetSpatialReady(true);
                Require(flow.StoryWordLimit == 4, "Physical workspaces require four words.");
                var words = new[] { "JUDY", "FOX", "RAIN", "TREE" };
                for (int index = 0; index < words.Length; index++)
                {
                    if (index > 0)
                    {
                        // Seed the preceding saved turn explicitly. These are gate/acceptance
                        // checks, not a simulated microphone or a full conversation playback.
                        var previous = flow.Record.words[index - 1];
                        flow.Record.turns.Add(new ConversationTurn { wordIndex = previous.index, groupId = previous.groupId,
                            word = previous.word, componentIds = previous.componentIds, question = "Offline question", answer = "Offline saved answer" });
                        Method(typeof(ConversationFlow), "SetState").Invoke(flow, new object[] { ConversationState.Silent, "Offline saved answer" });
                    }
                    string group = "offline-group-" + index;
                    var ids = new string[words[index].Length];
                    for (int c = 0; c < ids.Length; c++) ids[c] = group + "-letter-" + c;
                    Require(flow.CanComplete(index), "Accepted-answer history opens physical round " + (index + 1) + ".");
                    if (index == 2)
                        Require(!flow.BeginWord(index, group, words[index], true, Vector3.zero, ids) && flow.CanComplete(index),
                            "The third word cannot be marked final, and rejection restores its input gate.");
                    if (index == 3)
                        Require(!flow.BeginWord(index, group, words[index], false, Vector3.zero, ids) && flow.CanComplete(index),
                            "The fourth word requires the final-round marker.");
                    Require(flow.BeginWord(index, group, words[index], index == 3, Vector3.zero, ids),
                        "Physical round " + (index + 1) + " accepts its own word and component identities.");
                    flow.StopAllCoroutines();
                }
                Require(flow.Record.words.Count == 4 && !flow.Record.words[2].finishMarker && flow.Record.words[3].finishMarker,
                    "JUDY/FOX/RAIN/TREE retain four records, with only TREE final.");
                Require(provider.stories == 0 && flow.Record.turns.Count == 3 && flow.LastStoryOutput == null,
                    "Accepting the fourth word does not generate a story before its answer.");
                Require(!flow.CanComplete(4), "A fifth captured anchor cannot overflow the current conversation's four rounds.");
                Require(root.GetComponent<MicrophoneAnswerInput>() == null && !flow.IsLive,
                    "Acceptance verification never constructs a microphone or a live API provider.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        static void CheckCueProtocol()
        {
            const string json = "{\"schemaVersion\":1,\"revision\":2,\"assets\":[],\"cues\":[" +
                "{\"id\":\"q1-voice\",\"cueKey\":\"fox-q1\",\"event\":\"question.started\",\"enabled\":true}," +
                "{\"id\":\"q1-image\",\"cueKey\":\"fox-q1\",\"event\":\"question.started\",\"enabled\":true}," +
                "{\"id\":\"q2-voice\",\"cueKey\":\"fox-q2\",\"event\":\"question.started\",\"enabled\":true}," +
                "{\"id\":\"automatic\",\"cueKey\":\"\",\"event\":\"word.accepted\",\"enabled\":true}]}";
            var manifest = JsonUtility.FromJson<DesignerManifest>(json);
            Require(manifest.cues[0].@event == "question.started" && manifest.cues[1].cueKey == "fox-q1",
                "Dashboard JSON event and cueKey fields survive Unity deserialization.");
            Require(string.Join(",", Select(manifest, "fox-q1")) == "q1-voice,q1-image",
                "A manual key selects the authored question.started voice/image group without starting the second FOX question.");
            Require(string.Join(",", Select(manifest, "", "word.accepted")) == "automatic" && Select(manifest, "", "question.started").Count == 0,
                "Automatic events cannot start keyed filming steps.");
            Require(string.Join(",", Select(manifest, "q2-voice")) == "q2-voice",
                "An explicit asset cue ID still previews one member of a scripted group.");
        }
        static List<string> Select(DesignerManifest manifest, string key, string eventName = "manual")
        {
            var selected = new List<string>();
            var matcher = typeof(WordMediaPresenter).GetMethod("MatchesTrigger", BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var cue in manifest.cues)
                if ((bool)matcher.Invoke(null, new object[] { eventName, key, cue.@event, cue.cueKey, cue.id })) selected.Add(cue.id);
            return selected;
        }

        static void CheckLocalTargetRouting()
        {
            var templateRoot = new GameObject("Shared local media template");
            var boxRoot = new GameObject("Local cue box anchor");
            var stageRoot = new GameObject("Local cue stage anchor");
            try
            {
                var template = templateRoot.AddComponent<WordMediaPresenter>();
                template.UseDashboardCatalog = false;
                template.LocalBindings = new[] {
                    new LocalMediaBinding { id = "box-image", wordFilter = "FOX" },
                    new LocalMediaBinding { id = "stage-image", target = "stage", wordFilter = "FOX" }
                };
                var box = WordMediaPresenter.Attach(template, boxRoot.transform, "routing", "fox", 0, "FOX", "judy", true);
                var stage = WordMediaPresenter.Attach(template, stageRoot.transform, "routing", "", -1, "", "judy", true, "stage");
                var context = new PresentationEventContext { eventName = "word.accepted", word = "FOX" };
                var matcher = Method(typeof(WordMediaPresenter), "MatchesLocalBinding");
                Func<WordMediaPresenter, LocalMediaBinding, bool> matches = (presenter, binding) =>
                    (bool)matcher.Invoke(presenter, new object[] { binding, context });
                Require(matches(box, box.LocalBindings[0]) && !matches(stage, stage.LocalBindings[0]),
                    "A shared presenter template routes its default local cue to the box only, avoiding duplicate stage playback.");
                Require(!matches(box, box.LocalBindings[1]) && matches(stage, stage.LocalBindings[1]),
                    "An explicit stage local cue does not also play on a word box.");
                var legacy = JsonUtility.FromJson<LocalMediaBinding>("{\"id\":\"legacy\",\"trigger\":1,\"enabled\":true,\"wordFilter\":\"FOX\"}");
                Require(matches(box, legacy) && !matches(stage, legacy),
                    "Existing serialized local bindings without target keep box-only behavior.");
                legacy.target = "";
                Require(matches(box, legacy) && !matches(stage, legacy),
                    "An empty legacy target also stays box-only.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(boxRoot); UnityEngine.Object.DestroyImmediate(stageRoot);
                UnityEngine.Object.DestroyImmediate(templateRoot);
            }
        }

        static void CheckResetCancellation()
        {
            var root = new GameObject("Offline media cancellation verification");
            var other = new GameObject("Other workspace");
            try
            {
                var presenter = WordMediaPresenter.Attach(null, root.transform, "judy-session", "box", 0, "FOX", "judy", true);
                var independent = WordMediaPresenter.Attach(null, other.transform, "demo-session", "other", 0, "TREE", "demo", false);
                presenter.UseDashboardCatalog = independent.UseDashboardCatalog = false;
                // Ordinary MonoBehaviours need not receive lifecycle messages in
                // Edit mode. Exercise the real subscription seam explicitly once.
                foreach (var component in new[] { presenter, independent })
                {
                    Method(typeof(WordMediaPresenter), "OnDisable").Invoke(component, null);
                    Method(typeof(WordMediaPresenter), "OnEnable").Invoke(component, null);
                }
                var cueType = typeof(WordMediaPresenter).GetNestedType("Cue", BindingFlags.NonPublic);
                var cue = Activator.CreateInstance(cueType);
                cueType.GetField("due").SetValue(cue, Time.realtimeSinceStartupAsDouble + 600);
                cueType.GetField("action").SetValue(cue, "image");
                int generation = (int)typeof(WordMediaPresenter).GetField("generation", Private).GetValue(presenter);
                var pending = (IEnumerator)Method(typeof(WordMediaPresenter), "PlayAt").Invoke(presenter,
                    new[] { cue, new PresentationEventContext { sessionId = "judy-session", workspaceId = "judy" }, (object)generation });
                Require(pending.MoveNext(), "A delayed media cue waits without starting any download or playback.");
                PresentationEvents.ResetSession("judy-session");
                Require(!pending.MoveNext() && presenter.SessionId == null,
                    "Session reset invalidates an already scheduled cue before it can load or create media.");
                Require(independent.SessionId == "demo-session", "Resetting Judy presentation does not cancel the Demo workspace session.");
            }
            finally
            {
                foreach (var owner in new[] { root, other })
                    foreach (var component in owner.GetComponentsInChildren<WordMediaPresenter>(true))
                        Method(typeof(WordMediaPresenter), "OnDisable").Invoke(component, null);
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(other);
            }
        }
        static MethodInfo Method(Type type, string name) => type.GetMethod(name, Private);
        static void Require(bool ok, string detail)
        { if (!ok) throw new InvalidOperationException("Team workspaces: " + detail); checks.Add(detail); }
    }
}
