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

    /// <summary>
    /// How long the history of events is kept.
    /// </summary>
    public TimeSpan EventRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How long the health measurements are kept.
    /// </summary>
    public TimeSpan HealthRetention { get; init; } = TimeSpan.FromHours(24);

    public IReadOnlyList<GroupConfig> Groups { get; init; } = [];

    /// <summary>
    /// Windows services that are watched without being started from here.
    /// </summary>
    public ServicesConfig Services { get; init; } = new();
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

    /// <summary>
    /// The single daily window of 1.x.
    /// </summary>
    public BoostConfig Boost { get; init; } = new();

    public IReadOnlyList<BoostWindow> BoostWindows { get; init; } = [];

    public QueueScalingConfig? QueueScaling { get; init; }

    public RecycleConfig? Recycle { get; init; }

    /// <summary>
    /// The extra workers the boost asks for at this moment. Windows that overlap do not add
    /// up: the one asking for the most wins.
    /// </summary>
    public int BoostWorkersAt(DateTime localNow)
    {
        var extra = Boost.IsActive(localNow.TimeOfDay) ? Boost.BoostWorkers : 0;
        foreach (var window in BoostWindows)
            if (window.IsActive(localNow))
                extra = Math.Max(extra, window.Workers);
        return extra;
    }
}

/// <summary>
/// A window in which the group runs extra workers, on some days of the week or on all of them.
/// </summary>
public sealed record BoostWindow
{
    public int Workers { get; init; }

    public TimeSpan StartTime { get; init; }

    public TimeSpan EndTime { get; init; }

    /// <summary>
    /// Null means every day. The day is the one the window starts on.
    /// </summary>
    public IReadOnlySet<DayOfWeek>? Days { get; init; }

    public bool IsActive(DateTime localNow)
    {
        if (Workers <= 0 || StartTime == EndTime)
            return false;

        var time = localNow.TimeOfDay;
        if (StartTime < EndTime)
            return time >= StartTime && time < EndTime && OnDay(localNow.DayOfWeek);

        // Crosses midnight: after it, the window is the one that started the day before.
        if (time >= StartTime)
            return OnDay(localNow.DayOfWeek);
        return time < EndTime && OnDay(localNow.AddDays(-1).DayOfWeek);
    }

    private bool OnDay(DayOfWeek day) => Days is null || Days.Contains(day);
}

/// <summary>
/// Extra workers while a queue has messages piling up.
/// </summary>
public sealed record QueueScalingConfig
{
    public required string Queue { get; init; }

    /// <summary>
    /// One extra worker for each this many pending messages.
    /// </summary>
    public int PendingPerWorker { get; init; } = 50;

    /// <summary>
    /// The most workers the group may have, counting everything.
    /// </summary>
    public int MaxWorkers { get; init; }

    /// <summary>
    /// How long the extra workers stay after the queue no longer asks for them.
    /// </summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// A time of day at which the workers of the group are replaced, one by one.
/// </summary>
public sealed record RecycleConfig
{
    public TimeSpan Time { get; init; }

    public IReadOnlySet<DayOfWeek>? Days { get; init; }

    /// <summary>
    /// The next time the recycle is due, strictly after the given moment.
    /// </summary>
    public DateTime NextAfter(DateTime localNow)
    {
        for (var ahead = 0; ahead <= 7; ahead++)
        {
            var candidate = localNow.Date.AddDays(ahead) + Time;
            if (candidate > localNow && (Days is null || Days.Contains(candidate.DayOfWeek)))
                return candidate;
        }
        return DateTime.MaxValue;
    }
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
                EventRetention = TimeSpan.FromDays(Integer(root, "EventRetentionDays", "", defaultValue: 30, minimum: 1)),
                HealthRetention = TimeSpan.FromHours(Integer(root, "HealthRetentionHours", "", defaultValue: 24, minimum: 1)),
                Groups = ReadGroups(root, general),
                Services = ReadServices(root)
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
                Boost = ReadBoost(item, where),
                BoostWindows = ReadBoostWindows(item, where),
                QueueScaling = ReadQueueScaling(item, where),
                Recycle = ReadRecycle(item, where)
            });
        }
        return groups;
    }

    private static ServicesConfig ReadServices(JsonElement root)
    {
        if (!root.TryGetProperty("Services", out var services) || services.ValueKind == JsonValueKind.Null)
            return new ServicesConfig();
        if (services.ValueKind != JsonValueKind.Object)
            throw new ConfigException("\"Services\" must be an object");

        var folders = new List<string>();
        if (services.TryGetProperty("SuggestFrom", out var suggest) && suggest.ValueKind != JsonValueKind.Null)
        {
            if (suggest.ValueKind != JsonValueKind.Array || suggest.EnumerateArray().Any(folder => folder.ValueKind != JsonValueKind.String))
                throw new ConfigException("\"SuggestFrom\" must be a list of folders in \"Services\"");
            folders.AddRange(suggest.EnumerateArray().Select(folder => folder.GetString()!.Trim()).Where(folder => folder.Length > 0));
        }

        var items = new List<MonitoredServiceConfig>();
        if (services.TryGetProperty("Items", out var array) && array.ValueKind != JsonValueKind.Null)
        {
            if (array.ValueKind != JsonValueKind.Array)
                throw new ConfigException("\"Items\" must be a list in \"Services\"");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new ConfigException("Every item of \"Services\" must be an object");
                var name = Text(item, "Name", "a service")?.Trim();
                if (string.IsNullOrEmpty(name))
                    throw new ConfigException("Every service needs a \"Name\"");
                if (!names.Add(name))
                    throw new ConfigException($"The service \"{name}\" is listed twice");

                var where = $"service \"{name}\"";
                items.Add(new MonitoredServiceConfig
                {
                    Name = name,
                    AutoRestart = Boolean(item, "AutoRestart", where, defaultValue: false),
                    StopTimeout = Milliseconds(item, "StopTimeoutMs", where, TimeSpan.FromSeconds(30), minimum: 1000),
                    LogFiles = Text(item, "LogFiles", where) is { Length: > 0 } files ? files : null,
                    Check = ReadCheck(item, where)
                });
            }
        }
        return new ServicesConfig { SuggestFrom = folders, Items = items };
    }

    private static ServiceCheck? ReadCheck(JsonElement service, string where)
    {
        if (!service.TryGetProperty("Check", out var check) || check.ValueKind == JsonValueKind.Null)
            return null;
        if (check.ValueKind != JsonValueKind.Object)
            throw new ConfigException($"\"Check\" must be an object in {where}");

        where = "the check of " + where;
        var tcp = Text(check, "Tcp", where) is { Length: > 0 } address ? address.Trim() : null;
        var url = Text(check, "Url", where) is { Length: > 0 } link ? link.Trim() : null;
        if ((tcp is null) == (url is null))
            throw new ConfigException($"Give either \"Tcp\" or \"Url\" in {where}");
        if (tcp is not null && (tcp.LastIndexOf(':') is var colon && (colon <= 0 || !int.TryParse(tcp[(colon + 1)..], out var port) || port is < 1 or > 65535)))
            throw new ConfigException($"\"Tcp\" must be a host and a port, like localhost:8080, in {where}");
        if (url is not null && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            throw new ConfigException($"\"Url\" must be an http or https address in {where}");
        return new ServiceCheck(tcp, url);
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

    private static List<BoostWindow> ReadBoostWindows(JsonElement group, string where)
    {
        var windows = new List<BoostWindow>();
        if (!group.TryGetProperty("BoostWindows", out var array) || array.ValueKind == JsonValueKind.Null)
            return windows;
        if (array.ValueKind != JsonValueKind.Array)
            throw new ConfigException($"\"BoostWindows\" must be a list in {where}");

        where = "a boost window of " + where;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ConfigException($"Every boost window must be an object in {where}");
            windows.Add(new BoostWindow
            {
                Workers = Integer(item, "Workers", where, required: true, minimum: 1),
                StartTime = TimeOfDay(item, "StartTime", where),
                EndTime = TimeOfDay(item, "EndTime", where),
                Days = Days(item, where)
            });
        }
        return windows;
    }

    private static QueueScalingConfig? ReadQueueScaling(JsonElement group, string where)
    {
        if (!group.TryGetProperty("QueueScaling", out var scaling) || scaling.ValueKind == JsonValueKind.Null)
            return null;
        if (scaling.ValueKind != JsonValueKind.Object)
            throw new ConfigException($"\"QueueScaling\" must be an object in {where}");

        where = "the queue scaling of " + where;
        var queue = Text(scaling, "Queue", where);
        if (string.IsNullOrWhiteSpace(queue))
            throw new ConfigException($"\"Queue\" is required in {where}");
        return new QueueScalingConfig
        {
            Queue = queue,
            PendingPerWorker = Integer(scaling, "PendingPerWorker", where, defaultValue: 50, minimum: 1),
            MaxWorkers = Integer(scaling, "MaxWorkers", where, required: true, minimum: 1),
            Cooldown = Milliseconds(scaling, "CooldownMs", where, TimeSpan.FromMinutes(2), minimum: 0)
        };
    }

    private static RecycleConfig? ReadRecycle(JsonElement group, string where)
    {
        if (!group.TryGetProperty("Recycle", out var recycle) || recycle.ValueKind == JsonValueKind.Null)
            return null;
        if (recycle.ValueKind != JsonValueKind.Object)
            throw new ConfigException($"\"Recycle\" must be an object in {where}");

        where = "the recycle of " + where;
        if (!recycle.TryGetProperty("Time", out _))
            throw new ConfigException($"\"Time\" is required in {where}");
        return new RecycleConfig { Time = TimeOfDay(recycle, "Time", where), Days = Days(recycle, where) };
    }

    private static readonly Dictionary<string, DayOfWeek> DayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sun"] = DayOfWeek.Sunday, ["mon"] = DayOfWeek.Monday, ["tue"] = DayOfWeek.Tuesday, ["wed"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday, ["fri"] = DayOfWeek.Friday, ["sat"] = DayOfWeek.Saturday,
        ["sunday"] = DayOfWeek.Sunday, ["monday"] = DayOfWeek.Monday, ["tuesday"] = DayOfWeek.Tuesday, ["wednesday"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday, ["friday"] = DayOfWeek.Friday, ["saturday"] = DayOfWeek.Saturday
    };

    private static HashSet<DayOfWeek>? Days(JsonElement element, string where)
    {
        if (!element.TryGetProperty("Days", out var array) || array.ValueKind == JsonValueKind.Null)
            return null;
        if (array.ValueKind != JsonValueKind.Array)
            throw new ConfigException($"\"Days\" must be a list in {where}");

        var days = new HashSet<DayOfWeek>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !DayNames.TryGetValue(item.GetString()!.Trim(), out var day))
                throw new ConfigException($"\"Days\" takes mon, tue, wed, thu, fri, sat and sun in {where}");
            days.Add(day);
        }
        return days.Count == 0 ? null : days;
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
