using System.Text.Json;
using WorkerControl.Core;

namespace WorkerControl.Service;

/// <summary>
/// How many messages wait in each queue, read from the metrics of ZapMQ every few seconds.
/// </summary>
internal sealed class QueueMonitor : IQueueMonitor, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly Timer _timer;
    private volatile IReadOnlyDictionary<string, int>? _pending;
    private int _reading;
    private bool _complained;

    public QueueMonitor(string host, int port, ILogger logger)
    {
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}/"), Timeout = TimeSpan.FromSeconds(4) };
        _timer = new Timer(_ => Read(), null, TimeSpan.Zero, Interval);
    }

    /// <summary>
    /// A queue the broker does not list has nothing waiting.
    /// </summary>
    public int? PendingMessages(string queue) =>
        _pending is { } pending ? pending.GetValueOrDefault(queue) : null;

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }

    private void Read()
    {
        if (Interlocked.Exchange(ref _reading, 1) == 1)
            return;
        try
        {
            using var document = JsonDocument.Parse(_http.GetStringAsync("metrics").GetAwaiter().GetResult());
            // 2.1 answers an object with the queues inside; 2.0 answered the list itself.
            var root = document.RootElement;
            var queues = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("queues", out var inside) ? inside : root;
            if (queues.ValueKind != JsonValueKind.Array)
                throw new JsonException("no list of queues");

            var pending = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var queue in queues.EnumerateArray())
                if (queue.TryGetProperty("name", out var name) && queue.TryGetProperty("pending", out var count))
                    pending[name.GetString() ?? ""] = count.GetInt32();

            _pending = pending;
            _complained = false;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _pending = null;
            if (!_complained)
                _logger.LogWarning("The queues of ZapMQ cannot be read ({Error}); scaling by the queue waits until they can. A 1.x server does not offer this", error.Message);
            _complained = true;
        }
        finally
        {
            Interlocked.Exchange(ref _reading, 0);
        }
    }
}
