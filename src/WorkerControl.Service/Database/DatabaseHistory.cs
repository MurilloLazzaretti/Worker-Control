using Microsoft.Data.Sqlite;

namespace WorkerControl.Service.Database;

public sealed record DatabasePoint(DateTimeOffset At, bool Online, double? ResponseMs, double? CpuPercent, double? OtherCpuPercent, double? MemoryKb,
    double? Sessions, double? Running, double? Blocked, double? BatchesPerSecond);

/// <summary>
/// How the instance was doing over time, kept in a SQLite file next to the service.
/// </summary>
internal sealed class DatabaseHistory : IDisposable
{
    private const int MaxPoints = 360;

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public static DatabaseHistory? TryOpen(string directory, ILogger logger)
    {
        try
        {
            return new DatabaseHistory(directory);
        }
        catch (Exception error)
        {
            logger.LogError("The history of the database is not being kept, because its file could not be opened: {Error}", error.Message);
            return null;
        }
    }

    private DatabaseHistory(string directory)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "database.db"), Pooling = false }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS sample (
                at INTEGER PRIMARY KEY,
                online INTEGER NOT NULL,
                response REAL, cpu REAL, other_cpu REAL, memory REAL, sessions REAL, running REAL, blocked REAL, batches REAL);
            """;
        command.ExecuteNonQuery();
    }

    public void Add(DatabasePoint point)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO sample VALUES ($at, $online, $response, $cpu, $other, $memory, $sessions, $running, $blocked, $batches)";
            command.Parameters.AddWithValue("$at", point.At.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$online", point.Online ? 1 : 0);
            command.Parameters.AddWithValue("$response", (object?)point.ResponseMs ?? DBNull.Value);
            command.Parameters.AddWithValue("$cpu", (object?)point.CpuPercent ?? DBNull.Value);
            command.Parameters.AddWithValue("$other", (object?)point.OtherCpuPercent ?? DBNull.Value);
            command.Parameters.AddWithValue("$memory", (object?)point.MemoryKb ?? DBNull.Value);
            command.Parameters.AddWithValue("$sessions", (object?)point.Sessions ?? DBNull.Value);
            command.Parameters.AddWithValue("$running", (object?)point.Running ?? DBNull.Value);
            command.Parameters.AddWithValue("$blocked", (object?)point.Blocked ?? DBNull.Value);
            command.Parameters.AddWithValue("$batches", (object?)point.BatchesPerSecond ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The period in at most a few hundred points, each the average of its slice. A slice in
    /// which the instance did not answer once is not online.
    /// </summary>
    public IReadOnlyList<DatabasePoint> Read(DateTimeOffset from, DateTimeOffset to)
    {
        var seconds = Math.Max(1, (long)(to - from).TotalSeconds);
        var slice = Math.Max(1, (long)Math.Ceiling(seconds / (double)MaxPoints));
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT (at / $slice) * $slice, MIN(online), AVG(response), AVG(cpu), AVG(other_cpu), AVG(memory), AVG(sessions), AVG(running), MAX(blocked), AVG(batches)
                FROM sample WHERE at >= $from AND at <= $to GROUP BY at / $slice ORDER BY 1
                """;
            command.Parameters.AddWithValue("$slice", slice);
            command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
            using var reader = command.ExecuteReader();
            var points = new List<DatabasePoint>();
            double? Value(int column) => reader.IsDBNull(column) ? null : reader.GetDouble(column);
            while (reader.Read())
                points.Add(new DatabasePoint(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), reader.GetInt64(1) == 1,
                    Value(2), Value(3), Value(4), Value(5), Value(6), Value(7), Value(8), Value(9)));
            return points;
        }
    }

    public void Prune(DateTimeOffset before)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM sample WHERE at < $before";
            command.Parameters.AddWithValue("$before", before.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
    }

    public void Dispose() => _connection.Dispose();
}
