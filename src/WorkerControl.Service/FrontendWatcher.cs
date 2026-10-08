using System.Text.Json;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;

namespace WorkerControl.Service;

/// <summary>
/// The micro frontends published on this machine: which modules there are, in which version,
/// whether each one answers, and when each one was published.
///
/// Everything comes from files: the manifest that lists the modules, the entry file of each
/// one and, beside it, the file where the module says its version. What changed since the
/// last look is a publication, and is kept.
/// </summary>
internal sealed class FrontendWatcher(string directory, Func<string, string?, (int? Status, string? Error)> fetch, TimeProvider time, ILogger logger)
{
    public const string GroupPrefix = "frontend:";

    /// <summary>
    /// What stands for the files of the application itself, around the modules.
    /// </summary>
    public const string Shell = "(shell)";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private const int KeptPublications = 300;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private sealed class Memory
    {
        public Dictionary<string, AppMemory> Apps { get; set; } = [];
        public List<Publication> Publications { get; set; } = [];
    }

    private sealed class AppMemory
    {
        public DateTimeOffset? ShellAt { get; set; }
        public Dictionary<string, ModuleMemory> Modules { get; set; } = [];
    }

    private sealed class ModuleMemory
    {
        public DateTimeOffset? EntryAt { get; set; }
        public string? Version { get; set; }
        public int? Build { get; set; }
        public bool? Online { get; set; }
        public int Files { get; set; }
        public long Bytes { get; set; }
    }

    public sealed record Publication(DateTimeOffset At, string App, string Module, string Kind, string? FromVersion, string? ToVersion, int? FromBuild, int? ToBuild, int Count);

    public sealed record VersionEntry(string? Version, string? Date, IReadOnlyList<string> Descriptions);

    public sealed record Module(string Name, string Entry, string State, bool? Online, int? Status, string? Problem, string? Title, int? Build,
        IReadOnlyList<VersionEntry> Versions, DateTimeOffset? PublishedAt, int Files, long Bytes);

    public sealed record App(string Name, string Root, string? BaseUrl, string? Problem, DateTimeOffset CheckedAt, DateTimeOffset? ShellAt,
        IReadOnlyList<Module> Modules, IReadOnlyList<string> Orphans);

    private readonly object _gate = new();
    private readonly string _path = Path.Combine(directory, "frontends.json");
    private IReadOnlyList<FrontendConfig> _config = [];
    private IReadOnlyList<App> _apps = [];
    private Memory? _memory;
    private DateTimeOffset _next = DateTimeOffset.MinValue;
    private int _scanning;

    public event Action<SupervisorEvent>? Event;

    public void ApplyConfig(IReadOnlyList<FrontendConfig> config)
    {
        lock (_gate)
        {
            if (_config.SequenceEqual(config))
                return;
            _config = config;
            _apps = [.. _apps.Where(app => config.Any(item => item.Name == app.Name))];
            _next = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Looks again when it is time to, without holding whoever calls.
    /// </summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_config.Count == 0 || time.GetUtcNow() < _next)
                return;
            _next = time.GetUtcNow() + Interval;
        }
        if (Interlocked.Exchange(ref _scanning, 1) == 1)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                Scan();
            }
            catch (Exception error)
            {
                logger.LogError(error, "The frontends could not be looked at");
            }
            finally
            {
                Interlocked.Exchange(ref _scanning, 0);
            }
        });
    }

    public (IReadOnlyList<App> Apps, IReadOnlyList<Publication> Publications) Snapshot()
    {
        lock (_gate)
            return (_apps, [.. (_memory ??= Load()).Publications.AsEnumerable().Reverse()]);
    }

    /// <summary>
    /// Looks at every configured frontend now.
    /// </summary>
    public void Scan()
    {
        IReadOnlyList<FrontendConfig> config;
        Memory memory;
        lock (_gate)
        {
            config = _config;
            memory = _memory ??= Load();
        }

        var now = time.GetUtcNow();
        var apps = new List<App>();
        var raised = new List<SupervisorEvent>();
        var changed = false;
        foreach (var item in config)
        {
            var known = memory.Apps.TryGetValue(item.Name, out var remembered);
            remembered ??= new AppMemory();
            apps.Add(Look(item, remembered, firstLook: !known, now, memory.Publications, raised, ref changed));
            memory.Apps[item.Name] = remembered;
        }

        lock (_gate)
        {
            _apps = apps;
            if (memory.Publications.Count > KeptPublications)
                memory.Publications.RemoveRange(0, memory.Publications.Count - KeptPublications);
            if (changed)
                Save(memory);
        }
        foreach (var e in raised)
            Event?.Invoke(e);
    }

    private App Look(FrontendConfig config, AppMemory memory, bool firstLook, DateTimeOffset now, List<Publication> publications, List<SupervisorEvent> raised, ref bool changed)
    {
        App Failed(string problem) => new(config.Name, config.Root, config.BaseUrl, problem, now, null, [], []);

        if (!Directory.Exists(config.Root))
            return Failed($"The folder {config.Root} does not exist");

        var manifestPath = Resolve(config.Root, config.Manifest);
        Dictionary<string, string> entries;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            entries = document.RootElement.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return Failed($"The manifest {config.Manifest} could not be read: {error.Message}");
        }

        var group = GroupPrefix + config.Name;
        var modules = new List<Module>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenForTheFirstTime = 0;

        foreach (var (name, entry) in entries.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var file = Resolve(config.Root, entry);
            var folder = Path.GetDirectoryName(file)!;
            folders.Add(Path.GetFullPath(folder));
            var exists = File.Exists(file);
            DateTimeOffset? entryAt = exists ? Whole(File.GetLastWriteTimeUtc(file)) : null;
            var (title, build, versions) = exists ? ReadVersion(Path.Combine(folder, config.VersionFile)) : (null, null, []);

            var had = memory.Modules.TryGetValue(name, out var before);
            before ??= new ModuleMemory();

            // Counting the files of a module is only worth doing again when it was published.
            if (exists && (before.EntryAt != entryAt || before.Files == 0))
                (before.Files, before.Bytes) = Measure(folder);

            bool? online = null;
            int? status = null;
            string? problem = exists ? null : "The entry file is not on disk";
            if (config.BaseUrl is { Length: > 0 } baseUrl)
            {
                var (code, error) = fetch(baseUrl.TrimEnd('/') + "/" + entry.TrimStart('/'), config.Host);
                status = code;
                online = code == 200;
                if (!online.Value)
                    problem ??= code is null ? "Did not answer: " + error : $"Answered {code}";
            }

            var version = versions.FirstOrDefault()?.Version;
            if (!had)
            {
                if (firstLook)
                {
                    seenForTheFirstTime++;
                }
                else if (exists)
                {
                    publications.Add(new Publication(now, config.Name, name, "new", null, version, null, build, 1));
                    raised.Add(new SupervisorEvent(now, EventKind.FrontendPublished, group, null, $"{name}: new module{Describe(null, version, null, build)}"));
                }
                changed = true;
            }
            else if (exists && before.EntryAt is not null && before.EntryAt != entryAt)
            {
                publications.Add(new Publication(now, config.Name, name, "published", before.Version, version, before.Build, build, 1));
                raised.Add(new SupervisorEvent(now, EventKind.FrontendPublished, group, null, $"{name}: published{Describe(before.Version, version, before.Build, build)}"));
                changed = true;
            }

            if (had && before.Online == true && online == false)
                raised.Add(new SupervisorEvent(now, EventKind.FrontendDown, group, null, $"{name}: {problem}"));
            else if (had && before.Online == false && online == true)
                raised.Add(new SupervisorEvent(now, EventKind.FrontendUp, group, null, $"{name}: answering again"));

            changed |= before.EntryAt != entryAt || before.Version != version || before.Build != build || before.Online != online;
            (before.EntryAt, before.Version, before.Build, before.Online) = (entryAt ?? before.EntryAt, version, build, online);
            memory.Modules[name] = before;

            modules.Add(new Module(name, entry, !exists ? "Incomplete" : online == false ? "Down" : "Up", online, status, problem, title, build, versions,
                entryAt, exists ? before.Files : 0, exists ? before.Bytes : 0));
        }

        foreach (var gone in memory.Modules.Keys.Where(name => !entries.ContainsKey(name)).ToList())
        {
            memory.Modules.Remove(gone);
            publications.Add(new Publication(now, config.Name, gone, "removed", null, null, null, null, 1));
            raised.Add(new SupervisorEvent(now, EventKind.FrontendPublished, group, null, $"{gone}: left the manifest"));
            changed = true;
        }

        // The application around the modules: when it changes, everybody gets new code.
        DateTimeOffset? shellAt = null;
        var index = Path.Combine(config.Root, "index.html");
        if (File.Exists(index))
        {
            shellAt = Whole(File.GetLastWriteTimeUtc(index));
            if (!firstLook && memory.ShellAt is not null && memory.ShellAt != shellAt)
            {
                publications.Add(new Publication(now, config.Name, Shell, "published", null, null, null, null, 1));
                raised.Add(new SupervisorEvent(now, EventKind.FrontendPublished, group, null, "the application around the modules was published"));
            }
            changed |= memory.ShellAt != shellAt;
            memory.ShellAt = shellAt;
        }

        if (firstLook && seenForTheFirstTime > 0)
            publications.Add(new Publication(now, config.Name, "*", "first-seen", null, null, null, null, seenForTheFirstTime));

        return new App(config.Name, config.Root, config.BaseUrl, null, now, shellAt, modules, Orphans(folders));
    }

    /// <summary>
    /// Folders beside the ones of the modules that no entry of the manifest points to.
    /// </summary>
    private static List<string> Orphans(HashSet<string> folders)
    {
        var orphans = new List<string>();
        foreach (var parent in folders.Select(folder => Path.GetDirectoryName(folder)).Where(parent => parent is not null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Modules that live at the root itself have everything else as neighbours.
            if (folders.Contains(Path.GetFullPath(parent!)))
                continue;
            try
            {
                orphans.AddRange(Directory.GetDirectories(parent!)
                    .Where(folder => !folders.Contains(Path.GetFullPath(folder)))
                    .Select(folder => Path.GetFileName(folder)!));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Nothing to say about a folder that cannot be listed.
            }
        }
        orphans.Sort(StringComparer.OrdinalIgnoreCase);
        return orphans;
    }

    private static (string? Title, int? Build, IReadOnlyList<VersionEntry> Versions) ReadVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
                return (null, null, []);
            var root = JObject.Parse(File.ReadAllText(path));
            var versions = (root["versions"] as JArray ?? [])
                .OfType<JObject>()
                .Select(item => new VersionEntry(
                    item["version"]?.ToString(),
                    item["date"]?.ToString(),
                    [.. (item["descriptions"] as JArray ?? []).Select(description => description.ToString())]))
                .ToList();
            return ((string?)root["nome"] ?? (string?)root["name"], root["build"]?.Type == JTokenType.Integer ? (int)root["build"]! : null, versions);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException or InvalidCastException)
        {
            return (null, null, []);
        }
    }

    private static (int Files, long Bytes) Measure(string folder)
    {
        try
        {
            var files = new DirectoryInfo(folder).GetFiles("*", SearchOption.AllDirectories);
            return (files.Length, files.Sum(file => file.Length));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    private static string Describe(string? fromVersion, string? toVersion, int? fromBuild, int? toBuild)
    {
        var parts = new List<string>();
        if (toVersion is not null)
            parts.Add(fromVersion is null || fromVersion == toVersion ? $"version {toVersion}" : $"version {fromVersion} → {toVersion}");
        if (toBuild is not null)
            parts.Add(fromBuild is null || fromBuild == toBuild ? $"build {toBuild}" : $"build {fromBuild} → {toBuild}");
        return parts.Count == 0 ? "" : ", " + string.Join(", ", parts);
    }

    /// <summary>
    /// A path of the manifest, written for the web, as a path of this machine.
    /// </summary>
    private static string Resolve(string root, string relative) =>
        Path.Combine(root, relative.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));

    /// <summary>
    /// Without the fraction of a second, which does not survive being written and read back.
    /// </summary>
    private static DateTimeOffset Whole(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private Memory Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<Memory>(File.ReadAllText(_path)) ?? new Memory() : new Memory();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning("What was known about the frontends could not be read and starts over: {Error}", error.Message);
            return new Memory();
        }
    }

    private void Save(Memory memory)
    {
        try
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(memory, Format));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("What is known about the frontends could not be written: {Error}", error.Message);
        }
    }

    /// <summary>
    /// Asks an address for a file the way a browser would, optionally saying which site it
    /// is for when the address is only the machine.
    /// </summary>
    public static Func<string, string?, (int? Status, string? Error)> Http(HttpClient client) => (url, host) =>
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(host))
                request.Headers.Host = host;
            using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
            return ((int)response.StatusCode, null);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return (null, (error.InnerException ?? error).Message);
        }
    };
}
