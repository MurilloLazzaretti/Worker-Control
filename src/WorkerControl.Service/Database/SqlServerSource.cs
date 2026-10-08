using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace WorkerControl.Service.Database;

/// <summary>
/// The health of a SQL Server instance, from its management views.
/// </summary>
internal sealed class SqlServerSource : IDatabaseSource
{
    private const int CommandSeconds = 10;

    public static string ConnectionString(DatabaseConnection connection, string database = "master")
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = connection.Server,
            InitialCatalog = database,
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

    // ---------------------------------------------------------------- objects

    public static string Kind(string variety) => variety switch
    {
        "U" => "Table",
        "V" => "View",
        "P" => "Procedure",
        "FN" or "IF" or "TF" => "Function",
        "T" or "TT" => "Type",
        _ => variety
    };

    private static CatalogObject Listed(DbDataReader row) =>
        new((int)Number(row, 0), Kind(Text(row, 3)), Text(row, 1), Text(row, 2), Text(row, 3), Moment(row, 4), Moment(row, 5),
            row.IsDBNull(6) ? null : Number(row, 6), row.IsDBNull(7) ? null : Number(row, 7));

    public async Task<IReadOnlyList<CatalogObject>> ObjectsAsync(DatabaseConnection connection, string database, CancellationToken stopping)
    {
        await using var sql = await OpenAsync(connection, stopping, database);
        List<CatalogObject> objects;
        try
        {
            objects = await ReadAsync(sql, Queries.Objects, stopping, Listed);
        }
        catch (SqlException) when (sql.State == System.Data.ConnectionState.Open)
        {
            // Without leave to see how big the tables are, the list still comes.
            objects = await ReadAsync(sql, Queries.ObjectsPlain, stopping, Listed);
        }
        objects.AddRange(await ReadAsync(sql, Queries.Types, stopping, Listed));
        return objects;
    }

    public async Task<CatalogDetail> ObjectAsync(DatabaseConnection connection, string database, CatalogObject target, CancellationToken stopping)
    {
        await using var sql = await OpenAsync(connection, stopping, database);
        var problems = new Dictionary<string, string>();
        Task<List<T>?> By<T>(string part, string text, int id, Func<DbDataReader, T> read) =>
            Part(sql, part, text, problems, stopping, command => command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.Int) { Value = id }), read);

        static RawType Raw(DbDataReader row, int at) => new(Text(row, at), row.IsDBNull(at + 1) ? null : Text(row, at + 1), (int)Number(row, at + 2), (int)Number(row, at + 3), (int)Number(row, at + 4));
        static string? Maybe(DbDataReader row, int at) => row.IsDBNull(at) ? null : Text(row, at);
        static bool Flag(DbDataReader row, int at) => !row.IsDBNull(at) && Convert.ToBoolean(row.GetValue(at));

        Task<List<CatalogColumn>?> ColumnsOf(int id) => By("Columns", Queries.Columns, id, row =>
            new CatalogColumn(Text(row, 0), Raw(row, 1), Flag(row, 6), Flag(row, 7), Maybe(row, 8), Maybe(row, 9), Maybe(row, 10), Maybe(row, 11), Flag(row, 12), Maybe(row, 13), Flag(row, 14), Maybe(row, 15)));

        async Task<List<CatalogIndex>> IndexesOf(int id)
        {
            var rows = await By("Indexes", Queries.Indexes, id, row => (Id: (int)Number(row, 0), Name: Text(row, 1), Variety: Text(row, 2), Unique: Flag(row, 3), Primary: Flag(row, 4),
                Constraint: Flag(row, 5), Disabled: Flag(row, 6), Filter: Maybe(row, 7), Column: new CatalogIndexColumn(Text(row, 8), Flag(row, 9), Flag(row, 10))));
            return [.. (rows ?? []).GroupBy(row => row.Id).Select(group => new CatalogIndex(group.First().Name, group.First().Variety, group.First().Unique, group.First().Primary,
                group.First().Constraint, group.First().Disabled, group.First().Filter, [.. group.Select(row => row.Column)]))];
        }

        Task<List<CatalogCheck>?> ChecksOf(int id) => By("Checks", Queries.Checks, id, row => new CatalogCheck(Text(row, 0), Text(row, 1), Flag(row, 2)));

        static CatalogReference Reference(DbDataReader row) => new(Maybe(row, 0), Text(row, 1), row.IsDBNull(2) ? null : Kind(Text(row, 2)), Maybe(row, 3));
        static List<CatalogReference> Sorted(List<CatalogReference>? references) =>
            [.. (references ?? []).OrderBy(reference => reference.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)];

        var collation = (await Part(sql, "Collation", Queries.Collation, problems, stopping, row => Maybe(row, 0)))?.FirstOrDefault();
        var detail = new CatalogDetail { Object = target };

        if (target.Kind == "Type")
        {
            var made = (await By("Type", Queries.TypeBase, target.Id, row => (Raw: new RawType(Text(row, 0), null, (int)Number(row, 1), (int)Number(row, 2), (int)Number(row, 3)), Nullable: Flag(row, 4), Table: Whole(row, 5))))?.FirstOrDefault();
            var usedBy = Sorted(await By("UsedBy", Queries.TypeUsedBy, target.Id, Reference));
            if (made is { Table: { } table })
            {
                detail = detail with { Columns = await ColumnsOf(table) ?? [], Indexes = await IndexesOf(table), Checks = await ChecksOf(table) ?? [], UsedBy = usedBy };
                detail = detail with { Script = SqlScript.TableType(detail, collation) };
            }
            else if (made is not null)
            {
                var from = SqlScript.TypeName(made.Value.Raw);
                detail = detail with { BaseType = from, UsedBy = usedBy, Script = SqlScript.ScalarType(target, from, made.Value.Nullable) };
            }
        }
        else
        {
            var uses = Sorted(await By("Uses", Queries.Uses, target.Id, Reference));
            var usedBy = Sorted(await By("UsedBy", Queries.UsedBy, target.Id, Reference));
            detail = detail with { Columns = target.Kind == "Procedure" ? [] : await ColumnsOf(target.Id) ?? [], Uses = uses, UsedBy = usedBy };

            if (target.Kind == "Table")
            {
                var keys = await By("ForeignKeys", Queries.ForeignKeys, target.Id, row => (Name: Text(row, 0), Column: Text(row, 1), Schema: Text(row, 2), Table: Text(row, 3), Referenced: Text(row, 4),
                    OnDelete: Text(row, 5), OnUpdate: Text(row, 6), Disabled: Flag(row, 7)));
                detail = detail with
                {
                    Indexes = await IndexesOf(target.Id),
                    Checks = await ChecksOf(target.Id) ?? [],
                    ForeignKeys = [.. (keys ?? []).GroupBy(key => key.Name).Select(group => new CatalogForeignKey(group.Key, [.. group.Select(key => key.Column)], group.First().Schema, group.First().Table,
                        [.. group.Select(key => key.Referenced)], group.First().OnDelete, group.First().OnUpdate, group.First().Disabled))],
                    Triggers = await By("Triggers", Queries.Triggers, target.Id, row => new CatalogTrigger(Text(row, 0), Flag(row, 1), Flag(row, 2), Maybe(row, 3))) ?? []
                };
                detail = detail with { Script = SqlScript.Table(detail, collation) };
            }
            else
            {
                var text = (await By("Definition", Queries.Definition, target.Id, row => Maybe(row, 0)))?.FirstOrDefault();
                var parameters = await By("Parameters", Queries.Parameters, target.Id, row => (Id: (int)Number(row, 0), Parameter: new CatalogParameter(Text(row, 1), Raw(row, 2), Flag(row, 7), Flag(row, 8)))) ?? [];
                var defaults = SqlScript.ParameterDefaults(text, parameters.Select(parameter => parameter.Parameter.Name));
                detail = detail with
                {
                    Parameters = [.. parameters.Where(parameter => parameter.Id > 0).Select(parameter => defaults.TryGetValue(parameter.Parameter.Name, out var value) ? parameter.Parameter with { Default = value } : parameter.Parameter)],
                    Returns = parameters.Where(parameter => parameter.Id == 0).Select(parameter => parameter.Parameter.Type).FirstOrDefault(),
                    Script = text is null ? null : text.Trim() + "\n",
                    ScriptSource = text is null ? "encrypted" : "instance",
                    Triggers = target.Kind == "View" ? await By("Triggers", Queries.Triggers, target.Id, row => new CatalogTrigger(Text(row, 0), Flag(row, 1), Flag(row, 2), Maybe(row, 3))) ?? [] : []
                };
            }
        }

        return detail with { Fingerprint = detail.Script is null ? null : SqlScript.Fingerprint(detail.Script), Problems = problems };
    }

    public async Task<ChangeAuthor?> WhoChangedAsync(DatabaseConnection connection, string database, int objectId, CancellationToken stopping)
    {
        try
        {
            await using var sql = await OpenAsync(connection, stopping, database);
            var found = await ReadAsync(sql, Queries.WhoChanged, stopping, command => command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.Int) { Value = objectId }),
                row => new ChangeAuthor(row.IsDBNull(0) ? null : Text(row, 0), row.IsDBNull(1) ? null : Text(row, 1), row.IsDBNull(2) ? null : Text(row, 2)));
            return found.FirstOrDefault();
        }
        catch (Exception error) when (error is SqlException or InvalidOperationException or InvalidCastException)
        {
            // Not every user may read the trace, and not every instance keeps one.
            return null;
        }
    }

    private static DateTime? Moment(DbDataReader row, int column) => row.IsDBNull(column) ? null : DateTime.SpecifyKind(row.GetDateTime(column), DateTimeKind.Utc);

    private static async Task<SqlConnection> OpenAsync(DatabaseConnection connection, CancellationToken stopping, string database = "master")
    {
        var sql = new SqlConnection(ConnectionString(connection, database));
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
    private static Task<List<T>?> Part<T>(SqlConnection sql, string name, string text, Dictionary<string, string> problems, CancellationToken stopping, Func<DbDataReader, T> read) =>
        Part(sql, name, text, problems, stopping, null, read);

    private static async Task<List<T>?> Part<T>(SqlConnection sql, string name, string text, Dictionary<string, string> problems, CancellationToken stopping, Action<SqlCommand>? prepare, Func<DbDataReader, T> read)
    {
        try
        {
            return await ReadAsync(sql, text, stopping, prepare, read);
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
