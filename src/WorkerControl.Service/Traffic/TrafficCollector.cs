using System.Diagnostics;
using System.Text.Json;
using WorkerControl.Core;

namespace WorkerControl.Service.Traffic;

/// <summary>
/// Where the reading of the access log is: for whoever wants to know if the numbers are fresh.
/// </summary>
internal sealed record TrafficSource(string File, bool Found, long Size, long Offset, DateTimeOffset? LastLineAt, long Lines, long Refused, string? Problem);

/// <summary>
/// Follows the access log of the reverse proxy: reads what was written since the last look,
/// counts it by route and by minute, keeps the count, and renames the file when it gets big.
/// It never holds whoever ticks it, and never reads a request twice: where it stopped is
/// written down.
/// </summary>
internal sealed class TrafficCollector(string directory, TrafficStore store, TimeProvider time, ILogger logger, Func<string, bool>? reopen = null) : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaintainEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinutesKeptFor = TimeSpan.FromHours(48);
    private const int ReadAtOnce = 4 * 1024 * 1024;
    private const string Others = "(outras)";

    private sealed class Position
    {
        public string File { get; set; } = "";
        public long Offset { get; set; }
    }

    private readonly object _gate = new();
    private readonly string _positionPath = Path.Combine(directory, "traffic.json");
    private TrafficConfig? _config;
    private List<string[]> _templates = [];
    private Position? _position;
    private DateTimeOffset _next = DateTimeOffset.MinValue;
    private DateTimeOffset _nextMaintenance = DateTimeOffset.MinValue;
    private int _working;
    private byte[] _buffer = new byte[ReadAtOnce];

    // Routes counted today, to stop counting new ones past the limit.
    private readonly HashSet<string> _routesToday = new(StringComparer.Ordinal);
    private long _day;

    private TrafficSource _source = new("", false, 0, 0, null, 0, 0, null);
    private long _lines, _refused;
    private DateTimeOffset? _lastLineAt;

    public TrafficStore Store => store;

    public TrafficSource Source => Volatile.Read(ref _source);

    public bool Configured
    {
        get
        {
            lock (_gate)
                return _config is not null;
        }
    }

    public void ApplyConfig(TrafficConfig? config)
    {
        lock (_gate)
        {
            if (_config == config)
                return;
            _config = config;
            _templates = config is null ? [] : TrafficLog.Templates(config.Routes);
            _next = DateTimeOffset.MinValue;
        }
    }

    public void Tick()
    {
        lock (_gate)
        {
            if (_config is null || time.GetUtcNow() < _next)
                return;
            _next = time.GetUtcNow() + Interval;
        }
        if (Interlocked.Exchange(ref _working, 1) == 1)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                Collect();
            }
            catch (Exception error)
            {
                logger.LogError(error, "The access log could not be read");
            }
            finally
            {
                Interlocked.Exchange(ref _working, 0);
            }
        });
    }

    /// <summary>
    /// Reads what is new in the log now, and does the housekeeping that is due.
    /// </summary>
    public void Collect()
    {
        TrafficConfig? config;
        List<string[]> templates;
        lock (_gate)
        {
            config = _config;
            templates = _templates;
        }
        if (config is null)
            return;

        var position = _position ??= LoadPosition();
        if (!string.Equals(position.File, config.AccessLog, StringComparison.OrdinalIgnoreCase))
        {
            // Another file: it is read from its start.
            position.File = config.AccessLog;
            position.Offset = 0;
        }

        string? problem = null;
        long size = 0;
        var found = File.Exists(config.AccessLog);
        if (found)
        {
            try
            {
                // Everything there is, a piece at a time, so a long backlog does not sit in memory.
                while (ReadPiece(config.AccessLog, position, config, templates, out size) && position.Offset < size)
                {
                }
                if (config.ReopenCommand is not null && size > (long)config.RotateAtMb * 1024 * 1024 && position.Offset >= size)
                    Rotate(config, position, templates);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                problem = error.Message;
            }
        }
        else
        {
            problem = "The file does not exist";
        }

        Volatile.Write(ref _source, new TrafficSource(config.AccessLog, found, size, position.Offset, _lastLineAt, _lines, _refused, problem));

        var now = time.GetUtcNow();
        if (now >= _nextMaintenance)
        {
            _nextMaintenance = now + MaintainEvery;
            store.Maintain(now, MinutesKeptFor, TimeSpan.FromDays(config.RetentionDays));
        }
    }

    /// <summary>
    /// Reads one piece of the file from where the reading stopped, counts its whole lines and
    /// moves on. False when there was nothing new.
    /// </summary>
    private bool ReadPiece(string path, Position position, TrafficConfig config, List<string[]> templates, out long size)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        size = stream.Length;
        // Shorter than what was already read: it is not the same file any more.
        if (size < position.Offset)
            position.Offset = 0;
        if (size == position.Offset)
            return false;

        stream.Seek(position.Offset, SeekOrigin.Begin);
        var read = 0;
        while (read < _buffer.Length)
        {
            var got = stream.Read(_buffer, read, _buffer.Length - read);
            if (got == 0)
                break;
            read += got;
        }

        // Only whole lines: what the proxy is still writing is left for the next time.
        var end = Array.LastIndexOf(_buffer, (byte)'\n', read - 1, read);
        if (end < 0)
        {
            // A line longer than the piece is not one of ours; it is skipped.
            if (read == _buffer.Length)
            {
                position.Offset += read;
                _refused++;
                SavePosition(position);
                return true;
            }
            return false;
        }

        Count(_buffer.AsSpan(0, end + 1), config, templates);
        position.Offset += end + 1;
        SavePosition(position);
        return true;
    }

    private void Count(ReadOnlySpan<byte> lines, TrafficConfig config, List<string[]> templates)
    {
        var tallies = new Dictionary<(long, RouteKey), Tally>();
        var addresses = new HashSet<(long, string, string, string, string)>();
        var errors = new List<StoredError>();

        while (lines.Length > 0)
        {
            var end = lines.IndexOf((byte)'\n');
            var line = lines[..end].TrimEnd((byte)'\r');
            lines = lines[(end + 1)..];
            if (line.Length == 0)
                continue;

            var hit = TrafficLog.Parse(line);
            if (hit is null)
            {
                _refused++;
                continue;
            }
            _lines++;
            _lastLineAt = hit.At;
            if (config.Ignore.Any(prefix => hit.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                continue;

            var (route, kind, app) = TrafficLog.Normalize(hit.Path, config.GroupBy, templates);
            var seconds = hit.At.ToUnixTimeSeconds();

            // A day that sees more routes than it should is being fed paths made up by somebody.
            var day = seconds / 86400;
            if (day != _day)
            {
                _day = day;
                _routesToday.Clear();
            }
            if (!_routesToday.Contains(hit.Host + hit.Method + route))
            {
                if (_routesToday.Count < config.MaxRoutes)
                    _routesToday.Add(hit.Host + hit.Method + route);
                else
                    route = Others;
            }

            var key = (seconds / TrafficStore.Minute * TrafficStore.Minute, new RouteKey(hit.Host, hit.Method, route, kind, app, hit.Upstream));
            if (!tallies.TryGetValue(key, out var tally))
                tallies[key] = tally = new Tally();
            tally.Add(hit.Status, hit.Bytes, hit.Seconds);

            if (hit.Ip.Length > 0)
                addresses.Add((seconds / TrafficStore.Hour * TrafficStore.Hour, hit.Host, kind, app, hit.Ip));

            if (hit.Status >= 500 && config.KeepErrors > 0)
                errors.Add(new StoredError(hit.At, hit.Host, hit.Method, hit.Path, route, app, hit.Status, hit.Upstream, Math.Round(hit.Seconds * 1000, 1)));
        }

        if (tallies.Count > 0)
            store.Add(tallies, addresses, errors, config.KeepErrors);
    }

    /// <summary>
    /// Puts the file aside under another name and makes the proxy start a new one. What the
    /// proxy still wrote in the old one, until it noticed, is read too.
    /// </summary>
    private void Rotate(TrafficConfig config, Position position, List<string[]> templates)
    {
        var folder = Path.GetDirectoryName(config.AccessLog) ?? ".";
        var name = Path.GetFileNameWithoutExtension(config.AccessLog);
        var extension = Path.GetExtension(config.AccessLog);
        var aside = Path.Combine(folder, $"{name}.{time.GetLocalNow():yyyyMMdd-HHmmss}{extension}");

        File.Move(config.AccessLog, aside);
        var reopened = (reopen ?? RunCommand)(config.ReopenCommand!);
        if (!reopened)
            logger.LogWarning("The access log was put aside as {File}, but the proxy did not confirm starting a new one", aside);
        else
            logger.LogInformation("The access log passed {Size} MB and was put aside as {File}", config.RotateAtMb, aside);

        // The proxy keeps the old file open until it reopens; give it an instant and read the rest.
        Thread.Sleep(200);
        var rest = new Position { File = aside, Offset = position.Offset };
        while (ReadPieceOf(aside, rest, config, templates))
        {
        }
        position.Offset = 0;
        SavePosition(position);

        foreach (var old in Directory.GetFiles(folder, $"{name}.*{extension}")
                     .Where(file => !string.Equals(file, config.AccessLog, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(file => file, StringComparer.OrdinalIgnoreCase)
                     .Skip(config.KeepFiles))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("An old access log could not be removed: {Error}", error.Message);
            }
        }
    }

    private bool ReadPieceOf(string path, Position position, TrafficConfig config, List<string[]> templates)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= position.Offset)
            return false;
        stream.Seek(position.Offset, SeekOrigin.Begin);
        var read = stream.Read(_buffer, 0, _buffer.Length);
        var end = read > 0 ? Array.LastIndexOf(_buffer, (byte)'\n', read - 1, read) : -1;
        if (end < 0)
            return false;
        Count(_buffer.AsSpan(0, end + 1), config, templates);
        position.Offset += end + 1;
        return true;
    }

    /// <summary>
    /// Runs the command that makes the proxy open its log again, from the folder of its
    /// executable, which is where it looks for its own files.
    /// </summary>
    private bool RunCommand(string commandLine)
    {
        try
        {
            var executable = ServiceCommandLine.Executable(commandLine) ?? commandLine;
            var start = commandLine.IndexOf(executable, StringComparison.OrdinalIgnoreCase);
            var arguments = start >= 0 ? commandLine[(start + executable.Length)..].TrimStart('"').Trim() : "";
            using var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return process is not null && process.WaitForExit(10000) && process.ExitCode == 0;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            logger.LogWarning("The command to reopen the access log failed: {Error}", error.Message);
            return false;
        }
    }

    private Position LoadPosition()
    {
        try
        {
            return File.Exists(_positionPath) ? JsonSerializer.Deserialize<Position>(File.ReadAllText(_positionPath)) ?? new Position() : new Position();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Position();
        }
    }

    private void SavePosition(Position position)
    {
        try
        {
            var temporary = _positionPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(position));
            File.Move(temporary, _positionPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Where the reading of the access log stopped could not be written: {Error}", error.Message);
        }
    }

    public void Dispose()
    {
        _buffer = [];
    }
}
