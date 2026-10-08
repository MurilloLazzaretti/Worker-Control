using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WorkerControl.Service.Database;

/// <summary>
/// The answers about the database, as the administration contract gives them.
/// </summary>
internal static class DatabaseAnswers
{
    private static readonly JsonSerializer Format = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Include });

    private static JToken Token(object? value) => value is null ? JValue.CreateNull() : JToken.FromObject(value, Format);

    public static JObject State(DatabaseState state) => Admin.Ok(answer =>
    {
        answer["Configured"] = state.Configured;
        answer["Name"] = state.Name;
        answer["Server"] = state.Server;
        answer["Databases"] = new JArray(state.Databases);
        answer["Online"] = state.Online;
        answer["Error"] = state.Error;
        answer["Since"] = state.Since;
        answer["CheckedAt"] = state.CheckedAt;
        answer["ResponseMs"] = state.ResponseMs;
        answer["BatchesPerSecond"] = state.BatchesPerSecond;
        answer["Instance"] = Token(state.Fast?.Instance);
        answer["Machine"] = Token(state.Fast?.Machine);
        answer["Resources"] = Token(state.Fast?.Resources);
        answer["DatabaseList"] = Token(state.Fast?.Databases);
        answer["Sessions"] = Token(state.Fast?.Sessions);
        answer["Activity"] = Token(state.Fast?.Activity);
        answer["Volumes"] = Token(state.Slow?.Volumes);
        answer["Backups"] = Token(state.Slow?.Backups);
        answer["Jobs"] = Token(state.Slow?.Jobs);
        answer["SlowAt"] = state.SlowAt;
        answer["Alerts"] = Token(state.Alerts.Select(alert => new { alert.Kind, alert.Subject, alert.Severity, alert.Value, alert.Since }));
        // The parts the user of the connection may not read, each with what the instance said.
        var problems = new JObject();
        foreach (var (part, message) in (state.Fast?.Problems ?? new Dictionary<string, string>()).Concat(state.Slow?.Problems ?? new Dictionary<string, string>()))
            problems[part] = message;
        answer["Problems"] = problems;
    });

    public static JObject History(IReadOnlyList<DatabasePoint> points) => Admin.Ok(answer => answer["Points"] = Token(points));

    public static JObject Expensive(IReadOnlyList<ExpensiveQuery> queries) => Admin.Ok(answer => answer["Queries"] = Token(queries));
}
