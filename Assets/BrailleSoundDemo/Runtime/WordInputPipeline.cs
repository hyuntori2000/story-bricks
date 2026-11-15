using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace BrailleSoundDemo
{
    public enum WordInputMode { Random, Python }

    [Serializable] public sealed class WordInputMessage
    {
        public string type;
        public int protocolVersion;
        public string requestId;
        public long sequence;
        public string sessionId;
        public int frameRevision = -1;
        public int groupIndex = -1;
        public string groupId;
        public string word;
        public bool finishMarker;
        public string[] componentIds;
        public Vector3 position;
    }
    [Serializable] public sealed class WordInputAck
    {
        public string type = "ack";
        public string requestId;
        public bool accepted;
        public string code;
        public string detail;
    }
    [Serializable] public sealed class WordInputStatus
    {
        public string type = "status";
        public int protocolVersion = 1;
        public string sessionId;
        public int frameRevision;
        public string inputMode;
        public bool spatialReady;
        public bool audioReady;
        public string state;
        public int nextIndex;
        public long lastSequence;
        public string activeGroupId;
    }

    /// <summary>One validated observation boundary for local random placement and Python recognition.</summary>
    public sealed class WordInputPipeline : MonoBehaviour
    {
        public WordInputMode Mode { get; private set; } = WordInputMode.Random;
        public bool PythonMode => Mode == WordInputMode.Python;
        public PythonWordBridge Bridge { get; private set; }
        public string LastError { get; private set; } = "";
        public long LastSequence => lastSequence;
        public bool AudioReady => demo != null && demo.Tracking.IsSpatialReady && (!PythonMode ||
            (Bridge != null && Bridge.Connected && (string.IsNullOrEmpty(demo.Flow.ActiveGroupId) ||
                (freshRemotePose && freshFrame == demo.Tracking.FrameRevision && sessionId == demo.Flow.Record.sessionId))));

        readonly string[] fixtureWords = { "CAT", "RAIN", "SLEEP" };
        readonly Dictionary<string, CachedAck> cache = new Dictionary<string, CachedAck>();
        readonly Queue<string> cacheOrder = new Queue<string>();
        DemoExperience demo;
        string sessionId;
        long lastSequence, localSequence;
        bool freshRemotePose;
        int freshFrame = -1;
        float nextStatusTime;
        sealed class CachedAck { public string fingerprint, json; }

        public void Initialize(DemoExperience experience)
        {
            if (demo != null) return;
            demo = experience ?? throw new ArgumentNullException(nameof(experience));
            Bridge = gameObject.AddComponent<PythonWordBridge>();
            Bridge.ConnectionChanged += OnConnection;
            Bridge.MessageReceived += OnMessage;
            demo.Flow.Changed += OnFlowChanged;
            demo.Tracking.BoardFrameChanged += OnFrameChanged;
            ResetSession();
        }

        public void SetPythonMode(bool enabled)
        {
            if (demo == null || PythonMode == enabled) return;
            Mode = enabled ? WordInputMode.Python : WordInputMode.Random;
            freshRemotePose = false; freshFrame = -1;
            demo.ResetDemo();
            ResetSession();
            Bridge.SetEnabled(enabled);
            demo.Flow.SetSpatialReady(AudioReady);
            SendStatus();
        }

        public void ResetSession()
        {
            if (demo == null || demo.Flow.Record == null) return;
            string current = demo.Flow.Record.sessionId;
            if (sessionId == current) return;
            sessionId = current; lastSequence = 0; localSequence = 0;
            freshRemotePose = false; freshFrame = -1; LastError = "";
            cache.Clear(); cacheOrder.Clear();
        }

        public bool SubmitLocal(RecognizedWordObservation observation)
        {
            ResetSession();
            if (demo == null || PythonMode || observation == null) return false;
            var message = new WordInputMessage {
                type = "complete", protocolVersion = 1, requestId = "local-" + (++localSequence), sequence = localSequence,
                sessionId = sessionId, frameRevision = demo.Tracking.FrameRevision,
                groupIndex = observation.groupIndex, groupId = observation.groupId, word = observation.word,
                finishMarker = observation.isFinish, componentIds = observation.componentIds,
                position = demo.DetectionOrigin.InverseTransformPoint(observation.worldPosition)
            };
            var error = ValidateCommon(message) ?? ValidateComplete(message);
            if (error != null) { Reject(message.requestId, "invalid", error); return false; }
            return ApplyComplete(message);
        }

        /// <summary>Unity main-thread entry point. Returns one JSON acknowledgment; transport sends it separately.</summary>
        public string HandleJson(string json)
        {
            ResetSession();
            if (!TryDecode(json, out var message, out string decodeError)) return Reject("", "invalid_json", decodeError);
            return HandleMessage(message);
        }

        public static bool TryDecode(string json, out WordInputMessage message, out string error)
        {
            message = null; error = "";
            try
            {
                if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > JsonLineFramer.MaximumBytes)
                    throw new ArgumentException("Expected a JSON object of at most 16 KB.");
                var shape = InputJsonShape.Parse(json);
                shape.RequireObjectFields("type", "protocolVersion", "requestId", "sequence", "sessionId", "frameRevision", "groupId", "position");
                foreach (var field in new[] { "type", "requestId", "sessionId", "groupId" }) shape.RequireKind(field, 's');
                shape.RequireInteger("sequence"); shape.RequireInteger("protocolVersion", true); shape.RequireInteger("frameRevision", true);
                shape.fields["position"].RequireObjectFields("x", "y", "z");
                foreach (var axis in new[] { "x", "y", "z" })
                    if (shape.fields["position"].fields[axis].kind != 'n') throw new ArgumentException("Position axes must be JSON numbers.");
                message = new WordInputMessage();
                JsonUtility.FromJsonOverwrite(json, message);
                if (message.type == "complete")
                {
                    shape.RequireObjectFields("groupIndex", "word", "finishMarker", "componentIds");
                    shape.RequireInteger("groupIndex", true); shape.RequireKind("word", 's');
                    shape.RequireKind("finishMarker", 'b'); shape.RequireKind("componentIds", 'a');
                    foreach (var item in shape.fields["componentIds"].items) if (item.kind != 's') throw new ArgumentException("componentIds must contain JSON strings.");
                }
                return true;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException || e is OverflowException)
            { error = e.Message; message = null; return false; }
        }

        string HandleMessage(WordInputMessage message)
        {
            if (demo == null || !PythonMode) return Reject(message.requestId, "wrong_mode", "Select PYTHON USB input mode first.");
            if (Bridge == null || !Bridge.Connected) return Reject(message.requestId, "disconnected", "Python connection is not active.");
            string invalid = ValidateCommon(message);
            if (invalid != null) return Reject(message.requestId, "invalid", invalid);
            string fingerprint = Fingerprint(message);
            if (cache.TryGetValue(message.requestId, out var previous))
                return previous.fingerprint == fingerprint ? previous.json : Reject(message.requestId, "request_conflict", "A requestId cannot be reused with different content.");
            if (message.sequence <= lastSequence)
                return Remember(message, fingerprint, Reject(message.requestId, "stale_sequence", "Sequence must be greater than lastSequence."));
            if (message.type != "complete" && message.type != "pose")
                return Remember(message, fingerprint, Reject(message.requestId, "unknown_type", "Only complete and pose messages are supported."));
            invalid = message.type == "complete" ? ValidateComplete(message) : ValidatePose(message);
            if (invalid != null) return Remember(message, fingerprint, Reject(message.requestId, "invalid", invalid));
            lastSequence = message.sequence;
            if (message.type == "complete" && !string.IsNullOrEmpty(demo.Flow.ActiveGroupId) && !freshRemotePose)
                return Remember(message, fingerprint, Reject(message.requestId, "pose_required", "Send a fresh pose for the active group after reconnect or board-frame change."));
            bool accepted = message.type == "complete" ? ApplyComplete(message) : ApplyPose(message);
            string reply = accepted ? JsonUtility.ToJson(new WordInputAck { requestId = message.requestId, accepted = true, code = "accepted", detail = message.type + " applied" })
                : Reject(message.requestId, "busy", "The current conversation step does not accept this observation yet.");
            return Remember(message, fingerprint, reply);
        }

        string ValidateCommon(WordInputMessage message)
        {
            if (demo == null) return "Input pipeline is not initialized.";
            if (message.protocolVersion != 1) return "Expected protocolVersion 1.";
            if (!Identifier(message.requestId)) return "requestId must contain 1 to 80 ASCII letters, digits, underscores, periods, colons or hyphens.";
            if (message.sequence <= 0) return "sequence must be a positive integer.";
            if (message.sessionId != sessionId) return "Observation belongs to a stale session.";
            if (message.frameRevision != demo.Tracking.FrameRevision) return "Observation belongs to a stale board frame.";
            if (!demo.Tracking.IsSpatialReady) return "Head tracking and the board frame must be valid.";
            if (!Identifier(message.groupId)) return "groupId must contain 1 to 80 ASCII letters, digits, underscores, periods, colons or hyphens.";
            if (!Finite(message.position) || message.position.sqrMagnitude > 25) return "Position must be finite board-local metres within 5 metres of the origin.";
            return null;
        }
        string ValidateComplete(WordInputMessage message)
        {
            if (message.groupIndex < 0 || message.groupIndex >= fixtureWords.Length) return "groupIndex must be 0, 1 or 2.";
            string expected = fixtureWords[message.groupIndex];
            message.word = (message.word ?? "").Trim().ToUpperInvariant();
            if (message.word != expected || message.finishMarker != (message.groupIndex == 2)) return "Expected CAT, RAIN, then SLEEP with its separate finish marker.";
            int count = expected.Length + (message.finishMarker ? 1 : 0);
            if (message.componentIds == null || message.componentIds.Length != count) return "Provide one ordered component ID per letter and finish marker.";
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in message.componentIds) if (!Identifier(id) || !ids.Add(id)) return "Component IDs must be distinct, at most 80 characters, and use ASCII letters, digits, underscores, periods, colons or hyphens.";
            foreach (var word in demo.Flow.Record.words)
            {
                if (word.groupId == message.groupId) return "A completed group cannot be submitted as a different word.";
                foreach (var id in word.componentIds) if (ids.Contains(id)) return "A component is already assigned to a completed word.";
            }
            return null;
        }
        string ValidatePose(WordInputMessage message)
        {
            return string.IsNullOrEmpty(demo.Flow.ActiveGroupId) || message.groupId != demo.Flow.ActiveGroupId
                ? "Pose updates must identify the accepted active word group." : null;
        }
        bool ApplyComplete(WordInputMessage message)
        {
            var observation = new RecognizedWordObservation {
                groupIndex = message.groupIndex, groupId = message.groupId, word = message.word,
                rawText = message.word + (message.finishMarker ? "#" : ""), isFinish = message.finishMarker,
                componentIds = (string[])message.componentIds.Clone(),
                worldPosition = demo.DetectionOrigin.TransformPoint(message.position)
            };
            bool accepted = demo.ApplyRecognition(observation);
            if (accepted) MarkFresh();
            return accepted;
        }
        bool ApplyPose(WordInputMessage message)
        {
            bool accepted = demo.ApplyRecognizedGroupPose(message.groupId, message.position, Quaternion.identity);
            if (accepted) MarkFresh();
            return accepted;
        }
        void MarkFresh()
        {
            freshRemotePose = true; freshFrame = demo.Tracking.FrameRevision; LastError = "";
            demo.Flow.SetSpatialReady(AudioReady);
        }
        string Remember(WordInputMessage message, string fingerprint, string json)
        {
            if (cache.Count >= 128) cache.Remove(cacheOrder.Dequeue());
            cache[message.requestId] = new CachedAck { fingerprint = fingerprint, json = json };
            cacheOrder.Enqueue(message.requestId);
            return json;
        }
        string Reject(string request, string code, string detail)
        {
            if (LastError != detail) Debug.LogWarning("[Word input rejected] " + detail);
            LastError = detail;
            return JsonUtility.ToJson(new WordInputAck { requestId = request ?? "", accepted = false, code = code, detail = detail });
        }
        static string Fingerprint(WordInputMessage message)
        {
            using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(JsonUtility.ToJson(message))));
        }
        public WordInputStatus CaptureStatus()
        {
            ResetSession();
            return new WordInputStatus {
                sessionId = sessionId, frameRevision = demo.Tracking.FrameRevision,
                inputMode = PythonMode ? "python" : "random", spatialReady = demo.Tracking.IsSpatialReady,
                audioReady = AudioReady, state = demo.Flow.State.ToString(), nextIndex = demo.Flow.Record.words.Count,
                lastSequence = lastSequence, activeGroupId = demo.Flow.ActiveGroupId
            };
        }
        void SendStatus() { if (demo != null && Bridge != null && Bridge.Connected) Bridge.Send(JsonUtility.ToJson(CaptureStatus())); }
        void OnMessage(string json) { Bridge.Send(HandleJson(json)); SendStatus(); }
        void OnFlowChanged() { ResetSession(); SendStatus(); }
        void OnConnection(bool connected)
        {
            freshRemotePose = false; freshFrame = -1;
            if (PythonMode && demo != null) demo.Flow.SetSpatialReady(AudioReady);
            SendStatus();
        }
        void OnFrameChanged()
        {
            freshRemotePose = false; freshFrame = -1;
            if (PythonMode) demo.Flow.SetSpatialReady(false);
            SendStatus();
        }
        void Update()
        {
            if (demo == null) return;
            ResetSession();
            if (Time.unscaledTime < nextStatusTime) return;
            nextStatusTime = Time.unscaledTime + .2f;
            SendStatus();
        }
        void OnDestroy()
        {
            if (demo != null) { demo.Flow.Changed -= OnFlowChanged; demo.Tracking.BoardFrameChanged -= OnFrameChanged; }
            if (Bridge != null) { Bridge.ConnectionChanged -= OnConnection; Bridge.MessageReceived -= OnMessage; Bridge.SetEnabled(false); }
        }
        static bool Identifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 80) return false;
            foreach (char c in value)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == ':' || c == '-')) return false;
            return true;
        }
        static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }

    /// <summary>Strict JSON shape validation before Unity's permissive field deserializer.</summary>
    internal sealed class InputJsonShape
    {
        public char kind;
        public string scalar;
        public Dictionary<string, InputJsonShape> fields;
        public List<InputJsonShape> items;
        public void RequireObjectFields(params string[] names)
        {
            if (kind != 'o') throw new ArgumentException("Expected a JSON object.");
            foreach (string name in names) if (!fields.ContainsKey(name)) throw new ArgumentException("Missing field: " + name);
        }
        public void RequireKind(string name, char expected)
        {
            if (fields[name].kind != expected) throw new ArgumentException("Wrong JSON type for " + name);
        }
        public void RequireInteger(string name, bool int32 = false)
        {
            RequireKind(name, 'n');
            if (!long.TryParse(fields[name].scalar, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long value) || (int32 && (value < int.MinValue || value > int.MaxValue)))
                throw new ArgumentException("Expected a bounded integer for " + name);
        }
        public static InputJsonShape Parse(string text)
        {
            var reader = new Reader(text); var result = reader.Value(0); reader.Space();
            if (!reader.End) throw new ArgumentException("Unexpected trailing JSON content.");
            return result;
        }
        sealed class Reader
        {
            readonly string text; int at;
            public Reader(string value) { text = value; }
            public bool End => at >= text.Length;
            public void Space() { while (!End && (text[at] == ' ' || text[at] == '\r' || text[at] == '\n' || text[at] == '\t')) at++; }
            bool Take(char c) { Space(); if (!End && text[at] == c) { at++; return true; } return false; }
            void Need(char c) { if (!Take(c)) throw new ArgumentException("Malformed JSON; expected " + c); }
            public InputJsonShape Value(int depth)
            {
                if (depth > 16) throw new ArgumentException("JSON nesting exceeds 16 levels.");
                Space(); if (End) throw new ArgumentException("Incomplete JSON.");
                if (Take('{'))
                {
                    var node = new InputJsonShape { kind = 'o', fields = new Dictionary<string, InputJsonShape>(StringComparer.Ordinal) };
                    if (Take('}')) return node;
                    do { string key = Text(); Need(':'); if (node.fields.ContainsKey(key)) throw new ArgumentException("Duplicate JSON field: " + key); node.fields.Add(key, Value(depth + 1)); } while (Take(','));
                    Need('}'); return node;
                }
                if (Take('['))
                {
                    var list = new List<InputJsonShape>();
                    if (!Take(']')) { do { list.Add(Value(depth + 1)); } while (Take(',')); Need(']'); }
                    return new InputJsonShape { kind = 'a', items = list };
                }
                if (text[at] == '"') { Text(); return new InputJsonShape { kind = 's' }; }
                foreach (string literal in new[] { "true", "false", "null" })
                    if (text.Length - at >= literal.Length && string.CompareOrdinal(text, at, literal, 0, literal.Length) == 0)
                    { at += literal.Length; return new InputJsonShape { kind = literal == "null" ? '0' : 'b' }; }
                int start = at;
                if (!End && text[at] == '-') at++;
                if (!End && text[at] == '0') at++;
                else { int digits = at; while (!End && text[at] >= '0' && text[at] <= '9') at++; if (at == digits) throw new ArgumentException("Expected a JSON value."); }
                if (!End && text[at] == '.') { at++; Digits(); }
                if (!End && (text[at] == 'e' || text[at] == 'E')) { at++; if (!End && (text[at] == '+' || text[at] == '-')) at++; Digits(); }
                if (at == start) throw new ArgumentException("Expected a JSON number.");
                return new InputJsonShape { kind = 'n', scalar = text.Substring(start, at - start) };
            }
            void Digits() { int start = at; while (!End && text[at] >= '0' && text[at] <= '9') at++; if (at == start) throw new ArgumentException("Malformed JSON number."); }
            string Text()
            {
                Need('"'); var result = new StringBuilder();
                while (!End)
                {
                    char c = text[at++]; if (c == '"') return result.ToString();
                    if (c < 32) throw new ArgumentException("Unescaped control character in JSON string.");
                    if (c != '\\') { result.Append(c); continue; }
                    if (End) break; c = text[at++];
                    switch (c)
                    {
                        case '"': case '\\': case '/': result.Append(c); break;
                        case 'b': result.Append('\b'); break; case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break; case 'r': result.Append('\r'); break; case 't': result.Append('\t'); break;
                        case 'u':
                            if (at + 4 > text.Length || !ushort.TryParse(text.Substring(at, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ushort hex)) throw new ArgumentException("Invalid JSON Unicode escape.");
                            result.Append((char)hex); at += 4; break;
                        default: throw new ArgumentException("Invalid JSON escape.");
                    }
                }
                throw new ArgumentException("Unterminated JSON string.");
            }
        }
    }
}
