using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;
using ZapMQ;

namespace WorkerControl.Service;

/// <summary>
/// The conversation with the workers and with whoever administers the service, over ZapMQ.
/// </summary>
internal sealed class ZapMQBroker : IBroker, IDisposable
{
    // A failure of the broker has to be known before the supervisor judges an unanswered
    // keep-alive (Supervisor.VerdictDelay), so the canary may take 3 s at most to give up.
    private static readonly TimeSpan CanaryTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CanaryHold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A message whose sender wants to know when nobody answered it in time.
    /// </summary>
    private abstract class Awaited
    {
        [JsonIgnore]
        public Action? Expired { get; set; }
    }

    private sealed class KeepAliveBody : Awaited
    {
        public string ProcessId { get; set; } = "";
    }

    private sealed class CanaryBody : Awaited
    {
        public string Canary { get; set; } = "";
    }

    private readonly ZapMQWrapper _publisher;
    private readonly ZapMQWrapper _canaryConsumer;
    private readonly ZapMQWrapper _adminConsumer;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _canaryQueue = "WorkerControl.Canary." + Environment.ProcessId;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Thread _canary;
    private long _lastSuccess = DateTimeOffset.MinValue.UtcTicks;
    private long _lastFailure = DateTimeOffset.MinValue.UtcTicks;
    private int _healthy = -1;

    public ZapMQBroker(string host, int port, TimeProvider time, ILogger logger, Func<JObject, JObject?> admin)
    {
        _time = time;
        _logger = logger;

        _publisher = new ZapMQWrapper(host, port);
        _publisher.OnRPCExpired = message => (message.Body as Awaited)?.Expired?.Invoke();

        // The canary's own consumer holds each message for a moment, so it gets a wrapper to
        // itself and the administration is never kept waiting behind it.
        _canaryConsumer = new ZapMQWrapper(host, port);
        _canaryConsumer.Bind(_canaryQueue, (ZapJSONMessage _, out bool processing) =>
        {
            processing = false;
            _stopping.Token.WaitHandle.WaitOne(CanaryHold);
            return new { Alive = true };
        });

        _adminConsumer = new ZapMQWrapper(host, port);
        _adminConsumer.Bind(LegacyAdmin.Queue, (ZapJSONMessage message, out bool processing) =>
        {
            processing = false;
            try
            {
                return admin(message.Body as JObject ?? new JObject())!;
            }
            catch (Exception error)
            {
                _logger.LogError(error, "An administration request failed");
                return null!;
            }
        });

        _canary = new Thread(WatchBroker) { IsBackground = true, Name = "WorkerControl canary" };
        _canary.Start();
    }

    public bool SendKeepAlive(int processId, TimeSpan timeout, Action answered, Action expired)
    {
        var body = new KeepAliveBody { ProcessId = processId.ToString(), Expired = expired };
        return Guarded(() => _publisher.SendRPCMessage(processId.ToString(), body, _ => answered(), (int)timeout.TotalMilliseconds));
    }

    public bool SendSafeStop(int processId) =>
        Guarded(() => _publisher.SendMessage(processId + "SS", new { Text = "STOP" }, 10000));

    public bool HealthySince(DateTimeOffset instant)
    {
        var success = Interlocked.Read(ref _lastSuccess);
        var failure = Interlocked.Read(ref _lastFailure);
        return failure < instant.UtcTicks
            && success > failure
            && _time.GetUtcNow().UtcTicks - success <= Fresh.Ticks;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _adminConsumer.StopThreads();
        _canaryConsumer.StopThreads();
        _publisher.StopThreads();
    }

    private bool Guarded(Func<bool> send)
    {
        try
        {
            return send();
        }
        catch (Exception error)
        {
            _logger.LogDebug("A message could not be sent to ZapMQ: {Error}", error.Message);
            return false;
        }
    }

    /// <summary>
    /// Keeps a message of its own travelling through the broker and back, almost all the time.
    /// A keep-alive that went unanswered only says something about the worker if, meanwhile,
    /// this message never failed to come back. It does not matter how the broker failed
    /// (stopped, restarted, unreachable) nor which protocol is in use.
    /// </summary>
    private void WatchBroker()
    {
        var stop = _stopping.Token;
        while (!stop.IsCancellationRequested)
        {
            using var done = new ManualResetEventSlim(false);
            var back = false;
            var body = new CanaryBody { Canary = Guid.NewGuid().ToString("N"), Expired = () => done.Set() };
            var sent = Guarded(() => _publisher.SendRPCMessage(_canaryQueue, body, _ =>
            {
                back = true;
                done.Set();
            }, (int)CanaryTimeout.TotalMilliseconds));

            if (sent)
            {
                try
                {
                    done.Wait(CanaryTimeout + TimeSpan.FromSeconds(1), stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            var now = _time.GetUtcNow().UtcTicks;
            if (sent && back)
            {
                Interlocked.Exchange(ref _lastSuccess, now);
                Report(healthy: true);
            }
            else
            {
                Interlocked.Exchange(ref _lastFailure, now);
                Report(healthy: false);
                stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            }
        }
    }

    private void Report(bool healthy)
    {
        if (Interlocked.Exchange(ref _healthy, healthy ? 1 : 0) == (healthy ? 1 : 0))
            return;

        if (healthy)
            _logger.LogInformation("ZapMQ is answering; keep-alives count from now on");
        else
            _logger.LogWarning("ZapMQ is not answering; until it does, no worker is ended for an unanswered keep-alive");
    }
}
