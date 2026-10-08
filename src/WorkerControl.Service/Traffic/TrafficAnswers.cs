using Newtonsoft.Json.Linq;

namespace WorkerControl.Service.Traffic;

/// <summary>
/// The answers of the administration contract about traffic.
/// </summary>
internal static class TrafficAnswers
{
    /// <summary>
    /// The period a request is about: <c>From</c> and <c>To</c>, or the last <c>Minutes</c>
    /// (an hour, when nothing is said).
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) Period(JObject request, DateTimeOffset now)
    {
        var to = request.Value<DateTimeOffset?>("To") ?? now;
        var from = request.Value<DateTimeOffset?>("From") ?? to.AddMinutes(-Math.Clamp(request.Value<int?>("Minutes") ?? 60, 1, 60 * 24 * 366));
        return (from, to);
    }

    public static TrafficFilter Filter(JObject request)
    {
        string? Text(string name) => request.Value<string>(name) is { Length: > 0 } value ? value : null;
        return new TrafficFilter(Text("Kind"), Text("Host"), Text("App"), Text("Route"), Text("Method"));
    }

    /// <summary>
    /// How long each point of a chart stands for, so that any period gives a readable line.
    /// </summary>
    public static int Step(TimeSpan period) => period.TotalHours switch
    {
        <= 2 => 60,
        <= 26 => 600,
        <= 24 * 8 => 3600,
        _ => 6 * 3600
    };

    public static JObject Summary(TrafficStore store, TrafficSource source, bool configured, DateTimeOffset from, DateTimeOffset to, TrafficFilter filter) => Admin.Ok(answer =>
    {
        var step = Step(to - from);
        var total = store.Query(from, to, filter, []).Select(row => row.Tally).FirstOrDefault() ?? new Tally();
        var (users, usersByApp) = store.Users(from, to, filter);

        answer["Configured"] = configured;
        answer["Source"] = new JObject
        {
            ["File"] = source.File,
            ["Found"] = source.Found,
            ["Size"] = source.Size,
            ["Pending"] = Math.Max(0, source.Size - source.Offset),
            ["LastLineAt"] = source.LastLineAt,
            ["Lines"] = source.Lines,
            ["Refused"] = source.Refused,
            ["Problem"] = source.Problem
        };
        answer["From"] = from;
        answer["To"] = to;
        answer["StepSeconds"] = step;
        answer["Totals"] = Describe(total, users);
        answer["Series"] = new JArray(store.Query(from, to, filter, [$"(at - at % {step})"])
            .OrderBy(row => Convert.ToInt64(row.Group[0]))
            .Select(row =>
            {
                var point = Describe(row.Tally, null);
                point["At"] = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(row.Group[0]));
                return point;
            }));
        answer["Apps"] = new JArray(store.Query(from, to, filter, ["app", "kind"]).Select(row =>
        {
            var app = Describe(row.Tally, usersByApp.GetValueOrDefault((string)row.Group[0]));
            app["App"] = (string)row.Group[0];
            app["Kind"] = (string)row.Group[1];
            return app;
        }));
        answer["Upstreams"] = new JArray(store.Query(from, to, filter, ["upstream", "app"])
            .Where(row => ((string)row.Group[0]).Length > 0)
            .OrderBy(row => (string)row.Group[1], StringComparer.Ordinal).ThenBy(row => (string)row.Group[0], StringComparer.Ordinal)
            .Select(row =>
            {
                var upstream = Describe(row.Tally, null);
                upstream["Upstream"] = (string)row.Group[0];
                upstream["App"] = (string)row.Group[1];
                return upstream;
            }));
        answer["Hosts"] = new JArray(store.Query(from, to, filter with { Host = null }, ["host"]).Select(row => (string)row.Group[0]));
    });

    /// <summary>
    /// The endpoints of a period, each with how it did in the same stretch of the day before.
    /// </summary>
    public static JObject Routes(TrafficStore store, DateTimeOffset from, DateTimeOffset to, TrafficFilter filter, string? search, string? sort, int limit, bool includePreflight = false) => Admin.Ok(answer =>
    {
        string[] columns = ["host", "method", "route", "kind", "app"];
        var before = store.Query(from.AddDays(-1), to.AddDays(-1), filter, columns, search)
            .ToDictionary(row => string.Join('\n', row.Group), row => row.Tally, StringComparer.Ordinal);

        // What a browser asks before the real request says nothing about the endpoint.
        var rows = store.Query(from, to, filter, columns, search).Where(row => includePreflight || (string)row.Group[1] != "OPTIONS").Select(row =>
        {
            before.TryGetValue(string.Join('\n', row.Group), out var previous);
            return (row.Group, row.Tally, Previous: previous);
        });
        rows = sort switch
        {
            "slow" => rows.OrderByDescending(row => row.Tally.Percentile(0.95) ?? 0),
            "errors" => rows.OrderByDescending(row => row.Tally.S5).ThenByDescending(row => row.Tally.S4),
            // Worse than yesterday: how much the slowest requests slowed down, among the ones asked for enough to tell.
            "worse" => rows.Where(row => row.Previous is { Count: >= 5 } && row.Tally.Count >= 5)
                .OrderByDescending(row => (row.Tally.Percentile(0.95) ?? 0) - (row.Previous!.Percentile(0.95) ?? 0)),
            _ => rows.OrderByDescending(row => row.Tally.Count)
        };

        answer["From"] = from;
        answer["To"] = to;
        answer["Routes"] = new JArray(rows.Take(Math.Clamp(limit, 1, 500)).Select(row =>
        {
            var route = Describe(row.Tally, null);
            route["Host"] = (string)row.Group[0];
            route["Method"] = (string)row.Group[1];
            route["Route"] = (string)row.Group[2];
            route["Kind"] = (string)row.Group[3];
            route["App"] = (string)row.Group[4];
            route["PreviousCount"] = row.Previous?.Count;
            route["PreviousP95"] = row.Previous?.Percentile(0.95);
            return route;
        }));
    });

    /// <summary>
    /// Where the reverse proxy sent the requests of a period, and who is behind each address:
    /// what joins the traffic to the processes of the machine.
    /// </summary>
    public static JObject Upstreams(TrafficStore store, IMachineNetwork machine, DateTimeOffset from, DateTimeOffset to) => Admin.Ok(answer =>
    {
        var rows = store.Query(from, to, new TrafficFilter(), ["upstream", "app"]).Where(row => ((string)row.Group[0]).Length > 0).ToList();
        var owners = UpstreamResolver.Resolve(rows.Select(row => (string)row.Group[0]), machine).ToDictionary(owner => owner.Upstream, StringComparer.Ordinal);

        answer["From"] = from;
        answer["To"] = to;
        answer["Minutes"] = Math.Max(1, (to - from).TotalMinutes);
        answer["Upstreams"] = new JArray(rows.Select(row =>
        {
            var upstream = Describe(row.Tally, null);
            upstream["Upstream"] = (string)row.Group[0];
            upstream["App"] = (string)row.Group[1];
            var owner = owners.GetValueOrDefault((string)row.Group[0]);
            upstream["Site"] = owner?.Site;
            upstream["Processes"] = new JArray((owner?.Processes ?? []).Select(process => new JObject { ["ProcessId"] = process.ProcessId, ["Name"] = process.Name }));
            return upstream;
        }));
    });

    /// <summary>
    /// The screens the requests came from and, for each name given, how much the screens
    /// under that name were used.
    /// </summary>
    public static JObject Pages(TrafficStore store, DateTimeOffset from, DateTimeOffset to, string? host, string? search, int limit, IReadOnlyList<string> names) => Admin.Ok(answer =>
    {
        answer["From"] = from;
        answer["To"] = to;
        answer["Pages"] = new JArray(store.Pages(from, to, host, search, limit).Select(page => new JObject
        {
            ["Host"] = page.Host,
            ["Page"] = page.Page,
            ["Count"] = page.Count,
            ["Users"] = page.Users
        }));
        answer["Named"] = new JArray(names.Take(100).Select(name =>
        {
            var (count, users) = store.PagesNamed(from, to, host, name);
            return new JObject { ["Name"] = name, ["Count"] = count, ["Users"] = users };
        }));
    });

    public static JObject Errors(TrafficStore store, TrafficFilter filter, int limit) => Admin.Ok(answer =>
        answer["Errors"] = new JArray(store.Errors(limit, filter).Select(error => new JObject
        {
            ["At"] = error.At,
            ["Host"] = error.Host,
            ["Method"] = error.Method,
            ["Path"] = error.Path,
            ["Route"] = error.Route,
            ["App"] = error.App,
            ["Status"] = error.Status,
            ["Upstream"] = error.Upstream,
            ["Milliseconds"] = error.Milliseconds
        })));

    private static JObject Describe(Tally tally, long? users) => new()
    {
        ["Count"] = tally.Count,
        ["S2"] = tally.S2,
        ["S3"] = tally.S3,
        ["S4"] = tally.S4,
        ["S5"] = tally.S5,
        ["Bytes"] = tally.Bytes,
        ["Average"] = tally.Average,
        ["P50"] = tally.Percentile(0.50),
        ["P95"] = tally.Percentile(0.95),
        ["P99"] = tally.Percentile(0.99),
        ["Users"] = users
    };
}
