using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WorkerControl.Service;

/// <summary>
/// Trace for the workers that only know the way of 1.x: the worker is told a port of this
/// machine, connects to it and writes its trace there. This takes the place of whoever used
/// to open that port, and passes what arrives on to a queue of the broker, in batches, in the
/// same format the newer workers publish by themselves.
///
/// Like theirs, it lasts only while it is asked for: each request holds for a while, and
/// without another one the trace is turned off.
/// </summary>
internal sealed class TraceRelay(ZapMQBroker broker, TimeProvider time, ILogger logger) : IDisposable
{
    private static readonly TimeSpan ConnectPatience = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FlushEvery = TimeSpan.FromMilliseconds(250);
    private const int MaxBufferedLines = 5000;
    private const int MaxLinesPerBatch = 500;
    private const int BatchTtlMs = 10000;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Dictionary<int, Session> _sessions = [];

    /// <summary>
    /// Starts passing the trace of a worker on to a queue, or keeps doing it for longer.
    /// Returns what went wrong, or null.
    /// </summary>
    public string? Start(int processId, string queue, TimeSpan lease)
    {
        Session? session;
        lock (_sessions)
        {
            if (_sessions.TryGetValue(processId, out session) && !session.Ended)
            {
                session.Renew(queue, time.GetUtcNow() + lease);
                return null;
            }
        }

        session = new Session(this, processId, queue, time.GetUtcNow() + lease);
        var problem = session.Open();
        if (problem is not null)
        {
            session.Close(tellWorker: false);
            return problem;
        }

        lock (_sessions)
            _sessions[processId] = session;
        session.Run();
        return null;
    }

    public void Stop(int processId)
    {
        Session? session;
        lock (_sessions)
            _sessions.Remove(processId, out session);
        session?.Close(tellWorker: true);
    }

    public void Dispose()
    {
        List<Session> sessions;
        lock (_sessions)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }
        foreach (var session in sessions)
            session.Close(tellWorker: true);
    }

    private void Forget(Session session)
    {
        lock (_sessions)
        {
            if (_sessions.GetValueOrDefault(session.ProcessId) == session)
                _sessions.Remove(session.ProcessId);
        }
    }

    /// <summary>
    /// What a worker wrote, as text. The .NET wrapper sends UTF-8 and the Delphi one the ANSI
    /// code page of the machine; nothing in between says which.
    /// </summary>
    internal static string Decode(byte[] bytes, int count)
    {
        try
        {
            return StrictUtf8.GetString(bytes, 0, count);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes, 0, count);
        }
    }

    private sealed record Line(long Seq, DateTime At, string Text);

    private sealed class Session(TraceRelay relay, int processId, string queue, DateTimeOffset leaseEnd)
    {
        private readonly object _gate = new();
        private readonly Queue<Line> _lines = new();
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private TcpClient? _worker;
        private string _queue = queue;
        private DateTimeOffset _leaseEnd = leaseEnd;
        private long _sequence;
        private long _dropped;
        private int _closed;

        public int ProcessId => processId;

        public bool Ended => Volatile.Read(ref _closed) == 1;

        public void Renew(string queue, DateTimeOffset leaseEnd)
        {
            lock (_gate)
            {
                _queue = queue;
                _leaseEnd = leaseEnd;
            }
        }

        /// <summary>
        /// Opens the port, tells the worker and waits for it to connect.
        /// </summary>
        public string? Open()
        {
            try
            {
                _listener.Start(1);
                var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                var accepting = _listener.AcceptTcpClientAsync();
                if (!relay.Ask(processId + "TR", new { message = "start trace", port }))
                    return "The request could not be sent to the worker";
                if (!accepting.Wait(ConnectPatience))
                    return "The worker did not connect to send its trace";
                _worker = accepting.Result;
                return null;
            }
            catch (Exception error) when (error is SocketException or AggregateException or ObjectDisposedException)
            {
                return "The trace could not be started: " + (error.InnerException ?? error).Message;
            }
        }

        public void Run()
        {
            new Thread(Read) { IsBackground = true, Name = $"WorkerControl trace of {processId}" }.Start();
            new Thread(Flush) { IsBackground = true, Name = $"WorkerControl trace of {processId}" }.Start();
        }

        public void Close(bool tellWorker)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
                return;
            // Anything that is not the request to start turns the trace of a 1.x worker off.
            if (tellWorker)
                relay.Tell(processId + "TR", new { message = "stop trace", port = 0 });
            try
            {
                _worker?.Close();
                _listener.Stop();
            }
            catch (SocketException)
            {
                // Already gone.
            }
            relay.Forget(this);
        }

        private void Read()
        {
            var buffer = new byte[16 * 1024];
            try
            {
                var stream = _worker!.GetStream();
                while (!Ended)
                {
                    var count = stream.Read(buffer, 0, buffer.Length);
                    if (count == 0)
                        break;
                    // The worker writes each call as it is, with nothing between one and the
                    // next: what arrives together is shown together.
                    var text = Decode(buffer, count).TrimEnd('\r', '\n');
                    lock (_gate)
                    {
                        if (_lines.Count >= MaxBufferedLines)
                        {
                            _lines.Dequeue();
                            _dropped++;
                        }
                        _lines.Enqueue(new Line(++_sequence, relay.Now.UtcDateTime, text));
                    }
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or SocketException)
            {
                // The worker left, or the trace was turned off.
            }
            // What was read last still goes out.
            Thread.Sleep(FlushEvery + FlushEvery);
            Close(tellWorker: false);
        }

        private void Flush()
        {
            while (!Ended)
            {
                Thread.Sleep(FlushEvery);

                List<Line> batch;
                string queue;
                long dropped;
                lock (_gate)
                {
                    if (relay.Now > _leaseEnd)
                    {
                        batch = [];
                        queue = "";
                        dropped = -1;
                    }
                    else
                    {
                        if (_lines.Count == 0 && _dropped == 0)
                            continue;
                        batch = new List<Line>(Math.Min(_lines.Count, MaxLinesPerBatch));
                        while (_lines.Count > 0 && batch.Count < MaxLinesPerBatch)
                            batch.Add(_lines.Dequeue());
                        queue = _queue;
                        dropped = _dropped;
                        _dropped = 0;
                    }
                }

                if (dropped < 0)
                {
                    relay.Log.LogInformation("Trace of worker {ProcessId} turned off: nobody asked for it again", processId);
                    Close(tellWorker: true);
                    return;
                }

                // Trace is disposable: what could not be sent is counted, not sent again.
                if (!relay.Tell(queue, new { ProcessId = processId.ToString(), Dropped = dropped, Lines = batch }, BatchTtlMs))
                {
                    lock (_gate)
                        _dropped += dropped + batch.Count;
                }
            }
        }
    }

    private DateTimeOffset Now => time.GetUtcNow();

    private ILogger Log => logger;

    private bool Ask(string queue, object body) => broker.Ask(queue, body, (int)ConnectPatience.TotalMilliseconds);

    private bool Tell(string queue, object body, int ttlMs = 10000) => broker.Tell(queue, body, ttlMs);
}
