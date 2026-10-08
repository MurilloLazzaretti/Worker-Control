using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;
using WorkerControl.Service.Traffic;

namespace WorkerControl.Service.Tests;

/// <summary>
/// The traffic read from the access log of the reverse proxy.
/// </summary>
public sealed class TrafficTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("traffic-test-").FullName;
    private readonly string _log;
    private readonly TrafficStore _store;
    private readonly List<string> _reopened = [];
    /// <summary>
    /// A whole hour not long ago: the collector lets go of what is older than it keeps, by the real clock.
    /// </summary>
    private static readonly DateTimeOffset Noon = new(DateTimeOffset.UtcNow.AddHours(-3).Ticks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour, TimeSpan.Zero);

    public TrafficTests()
    {
        _log = Path.Combine(_folder, "access.zapmq.log");
        _store = new TrafficStore(_folder);
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private TrafficConfig Config(int rotateAtMb = 100, int maxRoutes = 2000, string? reopen = null, string[]? ignore = null) =>
        new() { AccessLog = _log, RotateAtMb = rotateAtMb, MaxRoutes = maxRoutes, ReopenCommand = reopen, Ignore = ignore ?? [], KeepFiles = 1 };

    private TrafficCollector Collector(TrafficConfig? config = null)
    {
        var collector = new TrafficCollector(_folder, _store, TimeProvider.System, NullLogger.Instance, command =>
        {
            _reopened.Add(command);
            // What the proxy does when told to reopen: a new, empty file.
            File.WriteAllText(_log, "");
            return true;
        });
        collector.ApplyConfig(config ?? Config());
        return collector;
    }

    private static string Line(string path, int status = 200, double seconds = 0.012, string ip = "10.0.0.1", string method = "GET", string host = "api.test",
        string upstream = "127.0.0.1:9002", DateTimeOffset? at = null, string referer = "") =>
        new JObject
        {
            ["t"] = (at ?? Noon).ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ["ms"] = (at ?? Noon).ToUnixTimeMilliseconds() / 1000.0,
            ["ip"] = ip,
            ["h"] = host,
            ["m"] = method,
            ["u"] = path,
            ["s"] = status,
            ["b"] = 512,
            ["rt"] = seconds,
            ["ua"] = upstream,
            ["us"] = status.ToString(),
            ["ut"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ref"] = referer
        }.ToString(Newtonsoft.Json.Formatting.None);

    private void Write(params string[] lines) => File.AppendAllText(_log, string.Join("\n", lines) + "\n");

    private Tally Total(TrafficFilter? filter = null) =>
        _store.Query(Noon.AddHours(-1), Noon.AddHours(1), filter ?? new TrafficFilter(), []).Select(row => row.Tally).FirstOrDefault() ?? new Tally();

    private List<(string Route, long Count)> Routes() =>
        [.. _store.Query(Noon.AddHours(-1), Noon.AddHours(1), new TrafficFilter(), ["route"]).Select(row => ((string)row.Group[0], row.Tally.Count)).OrderBy(row => row.Item1)];

    [Fact]
    public void A_line_of_the_proxy_is_read_without_what_identifies_the_request()
    {
        var hit = TrafficLog.Parse(Encoding.UTF8.GetBytes(
            """{"t":"2026-10-08T11:54:25-03:00","ms":1791471265.677,"ip":"10.1.2.3","h":"Dev.Example.com","m":"get","u":"/api/producao/lote/48213?token=abc&x=1","s":200,"b":13279,"rt":0.137,"ua":"127.0.0.1:9014, 127.0.0.1:9015","us":"502, 200","ut":"0.001, 0.136","ref":"http://x/"}"""))!;

        Assert.Equal("/api/producao/lote/48213", hit.Path);
        Assert.Equal("dev.example.com", hit.Host);
        Assert.Equal("GET", hit.Method);
        Assert.Equal(200, hit.Status);
        Assert.Equal(0.137, hit.Seconds);
        // After a retry, who answered is the last one tried.
        Assert.Equal("127.0.0.1:9015", hit.Upstream);
        Assert.Equal(DateTimeOffset.Parse("2026-10-08T14:54:25Z"), hit.At);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"t":"2026-10-08T11:54:25-03:00"}""")]
    [InlineData("""{"t":"yesterday","u":"/x"}""")]
    [InlineData("""{"t":"2026-10-08T11:54:25-03:00","u":"/x","s":20""")]
    public void What_is_not_a_whole_line_of_the_format_is_refused(string line) =>
        Assert.Null(TrafficLog.Parse(Encoding.UTF8.GetBytes(line)));

    [Theory]
    [InlineData("/api/producao/lote/48213/itens", "/api/producao/lote/{id}/itens", "api", "api/producao")]
    [InlineData("/api/cadastro/usuario/3f2504e0-4f89-11d3-9a0c-0305e82c3301", "/api/cadastro/usuario/{id}", "api", "api/cadastro")]
    [InlineData("/api/relatorio/arquivo/9f86d081884c7d659a2feaa0c55ad015", "/api/relatorio/arquivo/{id}", "api", "api/relatorio")]
    [InlineData("/api/producao/ordem/VG000017", "/api/producao/ordem/{id}", "api", "api/producao")]
    [InlineData("/api/producao/rastreabilidade/%7B%22centro%22%3A%223040%22%7D", "/api/producao/rastreabilidade/{id}", "api", "api/producao")]
    [InlineData("/api/global/login", "/api/global/login", "api", "api/global")]
    [InlineData("/socketio/", "/socketio", "api", "socketio")]
    [InlineData("/", "/", "api", "(raiz)")]
    [InlineData("/mfe/producao/main.44e04686b4c4c427.js", "/mfe/producao/*.js", "static", "mfe/producao")]
    [InlineData("/mfe/producao/assets/img/logo.PNG", "/mfe/producao/assets/img/*.png", "static", "mfe/producao")]
    [InlineData("/index.html", "/*.html", "static", "(raiz)")]
    [InlineData("/assets/mf.manifest.json", "/assets/*.json", "static", "assets")]
    [InlineData("/zapmq/api/workers/status", "/zapmq/api/workers/status", "api", "zapmq")]
    [InlineData("/api/v2/orders", "/api/v2/orders", "api", "api/v2")]
    [InlineData("/api/admin/download/EP.04.00.001_15.docx", "/api/admin/download/{arquivo}", "api", "api/admin")]
    [InlineData("/api/relatorio/arquivo/planilha final.xlsx", "/api/relatorio/arquivo/{arquivo}", "api", "api/relatorio")]
    public void A_path_is_counted_as_its_route(string path, string route, string kind, string app) =>
        Assert.Equal((route, kind, app), TrafficLog.Normalize(path, ["api", "mfe"], []));

    [Fact]
    public void A_route_written_by_hand_wins_over_the_general_rule()
    {
        var templates = TrafficLog.Templates(["/api/pedidos/{codigo}/itens", "/api/pedidos/{codigo}"]);

        Assert.Equal("/api/pedidos/{codigo}/itens", TrafficLog.Normalize("/api/pedidos/abc/itens", ["api"], templates).Route);
        Assert.Equal("/api/pedidos/{codigo}", TrafficLog.Normalize("/API/Pedidos/xyz", ["api"], templates).Route);
        Assert.Equal("/api/pedidos/abc/outra/coisa", TrafficLog.Normalize("/api/pedidos/abc/outra/coisa", ["api"], templates).Route);
    }

    [Fact]
    public void The_percentiles_come_from_the_ranges_the_times_are_counted_in()
    {
        var tally = new Tally();
        for (var i = 0; i < 90; i++)
            tally.Add(200, 100, 0.008);
        for (var i = 0; i < 9; i++)
            tally.Add(200, 100, 0.180);
        tally.Add(500, 100, 30);

        Assert.InRange(tally.Percentile(0.50)!.Value, 5, 10);
        Assert.InRange(tally.Percentile(0.95)!.Value, 100, 250);
        Assert.Equal(10000, tally.Percentile(0.999));
        Assert.Equal(100, tally.Count);
        Assert.Equal(1, tally.S5);
        Assert.Null(new Tally().Percentile(0.5));
    }

    [Fact]
    public void What_is_written_to_the_log_is_counted_once_however_many_times_it_is_looked_at()
    {
        var collector = Collector();
        Write(Line("/api/pedidos/1"), Line("/api/pedidos/2", status: 404), Line("/api/pedidos/3", status: 500, seconds: 2.5), Line("/mfe/app/main.abc123.js", host: "app.test", upstream: ""));

        collector.Collect();
        collector.Collect();

        Assert.Equal(4, Total().Count);
        Assert.Equal((2, 0, 1, 1), (Total().S2, Total().S3, Total().S4, Total().S5));
        Assert.Equal([("/api/pedidos/{id}", 3), ("/mfe/app/*.js", 1)], Routes());
        Assert.Equal(3, Total(new TrafficFilter(Kind: "api")).Count);
        Assert.Equal(1, Total(new TrafficFilter(Host: "app.test")).Count);

        Write(Line("/api/pedidos/4"));
        collector.Collect();
        Assert.Equal(5, Total().Count);
        Assert.Equal(5, collector.Source.Lines);
        Assert.Equal(0, collector.Source.Size - collector.Source.Offset);
    }

    [Fact]
    public void A_line_still_being_written_waits_for_its_end()
    {
        var collector = Collector();
        var line = Line("/api/pedidos/1");
        File.AppendAllText(_log, Line("/api/a") + "\n" + line[..40]);

        collector.Collect();
        Assert.Equal(1, Total().Count);

        File.AppendAllText(_log, line[40..] + "\n");
        collector.Collect();

        Assert.Equal(2, Total().Count);
        Assert.Equal(0, collector.Source.Refused);
    }

    [Fact]
    public void Where_the_reading_stopped_survives_the_service()
    {
        Write(Line("/api/a"), Line("/api/b"));
        Collector().Collect();

        Write(Line("/api/c"));
        Collector().Collect();

        Assert.Equal(3, Total().Count);
    }

    [Fact]
    public void A_file_that_got_shorter_is_another_file_and_is_read_from_its_start()
    {
        var collector = Collector();
        Write(Line("/api/a"), Line("/api/b"), Line("/api/c"));
        collector.Collect();

        File.WriteAllText(_log, Line("/api/d") + "\n");
        collector.Collect();

        Assert.Equal(4, Total().Count);
    }

    [Fact]
    public void Users_are_the_different_addresses_and_errors_are_kept_to_be_looked_at()
    {
        var collector = Collector();
        Write(Line("/api/pedidos/1", ip: "10.0.0.1"), Line("/api/pedidos/2", ip: "10.0.0.1"), Line("/api/pedidos/3", ip: "10.0.0.2"),
            Line("/api/estoque/9", ip: "10.0.0.3", status: 502, seconds: 0.4, upstream: "127.0.0.1:9003"));

        collector.Collect();

        var (all, byApp) = _store.Users(Noon.AddHours(-1), Noon.AddHours(1), new TrafficFilter());
        Assert.Equal(3, all);
        Assert.Equal(2, byApp["api/pedidos"]);
        Assert.Equal(1, byApp["api/estoque"]);

        var error = Assert.Single(_store.Errors(10, new TrafficFilter()));
        Assert.Equal(("/api/estoque/9", "/api/estoque/{id}", 502, "127.0.0.1:9003", 400), (error.Path, error.Route, error.Status, error.Upstream, error.Milliseconds));
    }

    [Fact]
    public void Past_the_limit_of_routes_of_a_day_the_rest_is_added_up_as_one()
    {
        var collector = Collector(Config(maxRoutes: 10));
        Write([.. Enumerable.Range(0, 25).Select(n => Line($"/scan/path-{(char)('a' + n)}"))]);

        collector.Collect();

        var routes = Routes();
        Assert.Equal(11, routes.Count);
        Assert.Equal(15, routes.Single(route => route.Route == "(outras)").Count);
        Assert.Equal(25, Total().Count);
    }

    [Fact]
    public void A_file_that_got_big_is_put_aside_and_the_proxy_told_to_start_another()
    {
        var collector = Collector(Config(rotateAtMb: 1, reopen: "nginx -s reopen"));
        Write([.. Enumerable.Range(0, 6000).Select(n => Line($"/api/pedidos/{n}"))]);
        Assert.True(new FileInfo(_log).Length > 1024 * 1024);

        collector.Collect();

        Assert.Equal(["nginx -s reopen"], _reopened);
        Assert.Equal(6000, Total().Count);
        Assert.Equal(0, new FileInfo(_log).Length);
        Assert.Single(Directory.GetFiles(_folder, "access.zapmq.*.log"));

        // The new file is read from its start.
        Write(Line("/api/depois"));
        collector.Collect();
        Assert.Equal(6001, Total().Count);
    }

    [Fact]
    public void Without_a_command_to_reopen_the_file_is_never_touched()
    {
        var collector = Collector(Config(rotateAtMb: 1));
        Write([.. Enumerable.Range(0, 6000).Select(n => Line($"/api/pedidos/{n}"))]);

        collector.Collect();

        Assert.Empty(_reopened);
        Assert.True(new FileInfo(_log).Length > 1024 * 1024);
        Assert.Equal(6000, Total().Count);
    }

    [Fact]
    public void Old_minutes_become_hours_and_what_is_past_keeping_goes()
    {
        var collector = Collector();
        var old = Noon.AddDays(-5);
        var ancient = Noon.AddDays(-40);
        Write(Line("/api/a", at: old), Line("/api/a", at: old.AddMinutes(7)), Line("/api/a", at: old.AddMinutes(8), status: 500), Line("/api/a", at: ancient), Line("/api/a"));
        collector.Collect();

        _store.Maintain(Noon, TimeSpan.FromHours(48), TimeSpan.FromDays(30));

        var hours = _store.Query(old.AddHours(-1), old.AddHours(2), new TrafficFilter(), ["at"]);
        var hour = Assert.Single(hours);
        Assert.Equal(3, hour.Tally.Count);
        Assert.Equal(1, hour.Tally.S5);
        Assert.Empty(_store.Query(ancient.AddHours(-1), ancient.AddHours(1), new TrafficFilter(), []).Where(row => row.Tally.Count > 0));
        Assert.Equal(1, Total().Count);
    }

    [Fact]
    public void The_answers_of_the_contract_tell_totals_series_applications_and_endpoints()
    {
        var collector = Collector();
        Write(Line("/api/pedidos/1", seconds: 0.020), Line("/api/pedidos/2", seconds: 0.030, upstream: "127.0.0.1:9003"), Line("/api/estoque/1", status: 500, seconds: 1.2),
            Line("/api/pedidos/9", at: Noon.AddDays(-1), seconds: 0.004));
        collector.Collect();

        var summary = TrafficAnswers.Summary(_store, collector.Source, true, Noon.AddMinutes(-30), Noon.AddMinutes(30), new TrafficFilter(Kind: "api"));
        Assert.True((bool)summary["Ok"]!);
        Assert.Equal(3, (long)summary["Totals"]!["Count"]!);
        Assert.Equal(1, (long)summary["Totals"]!["S5"]!);
        Assert.Equal(1, (long)summary["Totals"]!["Users"]!);
        Assert.Equal(60, (int)summary["StepSeconds"]!);
        Assert.Equal(3, (long)Assert.Single(summary["Series"]!)["Count"]!);
        Assert.Equal(["api/pedidos", "api/estoque"], summary["Apps"]!.Select(app => (string)app["App"]!));
        Assert.Equal(2, summary["Upstreams"]!.Count(upstream => (string)upstream["App"]! == "api/pedidos"));
        Assert.Equal(_log, (string)summary["Source"]!["File"]!);

        var routes = TrafficAnswers.Routes(_store, Noon.AddMinutes(-30), Noon.AddMinutes(30), new TrafficFilter(), null, "slow", 10);
        Assert.Equal(["/api/estoque/{id}", "/api/pedidos/{id}"], routes["Routes"]!.Select(route => (string)route["Route"]!));
        Assert.Equal(1, (long)routes["Routes"]![1]!["PreviousCount"]!);

        var searched = TrafficAnswers.Routes(_store, Noon.AddMinutes(-30), Noon.AddMinutes(30), new TrafficFilter(), "estoque", null, 10);
        Assert.Single(searched["Routes"]!);

        Assert.Equal("/api/estoque/1", (string)TrafficAnswers.Errors(_store, new TrafficFilter(), 10)["Errors"]![0]!["Path"]!);
    }

    [Fact]
    public void What_is_told_to_be_ignored_is_not_counted()
    {
        var collector = Collector(Config(ignore: ["/painel/"]));
        Write(Line("/painel/api/status"), Line("/PAINEL/api/live"), Line("/api/pedidos/1"), Line("/painelzinho"));

        collector.Collect();

        Assert.Equal([("/api/pedidos/{id}", 1), ("/painelzinho", 1)], Routes());
        Assert.Equal(4, collector.Source.Lines);
    }

    [Fact]
    public void A_connection_handed_over_counts_as_a_request_and_not_as_an_answer_time()
    {
        var collector = Collector();
        Write(Line("/socket/", status: 101, seconds: 1800), Line("/socket/", status: 200, seconds: 0.010));

        collector.Collect();

        Assert.Equal(2, Total().Count);
        Assert.InRange(Total().Percentile(0.99)!.Value, 5, 10);
        Assert.Equal(10, Total().Average);
    }

    [Fact]
    public void The_same_instance_is_one_whichever_way_the_proxy_reached_it_and_preflights_are_left_out_of_the_endpoints()
    {
        var collector = Collector();
        Write(Line("/api/a", upstream: "[::1]:9014"), Line("/api/a", upstream: "127.0.0.1:9014"), Line("/api/a", upstream: "localhost:9014"), Line("/api/a", method: "OPTIONS", upstream: "127.0.0.1:9014"));
        collector.Collect();

        var summary = TrafficAnswers.Summary(_store, collector.Source, true, Noon.AddMinutes(-30), Noon.AddMinutes(30), new TrafficFilter());
        Assert.Equal("127.0.0.1:9014", (string)Assert.Single(summary["Upstreams"]!)["Upstream"]!);
        Assert.Equal(4, (long)summary["Totals"]!["Count"]!);

        var routes = TrafficAnswers.Routes(_store, Noon.AddMinutes(-30), Noon.AddMinutes(30), new TrafficFilter(), null, null, 10);
        Assert.Equal("GET", (string)Assert.Single(routes["Routes"]!)["Method"]!);
    }

    [Theory]
    [InlineData("http://App.Test/home/producao/rastreabilidade?lote=VG000017&tirada=29", "app.test", "/home/producao/rastreabilidade")]
    [InlineData("https://app.test/home/pedidos/48213/itens#topo", "app.test", "/home/pedidos/{id}/itens")]
    [InlineData("http://app.test/", "app.test", "/")]
    public void The_screen_a_request_came_from_is_kept_without_what_identifies_the_visit(string referer, string host, string page) =>
        Assert.Equal((host, page), TrafficLog.Page(referer));

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("android-app://com.example")]
    public void A_referer_that_is_not_a_page_is_nothing(string referer) =>
        Assert.Null(TrafficLog.Page(referer));

    [Fact]
    public void The_screens_are_counted_by_what_is_asked_from_them_and_the_people_on_them()
    {
        var collector = Collector(Config(ignore: ["/painel/"]));
        Write(
            Line("/mfe/producao/version.json", ip: "10.0.0.1", referer: "http://app.test/home/producao/rastreabilidade?lote=1", upstream: ""),
            Line("/mfe/producao/version.json", ip: "10.0.0.2", referer: "http://app.test/home/producao/rastreabilidade?lote=2", upstream: ""),
            Line("/api/producao/ordem", ip: "10.0.0.1", referer: "http://app.test/home/producao/ordens"),
            Line("/mfe/cadastro/main.abc.js", ip: "10.0.0.3", referer: "http://app.test/home/cadastro/materiais", upstream: ""),
            // To another site the browser says the site alone; that is no screen. Nor is an ignored path, or no referer.
            Line("/api/producao/lote/7", ip: "10.0.0.9", host: "api.test", referer: "http://app.test/"),
            Line("/api/x", ip: "10.0.0.9", referer: "http://app.test/painel/telas"),
            Line("/api/y", ip: "10.0.0.9"));
        collector.Collect();

        var pages = TrafficAnswers.Pages(_store, Noon.AddMinutes(-30), Noon.AddMinutes(30), null, null, 10, ["producao", "cadastro", "qualidade", "prod"]);

        Assert.Equal(["/home/producao/rastreabilidade", "/home/cadastro/materiais", "/home/producao/ordens"],
            pages["Pages"]!.OrderByDescending(page => (long)page["Count"]!).ThenBy(page => (string)page["Page"]!).Select(page => (string)page["Page"]!));
        Assert.Equal(2, (long)pages["Pages"]!.Single(page => (string)page["Page"]! == "/home/producao/rastreabilidade")["Users"]!);

        var named = pages["Named"]!.ToDictionary(item => (string)item["Name"]!, item => ((long)item["Count"]!, (long)item["Users"]!));
        Assert.Equal((3, 2), named["producao"]);
        Assert.Equal((1, 1), named["cadastro"]);
        Assert.Equal((0, 0), named["qualidade"]);
        // Part of a name is not the name.
        Assert.Equal((0, 0), named["prod"]);

        var searched = TrafficAnswers.Pages(_store, Noon.AddMinutes(-30), Noon.AddMinutes(30), "app.test", "ordens", 10, []);
        Assert.Equal("/home/producao/ordens", (string)Assert.Single(searched["Pages"]!)["Page"]!);
    }
}
