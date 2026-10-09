using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;
using WorkerControl.Service.Database;

namespace WorkerControl.Service.Tests;

/// <summary>
/// An instance that says whatever the test wants.
/// </summary>
internal sealed class PretendInstance : IDatabaseSource
{
    public string? Unreachable { get; set; }
    public FastSample Fast { get; set; } = Healthy();
    public SlowSample Slow { get; set; } = new() { Volumes = [], Backups = [], Jobs = [] };
    public List<ExpensiveQuery> Expensive { get; } = [];
    public List<DatabaseConnection> Connections { get; } = [];
    public int ExpensiveAsked { get; private set; }

    public static FastSample Healthy() => new()
    {
        Instance = new InstanceInfo("HOST\\ONE", "SQL Server 2019", "15.0.4000.1", "RTM", "Standard Edition (64-bit)"),
        Machine = new InstanceMachine(86400, 8, 16_000_000),
        Resources = new InstanceResources { CpuPercent = 12, OtherCpuPercent = 5, MemoryKb = 4_000_000, TargetMemoryKb = 8_000_000, Connections = 30, Batches = 1000 },
        Databases = [new DatabaseInfo("master", "ONLINE", "SIMPLE", "MULTI_USER", false, 150, true, 8000, 2000), new DatabaseInfo("Sales", "ONLINE", "FULL", "MULTI_USER", false, 150, false, 900_000, 50_000)],
        Sessions = [new SessionGroup("Orders.Api", "APP01", "app", "Sales", 12, 1, 0)],
        Activity = []
    };

    public Task<FastSample> FastAsync(DatabaseConnection connection, CancellationToken stopping)
    {
        Connections.Add(connection);
        return Unreachable is { } error ? throw new InvalidOperationException(error) : Task.FromResult(Fast);
    }

    public Task<SlowSample> SlowAsync(DatabaseConnection connection, CancellationToken stopping) => Task.FromResult(Slow);

    public IReadOnlyList<string> ExpensiveOf { get; private set; } = [];

    public List<CatalogObject> Catalog { get; } =
    [
        new(1, "Table", "dbo", "Orders", "U", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), 1200, 640),
        new(2, "Table", "dbo", "Customers", "U", null, new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc), 90, 64),
        new(3, "View", "dbo", "vwOrders", "V", null, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), null, null),
        new(4, "Procedure", "dbo", "spCloseOrders", "P", null, null, null, null),
        new(5, "Function", "audit", "fncOrdersOf", "TF", null, null, null, null),
        new(6, "Type", "dbo", "OrderList", "TT", null, null, null, null)
    ];
    public List<string> CatalogsRead { get; } = [];

    public Task<IReadOnlyList<CatalogObject>> ObjectsAsync(DatabaseConnection connection, string database, CancellationToken stopping)
    {
        CatalogsRead.Add(database);
        return Task.FromResult<IReadOnlyList<CatalogObject>>([.. Catalog]);
    }

    /// <summary>
    /// What creates each object, by name; "CREATE name" for the ones not said.
    /// </summary>
    public Dictionary<string, string> Scripts { get; } = [];
    public List<string> DetailsRead { get; } = [];
    public ChangeAuthor? Author { get; set; }

    public Task<CatalogDetail> ObjectAsync(DatabaseConnection connection, string database, CatalogObject target, CancellationToken stopping)
    {
        DetailsRead.Add(target.Name);
        var script = Scripts.GetValueOrDefault(target.Name, "CREATE " + target.Name);
        return Task.FromResult(new CatalogDetail { Object = target, Script = script, ScriptSource = "instance", Fingerprint = SqlScript.Fingerprint(script) });
    }

    public Task<ChangeAuthor?> WhoChangedAsync(DatabaseConnection connection, string database, int objectId, CancellationToken stopping) => Task.FromResult(Author);

    public Task<IReadOnlyList<ExpensiveQuery>> ExpensiveAsync(DatabaseConnection connection, IReadOnlyList<string> databases, CancellationToken stopping)
    {
        ExpensiveAsked++;
        ExpensiveOf = databases;
        return Task.FromResult<IReadOnlyList<ExpensiveQuery>>(Expensive);
    }
}

/// <summary>
/// Protection that can be told apart and undone, for a machine without the one of Windows.
/// </summary>
internal sealed class PretendProtector : ISecretProtector
{
    public bool Available => true;

    public string Protect(string plain) => Secret.Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("here:" + plain));

    public string? Unprotect(string kept)
    {
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(kept[Secret.Prefix.Length..]));
            return text.StartsWith("here:") ? text[5..] : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public sealed class DatabaseMonitorTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly PretendInstance _instance = new();
    private readonly PretendProtector _protector = new();
    private readonly Clock _clock = new();
    private readonly List<SupervisorEvent> _events = [];

    private DatabaseConfig Config(string? password = "secret", int backupHours = 0, params string[] databases) => new()
    {
        Databases = databases,
        Name = "Test", Server = "HOST\\ONE", User = "app", Password = password, BackupHours = backupHours, BlockingSeconds = 30, DiskFreePercent = 10
    };

    private DatabaseMonitor Monitor(DatabaseConfig config)
    {
        var monitor = new DatabaseMonitor(_instance, _protector, null, _clock, NullLogger.Instance);
        monitor.Event += _events.Add;
        monitor.ApplyConfig(config);
        return monitor;
    }

    private async Task Look(DatabaseMonitor monitor, DatabaseConfig config, int seconds = 30)
    {
        await monitor.SampleAsync(config);
        _clock.Now += TimeSpan.FromSeconds(seconds);
    }

    [Fact]
    public async Task An_instance_that_answers_is_online_with_what_it_said()
    {
        var config = Config();
        using var monitor = Monitor(config);
        Assert.Null(monitor.Snapshot().Online);

        await Look(monitor, config);

        var state = monitor.Snapshot();
        Assert.True(state.Online);
        Assert.Equal("SQL Server 2019", state.Fast!.Instance!.Product);
        Assert.Empty(state.Alerts);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task The_password_is_read_back_before_it_is_used()
    {
        var config = Config(_protector.Protect("secret"));
        using var monitor = Monitor(config);

        await Look(monitor, config);

        Assert.Equal("secret", _instance.Connections.Single().Password);
    }

    [Fact]
    public async Task A_password_protected_elsewhere_is_said_and_nothing_is_asked()
    {
        var config = Config(Secret.Prefix + Convert.ToBase64String("other machine"u8));
        using var monitor = Monitor(config);

        await Look(monitor, config);

        Assert.False(monitor.Snapshot().Online);
        Assert.Contains("another machine", monitor.Snapshot().Error);
        Assert.Empty(_instance.Connections);
    }

    [Fact]
    public async Task Going_down_and_coming_back_are_told_once_each()
    {
        var config = Config();
        using var monitor = Monitor(config);
        await Look(monitor, config);

        _instance.Unreachable = "network path not found";
        await Look(monitor, config);
        await Look(monitor, config);
        Assert.False(monitor.Snapshot().Online);
        Assert.Equal("network path not found", monitor.Snapshot().Error);
        // What it was is still known.
        Assert.NotNull(monitor.Snapshot().Fast);

        _instance.Unreachable = null;
        await Look(monitor, config);

        Assert.Equal([EventKind.DatabaseDown, EventKind.DatabaseUp], _events.Select(e => e.Kind));
        Assert.All(_events, e => Assert.Equal("database:Test", e.Group));
    }

    [Fact]
    public async Task The_rate_of_batches_comes_from_two_looks()
    {
        var config = Config();
        using var monitor = Monitor(config);
        await Look(monitor, config);
        Assert.Null(monitor.Snapshot().BatchesPerSecond);

        _instance.Fast = PretendInstance.Healthy() with { Resources = new InstanceResources { Batches = 1600 } };
        await Look(monitor, config);

        Assert.Equal(20, monitor.Snapshot().BatchesPerSecond);
    }

    [Fact]
    public async Task A_session_kept_waiting_for_long_is_an_alert_until_it_goes()
    {
        var config = Config();
        using var monitor = Monitor(config);
        Activity Waiting(long ms) => new(60, "suspended", "UPDATE", ms, 0, 0, "LCK_M_X", ms, 55, "Sales", "Orders.Api", "APP01", "app", 1, true, "UPDATE t SET a = ?");

        _instance.Fast = PretendInstance.Healthy() with { Activity = [Waiting(5_000)] };
        await Look(monitor, config);
        Assert.Empty(monitor.Snapshot().Alerts);

        _instance.Fast = PretendInstance.Healthy() with { Activity = [Waiting(45_000)] };
        await Look(monitor, config);
        var alert = Assert.Single(monitor.Snapshot().Alerts);
        Assert.Equal(("Blocking", "danger", 45.0), (alert.Kind, alert.Severity, alert.Value));
        var since = alert.Since;

        _instance.Fast = PretendInstance.Healthy() with { Activity = [Waiting(75_000)] };
        await Look(monitor, config);
        Assert.Equal(since, monitor.Snapshot().Alerts.Single().Since);

        _instance.Fast = PretendInstance.Healthy();
        await Look(monitor, config);
        Assert.Empty(monitor.Snapshot().Alerts);
        Assert.Equal([EventKind.DatabaseAlert, EventKind.DatabaseAlertCleared], _events.Select(e => e.Kind));
    }

    [Fact]
    public async Task Databases_disks_backups_and_jobs_each_raise_their_alert()
    {
        var config = Config(backupHours: 24);
        using var monitor = Monitor(config);
        _instance.Fast = PretendInstance.Healthy() with
        {
            Databases =
            [
                new DatabaseInfo("master", "ONLINE", "SIMPLE", "MULTI_USER", false, 150, true, 1, 1),
                new DatabaseInfo("Sales", "ONLINE", "FULL", "MULTI_USER", false, 150, false, 1, 1),
                new DatabaseInfo("Stock", "ONLINE", "FULL", "MULTI_USER", false, 150, false, 1, 1),
                new DatabaseInfo("Old", "SUSPECT", "FULL", "MULTI_USER", false, 150, false, 1, 1)
            ]
        };
        _instance.Slow = new SlowSample
        {
            Volumes = [new Volume("D:\\", "Data", 1000, 50), new Volume("E:\\", "Log", 1000, 500)],
            Backups = [new Backups("Sales", 60, null, 5), new Backups("Stock", 3000, null, null)],
            Jobs = [new Job("Nightly", true, 0, 600, 12, "failed"), new Job("Off", false, 0, 600, 1, ""), new Job("Fine", true, 1, 10, 1, "")]
        };

        await Look(monitor, config);

        // A database that is not online is one problem, not two.
        Assert.Equal(["Backup|Stock", "Disk|D:\\", "Job|Nightly", "State|Old"],
            monitor.Snapshot().Alerts.Select(alert => alert.Key).OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Only_the_databases_named_are_shown_when_any_is()
    {
        var config = Config(backupHours: 24, databases: ["sales", "Gone"]);
        using var monitor = Monitor(config);
        Activity Doing(int session, string database, int blockedBy = 0) => new(session, "running", "SELECT", 100, 1, 1, "", blockedBy > 0 ? 1000 : 0, blockedBy, database, "App", "H", "app", 0, true, "SELECT ?");
        _instance.Fast = PretendInstance.Healthy() with
        {
            Databases =
            [
                new DatabaseInfo("master", "ONLINE", "SIMPLE", "MULTI_USER", false, 150, true, 1, 1),
                new DatabaseInfo("Sales", "ONLINE", "FULL", "MULTI_USER", false, 150, false, 1, 1),
                new DatabaseInfo("Other", "SUSPECT", "FULL", "MULTI_USER", false, 150, false, 1, 1)
            ],
            Sessions = [new SessionGroup("App", "H", "app", "Sales", 3, 0, 0), new SessionGroup("App", "H", "app", "Other", 9, 0, 0), new SessionGroup("Tool", "H", "app", "master", 2, 0, 0)],
            // 10 works on Sales and waits for 20, which waits for 30; neither of those is on Sales. 40 has nothing to do with it.
            Activity = [Doing(10, "Sales", blockedBy: 20), Doing(20, "Other", blockedBy: 30), Doing(30, "master"), Doing(40, "Other")]
        };
        _instance.Slow = new SlowSample
        {
            Volumes =
            [
                new Volume("D:\\", "Data", 1000, 500) { Databases = ["Sales", "Other"] }, new Volume("E:\\", "Else", 1000, 10) { Databases = ["Other"] },
                new Volume("T:\\", "Temp", 1000, 500) { Databases = ["tempdb"] }
            ],
            Backups = [new Backups("Sales", 60, null, 5), new Backups("Other", 99999, null, null)],
            Jobs = []
        };

        await Look(monitor, config);

        var state = monitor.Snapshot();
        Assert.Equal(["Sales"], state.Fast!.Databases!.Select(database => database.Name));
        Assert.Equal(3, state.Fast.Sessions!.Sum(group => group.Sessions));
        Assert.Equal([10, 20, 30], state.Fast.Activity!.Select(item => item.SessionId).Order());
        Assert.Equal(["D:\\", "T:\\"], state.Slow!.Volumes!.Select(volume => volume.Mount));
        Assert.Equal(["Sales"], state.Slow.Backups!.Select(backup => backup.Database));
        // Nothing of the other databases asks for attention; a name the instance does not have does.
        Assert.Equal("Missing|Gone", Assert.Single(state.Alerts).Key);

        await monitor.ExpensiveAsync();
        Assert.Equal(["sales", "Gone"], _instance.ExpensiveOf);
    }

    [Fact]
    public async Task The_objects_are_only_of_a_database_the_configuration_names()
    {
        using var monitor = Monitor(Config(databases: ["Sales"]));

        var (database, objects, _) = await monitor.ObjectsAsync(null);
        Assert.Equal("Sales", database);
        Assert.Equal(6, objects.Count);
        await monitor.ObjectsAsync("SALES");
        Assert.Equal(["Sales"], _instance.CatalogsRead);

        var refused = await Assert.ThrowsAsync<DatabaseMonitor.Refused>(() => monitor.ObjectsAsync("Payroll"));
        Assert.Equal("unknown-database", refused.Code);
        Assert.Equal(["Sales"], _instance.CatalogsRead);

        using var none = Monitor(Config());
        Assert.Equal("unknown-database", (await Assert.ThrowsAsync<DatabaseMonitor.Refused>(() => none.ObjectsAsync(null))).Code);
    }

    [Fact]
    public async Task An_object_made_a_moment_ago_is_looked_for_again()
    {
        using var monitor = Monitor(Config(databases: ["Sales"]));
        await monitor.ObjectsAsync(null);
        _instance.Catalog.Add(new CatalogObject(7, "Procedure", "dbo", "spNew", "P", null, null, null, null));

        var (_, detail) = await monitor.ObjectAsync(null, "procedure", "DBO", "spnew");

        Assert.Equal("spNew", detail.Object.Name);
        Assert.Equal(2, _instance.CatalogsRead.Count);
        Assert.Equal("not-found", (await Assert.ThrowsAsync<DatabaseMonitor.Refused>(() => monitor.ObjectAsync(null, "Table", "dbo", "Nothing"))).Code);
    }

    [Fact]
    public async Task Backups_are_not_looked_at_unless_asked()
    {
        var config = Config(backupHours: 0);
        using var monitor = Monitor(config);
        _instance.Slow = new SlowSample { Backups = [], Volumes = [], Jobs = [] };

        await Look(monitor, config);

        Assert.Empty(monitor.Snapshot().Alerts);
    }

    [Fact]
    public async Task The_expensive_statements_are_asked_for_once_in_a_while()
    {
        var config = Config();
        using var monitor = Monitor(config);
        _instance.Expensive.Add(new ExpensiveQuery("Sales", "dbo.GetOrders", 10, 100, 200, 3000, 50, 1, true, false, false, "SELECT ?"));

        Assert.Single(await monitor.ExpensiveAsync());
        await monitor.ExpensiveAsync();
        Assert.Equal(1, _instance.ExpensiveAsked);

        _clock.Now += TimeSpan.FromMinutes(1);
        await monitor.ExpensiveAsync();
        Assert.Equal(2, _instance.ExpensiveAsked);
    }
}

public sealed class DatabasePartsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("database-test-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("SELECT * FROM t WHERE name = 'John O''Brien' AND age > 42", "SELECT * FROM t WHERE name = '?' AND age > ?")]
    [InlineData("UPDATE [my table] SET code = N'A-1', v = 0x1F, x = 1.5e3 WHERE id = @p1", "UPDATE [my table] SET code = '?', v = ?, x = ? WHERE id = @p1")]
    [InlineData("select col1, t2.c from tab1 t2 -- john's note\nwhere a=1", "select col1, t2.c from tab1 t2 where a=?")]
    [InlineData("exec dbo.proc1 /* 'secret' 99 */ @a = 'x'", "exec dbo.proc1 @a = '?'")]
    [InlineData("INSERT INTO t VALUES ('unterminated", "INSERT INTO t VALUES ('?'")]
    [InlineData("SELECT \"quoted name\", 'v'", "SELECT \"quoted name\", '?'")]
    public void The_values_written_in_a_statement_do_not_leave(string sql, string expected) => Assert.Equal(expected, SqlText.Mask(sql));

    /// <summary>
    /// Everything read from the instance is something it says about itself. A statement that
    /// names anything else would be reading the data of an application.
    /// </summary>
    [Fact]
    public void No_statement_reads_a_table_of_an_application()
    {
        var statements = typeof(Queries).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!);
        Assert.True(statements.Count >= 10);

        foreach (var (name, sql) in statements)
        {
            Assert.DoesNotMatch(new Regex(@"\b(INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE|DROP|ALTER|CREATE|TRUNCATE|INTO|USE|OPENROWSET|OPENQUERY)\b", RegexOptions.IgnoreCase), sql);
            // Names given inside the statement itself: its common table expressions and derived tables.
            var own = Regex.Matches(sql, @"\b(\w+)\s+AS\s*\(", RegexOptions.IgnoreCase).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (Match source in Regex.Matches(sql, @"\b(?:FROM|JOIN|APPLY)\s+([^\s(]+)", RegexOptions.IgnoreCase))
            {
                var target = source.Groups[1].Value;
                Assert.True(target.StartsWith("sys.", StringComparison.OrdinalIgnoreCase) || target.StartsWith("msdb.dbo.", StringComparison.OrdinalIgnoreCase) || own.Contains(target),
                    $"{name} reads from {target}");
            }
        }
    }

    [Fact]
    public void A_typed_password_is_replaced_in_the_file_and_no_copy_of_it_is_left()
    {
        var file = new ConfigFile(_directory);
        string Text(string password) => $$"""{ "ZapMQHost": "localhost", "ZapMQPort": 5679, "WorkerGroups": [], "Database": { "Name": "Test", "Server": "HOST", "User": "app", "Password": "{{password}}" } }""";
        File.WriteAllText(file.Path, Text("typed"));
        File.WriteAllText(file.Path + ".bak", Text("typed"));
        var protector = new PretendProtector();

        Assert.True(file.ProtectSecrets(protector));

        var kept = ConfigReader.Parse(file.Read()).Database!.Password!;
        Assert.StartsWith(Secret.Prefix, kept);
        Assert.Equal("typed", protector.Unprotect(kept));
        Assert.DoesNotContain("typed", file.Read());
        Assert.False(File.Exists(file.Path + ".bak"));
        // Once protected, it is left alone.
        Assert.False(file.ProtectSecrets(protector));
    }

    [Fact]
    public void Without_protection_on_the_machine_the_file_is_left_as_it_is()
    {
        var file = new ConfigFile(_directory);
        var text = """{ "ZapMQHost": "localhost", "ZapMQPort": 5679, "WorkerGroups": [], "Database": { "Server": "HOST", "User": "app", "Password": "typed" } }""";
        File.WriteAllText(file.Path, text);

        Assert.False(file.ProtectSecrets(new NoSecretProtector()));
        Assert.Equal(text, file.Read());
    }

    [Fact]
    public void The_history_is_given_in_slices()
    {
        using var history = DatabaseHistory.TryOpen(_directory, NullLogger.Instance)!;
        var start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 2880; i++)
            history.Add(new DatabasePoint(start.AddSeconds(i * 30), i != 100, 5, i % 2 == 0 ? 10 : 30, 0, 1000, 20, 1, i == 7 ? 3 : 0, 4));

        var points = history.Read(start, start.AddDays(1));
        Assert.InRange(points.Count, 300, 361);
        Assert.Equal(20, points[1].CpuPercent);
        Assert.Equal(3, points[0].Blocked);
        Assert.Single(points, point => !point.Online);

        history.Prune(start.AddHours(23));
        Assert.InRange(history.Read(start, start.AddDays(1)).Count, 10, 20);
    }

    [Theory]
    [InlineData("nvarchar", 100, 0, 0, "nvarchar(50)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", 20, 0, 0, "varchar(20)")]
    [InlineData("VARBINARY", -1, 0, 0, "varbinary(max)")]
    [InlineData("decimal", 9, 18, 2, "decimal(18, 2)")]
    [InlineData("datetime2", 8, 27, 7, "datetime2")]
    [InlineData("datetime2", 7, 23, 3, "datetime2(3)")]
    [InlineData("float", 8, 53, 0, "float")]
    [InlineData("int", 4, 10, 0, "int")]
    [InlineData("datetime", 8, 23, 3, "datetime")]
    public void A_type_is_written_as_a_script_writes_it(string name, int length, int precision, int scale, string expected) =>
        Assert.Equal(expected, SqlScript.TypeName(new RawType(name, null, length, precision, scale)));

    private static CatalogColumn Column(string name, string type, int length = 4, bool nullable = false, bool identity = false, string? @default = null, string? defaultName = null,
        bool systemNamed = false, string? computed = null, string? collation = null, string? typeSchema = null) =>
        new(name, new RawType(type, typeSchema, length, 18, 2), nullable, identity, identity ? "1" : null, identity ? "1" : null, defaultName, @default, systemNamed, computed, computed is not null, collation);

    [Fact]
    public void A_table_is_written_from_what_the_catalog_says()
    {
        var detail = new CatalogDetail
        {
            Object = new CatalogObject(1, "Table", "dbo", "Order Item", "U", null, null, 10, 8),
            Columns =
            [
                Column("Id", "int", identity: true),
                Column("Code", "varchar", 20, collation: "Latin1_General_BIN"),
                Column("Note", "nvarchar", -1, nullable: true, collation: "Latin1_General_CI_AS"),
                Column("Price", "decimal", 9, @default: "((0))", defaultName: "DF_Price"),
                Column("At", "datetime", 8, @default: "(getdate())", defaultName: "DF__Order__At__1A2B3C", systemNamed: true),
                Column("Was", "int", @default: "((1))", defaultName: "DF__Order__Was__36D11DD4"),
                Column("Kind", "OrderKind", 1, typeSchema: "dbo"),
                Column("Total", "decimal", computed: "([Price]*(2))")
            ],
            Indexes =
            [
                new CatalogIndex("PK_OrderItem", "CLUSTERED", true, true, false, false, null, [new("Id", false, false)]),
                new CatalogIndex("UQ_Code", "NONCLUSTERED", true, false, true, false, null, [new("Code", false, false)]),
                new CatalogIndex("IX_At", "NONCLUSTERED", false, false, false, false, "([At] IS NOT NULL)", [new("At", true, false), new("Kind", false, false), new("Price", false, true)])
            ],
            Checks = [new CatalogCheck("CK_Price", "([Price]>=(0))", false)],
            ForeignKeys = [new CatalogForeignKey("FK_Order", ["OrderId", "Line"], "dbo", "Order", ["Id", "Line"], "CASCADE", "NO_ACTION", true)],
            Triggers = [new CatalogTrigger("trItem", false, false, "CREATE TRIGGER dbo.trItem ON dbo.[Order Item] AFTER INSERT AS SET NOCOUNT ON\r\n")]
        };

        Assert.Equal("""
            CREATE TABLE [dbo].[Order Item] (
                [Id] int IDENTITY(1, 1) NOT NULL,
                [Code] varchar(20) COLLATE Latin1_General_BIN NOT NULL,
                [Note] nvarchar(max) NULL,
                [Price] decimal(18, 2) NOT NULL CONSTRAINT [DF_Price] DEFAULT ((0)),
                [At] datetime NOT NULL DEFAULT (getdate()),
                [Was] int NOT NULL DEFAULT ((1)),
                [Kind] [dbo].[OrderKind] NOT NULL,
                [Total] AS ([Price]*(2)) PERSISTED,
                CONSTRAINT [PK_OrderItem] PRIMARY KEY CLUSTERED ([Id] ASC),
                CONSTRAINT [UQ_Code] UNIQUE NONCLUSTERED ([Code] ASC),
                CONSTRAINT [CK_Price] CHECK ([Price]>=(0)),
                CONSTRAINT [FK_Order] FOREIGN KEY ([OrderId], [Line]) REFERENCES [dbo].[Order] ([Id], [Line]) ON DELETE CASCADE
            );
            GO

            CREATE NONCLUSTERED INDEX [IX_At] ON [dbo].[Order Item] ([At] DESC, [Kind] ASC) INCLUDE ([Price]) WHERE ([At] IS NOT NULL);
            GO

            ALTER TABLE [dbo].[Order Item] NOCHECK CONSTRAINT [FK_Order];
            GO

            CREATE TRIGGER dbo.trItem ON dbo.[Order Item] AFTER INSERT AS SET NOCOUNT ON

            """.ReplaceLineEndings("\n"), SqlScript.Table(detail, "Latin1_General_CI_AS"));
    }

    [Fact]
    public void Types_are_written_too()
    {
        var list = new CatalogDetail
        {
            Object = new CatalogObject(6, "Type", "dbo", "OrderList", "TT", null, null, null, null),
            Columns = [Column("Id", "int"), Column("Code", "varchar", 20, nullable: true)],
            Indexes = [new CatalogIndex("PK__x", "CLUSTERED", true, true, false, false, null, [new("Id", false, false)])]
        };
        Assert.Equal("CREATE TYPE [dbo].[OrderList] AS TABLE (\n    [Id] int NOT NULL,\n    [Code] varchar(20) NULL,\n    PRIMARY KEY CLUSTERED ([Id] ASC)\n);\n", SqlScript.TableType(list, null));
        Assert.Equal("CREATE TYPE [dbo].[Code] FROM varchar(20) NOT NULL;\n", SqlScript.ScalarType(new CatalogObject(7, "Type", "dbo", "Code", "T", null, null, null, null), "varchar(20)", false));
    }

    [Fact]
    public void The_defaults_of_the_parameters_are_read_from_the_text()
    {
        const string text = """
            -- @Old int = 99 was here once
            CREATE PROCEDURE [dbo].[spCloseOrders]
                @Period varchar(7),
                @User nvarchar(50) = N'system, the', /* @Fake int = 5 */
                @Limit decimal(18, 2) = 10.5,
                @When datetime = NULL,
                @Rows int = 0 OUTPUT,
                @Flag bit
            WITH RECOMPILE
            AS
            BEGIN
                DECLARE @Flag2 int = 7, @Period2 varchar(7) = 'x'
                SET @Flag = 1
            END
            """;

        var defaults = SqlScript.ParameterDefaults(text, ["@Period", "@User", "@Limit", "@When", "@Rows", "@Flag"]);

        Assert.Equal(new Dictionary<string, string> { ["@User"] = "N'system, the'", ["@Limit"] = "10.5", ["@When"] = "NULL", ["@Rows"] = "0" }, defaults);
        Assert.Empty(SqlScript.ParameterDefaults(null, ["@a"]));
        Assert.Equal("(1)", SqlScript.ParameterDefaults("CREATE FUNCTION f (@a int = (1), @b int) RETURNS int AS BEGIN RETURN @a END", ["@a", "@b"])["@a"]);
    }

    [Fact]
    public void The_fingerprint_does_not_change_with_line_endings_or_trailing_blanks()
    {
        var one = SqlScript.Fingerprint("CREATE VIEW v AS\r\n  SELECT 1   \r\n\r\n");
        Assert.Equal(one, SqlScript.Fingerprint("\nCREATE VIEW v AS\n  SELECT 1\n"));
        Assert.NotEqual(one, SqlScript.Fingerprint("CREATE VIEW v AS\n  SELECT 2\n"));
        Assert.Equal(64, one.Length);
    }

    [Fact]
    public void The_objects_are_given_a_page_at_a_time_with_what_there_is_of_each_kind()
    {
        var all = new PretendInstance().Catalog;

        var page = DatabaseAnswers.Objects("Sales", all, DateTimeOffset.UnixEpoch, kind: "Table", schema: null, search: "o", sort: "rows", limit: 1, offset: 0);

        Assert.Equal(2, page.Value<int>("Total"));
        Assert.Equal("Orders", Assert.Single(page["Objects"]!).Value<string>("Name"));
        // How many of each kind match the search, whichever kind is being looked at.
        Assert.Equal([2, 1, 1, 1, 1], page["Kinds"]!.Select(item => item.Value<int>("Count")));
        Assert.Equal(["audit", "dbo"], page["Schemas"]!.Select(item => item.Value<string>("Schema")));

        var audited = DatabaseAnswers.Objects("Sales", all, DateTimeOffset.UnixEpoch, null, "AUDIT", null, null, 100, 0);
        Assert.Equal("fncOrdersOf", Assert.Single(audited["Objects"]!).Value<string>("Name"));
        Assert.Equal("vwOrders", DatabaseAnswers.Objects("Sales", all, DateTimeOffset.UnixEpoch, null, null, "dbo.vw", "modified", 100, 0)["Objects"]![0]!.Value<string>("Name"));
    }

    [Fact]
    public void The_product_is_named_shortly() =>
        Assert.Equal("SQL Server 2019", SqlServerSource.Product("Microsoft SQL Server 2019 (RTM-CU18) (KB5017593) - 15.0.4261.1 (X64) \n\tSep 12 2022"));
}

/// <summary>
/// What changed in the objects of a database between one look and the next.
/// </summary>
public sealed class ObjectWatcherTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly string _directory = Directory.CreateTempSubdirectory("objects-test-").FullName;
    private readonly PretendInstance _instance = new();
    private readonly Clock _clock = new();
    private readonly ObjectHistory _history;
    private readonly ObjectWatcher _watcher;
    private readonly DatabaseConnection _connection = new("HOST", "app", "x", false, true);

    public ObjectWatcherTests()
    {
        _history = ObjectHistory.TryOpen(_directory, NullLogger.Instance)!;
        _watcher = new ObjectWatcher(_instance, _history, _clock) { Pause = TimeSpan.Zero };
    }

    public void Dispose()
    {
        _history.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<List<ObjectChange>> Look()
    {
        _instance.DetailsRead.Clear();
        _clock.Now += TimeSpan.FromMinutes(10);
        return (await _watcher.ScanAsync(_connection, "Sales", CancellationToken.None)).Changes;
    }

    /// <summary>
    /// The instance says the object was touched now.
    /// </summary>
    private void Touch(string name, string? script = null)
    {
        var at = _instance.Catalog.FindIndex(item => item.Name == name);
        _instance.Catalog[at] = _instance.Catalog[at] with { ModifiedAt = _clock.Now.UtcDateTime };
        if (script is not null)
            _instance.Scripts[name] = script;
    }

    [Fact]
    public async Task The_first_look_only_records_how_the_database_is()
    {
        Assert.Null(_history.BaselineAt("Sales"));

        Assert.Empty(await Look());

        Assert.Equal(6, _instance.DetailsRead.Count);
        Assert.Equal(6, _history.Known("sales").Count);
        Assert.NotNull(_history.BaselineAt("Sales"));
        Assert.Equal("CREATE Orders", _history.Script("Sales", "Table", "dbo", "orders"));
    }

    [Fact]
    public async Task Nothing_is_read_again_while_the_instance_says_nothing_was_touched()
    {
        await Look();

        Assert.Empty(await Look());

        Assert.Empty(_instance.DetailsRead);
    }

    [Fact]
    public async Task An_object_whose_script_changed_was_altered_and_both_versions_are_kept()
    {
        await Look();
        _instance.Author = new ChangeAuthor("DOMAIN\\ana", "DEV-PC", "SSMS");
        Touch("spCloseOrders", "CREATE spCloseOrders -- v2");

        var change = Assert.Single(await Look());

        Assert.Equal(["spCloseOrders"], _instance.DetailsRead);
        Assert.Equal(("Altered", "Procedure", "dbo", "spCloseOrders", "DOMAIN\\ana"), (change.Action, change.Kind, change.Schema, change.Name, change.Login));
        Assert.NotEqual(change.OldFingerprint, change.NewFingerprint);
        // The scripts are not carried around with the list of changes.
        Assert.Null(change.NewScript);
        var kept = _history.Change(change.Id)!;
        Assert.Equal(("CREATE spCloseOrders", "CREATE spCloseOrders -- v2", "DEV-PC"), (kept.OldScript, kept.NewScript, kept.Host));
        Assert.Empty(await Look());
    }

    [Fact]
    public async Task An_object_that_was_touched_and_is_the_same_did_not_change()
    {
        await Look();
        // A rebuilt index, a recompiled procedure: the instance says touched, the script says the same.
        Touch("Orders");

        Assert.Empty(await Look());
        Assert.Equal(["Orders"], _instance.DetailsRead);
        Assert.Empty(await Look());
        Assert.Empty(_instance.DetailsRead);
    }

    [Fact]
    public async Task What_is_made_dropped_or_renamed_is_told_as_such()
    {
        await Look();
        _instance.Catalog.Add(new CatalogObject(50, "View", "dbo", "vwNew", "V", null, _clock.Now.UtcDateTime, null, null));
        _instance.Catalog.RemoveAll(item => item.Name == "Customers");
        // The same object, under another name.
        var at = _instance.Catalog.FindIndex(item => item.Name == "spCloseOrders");
        _instance.Catalog[at] = _instance.Catalog[at] with { Name = "spCloseOrdersOld", ModifiedAt = _clock.Now.UtcDateTime };

        var changes = await Look();

        Assert.Equal(["Created View vwNew", "Dropped Table Customers", "Renamed Procedure spCloseOrdersOld"],
            changes.Select(change => $"{change.Action} {change.Kind} {change.Name}").Order());
        Assert.Equal("dbo.spCloseOrders", changes.Single(change => change.Action == "Renamed").OldName);
        Assert.Equal("CREATE Customers", _history.Change(changes.Single(change => change.Action == "Dropped").Id)!.OldScript);
        Assert.DoesNotContain(_history.Known("Sales"), item => item.Name is "Customers" or "spCloseOrders");
        Assert.Empty(await Look());
    }

    [Fact]
    public async Task A_change_a_package_brought_is_told_apart_from_one_done_straight_on_the_database()
    {
        await Look();
        _history.NoteApplied("Sales", "Procedure", "dbo", "spcloseorders", "pkg-1", _clock.Now);
        Touch("spCloseOrders", "CREATE spCloseOrders -- from the package");
        var first = Assert.Single(await Look());
        Assert.Equal("pkg-1", first.Package);
        Assert.Equal("pkg-1", _history.Change(first.Id)!.Package);

        // Looked at, the note is spent: the next change to the same object is nobody's package.
        Touch("spCloseOrders", "CREATE spCloseOrders -- by hand");
        Assert.Null(Assert.Single(await Look()).Package);

        // A script names no object: what changed with it is taken to be its doing.
        _history.NoteApplied("Sales", null, null, null, "pkg-2", _clock.Now);
        Touch("Orders", "CREATE Orders -- with a new column");
        Assert.Equal("pkg-2", Assert.Single(await Look()).Package);
    }

    [Fact]
    public async Task The_scripts_are_searched_for_a_text()
    {
        _instance.Scripts["spCloseOrders"] = "CREATE PROCEDURE dbo.spCloseOrders AS\r\n  SELECT Total FROM dbo.Orders -- total of the day\r\n  UPDATE dbo.Orders SET TOTAL = 0";
        _instance.Scripts["vwOrders"] = "CREATE VIEW dbo.vwOrders AS SELECT 100_percent FROM dbo.Orders";
        await Look();

        Assert.Equal(["spCloseOrders"], _history.Search("sales", "total", 50).Select(item => item.Name));
        // The marks a search understands are only text here.
        Assert.Equal(["vwOrders"], _history.Search("Sales", "100_percent", 50).Select(item => item.Name));
        Assert.Empty(_history.Search("Sales", "100%percent", 50));
        Assert.Empty(_history.Search("Other", "total", 50));
    }

    [Fact]
    public async Task A_first_look_that_was_cut_short_goes_on_without_calling_anything_a_change()
    {
        // Half of the database was recorded before the service stopped.
        foreach (var item in _instance.Catalog.Take(3))
            _history.Store("Sales", item, SqlScript.Fingerprint("CREATE " + item.Name), "CREATE " + item.Name);

        Assert.Empty(await Look());

        Assert.Equal(3, _instance.DetailsRead.Count);
        Assert.Equal(6, _history.Known("Sales").Count);
    }

    [Fact]
    public async Task The_changes_are_found_by_object_by_text_and_by_time()
    {
        await Look();
        Touch("spCloseOrders", "v2");
        await Look();
        Touch("spCloseOrders", "v3");
        Touch("vwOrders", "v2");
        await Look();

        Assert.Equal(3, _history.Changes(new ChangeFilter("sales", null, null, null, null, null, null, 100)).Total);
        var (latest, total) = _history.Changes(new ChangeFilter(null, "Procedure", "dbo", "spcloseorders", null, null, null, 1));
        Assert.Equal((2, 1), (total, latest.Count));
        Assert.Single(_history.Changes(new ChangeFilter(null, null, null, null, "vwor", null, null, 100)).Changes);
        Assert.Equal(2, _history.Changes(new ChangeFilter(null, null, null, null, null, _clock.Now.AddMinutes(-1), null, 100)).Total);
        Assert.Empty(_history.Changes(new ChangeFilter("Other", null, null, null, null, null, null, 100)).Changes);

        _history.Prune(_clock.Now.AddMinutes(-1));
        Assert.Equal(2, _history.Changes(new ChangeFilter(null, null, null, null, null, null, null, 100)).Total);
    }
}

/// <summary>
/// A database that takes whatever it is told to apply, and remembers it.
/// </summary>
internal sealed class PretendWriter : IDatabaseWriter
{
    public List<(string Database, ApplyRequest Request)> Applied { get; } = [];
    public ApplyResult Result { get; set; } = new(true, "altered", null, null, null, ["1 row affected"]);

    public Task<ApplyResult> ApplyAsync(DatabaseConnection connection, string database, ApplyRequest request, CancellationToken stopping)
    {
        Applied.Add((database, request));
        return Task.FromResult(Result);
    }
}

public sealed class SqlApplyTests
{
    [Fact]
    public void A_script_is_split_where_a_line_says_only_go()
    {
        var batches = SqlApply.Batches("""
            CREATE TABLE t (a int)
            GO
            -- GO in a comment line stays
            INSERT INTO t VALUES ('a
            GO
            b')
              go  -- the end of this one
            /* a block
            GO
            still the block */
            SELECT 1
            GO 3

            GO
            """);

        Assert.Equal(3, batches.Count);
        Assert.Equal("CREATE TABLE t (a int)", batches[0]);
        Assert.Contains("GO\nb')", batches[1]);
        Assert.StartsWith("-- GO in a comment line stays", batches[1]);
        Assert.Contains("GO\nstill the block */\nSELECT 1", batches[2]);
        Assert.Empty(SqlApply.Batches("  \n GO \n"));
    }

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.p AS SELECT 1", true, "ALTER PROCEDURE dbo.p AS SELECT 1")]
    [InlineData("create  proc dbo.p AS SELECT 1", true, "ALTER  proc dbo.p AS SELECT 1")]
    [InlineData("ALTER VIEW v AS SELECT 1", false, "CREATE VIEW v AS SELECT 1")]
    [InlineData("CREATE OR ALTER FUNCTION f() RETURNS int AS BEGIN RETURN 1 END", false, "CREATE FUNCTION f() RETURNS int AS BEGIN RETURN 1 END")]
    [InlineData("-- create view old\n/* ALTER PROC x */\n  CREATE VIEW v AS SELECT 'create view x'", true, "-- create view old\n/* ALTER PROC x */\n  ALTER VIEW v AS SELECT 'create view x'")]
    [InlineData("CREATE TRIGGER t ON x AFTER INSERT AS RETURN", true, "ALTER TRIGGER t ON x AFTER INSERT AS RETURN")]
    public void What_creates_a_module_is_made_to_alter_it_and_back(string script, bool alter, string expected) =>
        Assert.Equal(expected, SqlApply.AsCreate(script, alter));

    [Theory]
    [InlineData("SET NOCOUNT ON; CREATE VIEW v AS SELECT 1")]
    [InlineData("CREATE TABLE t (a int)")]
    [InlineData("DROP PROCEDURE p")]
    [InlineData("")]
    public void What_does_not_begin_by_creating_a_module_is_left_alone(string script) =>
        Assert.Null(SqlApply.AsCreate(script, alter: true));

    [Fact]
    public void A_drop_is_written_from_the_name_and_never_from_the_package()
    {
        Assert.Equal("DROP PROCEDURE [dbo].[sp]]Odd];", SqlApply.Drop("Procedure", "P", "dbo", "sp]Odd"));
        Assert.Equal("DROP TYPE [dbo].[List];", SqlApply.Drop("Type", "TT", "dbo", "List"));
        Assert.Throws<ArgumentException>(() => SqlApply.Drop("Database", null, "dbo", "x"));
    }
}

/// <summary>
/// The database through the administration contract, with the service running.
/// </summary>
[Collection("processes")]
public class DatabaseAdminTests
{
    private const string Section = """{ "Name": "Test", "Server": "HOST\\ONE", "User": "app", "Password": "typed", "Databases": ["Sales"], "SampleSeconds": 5 }""";

    [Fact]
    public async Task The_state_the_history_and_the_statements_are_answered_and_the_password_is_protected()
    {
        var instance = new PretendInstance();
        instance.Expensive.Add(new ExpensiveQuery("Sales", "dbo.GetOrders", 10, 100, 200, 3000, 50, 1, true, false, false, "SELECT ?"));
        await using var rig = new Rig { DatabaseJson = Section };
        var writer = new PretendWriter();
        rig.Register = services =>
        {
            services.AddSingleton<IDatabaseWriter>(writer);
            services.AddSingleton<IDatabaseSource>(instance);
            services.AddSingleton<ISecretProtector>(new PretendProtector());
        };
        rig.WriteConfig();
        await rig.StartBrokerAsync();
        await rig.StartServiceAsync();

        JObject? state = null;
        Assert.True(await Rig.Eventually(async () => (state = await rig.CommandAsync("Database"))?.Value<bool?>("Online") == true));
        Assert.Equal("Test", state!.Value<string>("Name"));
        Assert.Equal("SQL Server 2019", state["Instance"]!.Value<string>("Product"));
        Assert.Equal("Sales", Assert.Single(state["DatabaseList"]!).Value<string>("Name"));
        Assert.Equal(12, state["Sessions"]![0]!.Value<int>("Sessions"));
        Assert.Empty((JObject)state["Problems"]!);
        Assert.DoesNotContain("typed", state.ToString());

        Assert.Equal("typed", instance.Connections[0].Password);
        Assert.DoesNotContain("\"typed\"", rig.ConfigText());
        Assert.Contains(Secret.Prefix, rig.ConfigText());

        var history = await rig.CommandAsync("DatabaseHistory", request => request["Minutes"] = 60);
        Assert.NotEmpty((JArray)history!["Points"]!);

        var queries = await rig.CommandAsync("DatabaseQueries");
        Assert.Equal("dbo.GetOrders", queries!["Queries"]![0]!.Value<string>("Object"));

        var objects = await rig.CommandAsync("DatabaseObjects", request => request["Kind"] = "View");
        Assert.Equal(("Sales", 1), (objects!.Value<string>("Database"), objects.Value<int>("Total")));
        var view = await rig.CommandAsync("DatabaseObject", request => { request["Kind"] = "View"; request["Schema"] = "dbo"; request["Name"] = "vwOrders"; });
        Assert.Equal("CREATE vwOrders", view!.Value<string>("Script"));
        Assert.Equal("vwOrders", view["Object"]!.Value<string>("Name"));
        Assert.Equal(64, view.Value<string>("Fingerprint")!.Length);
        // The objects are looked at by themselves, and what changes is told.
        JObject? tracked = null;
        Assert.True(await Rig.Eventually(async () => (tracked = await rig.CommandAsync("Database"))?["Tracking"] is JArray { Count: 1 } tracking && tracking[0]["BaselineAt"]?.Type == JTokenType.Date));
        Assert.Equal(6, tracked!["Tracking"]![0]!.Value<int>("Known"));
        Assert.Equal(0, (await rig.CommandAsync("DatabaseChanges"))!.Value<int>("Total"));
        Assert.Equal("not-found", (await rig.CommandAsync("DatabaseChange", request => request["Id"] = 99))!["Error"]!.Value<string>("Code"));

        // An item of a package is applied to the database of the environment, whatever it is called here.
        var applied = await rig.CommandAsync("DatabaseApply", request =>
        {
            request["Package"] = "p-1"; request["Item"] = 2; request["Action"] = "Define"; request["Kind"] = "Procedure"; request["Schema"] = "dbo"; request["Name"] = "spCloseOrders";
            request["Script"] = "CREATE PROCEDURE dbo.spCloseOrders AS RETURN"; request["By"] = "ana";
        });
        Assert.Equal((true, "Sales", "altered"), (applied!.Value<bool>("Applied"), applied.Value<string>("Database"), applied.Value<string>("Did")));
        var (where, what) = Assert.Single(writer.Applied);
        Assert.Equal(("Sales", "p-1", 2, "Define", "spCloseOrders"), (where, what.Package, what.Item, what.Action, what.Name));
        // What the instance refused comes back as such, not as a command that failed.
        writer.Result = new ApplyResult(false, null, "Invalid column name 'x'.", 1, 12, []);
        var refused = await rig.CommandAsync("DatabaseApply", request => { request["Package"] = "p-1"; request["Item"] = 3; request["Action"] = "Script"; request["Script"] = "UPDATE t SET x = 1"; });
        Assert.Equal((true, false, "Invalid column name 'x'.", 12), (refused!.Value<bool>("Ok"), refused.Value<bool>("Applied"), refused.Value<string>("Problem"), refused.Value<int>("Line")));
        // Never to a database the configuration does not name.
        var elsewhere = await rig.CommandAsync("DatabaseApply", request => { request["Package"] = "p-1"; request["Item"] = 4; request["Action"] = "Script"; request["Script"] = "SELECT 1"; request["Database"] = "Payroll"; });
        Assert.Equal("unknown-database", elsewhere!["Error"]!.Value<string>("Code"));
        Assert.Equal("invalid-request", (await rig.CommandAsync("DatabaseApply", request => request["Package"] = "p-1"))!["Error"]!.Value<string>("Code"));
        Assert.Equal(2, writer.Applied.Count);

        // The scripts kept by the look at the objects can be searched.
        var search = await rig.CommandAsync("DatabaseSearch", request => request["Text"] = "create vw");
        Assert.True(search!.Value<bool>("Ready"));
        var hit = Assert.Single(search["Objects"]!);
        Assert.Equal(("vwOrders", 1, 1), (hit.Value<string>("Name"), hit.Value<int>("Matches"), hit["Lines"]![0]!.Value<int>("Number")));
        Assert.Equal("invalid-request", (await rig.CommandAsync("DatabaseSearch", request => request["Text"] = "x"))!["Error"]!.Value<string>("Code"));

        var other = await rig.CommandAsync("DatabaseObjects", request => request["Database"] = "Payroll");
        Assert.Equal("unknown-database", other!["Error"]!.Value<string>("Code"));
    }

    [Fact]
    public async Task Without_a_database_in_the_configuration_it_says_so()
    {
        await using var rig = await Rig.StartAsync();

        var state = await rig.CommandAsync("Database");

        Assert.True(state!.Value<bool>("Ok"));
        Assert.False(state.Value<bool>("Configured"));
        Assert.Equal("not-configured", (await rig.CommandAsync("DatabaseQueries"))!["Error"]!.Value<string>("Code"));
    }
}
