using System.Diagnostics;
using WorkerControl.Core;

namespace WorkerControl.Service.Database;

/// <summary>
/// Something about the instance that asks for attention. The subject is what it is about: a
/// database, a disk, a job.
/// </summary>
public sealed record DatabaseAlert(string Kind, string Subject, string Severity, double? Value, DateTimeOffset Since)
{
    public string Key => Kind + "|" + Subject;
}

public sealed record DatabaseState
{
    public bool Configured { get; init; }
    public string? Name { get; init; }
    public string? Server { get; init; }
    public IReadOnlyList<string> Databases { get; init; } = [];

    /// <summary>
    /// Null until the first look comes back.
    /// </summary>
    public bool? Online { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? Since { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    public double? ResponseMs { get; init; }
    public double? BatchesPerSecond { get; init; }
    public FastSample? Fast { get; init; }
    public SlowSample? Slow { get; init; }
    public DateTimeOffset? SlowAt { get; init; }
    public IReadOnlyList<DatabaseAlert> Alerts { get; init; } = [];
}

/// <summary>
/// Watches the database instance of the environment: whether it answers, how loaded it is and
/// what asks for attention on it. It looks from a thread of its own, so an instance that takes
/// long to answer never delays the supervision of the workers.
/// </summary>
internal sealed class DatabaseMonitor(IDatabaseSource source, ISecretProtector protector, DatabaseHistory? history, TimeProvider time, ILogger logger) : IDisposable
{
    public const string GroupPrefix = "database:";

    public static readonly TimeSpan SlowInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ExpensiveFor = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private DatabaseConfig? _config;
    private DatabaseState _state = new();
    private DateTimeOffset _next = DateTimeOffset.MinValue;
    private DateTimeOffset _nextSlow = DateTimeOffset.MinValue;
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;
    private (long Count, DateTimeOffset At)? _batches;
    private (IReadOnlyList<ExpensiveQuery> Queries, DateTimeOffset At)? _expensive;
    private int _sampling;

    public event Action<SupervisorEvent>? Event;

    public DatabaseHistory? History => history;

    public void ApplyConfig(DatabaseConfig? config)
    {
        lock (_gate)
        {
            if (Equals(config, _config))
                return;
            var same = config is not null && _config is not null && config.Server == _config.Server && config.User == _config.User && config.Password == _config.Password;
            _config = config;
            _next = _nextSlow = DateTimeOffset.MinValue;
            if (!same)
            {
                _batches = null;
                _expensive = null;
                _state = new DatabaseState();
            }
            _state = _state with { Configured = config is not null, Name = config?.Name, Server = config?.Server, Databases = config?.Databases ?? [] };
        }
    }

    public DatabaseState Snapshot()
    {
        lock (_gate)
            return _state;
    }

    /// <summary>
    /// Starts a look when it is time for one and the last one has come back.
    /// </summary>
    public void Tick()
    {
        DatabaseConfig? config;
        var now = time.GetUtcNow();
        lock (_gate)
        {
            config = _config;
            if (config is null || now < _next)
                return;
        }
        if (Interlocked.CompareExchange(ref _sampling, 1, 0) != 0)
            return;
        lock (_gate)
            _next = now + TimeSpan.FromSeconds(config.SampleSeconds);

        _ = Task.Run(async () =>
        {
            try
            {
                await SampleAsync(config);
            }
            catch (Exception error)
            {
                logger.LogError(error, "Looking at the database failed");
            }
            finally
            {
                Volatile.Write(ref _sampling, 0);
            }
        });
    }

    /// <summary>
    /// One look, as <see cref="Tick"/> would start it.
    /// </summary>
    internal async Task SampleAsync(DatabaseConfig config)
    {
        var now = time.GetUtcNow();
        if (Connection(config) is not { } connection)
        {
            Offline(config, now, "The password in the configuration was protected on another machine and cannot be read here; type it again");
            return;
        }

        FastSample fast;
        var watch = Stopwatch.StartNew();
        try
        {
            fast = await source.FastAsync(connection, _stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error)
        {
            Offline(config, now, error.Message);
            return;
        }
        var response = watch.Elapsed.TotalMilliseconds;
        var missing = Missing(config, fast);
        fast = Scope(config, fast);

        SlowSample? slow = null;
        if (now >= _nextSlow)
        {
            try
            {
                slow = Scope(config, await source.SlowAsync(connection, _stopping.Token));
                _nextSlow = now + SlowInterval;
            }
            catch (Exception error) when (!_stopping.IsCancellationRequested)
            {
                logger.LogWarning("The backups, jobs and disks of the database could not be read: {Error}", error.Message);
            }
        }

        double? rate = null;
        if (fast.Resources?.Batches is { } count)
        {
            if (_batches is { } before && count >= before.Count && now > before.At)
                rate = (count - before.Count) / (now - before.At).TotalSeconds;
            _batches = (count, now);
        }

        List<SupervisorEvent> events = [];
        lock (_gate)
        {
            if (!ReferenceEquals(config, _config))
                return;
            var previous = _state;
            slow ??= previous.Slow;
            var alerts = Alerts(config, fast, slow, missing, previous.Alerts, now);
            if (previous.Online == false)
                events.Add(Raise(config, now, EventKind.DatabaseUp, "the instance answers again"));
            foreach (var alert in alerts.Where(alert => previous.Alerts.All(old => old.Key != alert.Key)))
                events.Add(Raise(config, now, EventKind.DatabaseAlert, Describe(alert)));
            foreach (var gone in previous.Alerts.Where(old => alerts.All(alert => alert.Key != old.Key)))
                events.Add(Raise(config, now, EventKind.DatabaseAlertCleared, Describe(gone)));

            _state = previous with
            {
                Online = true,
                Error = null,
                Since = previous.Online == true ? previous.Since : now,
                CheckedAt = now,
                ResponseMs = response,
                BatchesPerSecond = rate,
                Fast = fast,
                Slow = slow,
                SlowAt = ReferenceEquals(slow, previous.Slow) ? previous.SlowAt : now,
                Alerts = alerts
            };
        }

        var activity = fast.Activity ?? [];
        history?.Add(new DatabasePoint(now, true, response, fast.Resources?.CpuPercent, fast.Resources?.OtherCpuPercent, fast.Resources?.MemoryKb,
            fast.Sessions?.Sum(group => group.Sessions), activity.Count(item => item.Running), activity.Count(item => item.BlockedBy > 0), rate));
        Prune(config, now);
        foreach (var raised in events)
            Event?.Invoke(raised);
    }

    private void Offline(DatabaseConfig config, DateTimeOffset now, string error)
    {
        SupervisorEvent? raised = null;
        lock (_gate)
        {
            if (!ReferenceEquals(config, _config))
                return;
            var previous = _state;
            if (previous.Online != false)
                raised = Raise(config, now, EventKind.DatabaseDown, error);
            // What was seen last stays, to tell what the instance is; what asked for attention on it does not.
            _state = previous with { Online = false, Error = error, Since = previous.Online == false ? previous.Since : now, CheckedAt = now, ResponseMs = null, BatchesPerSecond = null, Alerts = [] };
            _batches = null;
        }
        history?.Add(new DatabasePoint(now, false, null, null, null, null, null, null, null, null));
        if (raised is not null)
            Event?.Invoke(raised);
    }

    /// <summary>
    /// The statements that cost the most, asked for when someone wants to see them and kept for a moment.
    /// </summary>
    public async Task<IReadOnlyList<ExpensiveQuery>> ExpensiveAsync()
    {
        DatabaseConfig? config;
        var now = time.GetUtcNow();
        lock (_gate)
        {
            config = _config;
            if (_expensive is { } kept && now - kept.At < ExpensiveFor)
                return kept.Queries;
        }
        if (config is null)
            throw new InvalidOperationException("No database is configured");
        if (Connection(config) is not { } connection)
            throw new InvalidOperationException("The password in the configuration cannot be read on this machine");

        var queries = await source.ExpensiveAsync(connection, config.Databases, _stopping.Token);
        lock (_gate)
            _expensive = (queries, now);
        return queries;
    }

    private DatabaseConnection? Connection(DatabaseConfig config)
    {
        var password = config.Password;
        if (Secret.IsProtected(password))
        {
            password = protector.Unprotect(password!);
            if (password is null)
                return null;
        }
        return new DatabaseConnection(config.Server, config.User, password, config.Encrypt, config.TrustServerCertificate);
    }

    private static bool Watched(DatabaseConfig config, string database) =>
        config.Databases.Count == 0 || config.Databases.Contains(database, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The databases the configuration names that the instance does not have.
    /// </summary>
    private static List<string> Missing(DatabaseConfig config, FastSample fast) =>
        fast.Databases is { } all ? [.. config.Databases.Where(name => all.All(database => !database.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))] : [];

    /// <summary>
    /// Only what is of the databases the configuration names, when it names any: an instance
    /// is often shared with what belongs to other environments. Whoever keeps a session of
    /// theirs waiting stays, whatever database it is in.
    /// </summary>
    private static FastSample Scope(DatabaseConfig config, FastSample fast)
    {
        if (config.Databases.Count == 0)
            return fast;
        var activity = fast.Activity?.Where(item => Watched(config, item.Database)).ToList();
        if (activity is not null)
        {
            var kept = activity.Select(item => item.SessionId).ToHashSet();
            // Up the chain: who blocks whoever blocks them.
            for (var added = true; added;)
            {
                added = false;
                foreach (var blocker in fast.Activity!.Where(item => !kept.Contains(item.SessionId) && activity.Any(waiting => waiting.BlockedBy == item.SessionId)).ToList())
                {
                    activity.Add(blocker);
                    kept.Add(blocker.SessionId);
                    added = true;
                }
            }
        }
        return fast with
        {
            Databases = fast.Databases?.Where(database => Watched(config, database.Name)).ToList(),
            Sessions = fast.Sessions?.Where(group => Watched(config, group.Database)).ToList(),
            Activity = activity
        };
    }

    private static SlowSample Scope(DatabaseConfig config, SlowSample slow)
    {
        if (config.Databases.Count == 0)
            return slow;
        return slow with
        {
            // The temporary database is used by every other one.
            Volumes = slow.Volumes?.Where(volume => volume.Databases.Count == 0 || volume.Databases.Any(name => Watched(config, name) || name.Equals("tempdb", StringComparison.OrdinalIgnoreCase))).ToList(),
            Backups = slow.Backups?.Where(backup => Watched(config, backup.Database)).ToList()
        };
    }

    private static List<DatabaseAlert> Alerts(DatabaseConfig config, FastSample fast, SlowSample? slow, IReadOnlyList<string> missing, IReadOnlyList<DatabaseAlert> before, DateTimeOffset now)
    {
        var alerts = new List<DatabaseAlert>();
        void Add(string kind, string subject, string severity, double? value)
        {
            var since = before.FirstOrDefault(alert => alert.Kind == kind && alert.Subject == subject)?.Since ?? now;
            alerts.Add(new DatabaseAlert(kind, subject, severity, value, since));
        }

        foreach (var name in missing)
            Add("Missing", name, "danger", null);

        foreach (var database in fast.Databases ?? [])
            if (!database.State.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
                Add("State", database.Name, "danger", null);

        var waiting = (fast.Activity ?? []).Where(item => item.BlockedBy > 0 && item.WaitMs >= config.BlockingSeconds * 1000L).ToList();
        if (waiting.Count > 0)
            Add("Blocking", "", "danger", waiting.Max(item => item.WaitMs) / 1000.0);

        foreach (var volume in slow?.Volumes ?? [])
            if (volume.TotalBytes > 0 && volume.FreeBytes * 100.0 / volume.TotalBytes < config.DiskFreePercent)
                Add("Disk", volume.Mount, "warn", Math.Round(volume.FreeBytes * 100.0 / volume.TotalBytes, 1));

        if (config.BackupHours > 0 && slow?.Backups is { } backups && fast.Databases is { } databases)
            foreach (var database in databases.Where(database => !database.System && database.State.Equals("ONLINE", StringComparison.OrdinalIgnoreCase)))
            {
                var full = backups.FirstOrDefault(backup => backup.Database.Equals(database.Name, StringComparison.OrdinalIgnoreCase))?.FullMinutes;
                if (full is null || full > config.BackupHours * 60)
                    Add("Backup", database.Name, "warn", full is null ? null : Math.Round(full.Value / 60.0, 1));
            }

        foreach (var job in slow?.Jobs ?? [])
            if (job is { Enabled: true, Outcome: 0 })
                Add("Job", job.Name, "warn", null);

        return alerts;
    }

    private static string Describe(DatabaseAlert alert) => alert.Kind switch
    {
        "Missing" => $"the instance has no database called {alert.Subject}",
        "State" => $"the database {alert.Subject} is not online",
        "Blocking" => $"a session has been kept waiting by another for {alert.Value:0} s",
        "Disk" => $"the disk {alert.Subject} has {alert.Value:0.#}% free",
        "Backup" => alert.Value is null ? $"the database {alert.Subject} has no full backup" : $"the last full backup of {alert.Subject} is {alert.Value:0.#} h old",
        "Job" => $"the job {alert.Subject} failed the last time it ran",
        _ => $"{alert.Kind} {alert.Subject}"
    };

    private static SupervisorEvent Raise(DatabaseConfig config, DateTimeOffset now, EventKind kind, string detail) =>
        new(now, kind, GroupPrefix + config.Name, null, detail);

    private void Prune(DatabaseConfig config, DateTimeOffset now)
    {
        if (history is null || now < _nextPrune)
            return;
        _nextPrune = now + TimeSpan.FromHours(1);
        history.Prune(now - TimeSpan.FromDays(config.RetentionDays));
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
