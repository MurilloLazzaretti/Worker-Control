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

    private static readonly string[] Kinds = ["Table", "View", "Procedure", "Function", "Type"];

    /// <summary>
    /// A page of the objects of a database, with how many there are of each kind and in each
    /// schema among the ones that match the search.
    /// </summary>
    public static JObject Objects(string database, IReadOnlyList<CatalogObject> all, DateTimeOffset readAt, string? kind, string? schema, string? search, string? sort, int limit, int offset)
    {
        bool Matches(CatalogObject item) => string.IsNullOrWhiteSpace(search)
            || item.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) || (item.Schema + "." + item.Name).Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);

        var searched = all.Where(Matches).ToList();
        var ofSchema = searched.Where(item => string.IsNullOrEmpty(schema) || item.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)).ToList();
        var matching = ofSchema.Where(item => string.IsNullOrEmpty(kind) || item.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        var ordered = (sort switch
        {
            "modified" => matching.OrderByDescending(item => item.ModifiedAt ?? DateTime.MinValue),
            "rows" => matching.OrderByDescending(item => item.Rows ?? -1),
            "size" => matching.OrderByDescending(item => item.SizeKb ?? -1),
            _ => matching.OrderBy(item => item.Schema, StringComparer.OrdinalIgnoreCase)
        }).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();

        return Admin.Ok(answer =>
        {
            answer["Database"] = database;
            answer["ReadAt"] = readAt;
            answer["Total"] = ordered.Count;
            answer["Objects"] = Token(ordered.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 500)));
            answer["Kinds"] = Token(Kinds.Select(name => new { Kind = name, Count = ofSchema.Count(item => item.Kind == name) }));
            answer["Schemas"] = Token(searched.GroupBy(item => item.Schema, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new { Schema = group.Key, Count = group.Count() }));
        });
    }

    public static JObject Object(string database, CatalogDetail detail) => Admin.Ok(answer =>
    {
        answer["Database"] = database;
        foreach (var property in (JObject)Token(detail))
            answer[property.Key] = property.Value;
    });
}
