using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace BrailleSoundDemo
{
    /// <summary>Bounded UTF-8 JSON-line framing shared by the transport and its tests.</summary>
    public sealed class JsonLineFramer
    {
        public const int MaximumBytes = 16384;
        readonly List<byte> pending = new List<byte>();
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public void Feed(byte[] bytes, int count, Action<string> received)
        {
            if (count < 0 || count > bytes.Length) throw new ArgumentOutOfRangeException(nameof(count));
            for (int i = 0; i < count; i++)
            {
                if (bytes[i] == 10)
                {
                    if (pending.Count > 0 && pending[pending.Count - 1] == 13) pending.RemoveAt(pending.Count - 1);
                    if (pending.Count > 0) received(Utf8.GetString(pending.ToArray()));
                    pending.Clear();
                }
                else
                {
                    if (pending.Count >= MaximumBytes) throw new InvalidDataException("Incoming JSON line exceeds 16 KB.");
                    pending.Add(bytes[i]);
                }
            }
        }
        public static byte[] Encode(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.IndexOf('\n') >= 0 || line.IndexOf('\r') >= 0)
                throw new ArgumentException("Send one nonempty JSON line without literal line breaks.");
            byte[] bytes = Utf8.GetBytes(line + "\n");
            if (bytes.Length - 1 > MaximumBytes) throw new ArgumentException("Outgoing JSON line exceeds 16 KB.");
            return bytes;
        }
    }

    /// <summary>
    /// Unity connects to a Python server on loopback. On Quest use adb reverse tcp:8765 tcp:8765.
    /// Socket work stays off Unity's thread; callbacks are delivered by Update in wire order.
    /// Every disconnect discards unsent data and the partial incoming frame.
    /// </summary>
    public sealed class PythonWordBridge : MonoBehaviour
    {
        public const int Port = 8765;
        public bool IsEnabled { get; private set; }
        public bool Connected { get; private set; }
        public string Status { get; private set; } = "Python input disabled";
        public event Action<bool> ConnectionChanged;
        public event Action<string> MessageReceived;

        const int QueueLimit = 256;
        readonly ConcurrentQueue<Notification> notifications = new ConcurrentQueue<Notification>();
        readonly object socketLock = new object();
        CancellationTokenSource cancellation;
        Thread worker;
        TcpClient client;
        ConnectionQueue outgoing;
        int generation, notificationCount, connectionSerial, liveConnectionSerial;

        sealed class Notification
        {
            public int generation;
            public int connectionSerial;
            public string line, status;
            public bool? connected;
        }
        sealed class ConnectionQueue
        {
            public readonly ConcurrentQueue<byte[]> bytes = new ConcurrentQueue<byte[]>();
            public int count;
            public bool closed;
        }

        public void SetEnabled(bool value)
        {
            if (IsEnabled == value) return;
            StopTransport();
            if (!value) return;
            IsEnabled = true;
            Status = "Connecting to Python on 127.0.0.1:8765";
            cancellation = new CancellationTokenSource();
            int token = generation;
            var stop = cancellation.Token;
            worker = new Thread(() => Run(token, stop)) { IsBackground = true, Name = "Story Python input" };
            worker.Start();
        }

        public bool Send(string line)
        {
            if (!IsEnabled || !Connected) return false;
            byte[] bytes;
            try { bytes = JsonLineFramer.Encode(line); }
            catch (ArgumentException) { return false; }
            lock (socketLock)
            {
                var target = outgoing;
                if (target == null || target.closed || target.count >= QueueLimit) return false;
                target.count++;
                target.bytes.Enqueue(bytes);
                return true;
            }
        }

        void Update()
        {
            for (int n = 0; n < 64 && notifications.TryDequeue(out var note); n++)
            {
                Interlocked.Decrement(ref notificationCount);
                if (note.generation != generation || !IsEnabled) continue;
                if (note.status != null) Status = note.status;
                if (note.connected == true && note.connectionSerial != Volatile.Read(ref liveConnectionSerial)) continue;
                if (note.connected.HasValue)
                {
                    Connected = note.connected.Value;
                    ConnectionChanged?.Invoke(Connected);
                }
                if (note.line != null && Connected && note.connectionSerial == Volatile.Read(ref liveConnectionSerial)) MessageReceived?.Invoke(note.line);
            }
        }

        void Run(int token, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient connection = null;
                ConnectionQueue queue = null;
                bool established = false;
                int serial = 0;
                string failure = "Python disconnected; reconnecting";
                try
                {
                    connection = new TcpClient { NoDelay = true, SendTimeout = 1000 };
                    lock (socketLock) { if (stop.IsCancellationRequested) break; client = connection; }
                    var connect = connection.BeginConnect("127.0.0.1", Port, null, null);
                    using (connect.AsyncWaitHandle)
                    {
                        int result = WaitHandle.WaitAny(new[] { connect.AsyncWaitHandle, stop.WaitHandle }, 1000);
                        if (result != 0) throw new IOException(result == 1 ? "Connection cancelled" : "Python connection timed out");
                        connection.EndConnect(connect);
                    }
                    if (stop.IsCancellationRequested) break;
                    queue = new ConnectionQueue();
                    lock (socketLock)
                    {
                        if (stop.IsCancellationRequested) break;
                        outgoing = queue;
                        serial = Interlocked.Increment(ref connectionSerial);
                        Volatile.Write(ref liveConnectionSerial, serial);
                    }
                    established = true;
                    Post(new Notification { generation = token, connectionSerial = serial, connected = true, status = "Python connected via loopback:8765" });
                    var framer = new JsonLineFramer();
                    var buffer = new byte[4096];
                    var stream = connection.GetStream();
                    while (!stop.IsCancellationRequested)
                    {
                        for (int n = 0; n < 32 && queue.bytes.TryDequeue(out var bytes); n++)
                        {
                            lock (socketLock) queue.count--;
                            stream.Write(bytes, 0, bytes.Length);
                        }
                        if (!connection.Client.Poll(20000, SelectMode.SelectRead)) continue;
                        int count = connection.Client.Receive(buffer);
                        if (count == 0) break;
                        framer.Feed(buffer, count, line => Post(new Notification { generation = token, connectionSerial = serial, line = line }));
                    }
                }
                catch (Exception e) when (e is SocketException || e is IOException || e is ObjectDisposedException || e is DecoderFallbackException || e is InvalidOperationException)
                {
                    failure = "Python unavailable: " + e.Message;
                }
                finally
                {
                    lock (socketLock)
                    {
                        if (queue != null) { queue.closed = true; while (queue.bytes.TryDequeue(out _)) { } queue.count = 0; }
                        if (ReferenceEquals(outgoing, queue)) outgoing = null;
                        if (ReferenceEquals(client, connection)) client = null;
                        if (Volatile.Read(ref liveConnectionSerial) == serial) Volatile.Write(ref liveConnectionSerial, 0);
                    }
                    connection?.Close();
                }
                if (stop.IsCancellationRequested) break;
                // Allow connection-loss notifications through even after an incoming queue overflow.
                Post(new Notification { generation = token, connectionSerial = serial, connected = established ? (bool?)false : null, status = failure }, true);
                stop.WaitHandle.WaitOne(500);
            }
        }

        void Post(Notification note, bool essential = false)
        {
            if (Interlocked.Increment(ref notificationCount) > QueueLimit)
            {
                if (!essential)
                {
                    Interlocked.Decrement(ref notificationCount);
                    throw new InvalidOperationException("Python input queue is full; connection closed to discard backlog.");
                }
                while (Volatile.Read(ref notificationCount) > QueueLimit && notifications.TryDequeue(out _))
                    Interlocked.Decrement(ref notificationCount);
            }
            notifications.Enqueue(note);
        }

        void StopTransport()
        {
            generation++;
            cancellation?.Cancel();
            lock (socketLock)
            {
                if (outgoing != null) outgoing.closed = true;
                outgoing = null;
                client?.Close();
                client = null;
                Volatile.Write(ref liveConnectionSerial, 0);
            }
            // Closing the socket interrupts pending I/O; never hold Unity's thread indefinitely.
            var stoppedWorker = worker;
            var stoppedCancellation = cancellation;
            if (stoppedWorker != null && stoppedWorker.IsAlive && !stoppedWorker.Join(250))
                ThreadPool.QueueUserWorkItem(_ => { stoppedWorker.Join(); stoppedCancellation?.Dispose(); });
            else stoppedCancellation?.Dispose();
            worker = null;
            cancellation = null;
            while (notifications.TryDequeue(out _)) Interlocked.Decrement(ref notificationCount);
            bool wasConnected = Connected;
            IsEnabled = false; Connected = false; Status = "Python input disabled";
            if (wasConnected) ConnectionChanged?.Invoke(false);
        }
        void OnDisable() { StopTransport(); }
        void OnDestroy() { StopTransport(); }
    }
}
