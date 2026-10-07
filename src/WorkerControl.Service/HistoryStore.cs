using Microsoft.Data.Sqlite;
using WorkerControl.Core;

namespace WorkerControl.Service;

public sealed record StoredEvent(long Id, DateTimeOffset At, string Kind, string? Group, int? ProcessId, string Detail);

public sealed record StoredHealth(DateTimeOffset At, string Group, int ProcessId, string State, double UptimeSeconds, double? CpuPercent, long? MemoryBytes, double? KeepAliveMs);

/// <summary>
/// What happened and how the workers were doing, kept in a SQLite file next to the service.
/// </summary>
internal sealed class HistoryStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private readonly ILogger _logger;

    /// <summary>
    /// Null when the history cannot be kept on this machine. The supervision goes on without it.
    /// </summary>
    public static HistoryStore? TryOpen(string directory, ILogger logger)
    {
        try
        {
            return new HistoryStore(directory, logger);
        }
        catch (Exception error)
        {
            logger.LogError("The history is not being kept, because its file could not be opened: {Error}", error.Message);
            return null;
        }
    }

    private HistoryStore(string directory, ILogger logger)
    {
        _logger = logger;
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "workercontrol.db"),
            Pooling = false
        }.ToString());
        _connection.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                at INTEGER NOT NULL,
                kind TEXT NOT NULL,
                grp TEXT,
                pid INTEGER,
                detail TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS events_at ON events (at);
            CREATE TABLE IF NOT EXISTS health (
                at INTEGER NOT NULL,
                grp TEXT NOT NULL,
                pid INTEGER NOT NULL,
                state TEXT NOT NULL,
                uptime REAL NOT NULL,
                cpu REAL,
                memory INTEGER,
                keepalive REAL);
            CREATE INDEX IF NOT EXISTS health_at ON health (at);
            CREATE INDEX IF NOT EXISTS health_pid ON health (pid, at);
            """);
    }

    public void Add(SupervisorEvent e)
    {
        Guarded(() =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO events (at, kind, grp, pid, detail) VALUES ($at, $kind, $grp, $pid, $detail)";
            command.Parameters.AddWithValue("$at", e.At.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$kind", e.Kind.ToString());
            command.Parameters.AddWithValue("$grp", (object?)e.Group ?? DBNull.Value);
            command.Parameters.AddWithValue("$pid", (object?)e.ProcessId ?? DBNull.Value);
            command.Parameters.AddWithValue("$detail", e.Detail);
            command.ExecuteNonQuery();
        });
    }

    public void Add(IReadOnlyList<StoredHealth> samples)
    {
        if (samples.Count == 0)
            return;
        Guarded(() =>
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var sample in samples)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO health (at, grp, pid, state, uptime, cpu, memory, keepalive) VALUES ($at, $grp, $pid, $state, $uptime, $cpu, $memory, $keepalive)";
                command.Parameters.AddWithValue("$at", sample.At.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$grp", sample.Group);
                command.Parameters.AddWithValue("$pid", sample.ProcessId);
                command.Parameters.AddWithValue("$state", sample.State);
                command.Parameters.AddWithValue("$uptime", sample.UptimeSeconds);
                command.Parameters.AddWithValue("$cpu", (object?)sample.CpuPercent ?? DBNull.Value);
                command.Parameters.AddWithValue("$memory", (object?)sample.MemoryBytes ?? DBNull.Value);
                command.Parameters.AddWithValue("$keepalive", (object?)sample.KeepAliveMs ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        });
    }

    /// <summary>
    /// The most recent events first.
    /// </summary>
    public IReadOnlyList<StoredEvent> Events(string? group, string? kind, DateTimeOffset? from, DateTimeOffset? to, int limit)
    {
        var found = new List<StoredEvent>();
        Guarded(() =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, at, kind, grp, pid, detail FROM events
                WHERE ($grp IS NULL OR grp = $grp) AND ($kind IS NULL OR kind = $kind)
                  AND ($from IS NULL OR at >= $from) AND ($to IS NULL OR at <= $to)
                ORDER BY id DESC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$grp", (object?)group ?? DBNull.Value);
            command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
            command.Parameters.AddWithValue("$from", (object?)from?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            command.Parameters.AddWithValue("$to", (object?)to?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                found.Add(new StoredEvent(
                    reader.GetInt64(0),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.GetString(5)));
            }
        });
        return found;
    }

    /// <summary>
    /// The measurements of a worker or of a whole group, oldest first.
    /// </summary>
    public IReadOnlyList<StoredHealth> Health(string? group, int? processId, DateTimeOffset? from, DateTimeOffset? to, int limit)
    {
        var found = new List<StoredHealth>();
        Guarded(() =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT at, grp, pid, state, uptime, cpu, memory, keepalive FROM (
                    SELECT * FROM health
                    WHERE ($grp IS NULL OR grp = $grp) AND ($pid IS NULL OR pid = $pid)
                      AND ($from IS NULL OR at >= $from) AND ($to IS NULL OR at <= $to)
                    ORDER BY at DESC LIMIT $limit)
                ORDER BY at
                """;
            command.Parameters.AddWithValue("$grp", (object?)group ?? DBNull.Value);
            command.Parameters.AddWithValue("$pid", (object?)processId ?? DBNull.Value);
            command.Parameters.AddWithValue("$from", (object?)from?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            command.Parameters.AddWithValue("$to", (object?)to?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                found.Add(new StoredHealth(
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetDouble(4),
                    reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetDouble(7)));
            }
        });
        return found;
    }

    public void Prune(DateTimeOffset eventsBefore, DateTimeOffset healthBefore)
    {
        Guarded(() =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM events WHERE at < $events; DELETE FROM health WHERE at < $health";
            command.Parameters.AddWithValue("$events", eventsBefore.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$health", healthBefore.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        });
    }

    public void Dispose()
    {
        lock (_gate)
            _connection.Dispose();
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The history is worth having, but never worth stopping the supervision for.
    /// </summary>
    private void Guarded(Action work)
    {
        lock (_gate)
        {
            try
            {
                work();
            }
            catch (SqliteException error)
            {
                _logger.LogWarning("The history could not be read or written: {Error}", error.Message);
            }
        }
    }
}
