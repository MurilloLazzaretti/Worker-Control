using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;

namespace WorkerControl.Service;

/// <summary>
/// Runs the supervisor: reads the configuration, keeps it up to date, ticks a few times per
/// second, records what happens and answers whoever administers the service. When the service
/// stops, it stops the workers too.
/// </summary>
internal sealed class SupervisorService(IOptions<ServiceOptions> options, ILoggerFactory loggers, TimeProvider time, IHostApplicationLifetime lifetime, IServiceManager services) : BackgroundService
{
    /// <summary>
    /// A file of this name in the data folder when the service stops means: leave the workers
    /// running. They are found again on the next start.
    /// </summary>
    public const string DetachFile = "detach.flag";

    private static readonly TimeSpan StopMargin = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly ILogger _logger = loggers.CreateLogger("WorkerControl");
    private readonly ILogger _events = loggers.CreateLogger("WorkerControl.Events");
    private readonly string _directory = options.Value.ResolveDataDirectory();
    private readonly TimeSpan _tick = TimeSpan.FromMilliseconds(Math.Max(20, options.Value.TickMilliseconds));
    private readonly TimeSpan _healthInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.HealthSampleSeconds));
    private readonly ConcurrentDictionary<int, StoredHealth> _lastHealth = new();
    private readonly Dictionary<int, (TimeSpan Processor, DateTimeOffset At)> _processorTimes = [];
    private readonly DateTimeOffset _startedAt = time.GetUtcNow();
    private ConfigFile _file = null!;
    private int _reloadRequested;
    private string _appliedText = "";
    private WorkerControlConfig? _config;
    private Supervisor? _supervisor;
    private ZapMQBroker? _broker;
    private TraceRelay? _trace;
    private ServiceWatcher? _watcher;
    private FrontendWatcher? _frontends;
    private HistoryStore? _history;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        _file = new ConfigFile(_directory);
        var config = await WaitForConfigAsync(stopping);
        if (config is null)
            return;
        _config = config;

        var state = new StateStore(_directory, _logger);
        using var history = HistoryStore.TryOpen(_directory, _logger);
        _history = history;
        using var queues = new QueueMonitor(config.ZapMQHost, config.ZapMQPort, loggers.CreateLogger("WorkerControl.ZapMQ"));
        using var broker = new ZapMQBroker(config.ZapMQHost, config.ZapMQPort, time, loggers.CreateLogger("WorkerControl.ZapMQ"), Administer);
        _broker = broker;
        using var trace = new TraceRelay(broker, time, loggers.CreateLogger("WorkerControl.Trace"));
        _trace = trace;
        var supervisor = new Supervisor(new ProcessHost(), broker, time, queues);
        supervisor.Event += Record;
        _supervisor = supervisor;

        using var probe = new ServiceProbe();
        var monitored = new ServiceWatcher(services, new ProcessInspector(), probe, time, Environment.ProcessId);
        monitored.Event += Record;
        _watcher = monitored;

        using var web = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5), AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
        var frontends = new FrontendWatcher(_directory, FrontendWatcher.Http(web), time, loggers.CreateLogger("WorkerControl.Frontends"));
        frontends.Event += Record;
        frontends.ApplyConfig(config.Frontends);
        _frontends = frontends;

        Record(EventKind.ServiceStarted, $"version {ServiceHost.Version}");
        supervisor.ApplyConfig(config);
        monitored.ApplyConfig(config.Services);
        supervisor.Adopt(state.Load());
        _logger.LogInformation("Supervising {Groups} groups from {Directory}; ZapMQ at {Host}:{Port}",
            config.Groups.Count, _directory, config.ZapMQHost, config.ZapMQPort);

        using var watcher = WatchConfig();
        var savedVersion = -1L;
        var nextReload = time.GetUtcNow() + config.RateLoadConfig;
        var nextHealth = time.GetUtcNow() + _healthInterval;
        var nextPrune = time.GetUtcNow();

        while (!stopping.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            if (Interlocked.Exchange(ref _reloadRequested, 0) == 1 || now >= nextReload)
            {
                Reload(supervisor);
                nextReload = now + _config.RateLoadConfig;
            }

            supervisor.Tick();
            monitored.Tick();
            frontends.Tick();
            Save(supervisor, state, ref savedVersion);

            if (now >= nextHealth)
            {
                MeasureHealth(supervisor, monitored, now);
                nextHealth = now + _healthInterval;
            }
            if (now >= nextPrune)
            {
                history?.Prune(now - _config.EventRetention, now - _config.HealthRetention);
                nextPrune = now + PruneInterval;
            }

            try
            {
                await Task.Delay(_tick, stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var detach = Path.Combine(_directory, DetachFile);
        if (File.Exists(detach))
        {
            File.Delete(detach);
            state.Save(supervisor.GetRecords());
            _logger.LogWarning("Stopping without stopping the workers, as asked; they will be found again on the next start");
            Record(EventKind.ServiceStopped, "the workers were left running");
            return;
        }

        await StopWorkersAsync(supervisor, state);
        Record(EventKind.ServiceStopped, "the workers were stopped");
    }

    private async Task StopWorkersAsync(Supervisor supervisor, StateStore state)
    {
        _logger.LogInformation("Stopping: asking every worker to leave");
        supervisor.BeginStop();

        var patience = _config!.Groups.Select(group => group.SafeStopTimeout).DefaultIfEmpty(TimeSpan.Zero).Max() + StopMargin;
        var deadline = time.GetUtcNow() + patience;
        while (!supervisor.IsIdle && time.GetUtcNow() < deadline)
        {
            supervisor.Tick();
            await Task.Delay(_tick);
        }

        if (!supervisor.IsIdle)
        {
            _logger.LogWarning("Some workers were still running; ending them");
            supervisor.KillAll();
            for (var attempt = 0; attempt < 20 && !supervisor.IsIdle; attempt++)
            {
                await Task.Delay(_tick);
                supervisor.Tick();
            }
        }

        state.Save(supervisor.GetRecords());
        _logger.LogInformation("Stopped");
    }

    /// <summary>
    /// The service is of no use without a configuration, but a missing or broken file is no
    /// reason to stop either: it waits for a good one.
    /// </summary>
    private async Task<WorkerControlConfig?> WaitForConfigAsync(CancellationToken stopping)
    {
        string? complained = null;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var text = _file.Read();
                var config = ConfigReader.Parse(text);
                _appliedText = text;
                return config;
            }
            catch (Exception error) when (error is IOException or ConfigException or UnauthorizedAccessException)
            {
                if (complained != error.Message)
                    _logger.LogError("{File} cannot be used: {Error}. Waiting for it to be fixed", ConfigFile.Name, error.Message);
                complained = error.Message;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return null;
    }

    private void Reload(Supervisor supervisor)
    {
        string text;
        WorkerControlConfig config;
        try
        {
            text = _file.Read();
            if (text == _appliedText)
                return;
            config = ConfigReader.Parse(text);
        }
        catch (Exception error) when (error is IOException or ConfigException or UnauthorizedAccessException)
        {
            _logger.LogError("{File} was refused and the configuration in use stays: {Error}", ConfigFile.Name, error.Message);
            Record(EventKind.ConfigRefused, error.Message);
            return;
        }

        var current = _config!;
        if (config.ZapMQHost != current.ZapMQHost || config.ZapMQPort != current.ZapMQPort)
            _logger.LogWarning("The address of ZapMQ changed in {File}; it only takes effect when the service is restarted", ConfigFile.Name);

        _config = config;
        _appliedText = text;
        supervisor.ApplyConfig(config);
        _watcher?.ApplyConfig(config.Services);
        _frontends?.ApplyConfig(config.Frontends);
    }

    private FileSystemWatcher? WatchConfig()
    {
        try
        {
            var watcher = new FileSystemWatcher(_directory, ConfigFile.Name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime
            };
            FileSystemEventHandler changed = (_, _) => Interlocked.Exchange(ref _reloadRequested, 1);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (_, _) => Interlocked.Exchange(ref _reloadRequested, 1);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception error) when (error is IOException or ArgumentException or PlatformNotSupportedException)
        {
            // The periodic reload still picks changes up.
            _logger.LogWarning("Changes to {File} will only be noticed periodically: {Error}", ConfigFile.Name, error.Message);
            return null;
        }
    }

    private static void Save(Supervisor supervisor, StateStore state, ref long savedVersion)
    {
        var version = supervisor.RecordsVersion;
        if (version == savedVersion)
            return;
        state.Save(supervisor.GetRecords());
        savedVersion = version;
    }

    /// <summary>
    /// Processor use is the share of one measuring interval the worker spent running, over all
    /// the processors of the machine.
    /// </summary>
    private void MeasureHealth(Supervisor supervisor, ServiceWatcher watcher, DateTimeOffset now)
    {
        // The watched services are measured like the workers; they only have no keep-alive.
        var samples = supervisor.SampleHealth().Concat(watcher.GetStatus()
            .Where(service => service is { State: ServiceState.Running, Process: not null, Service.ProcessId: > 0 })
            .Select(service => new HealthSample(ServiceWatcher.GroupPrefix + service.Config.Name, service.Service!.ProcessId, WorkerState.Up,
                now - service.Process!.StartTime, new ProcessUsage(service.Process.ProcessorTime, service.Process.MemoryBytes), null)))
            .ToList();
        var stored = new List<StoredHealth>(samples.Count);
        foreach (var sample in samples)
        {
            double? cpu = null;
            if (sample.Usage is { } usage)
            {
                if (_processorTimes.TryGetValue(sample.ProcessId, out var before) && now > before.At)
                {
                    var share = (usage.ProcessorTime - before.Processor).TotalMilliseconds / (now - before.At).TotalMilliseconds;
                    cpu = Math.Round(Math.Clamp(share / Environment.ProcessorCount * 100, 0, 100), 2);
                }
                _processorTimes[sample.ProcessId] = (usage.ProcessorTime, now);
            }

            var health = new StoredHealth(now, sample.Group, sample.ProcessId, sample.State.ToString(), Math.Round(sample.Uptime.TotalSeconds, 1),
                cpu, sample.Usage?.MemoryBytes, sample.KeepAliveLatency?.TotalMilliseconds);
            stored.Add(health);
            _lastHealth[sample.ProcessId] = health;
        }

        var alive = samples.Select(sample => sample.ProcessId).ToHashSet();
        foreach (var gone in _processorTimes.Keys.Where(pid => !alive.Contains(pid)).ToList())
        {
            _processorTimes.Remove(gone);
            _lastHealth.TryRemove(gone, out _);
        }
        _history?.Add(stored);
    }

    // ---------------------------------------------------------------- administration

    private JObject? Administer(JObject request)
    {
        var supervisor = _supervisor;
        if (supervisor is null)
            return null;

        if (request.Value<string>("Command") is { } command)
        {
            try
            {
                return Command(supervisor, command, request);
            }
            catch (Exception error)
            {
                _logger.LogError(error, "The administration command {Command} failed", command);
                return Admin.Error("failed", error.Message);
            }
        }

        // The two commands of 1.x, answered as 1.x did.
        switch (request.Value<string>("Message"))
        {
            case "CurrentWorkers":
                return LegacyAdmin.CurrentWorkers(supervisor.GetStatus(), CultureInfo.CurrentCulture);

            case "ReloadConfig":
                Interlocked.Exchange(ref _reloadRequested, 1);
                return LegacyAdmin.Reloaded();

            default:
                return null;
        }
    }

    private JObject Command(Supervisor supervisor, string command, JObject request)
    {
        var by = request.Value<string>("By") is { Length: > 0 } who ? $" (by {who})" : "";
        switch (command)
        {
            case "Status":
                return Admin.Status(supervisor.GetStatus(), _startedAt, _config!.ZapMQHost, _config.ZapMQPort,
                    _broker?.HealthySince(time.GetUtcNow() - TimeSpan.FromSeconds(10)) ?? false, _lastHealth, _watcher!.GetStatus());

            case "Frontends":
            {
                var (apps, publications) = _frontends!.Snapshot();
                return Admin.Frontends(apps, publications);
            }

            case "ListServices":
            {
                var watched = _config!.Services.Items.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return Admin.Installed(services.List(), _config.Services.SuggestFrom, watched);
            }

            case "StartService":
            case "StopService":
            case "RestartService":
            {
                if (request.Value<string>("Name") is not { Length: > 0 } name)
                    return Admin.Error("invalid-request", "\"Name\" is required");
                var result = command switch
                {
                    "StartService" => _watcher!.Start(name),
                    "StopService" => _watcher!.Stop(name),
                    _ => _watcher!.Restart(name)
                };
                if (!result.Ok)
                    return Admin.Error(result.Code!, result.Message!);
                Record(EventKind.ManualAction, command switch { "StartService" => "start", "StopService" => "stop", _ => "restart" } + by, ServiceWatcher.GroupPrefix + name);
                return Admin.Ok();
            }

            case "GetConfig":
            {
                var text = _file.Read();
                return Admin.Ok(answer =>
                {
                    answer["Text"] = text;
                    answer["Config"] = JObject.Parse(text);
                });
            }

            case "SetConfig":
            {
                var given = request["Config"];
                var text = given switch
                {
                    JObject asObject => asObject.ToString(Newtonsoft.Json.Formatting.Indented),
                    JValue { Type: JTokenType.String } asText => (string)asText!,
                    _ => null
                };
                if (text is null)
                    return Admin.Error("invalid-request", "\"Config\" must be the configuration, as an object or as text");
                try
                {
                    _file.Write(text);
                }
                catch (ConfigException error)
                {
                    return Admin.Error("invalid-config", error.Message);
                }
                Record(EventKind.ManualAction, "configuration replaced" + by);
                Interlocked.Exchange(ref _reloadRequested, 1);
                return Admin.Ok();
            }

            case "SetGroupEnabled":
            {
                if (request.Value<string>("Group") is not { } group || request["Enabled"]?.Type != JTokenType.Boolean)
                    return Admin.Error("invalid-request", "\"Group\" and \"Enabled\" are required");
                var enabled = (bool)request["Enabled"]!;
                if (!_file.ChangeGroup(group, item => item["Enabled"] = enabled))
                    return Admin.Error("not-found", $"There is no group named \"{group}\"");
                Record(EventKind.ManualAction, (enabled ? "group enabled" : "group disabled") + by, group);
                Interlocked.Exchange(ref _reloadRequested, 1);
                return Admin.Ok();
            }

            case "SetGroupWorkers":
            {
                if (request.Value<string>("Group") is not { } group || request["TotalWorkers"]?.Type != JTokenType.Integer || (int)request["TotalWorkers"]! < 0)
                    return Admin.Error("invalid-request", "\"Group\" and a \"TotalWorkers\" of zero or more are required");
                var workers = (int)request["TotalWorkers"]!;
                if (!_file.ChangeGroup(group, item => item["TotalWorkers"] = workers))
                    return Admin.Error("not-found", $"There is no group named \"{group}\"");
                Record(EventKind.ManualAction, $"workers set to {workers}{by}", group);
                Interlocked.Exchange(ref _reloadRequested, 1);
                return Admin.Ok();
            }

            case "RestartWorker":
            {
                if (request["ProcessId"]?.Type != JTokenType.Integer)
                    return Admin.Error("invalid-request", "\"ProcessId\" is required");
                var pid = (int)request["ProcessId"]!;
                if (!supervisor.RestartWorker(pid))
                    return Admin.Error("not-found", $"No worker in service has the process id {pid}");
                Record(EventKind.ManualAction, "worker restart" + by, processId: pid);
                return Admin.Ok();
            }

            case "RestartGroup":
            {
                if (request.Value<string>("Group") is not { } group)
                    return Admin.Error("invalid-request", "\"Group\" is required");
                if (!supervisor.RestartGroup(group))
                    return Admin.Error("not-found", $"There is no group named \"{group}\"");
                Record(EventKind.ManualAction, "group restart" + by, group);
                return Admin.Ok();
            }

            case "Events" when _history is null:
            case "Health" when _history is null:
                return Admin.Error("history-unavailable", "The history is not being kept on this machine; see the log of the service");

            case "Events":
                return Admin.Events(_history!.Events(
                    request.Value<string>("Group"), request.Value<string>("Kind"),
                    request.Value<DateTimeOffset?>("From"), request.Value<DateTimeOffset?>("To"),
                    request.Value<int?>("Limit") ?? 200));

            case "Health":
                return Admin.Health(_history!.Health(
                    request.Value<string>("Group"), request.Value<int?>("ProcessId"),
                    request.Value<DateTimeOffset?>("From"), request.Value<DateTimeOffset?>("To"),
                    request.Value<int?>("Limit") ?? 1000));

            case "StartTrace":
            {
                if (request.Value<int?>("ProcessId") is not { } processId || request.Value<string>("Queue") is not { Length: > 0 } queue)
                    return Admin.Error("invalid-request", "\"ProcessId\" and \"Queue\" are required");
                if (!supervisor.GetStatus().Any(group => group.Workers.Any(worker => worker.ProcessId == processId)))
                    return Admin.Error("not-found", $"There is no worker with process id {processId}");
                var lease = TimeSpan.FromSeconds(Math.Clamp(request.Value<int?>("LeaseSeconds") ?? 30, 1, 300));
                return _trace!.Start(processId, queue, lease) is { } problem ? Admin.Error("trace-failed", problem) : Admin.Ok();
            }

            case "StopTrace":
            {
                if (request.Value<int?>("ProcessId") is not { } processId)
                    return Admin.Error("invalid-request", "\"ProcessId\" is required");
                _trace!.Stop(processId);
                return Admin.Ok();
            }

            case "DetachAndStop":
                File.WriteAllText(Path.Combine(_directory, DetachFile), "");
                Record(EventKind.ManualAction, "stop leaving the workers running" + by);
                // After the answer has gone out.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    lifetime.StopApplication();
                });
                return Admin.Ok();

            default:
                return Admin.Error("unknown-command", $"Unknown command \"{command}\"");
        }
    }

    // ---------------------------------------------------------------- records

    private void Record(EventKind kind, string detail, string? group = null, int? processId = null) =>
        Record(new SupervisorEvent(time.GetUtcNow(), kind, group, processId, detail));

    private void Record(SupervisorEvent e)
    {
        var level = e.Kind switch
        {
            EventKind.WorkerCrashed or EventKind.WorkerHung or EventKind.WorkerStartTimedOut or EventKind.WorkerStartFailed
                or EventKind.SafeStopTimedOut or EventKind.GroupUnstable or EventKind.ConfigRefused
                or EventKind.MonitoredCrashed or EventKind.MonitoredStopTimedOut or EventKind.MonitoredActionFailed or EventKind.MonitoredCheckFailed or EventKind.FrontendDown => LogLevel.Warning,
            _ => LogLevel.Information
        };

        // As text: an enum would be written between quotes.
        var kind = e.Kind.ToString();
        if (e.ProcessId is { } pid && e.Group is null)
            _events.Log(level, "{Kind} pid {ProcessId}: {Detail}", kind, pid, e.Detail);
        else if (e.ProcessId is { } worker)
            _events.Log(level, "{Kind} [{Group}] pid {ProcessId}: {Detail}", kind, e.Group, worker, e.Detail);
        else if (e.Group is not null)
            _events.Log(level, "{Kind} [{Group}]: {Detail}", kind, e.Group, e.Detail);
        else
            _events.Log(level, "{Kind}: {Detail}", kind, e.Detail);

        _history?.Add(e);
    }
}
