using System.Globalization;
using System.Text.Json;

namespace WorkerControl.Core;

/// <summary>
/// The contents of <c>ConfigWorkers.json</c>. The keys of 1.x keep their names and meaning; the
/// new ones are all optional.
/// </summary>
public sealed record WorkerControlConfig
{
    public string ZapMQHost { get; init; } = "localhost";

    public int ZapMQPort { get; init; } = 5679;

    /// <summary>
    /// How often the file is read again even if nothing announced a change.
    /// </summary>
    public TimeSpan RateLoadConfig { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Workers started per batch, all groups together.
    /// </summary>
    public int StartBatchSize { get; init; } = 4;

    public TimeSpan StartBatchInterval { get; init; } = TimeSpan.FromSeconds(2);

    public IReadOnlyList<GroupConfig> Groups { get; init; } = [];
}

public sealed record GroupConfig
{
    public required string Name { get; init; }

    public bool Enabled { get; init; }

    public required string ApplicationFullPath { get; init; }

    public string Arguments { get; init; } = "";

    /// <summary>
    /// Null means the folder of the worker's executable.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    public int TotalWorkers { get; init; }

    /// <summary>
    /// Interval between keep-alives and between checks of the number of workers.
    /// </summary>
    public TimeSpan MonitoringRate { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a worker already up may take to answer a keep-alive.
    /// </summary>
    public TimeSpan TimeoutKeepAlive { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a new worker has to answer its first keep-alive.
    /// </summary>
    public TimeSpan StartupGrace { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a worker has to leave after being asked to stop.
    /// </summary>
    public TimeSpan SafeStopTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A worker that goes away sooner than this after starting counts as a quick failure.
    /// </summary>
    public TimeSpan CrashWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Quick failures in a row before the group starts waiting between attempts.
    /// </summary>
    public int CrashLimit { get; init; } = 3;

    public BoostConfig Boost { get; init; } = new();

    /// <summary>
    /// How many workers the group should have at this time of day.
    /// </summary>
    public int DesiredWorkers(TimeSpan timeOfDay) =>
        !Enabled ? 0 : TotalWorkers + (Boost.IsActive(timeOfDay) ? Boost.BoostWorkers : 0);
}

/// <summary>
/// A daily window in which the group runs extra workers.
/// </summary>
public sealed record BoostConfig
{
    public bool Enabled { get; init; }

    public int BoostWorkers { get; init; }

    public TimeSpan StartTime { get; init; }

    public TimeSpan EndTime { get; init; }

    /// <summary>
    /// An end before the start means the window crosses midnight.
    /// </summary>
    public bool IsActive(TimeSpan timeOfDay)
    {
        if (!Enabled || BoostWorkers <= 0 || StartTime == EndTime)
            return false;

        return StartTime < EndTime
            ? timeOfDay >= StartTime && timeOfDay < EndTime
            : timeOfDay >= StartTime || timeOfDay < EndTime;
    }
}

public sealed class ConfigException(string message) : Exception(message);

/// <summary>
/// Reads <c>ConfigWorkers.json</c>. A file with anything wrong in it is refused as a whole, so
/// the configuration in use is never replaced by half of a new one.
/// </summary>
public static class ConfigReader
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static WorkerControlConfig Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Lenient);
        }
        catch (JsonException error)
        {
            throw new ConfigException("The file is not valid JSON: " + error.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ConfigException("The file must hold a JSON object");

            var general = new GroupDefaults(root, null);
            var config = new WorkerControlConfig
            {
                ZapMQHost = Text(root, "ZapMQHost", "") is { Length: > 0 } host ? host : throw new ConfigException("\"ZapMQHost\" is required"),
                ZapMQPort = Integer(root, "ZapMQPort", "", required: true, minimum: 1),
                RateLoadConfig = Milliseconds(root, "RateLoadConfig", "", TimeSpan.FromMinutes(3), minimum: 1000),
                StartBatchSize = Integer(root, "StartBatchSize", "", defaultValue: 4, minimum: 1),
                StartBatchInterval = Milliseconds(root, "StartBatchIntervalMs", "", TimeSpan.FromSeconds(2), minimum: 0),
                Groups = ReadGroups(root, general)
            };
            return config;
        }
    }

    private static List<GroupConfig> ReadGroups(JsonElement root, GroupDefaults general)
    {
        if (!root.TryGetProperty("WorkerGroups", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new ConfigException("\"WorkerGroups\" must be a list");

        var groups = new List<GroupConfig>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ConfigException("Every item of \"WorkerGroups\" must be an object");

            var name = Text(item, "Name", "a group");
            if (string.IsNullOrWhiteSpace(name))
                throw new ConfigException("Every group needs a \"Name\"");
            if (!names.Add(name))
                throw new ConfigException($"There are two groups named \"{name}\"");

            var where = $"group \"{name}\"";
            var path = Text(item, "ApplicationFullPath", where);
            if (string.IsNullOrWhiteSpace(path))
                throw new ConfigException($"\"ApplicationFullPath\" is required in {where}");

            var defaults = new GroupDefaults(item, general);
            groups.Add(new GroupConfig
            {
                Name = name,
                Enabled = Boolean(item, "Enabled", where, defaultValue: true),
                ApplicationFullPath = path,
                Arguments = Text(item, "Arguments", where) ?? "",
                WorkingDirectory = Text(item, "WorkingDirectory", where) is { Length: > 0 } folder ? folder : null,
                TotalWorkers = Integer(item, "TotalWorkers", where, required: true, minimum: 0),
                MonitoringRate = Milliseconds(item, "MonitoringRate", where, TimeSpan.FromSeconds(30), minimum: 1000),
                TimeoutKeepAlive = Milliseconds(item, "TimeoutKeepAlive", where, TimeSpan.FromSeconds(15), minimum: 1000),
                StartupGrace = defaults.Milliseconds("StartupGraceMs", TimeSpan.FromSeconds(60)),
                SafeStopTimeout = defaults.Milliseconds("SafeStopTimeoutMs", TimeSpan.FromSeconds(30)),
                CrashWindow = defaults.Milliseconds("CrashWindowMs", TimeSpan.FromSeconds(60)),
                CrashLimit = defaults.Integer("CrashLimit", 3),
                Boost = ReadBoost(item, where)
            });
        }
        return groups;
    }

    private static BoostConfig ReadBoost(JsonElement group, string where)
    {
        if (!group.TryGetProperty("Boost", out var boost) || boost.ValueKind == JsonValueKind.Null)
            return new BoostConfig();
        if (boost.ValueKind != JsonValueKind.Object)
            throw new ConfigException($"\"Boost\" must be an object in {where}");

        where = "the boost of " + where;
        return new BoostConfig
        {
            Enabled = Boolean(boost, "Enabled", where, defaultValue: false),
            BoostWorkers = Integer(boost, "BoostWorkers", where, defaultValue: 0, minimum: 0),
            StartTime = TimeOfDay(boost, "StartTime", where),
            EndTime = TimeOfDay(boost, "EndTime", where)
        };
    }

    /// <summary>
    /// A setting that may be given for one group or, at the root, for all of them.
    /// </summary>
    private sealed class GroupDefaults(JsonElement element, GroupDefaults? general)
    {
        public TimeSpan Milliseconds(string name, TimeSpan fallback) =>
            element.TryGetProperty(name, out _)
                ? ConfigReader.Milliseconds(element, name, "", fallback, minimum: 0)
                : general?.Milliseconds(name, fallback) ?? fallback;

        public int Integer(string name, int fallback) =>
            element.TryGetProperty(name, out _)
                ? ConfigReader.Integer(element, name, "", defaultValue: fallback, minimum: 1)
                : general?.Integer(name, fallback) ?? fallback;
    }

    private static string? Text(JsonElement element, string name, string where)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new ConfigException($"\"{name}\" must be text{In(where)}");
        return value.GetString();
    }

    private static bool Boolean(JsonElement element, string name, string where, bool defaultValue)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return defaultValue;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ConfigException($"\"{name}\" must be true or false{In(where)}")
        };
    }

    private static int Integer(JsonElement element, string name, string where, bool required = false, int defaultValue = 0, int minimum = int.MinValue)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return required ? throw new ConfigException($"\"{name}\" is required{In(where)}") : defaultValue;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new ConfigException($"\"{name}\" must be a whole number{In(where)}");
        if (number < minimum)
            throw new ConfigException($"\"{name}\" cannot be less than {minimum}{In(where)}");
        return number;
    }

    private static TimeSpan Milliseconds(JsonElement element, string name, string where, TimeSpan defaultValue, int minimum)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return defaultValue;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            throw new ConfigException($"\"{name}\" must be a number of milliseconds{In(where)}");
        if (number < minimum)
            throw new ConfigException($"\"{name}\" cannot be less than {minimum}{In(where)}");
        return TimeSpan.FromMilliseconds(number);
    }

    private static TimeSpan TimeOfDay(JsonElement element, string name, string where)
    {
        var text = Text(element, name, where);
        if (string.IsNullOrWhiteSpace(text))
            return TimeSpan.Zero;
        if (TimeSpan.TryParseExact(text.Trim(), ["h\\:mm\\:ss", "hh\\:mm\\:ss", "h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out var time)
            && time < TimeSpan.FromDays(1))
            return time;
        throw new ConfigException($"\"{name}\" must be a time of day like 22:30:00{In(where)}");
    }

    private static string In(string where) => where.Length == 0 ? "" : " in " + where;
}
