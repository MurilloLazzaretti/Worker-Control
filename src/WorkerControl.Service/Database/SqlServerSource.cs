using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace WorkerControl.Service.Database;

/// <summary>
/// The health of a SQL Server instance, from its management views.
/// </summary>
internal sealed class SqlServerSource : IDatabaseSource
{
    private const int CommandSeconds = 10;

    public static string ConnectionString(DatabaseConnection connection)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = connection.Server,
            InitialCatalog = "master",
            ApplicationName = "WorkerControl",
            ConnectTimeout = 5,
            Encrypt = connection.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = connection.TrustServerCertificate,
            // One look at a time: nothing here needs more.
            MaxPoolSize = 3
        };
        if (string.IsNullOrEmpty(connection.User))
            builder.IntegratedSecurity = true;
        else
        {
            builder.UserID = connection.User;
            builder.Password = connection.Password ?? "";
        }
        return builder.ConnectionString;
    }

    public async Task<FastSample> FastAsync(DatabaseConnection connection, CancellationToken stopping)
    {
        await using var sql = await OpenAsync(connection, stopping);
        var problems = new Dictionary<string, string>();

        var instance = (await Part(sql, "Instance", Queries.Instance, problems, stopping, row =>
            new InstanceInfo(row.GetString(0), Product(row.GetString(4)), Text(row, 1), Text(row, 2), Text(row, 3))))?.FirstOrDefault();

        var machine = (await Part(sql, "Machine", Queries.Machine, problems, stopping, row =>
            new InstanceMachine(Number(row, 0), (int)Number(row, 1), Number(row, 2))))?.FirstOrDefault();

        var cpu = (await Part(sql, "Cpu", Queries.Cpu, problems, stopping, row =>
            (Sql: row.IsDBNull(0) ? (int?)null : row.GetInt32(0), Idle: row.IsDBNull(1) ? (int?)null : row.GetInt32(1))))?.FirstOrDefault();
        var memory = (await Part(sql, "Memory", Queries.Memory, problems, stopping, row => (long?)Number(row, 0)))?.FirstOrDefault();
        var counters = await Part(sql, "Counters", Queries.Counters, problems, stopping, row => (Name: Text(row, 0), Instance: Text(row, 1), Value: Number(row, 2)));
        long? Counter(string name) => counters?.Where(counter => counter.Name == name).Select(counter => (long?)counter.Value).FirstOrDefault();

        var logUsed = counters?.Where(counter => counter.Name == "Percent Log Used")
            .GroupBy(counter => counter.Instance, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (double)group.First().Value, StringComparer.OrdinalIgnoreCase);

        var databases = await Part(sql, "Databases", Queries.Databases, problems, stopping, row =>
            new DatabaseInfo(Text(row, 0), Text(row, 1), Text(row, 2), Text(row, 3), Convert.ToBoolean(row.GetValue(4)), (int)Number(row, 5), Number(row, 6) == 1, Number(row, 7), Number(row, 8)));

        return new FastSample
        {
            Instance = instance,
            Machine = machine,
            Resources = new InstanceResources
            {
                CpuPercent = cpu?.Sql,
                OtherCpuPercent = cpu is { Sql: { } used, Idle: { } idle } ? Math.Max(0, 100 - idle - used) : null,
                MemoryKb = memory ?? Counter("Total Server Memory (KB)"),
                TargetMemoryKb = Counter("Target Server Memory (KB)"),
                PageLifeSeconds = Counter("Page life expectancy"),
                Connections = Counter("User Connections"),
                Batches = Counter("Batch Requests/sec")
            },
            Databases = databases?.Select(database => logUsed is not null && logUsed.TryGetValue(database.Name, out var used) ? database with { LogUsedPercent = used } : database).ToList(),
            Sessions = await Part(sql, "Sessions", Queries.Sessions, problems, stopping, row =>
                new SessionGroup(Text(row, 0), Text(row, 1), Text(row, 2), Text(row, 3), (int)Number(row, 4), (int)Number(row, 5), (int)Number(row, 6))),
            Activity = await Part(sql, "Activity", Queries.Activity, problems, stopping, row =>
                new Activity((int)Number(row, 0), Text(row, 1), Text(row, 2), Number(row, 3), Number(row, 4), Number(row, 5), Text(row, 6), Number(row, 7),
                    (int)Number(row, 8), Text(row, 9), Text(row, 10), Text(row, 11), Text(row, 12), (int)Number(row, 13), Number(row, 14) == 1, SqlText.Mask(Text(row, 15)))),
            Problems = problems
        };
    }

    public async Task<SlowSample> SlowAsync(DatabaseConnection connection, CancellationToken stopping)
    {
        await using var sql = await OpenAsync(connection, stopping);
        var problems = new Dictionary<string, string>();

        var files = await Part(sql, "Volumes", Queries.Volumes, problems, stopping, row => (Volume: new Volume(Text(row, 0), Text(row, 1), Number(row, 2), Number(row, 3)), Database: Text(row, 4)));
        var volumes = files?.GroupBy(file => file.Volume.Mount, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().Volume with { Databases = [.. group.Select(file => file.Database).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)] })
            .OrderBy(volume => volume.Mount, StringComparer.OrdinalIgnoreCase).ToList();
        var backups = await Part(sql, "Backups", Queries.Backups, problems, stopping, row => (Database: Text(row, 0), Type: Text(row, 1).Trim(), Minutes: (int)Number(row, 2)));
        var jobs = await Part(sql, "Jobs", Queries.Jobs, problems, stopping, row =>
            new Job(Text(row, 0), Convert.ToBoolean(row.GetValue(1)), Whole(row, 2), Whole(row, 3), Whole(row, 4), Text(row, 5)));

        return new SlowSample
        {
            Volumes = volumes,
            Backups = backups?.GroupBy(backup => backup.Database, StringComparer.OrdinalIgnoreCase).Select(group =>
            {
                int? Last(string type) => group.Where(backup => backup.Type == type).Select(backup => (int?)backup.Minutes).FirstOrDefault();
                return new Backups(group.Key, Last("D"), Last("I"), Last("L"));
            }).ToList(),
            Jobs = jobs,
            Problems = problems
        };
    }

    public async Task<IReadOnlyList<ExpensiveQuery>> ExpensiveAsync(DatabaseConnection connection, IReadOnlyList<string> databases, CancellationToken stopping)
    {
        await using var sql = await OpenAsync(connection, stopping);
        var names = databases.Count == 0 ? "" : "|" + string.Join("|", databases.Select(name => name.ToLowerInvariant())) + "|";
        return await ReadAsync(sql, Queries.Expensive, stopping, command => command.Parameters.Add(new SqlParameter("@names", System.Data.SqlDbType.NVarChar, 4000) { Value = names }), row =>
            new ExpensiveQuery(Text(row, 0), Text(row, 1), Number(row, 2), Number(row, 3) / 1000.0, Number(row, 4) / 1000.0, Number(row, 5), Number(row, 6) / 1000.0,
                (int)Number(row, 7), Number(row, 8) == 1, Number(row, 9) == 1, Number(row, 10) == 1, SqlText.Mask(Text(row, 11))));
    }

    private static async Task<SqlConnection> OpenAsync(DatabaseConnection connection, CancellationToken stopping)
    {
        var sql = new SqlConnection(ConnectionString(connection));
        try
        {
            await sql.OpenAsync(stopping);
            await using var command = sql.CreateCommand();
            command.CommandText = Queries.Preamble;
            command.CommandTimeout = CommandSeconds;
            await command.ExecuteNonQueryAsync(stopping);
            return sql;
        }
        catch
        {
            await sql.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// One part of a sample. What the user of the connection may not read, or the instance
    /// does not have, is told apart from the instance being unreachable: the rest still comes.
    /// </summary>
    private static async Task<List<T>?> Part<T>(SqlConnection sql, string name, string text, Dictionary<string, string> problems, CancellationToken stopping, Func<DbDataReader, T> read)
    {
        try
        {
            return await ReadAsync(sql, text, stopping, read);
        }
        catch (SqlException error) when (sql.State == System.Data.ConnectionState.Open)
        {
            problems[name] = error.Message;
            return null;
        }
        catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException)
        {
            problems[name] = error.Message;
            return null;
        }
    }

    private static Task<List<T>> ReadAsync<T>(SqlConnection sql, string text, CancellationToken stopping, Func<DbDataReader, T> read) =>
        ReadAsync(sql, text, stopping, null, read);

    private static async Task<List<T>> ReadAsync<T>(SqlConnection sql, string text, CancellationToken stopping, Action<SqlCommand>? prepare, Func<DbDataReader, T> read)
    {
        await using var command = sql.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = CommandSeconds;
        prepare?.Invoke(command);
        await using var reader = await command.ExecuteReaderAsync(stopping);
        var rows = new List<T>();
        while (await reader.ReadAsync(stopping))
            rows.Add(read(reader));
        return rows;
    }

    private static string Text(DbDataReader row, int column) => row.IsDBNull(column) ? "" : Convert.ToString(row.GetValue(column)) ?? "";

    private static long Number(DbDataReader row, int column) => row.IsDBNull(column) ? 0 : Convert.ToInt64(row.GetValue(column));

    private static int? Whole(DbDataReader row, int column) => row.IsDBNull(column) ? null : Convert.ToInt32(row.GetValue(column));

    /// <summary>
    /// "Microsoft SQL Server 2019 (RTM-CU18) ..." as "SQL Server 2019".
    /// </summary>
    internal static string Product(string version)
    {
        var line = version.Split('\n')[0].Trim();
        var cut = line.IndexOf('(');
        var name = (cut > 0 ? line[..cut] : line).Trim();
        return name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) ? name["Microsoft ".Length..] : name;
    }
}
