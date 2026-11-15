using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace BrailleSoundDemo
{
    /// <summary>Stable boundary between any recognizer and the presentation/audio layer.</summary>
    public sealed class BlockInputHub : MonoBehaviour
    {
        public Transform detectionOrigin;
        public bool acceptExternalInput;
        public event Action<BlockObservation> Changed;
        public event Action<string> Removed;
        public event Action<string> Activated;
        public event Action<string> Rejected;
        public int Count => latest.Count;
        public string LastError { get; private set; } = "";
        readonly Dictionary<string, BlockObservation> latest = new Dictionary<string, BlockObservation>();
        readonly ConcurrentQueue<string> pending = new ConcurrentQueue<string>();
        int queued, mainThread;
        void Awake() { mainThread = Thread.CurrentThread.ManagedThreadId; }
        public bool TryGet(string id, out BlockObservation observation)
        {
            if (latest.TryGetValue(id, out var value)) { observation = value.Copy(); return true; }
            observation = null; return false;
        }
        public void Submit(BlockObservation value)
        {
            RequireMainThread();
            if (value == null || string.IsNullOrWhiteSpace(value.id) || value.id.Length > 80) { Fail("A stable block id (1–80 characters) is required."); return; }
            if (!value.visible) { Remove(value.id); return; }
            if (string.IsNullOrWhiteSpace(value.word) || value.word.Length > 32) { Fail("A word (1–32 characters) is required."); return; }
            if (!Finite(value.position.x) || !Finite(value.position.y) || !Finite(value.position.z) || value.position.sqrMagnitude > 1000000) { Fail("Position must be finite metres within 1 km of the detection origin."); return; }
            var q = value.rotation;
            if (!Finite(q.x) || !Finite(q.y) || !Finite(q.z) || !Finite(q.w)) { Fail("Rotation must be a finite quaternion."); return; }
            var copy = value.Copy(); copy.word = copy.word.Trim().ToUpperInvariant();
            copy.rotation = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w < .00001f ? Quaternion.identity : q.normalized;
            if (!latest.ContainsKey(copy.id) && latest.Count >= 64) { Fail("Demo supports at most 64 tracked blocks."); return; }
            latest[copy.id] = copy; LastError = ""; Changed?.Invoke(copy.Copy());
        }
        public void Remove(string id)
        {
            RequireMainThread();
            if (!string.IsNullOrEmpty(id) && latest.Remove(id)) Removed?.Invoke(id);
        }
        public void Activate(string id)
        {
            RequireMainThread();
            if (!string.IsNullOrEmpty(id) && latest.ContainsKey(id)) Activated?.Invoke(id);
        }
        public void Clear()
        {
            RequireMainThread();
            foreach (var id in new List<string>(latest.Keys)) Remove(id);
            while (pending.TryDequeue(out _)) Interlocked.Decrement(ref queued);
        }
        /// <summary>Call from your recognizer on Unity's main thread. Select Real Input mode first.</summary>
        public void SubmitJson(string json)
        {
            RequireMainThread();
            if (!acceptExternalInput) { Fail("Switch to Real Input mode before submitting recognizer data."); return; }
            if (string.IsNullOrWhiteSpace(json) || json.Length > 8192) { Fail("Expected a JSON message up to 8 KB."); return; }
            try
            {
                // Overwrite a constructed message so omitted optional fields retain defaults.
                var m = new BlockInputMessage();
                JsonUtility.FromJsonOverwrite(json, m);
                switch (string.IsNullOrEmpty(m.operation) ? "upsert" : m.operation)
                {
                    case "upsert": Submit(new BlockObservation { id = m.id, word = m.word, position = m.position, rotation = m.rotation, visible = m.visible }); break;
                    case "activate": Activate(m.id); break;
                    case "remove": Remove(m.id); break;
                    default: Fail("Unknown operation: " + m.operation); break;
                }
            }
            catch (ArgumentException e) { Fail("Invalid JSON: " + e.Message); }
        }
        /// <summary>Thread-safe entry point for a future camera/network adapter. Returns false when queue is full.</summary>
        public bool EnqueueJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 8192) return false;
            if (Interlocked.Increment(ref queued) > 256) { Interlocked.Decrement(ref queued); return false; }
            pending.Enqueue(json); return true;
        }
        void Update()
        {
            for (int n = 0; n < 64 && pending.TryDequeue(out var json); n++) { Interlocked.Decrement(ref queued); SubmitJson(json); }
        }
        void RequireMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThread) throw new InvalidOperationException("Use EnqueueJson from worker threads.");
        }
        void Fail(string reason) { LastError = reason; Rejected?.Invoke(reason); }
        static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    }
}
