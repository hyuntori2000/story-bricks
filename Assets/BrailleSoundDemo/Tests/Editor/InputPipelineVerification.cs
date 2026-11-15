using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace BrailleSoundDemo.Tests
{
    /// <summary>Offline protocol/framing/random-placement checks. No microphone, API or audio playback.</summary>
    public static class InputPipelineVerification
    {
        [Serializable] sealed class Report
        {
            public bool passed;
            public int checks;
            public bool headsetTested = false;
            public bool networkTested = false;
            public string completedAt;
            public string error;
            public List<string> verified = new List<string>();
        }
        static Report report;
        public static void Run()
        {
            report = new Report();
            try
            {
                var source = new WordInputMessage { type = "complete", protocolVersion = 1, requestId = "request-1", sequence = 1,
                    sessionId = "session-1", frameRevision = 2, groupIndex = 0, groupId = "cat-group", word = "CAT",
                    finishMarker = false, componentIds = new[] { "brick-c", "brick-a", "brick-t" }, position = new Vector3(-1, .09f, .4f) };
                string json = JsonUtility.ToJson(source);
                Check(WordInputPipeline.TryDecode(json, out var parsed, out var error) && parsed.groupId == source.groupId && parsed.sequence == 1 && parsed.position == source.position, "Complete observation retains identity, sequence and local position.");
                json = "{\"type\":\"complete\",\"protocolVersion\":1,\"requestId\":\"request-1\",\"sequence\":1,\"sessionId\":\"session-1\",\"frameRevision\":2,\"groupIndex\":0,\"groupId\":\"cat-group\",\"word\":\"CAT\",\"finishMarker\":false,\"componentIds\":[\"brick-c\",\"brick-a\",\"brick-t\"],\"position\":{\"x\":-1.0,\"y\":0.09,\"z\":0.4}}";
                var invalid = new Dictionary<string, string> {
                    { "Missing position is rejected.", json.Substring(0, json.IndexOf(",\"position\"", StringComparison.Ordinal)) + "}" },
                    { "A missing position axis is rejected.", json.Replace("\"z\":0.4", "\"other\":0.4") },
                    { "Duplicate top-level fields are rejected.", json.Insert(1, "\"sequence\":9,") },
                    { "A floating sequence is rejected.", json.Replace("\"sequence\":1", "\"sequence\":1.5") },
                    { "An overflowing sequence is rejected.", json.Replace("\"sequence\":1", "\"sequence\":9223372036854775808") },
                    { "An overflowing frame revision is rejected.", json.Replace("\"frameRevision\":2", "\"frameRevision\":4294967298") },
                    { "A string protocolVersion is rejected.", json.Replace("\"protocolVersion\":1", "\"protocolVersion\":\"1\"") },
                    { "Numeric component IDs are rejected.", json.Replace("\"brick-c\"", "123") },
                    { "Nonstandard NaN numbers are rejected.", json.Replace("\"x\":-1.0", "\"x\":NaN") },
                    { "Trailing JSON content is rejected.", json + "{}" },
                    { "A literal control character in a string is rejected.", json.Replace("request-1", "request\n1") },
                    { "An oversized observation is rejected.", new string(' ', JsonLineFramer.MaximumBytes + 1) + json }
                };
                foreach (var item in invalid) Check(!WordInputPipeline.TryDecode(item.Value, out _, out _), item.Key);
                var framer = new JsonLineFramer(); var received = new List<string>();
                byte[] packet = Encoding.UTF8.GetBytes("{\"caption\":\"cat ☺\"}\n{\"type\":\"ack\"}\n");
                int split = 18;
                var first = new byte[split]; var second = new byte[packet.Length - split];
                Array.Copy(packet, first, first.Length); Array.Copy(packet, split, second, 0, second.Length);
                framer.Feed(first, first.Length, received.Add); framer.Feed(second, second.Length, received.Add);
                Check(received.Count == 2 && received[0] == "{\"caption\":\"cat ☺\"}" && received[1] == "{\"type\":\"ack\"}", "Fragmented UTF-8 and coalesced lines preserve messages.");
                bool oversized = false; try { new JsonLineFramer().Feed(new byte[JsonLineFramer.MaximumBytes + 1], JsonLineFramer.MaximumBytes + 1, _ => { }); } catch (InvalidDataException) { oversized = true; }
                Check(oversized, "An unterminated oversized wire frame is rejected before growing indefinitely.");
                bool newline = false; try { JsonLineFramer.Encode("{}\n{}"); } catch (ArgumentException) { newline = true; }
                Check(newline, "Outgoing payload cannot inject an extra wire frame.");
                var savedState = UnityEngine.Random.state;
                bool bounded = true, separated = true, extents = true; var varied = new HashSet<string>();
                try
                {
                    UnityEngine.Random.InitState(28771);
                    var lengths = new[] { 3, 4, 6 };
                    for (int sample = 0; sample < 500; sample++)
                    {
                        var centers = MockLetterLayout.GenerateRandomGroupPositions();
                        varied.Add(centers[0].ToString("F2"));
                        for (int i = 0; i < centers.Length; i++)
                        {
                            float halfWidth = (lengths[i] - 1) * .29f * .5f + .125f;
                            bounded &= float.IsFinite(centers[i].x) && centers[i].y == .09f && centers[i].z >= -1.04f && centers[i].z <= .92f;
                            extents &= Mathf.Abs(centers[i].x) + halfWidth <= 2.2f;
                            for (int j = 0; j < i; j++)
                            {
                                float otherWidth = (lengths[j] - 1) * .29f * .5f + .125f;
                                separated &= Vector3.Distance(centers[i], centers[j]) >= 1.25f &&
                                    (Mathf.Abs(centers[i].x - centers[j].x) >= halfWidth + otherWidth + .12f || Mathf.Abs(centers[i].z - centers[j].z) >= .44f);
                            }
                        }
                    }
                }
                finally { UnityEngine.Random.state = savedState; }
                Check(bounded, "500 random layouts remain in the usable board-local depth/height range.");
                Check(extents, "Every word's full letter width stays inside the tabletop.");
                Check(separated, "500 random layouts keep word bounds separate and centers at least 1.25 metres apart.");
                Check(varied.Count > 10, "Repeated resets vary completed-word positions.");
                report.passed = true;
                Debug.Log("WORD_INPUT_PIPELINE_VERIFY_OK " + report.checks + " checks; no headset, network, microphone or API used.");
            }
            catch (Exception e) { report.error = e.ToString(); throw; }
            finally
            {
                report.completedAt = DateTime.UtcNow.ToString("o");
                Directory.CreateDirectory("Documentation");
                File.WriteAllText("Documentation/INPUT_PIPELINE_VERIFICATION.json", JsonUtility.ToJson(report, true));
            }
        }
        static void Check(bool passed, string detail)
        {
            report.checks++; if (!passed) throw new InvalidOperationException(detail);
            report.verified.Add(detail);
        }
    }
}
