using Microsoft.Data.Sqlite;

namespace WorkerControl.Service.Traffic;

/// <summary>
/// What is counted of the requests that share a route, in one stretch of time.
/// </summary>
internal sealed class Tally
{
    /// <summary>
    /// Upper limits, in milliseconds, of the ranges the answer times are counted in. The last
    /// range has none. Enough to tell the median and the 95th and 99th percentiles.
    /// </summary>
    public static readonly double[] Limits = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];

    public long Count;
    public long S2, S3, S4, S5;
    public long Bytes;
    public double Seconds;
    public readonly long[] Times = new long[Limits.Length + 1];

    public void Add(int status, long bytes, double seconds)
    {
        Count++;
        switch (status / 100)
        {
            case 2: S2++; break;
            case 3: S3++; break;
            case 4: S4++; break;
            case 5: S5++; break;
        }
        Bytes += bytes;
        // A connection that was handed over to something else (a web socket) lasts for as long
        // as it is used; that is not an answer time.
        if (status == 101)
            return;
        Seconds += seconds;
        var milliseconds = seconds * 1000;
        var range = 0;
        while (range < Limits.Length && milliseconds > Limits[range])
            range++;
        Times[range]++;
    }

    public void Add(Tally other)
    {
        Count += other.Count;
        S2 += other.S2;
        S3 += other.S3;
        S4 += other.S4;
        S5 += other.S5;
        Bytes += other.Bytes;
        Seconds += other.Seconds;
        for (var i = 0; i < Times.Length; i++)
            Times[i] += other.Times[i];
    }

    /// <summary>
    /// The answer time, in milliseconds, that the given share of the requests stayed under.
    /// Read from the ranges, so it is as fine as they are. Null with no requests.
    /// </summary>
    public double? Percentile(double share)
    {
        var timed = Times.Sum();
        if (timed == 0)
            return null;
        var target = share * timed;
        long seen = 0;
        for (var i = 0; i < Times.Length; i++)
        {
            if (Times[i] == 0)
                continue;
            if (seen + Times[i] >= target)
            {
                var low = i == 0 ? 0 : Limits[i - 1];
                // Beyond the last limit there is no telling how far; the limit itself is said.
                if (i == Limits.Length)
                    return low;
                var inside = (target - seen) / Times[i];
                return Math.Round(low + (Limits[i] - low) * inside, 1);
            }
            seen += Times[i];
        }
        return Limits[^1];
    }

    public double? Average => Times.Sum() is var timed and > 0 ? Math.Round(Seconds / timed * 1000, 1) : null;
}

internal sealed record TrafficFilter(string? Kind = null, string? Host = null, string? App = null, string? Route = null, string? Method = null);

internal sealed record StoredError(DateTimeOffset At, string Host, string Method, string Path, string Route, string App, int Status, string Upstream, double Milliseconds);

/// <summary>
/// The traffic, kept by the minute and, once it is old, by the hour, in a SQLite file of its
/// own next to the service.
/// </summary>
internal sealed class TrafficStore : IDisposable
{
    public const int Minute = 60;
    public const int Hour = 3600;

    private static readonly string TimeColumns = string.Join(", ", Enumerable.Range(0, Tally.Limits.Length + 1).Select(i => "h" + i));
    private static readonly string Sums = "SUM(count), SUM(s2), SUM(s3), SUM(s4), SUM(s5), SUM(bytes), SUM(rt), " +
        string.Join(", ", Enumerable.Range(0, Tally.Limits.Length + 1).Select(i => $"SUM(h{i})"));

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public TrafficStore(string directory)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "traffic.db"), Pooling = false }.ToString());
        _connection.Open();
        Execute($"""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS traffic (
                res INTEGER NOT NULL, at INTEGER NOT NULL, host TEXT NOT NULL, method TEXT NOT NULL, route TEXT NOT NULL,
                kind TEXT NOT NULL, app TEXT NOT NULL, upstream TEXT NOT NULL,
                count INTEGER NOT NULL, s2 INTEGER NOT NULL, s3 INTEGER NOT NULL, s4 INTEGER NOT NULL, s5 INTEGER NOT NULL,
                bytes INTEGER NOT NULL, rt REAL NOT NULL,
                {string.Join(", ", Enumerable.Range(0, Tally.Limits.Length + 1).Select(i => $"h{i} INTEGER NOT NULL"))},
                PRIMARY KEY (res, at, host, method, route, upstream)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS traffic_at ON traffic (at);
            CREATE TABLE IF NOT EXISTS traffic_ip (
                at INTEGER NOT NULL, host TEXT NOT NULL, kind TEXT NOT NULL, app TEXT NOT NULL, ip TEXT NOT NULL,
                PRIMARY KEY (at, host, kind, app, ip)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS traffic_error (
                id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, host TEXT NOT NULL, method TEXT NOT NULL, path TEXT NOT NULL,
                route TEXT NOT NULL, app TEXT NOT NULL, status INTEGER NOT NULL, upstream TEXT NOT NULL, rt REAL NOT NULL);
            """);
    }

    /// <summary>
    /// Adds what was counted since the last time. Everything goes in, or nothing does.
    /// </summary>
    public void Add(IReadOnlyDictionary<(long Minute, RouteKey Key), Tally> tallies, IReadOnlyCollection<(long Hour, string Host, string Kind, string App, string Ip)> addresses,
        IReadOnlyList<StoredError> errors, int keepErrors)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            using (var command = _connection.CreateCommand())
            {
                var times = Enumerable.Range(0, Tally.Limits.Length + 1).ToList();
                command.CommandText = $"""
                    INSERT INTO traffic (res, at, host, method, route, kind, app, upstream, count, s2, s3, s4, s5, bytes, rt, {TimeColumns})
                    VALUES ({Minute}, $at, $host, $method, $route, $kind, $app, $upstream, $count, $s2, $s3, $s4, $s5, $bytes, $rt, {string.Join(", ", times.Select(i => "$h" + i))})
                    ON CONFLICT DO UPDATE SET count = count + excluded.count, s2 = s2 + excluded.s2, s3 = s3 + excluded.s3, s4 = s4 + excluded.s4, s5 = s5 + excluded.s5,
                        bytes = bytes + excluded.bytes, rt = rt + excluded.rt, {string.Join(", ", times.Select(i => $"h{i} = h{i} + excluded.h{i}"))}
                    """;
                var names = new[] { "$at", "$host", "$method", "$route", "$kind", "$app", "$upstream", "$count", "$s2", "$s3", "$s4", "$s5", "$bytes", "$rt" }
                    .Concat(times.Select(i => "$h" + i)).ToDictionary(name => name, name => command.Parameters.Add(name, SqliteType.Text));
                foreach (var ((minute, key), tally) in tallies)
                {
                    names["$at"].Value = minute;
                    names["$host"].Value = key.Host;
                    names["$method"].Value = key.Method;
                    names["$route"].Value = key.Route;
                    names["$kind"].Value = key.Kind;
                    names["$app"].Value = key.App;
                    names["$upstream"].Value = key.Upstream;
                    names["$count"].Value = tally.Count;
                    names["$s2"].Value = tally.S2;
                    names["$s3"].Value = tally.S3;
                    names["$s4"].Value = tally.S4;
                    names["$s5"].Value = tally.S5;
                    names["$bytes"].Value = tally.Bytes;
                    names["$rt"].Value = tally.Seconds;
                    foreach (var i in times)
                        names["$h" + i].Value = tally.Times[i];
                    command.ExecuteNonQuery();
                }
            }

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "INSERT OR IGNORE INTO traffic_ip (at, host, kind, app, ip) VALUES ($at, $host, $kind, $app, $ip)";
                var at = command.Parameters.Add("$at", SqliteType.Integer);
                var host = command.Parameters.Add("$host", SqliteType.Text);
                var kind = command.Parameters.Add("$kind", SqliteType.Text);
                var app = command.Parameters.Add("$app", SqliteType.Text);
                var ip = command.Parameters.Add("$ip", SqliteType.Text);
                foreach (var address in addresses)
                {
                    (at.Value, host.Value, kind.Value, app.Value, ip.Value) = (address.Hour, address.Host, address.Kind, address.App, address.Ip);
                    command.ExecuteNonQuery();
                }
            }

            if (errors.Count > 0)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "INSERT INTO traffic_error (at, host, method, path, route, app, status, upstream, rt) VALUES ($at, $host, $method, $path, $route, $app, $status, $upstream, $rt)";
                var parameters = new[] { "$at", "$host", "$method", "$path", "$route", "$app", "$status", "$upstream", "$rt" }.ToDictionary(name => name, name => command.Parameters.Add(name, SqliteType.Text));
                foreach (var error in errors)
                {
                    parameters["$at"].Value = error.At.ToUnixTimeMilliseconds();
                    parameters["$host"].Value = error.Host;
                    parameters["$method"].Value = error.Method;
                    parameters["$path"].Value = error.Path;
                    parameters["$route"].Value = error.Route;
                    parameters["$app"].Value = error.App;
                    parameters["$status"].Value = error.Status;
                    parameters["$upstream"].Value = error.Upstream;
                    parameters["$rt"].Value = error.Milliseconds;
                    command.ExecuteNonQuery();
                }
                using var trim = _connection.CreateCommand();
                trim.CommandText = "DELETE FROM traffic_error WHERE id <= (SELECT MAX(id) FROM traffic_error) - $keep";
                trim.Parameters.AddWithValue("$keep", keepErrors);
                trim.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>
    /// Turns the minutes that are old enough into hours and lets go of what is past keeping.
    /// </summary>
    public void Maintain(DateTimeOffset now, TimeSpan keepMinutesFor, TimeSpan keepFor)
    {
        var minutesBefore = now.Subtract(keepMinutesFor).ToUnixTimeSeconds() / Hour * Hour;
        var before = now.Subtract(keepFor).ToUnixTimeSeconds();
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var times = Enumerable.Range(0, Tally.Limits.Length + 1).ToList();
            Execute($"""
                INSERT INTO traffic (res, at, host, method, route, kind, app, upstream, count, s2, s3, s4, s5, bytes, rt, {TimeColumns})
                SELECT {Hour}, at - at % {Hour}, host, method, route, MIN(kind), MIN(app), upstream, {Sums}
                FROM traffic WHERE res = {Minute} AND at < {minutesBefore}
                GROUP BY at - at % {Hour}, host, method, route, upstream
                ON CONFLICT DO UPDATE SET count = count + excluded.count, s2 = s2 + excluded.s2, s3 = s3 + excluded.s3, s4 = s4 + excluded.s4, s5 = s5 + excluded.s5,
                    bytes = bytes + excluded.bytes, rt = rt + excluded.rt, {string.Join(", ", times.Select(i => $"h{i} = h{i} + excluded.h{i}"))};
                DELETE FROM traffic WHERE res = {Minute} AND at < {minutesBefore};
                DELETE FROM traffic WHERE at < {before};
                DELETE FROM traffic_ip WHERE at < {before};
                """);
            transaction.Commit();
        }
    }

    /// <summary>
    /// The traffic of a period, added up by whatever the columns say: a stretch of time, an
    /// application, a route. Each row is what it was grouped by, then its tally.
    /// </summary>
    public List<(object[] Group, Tally Tally)> Query(DateTimeOffset from, DateTimeOffset to, TrafficFilter filter, string[] groupBy, string? search = null, int limit = 10000)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            var where = Where(command, from, to, filter);
            if (!string.IsNullOrWhiteSpace(search))
            {
                where += " AND (route LIKE $search OR app LIKE $search)";
                command.Parameters.AddWithValue("$search", "%" + search.Trim().Replace("%", "").Replace("_", "\\_") + "%");
            }
            var columns = string.Join(", ", groupBy);
            command.CommandText = groupBy.Length == 0
                ? $"SELECT {Sums} FROM traffic WHERE {where}"
                : $"SELECT {columns}, {Sums} FROM traffic WHERE {where} GROUP BY {columns} ORDER BY SUM(count) DESC LIMIT {limit}";

            var rows = new List<(object[], Tally)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(groupBy.Length))
                    continue;
                var group = new object[groupBy.Length];
                for (var i = 0; i < groupBy.Length; i++)
                    group[i] = reader.GetValue(i);
                var at = groupBy.Length;
                var tally = new Tally
                {
                    Count = reader.GetInt64(at),
                    S2 = reader.GetInt64(at + 1),
                    S3 = reader.GetInt64(at + 2),
                    S4 = reader.GetInt64(at + 3),
                    S5 = reader.GetInt64(at + 4),
                    Bytes = reader.GetInt64(at + 5),
                    Seconds = reader.GetDouble(at + 6)
                };
                for (var i = 0; i < tally.Times.Length; i++)
                    tally.Times[i] = reader.GetInt64(at + 7 + i);
                rows.Add((group, tally));
            }
            return rows;
        }
    }

    /// <summary>
    /// How many different addresses asked for something in a period: in all, and by application.
    /// </summary>
    public (long All, Dictionary<string, long> ByApp) Users(DateTimeOffset from, DateTimeOffset to, TrafficFilter filter)
    {
        lock (_gate)
        {
            long all = 0;
            var byApp = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var grouped in new[] { false, true })
            {
                using var command = _connection.CreateCommand();
                var where = "at >= $from AND at < $to";
                command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds() / Hour * Hour);
                command.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
                if (filter.Kind is not null)
                {
                    where += " AND kind = $kind";
                    command.Parameters.AddWithValue("$kind", filter.Kind);
                }
                if (filter.Host is not null)
                {
                    where += " AND host = $host";
                    command.Parameters.AddWithValue("$host", filter.Host);
                }
                if (filter.App is not null)
                {
                    where += " AND app = $app";
                    command.Parameters.AddWithValue("$app", filter.App);
                }
                command.CommandText = grouped
                    ? $"SELECT app, COUNT(DISTINCT ip) FROM traffic_ip WHERE {where} GROUP BY app"
                    : $"SELECT COUNT(DISTINCT ip) FROM traffic_ip WHERE {where}";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (grouped)
                        byApp[reader.GetString(0)] = reader.GetInt64(1);
                    else
                        all = reader.GetInt64(0);
                }
            }
            return (all, byApp);
        }
    }

    public List<StoredError> Errors(int limit, TrafficFilter filter)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            var where = "1 = 1";
            if (filter.Host is not null)
            {
                where += " AND host = $host";
                command.Parameters.AddWithValue("$host", filter.Host);
            }
            if (filter.App is not null)
            {
                where += " AND app = $app";
                command.Parameters.AddWithValue("$app", filter.App);
            }
            command.CommandText = $"SELECT at, host, method, path, route, app, status, upstream, rt FROM traffic_error WHERE {where} ORDER BY id DESC LIMIT {Math.Clamp(limit, 1, 1000)}";
            var errors = new List<StoredError>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                errors.Add(new StoredError(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetString(7), reader.GetDouble(8)));
            }
            return errors;
        }
    }

    private static string Where(SqliteCommand command, DateTimeOffset from, DateTimeOffset to, TrafficFilter filter)
    {
        var where = "at >= $from AND at < $to";
        command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
        foreach (var (column, value) in new[] { ("kind", filter.Kind), ("host", filter.Host), ("app", filter.App), ("route", filter.Route), ("method", filter.Method) })
        {
            if (value is null)
                continue;
            where += $" AND {column} = ${column}";
            command.Parameters.AddWithValue("$" + column, value);
        }
        return where;
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
}
