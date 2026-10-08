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

    public Task<IReadOnlyList<ExpensiveQuery>> ExpensiveAsync(DatabaseConnection connection, CancellationToken stopping)
    {
        ExpensiveAsked++;
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

    private DatabaseConfig Config(string? password = "secret", int backupHours = 0) => new()
    {
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

    [Fact]
    public void The_product_is_named_shortly() =>
        Assert.Equal("SQL Server 2019", SqlServerSource.Product("Microsoft SQL Server 2019 (RTM-CU18) (KB5017593) - 15.0.4261.1 (X64) \n\tSep 12 2022"));
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
        rig.Register = services =>
        {
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
        Assert.Equal("Sales", state["DatabaseList"]![1]!.Value<string>("Name"));
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
