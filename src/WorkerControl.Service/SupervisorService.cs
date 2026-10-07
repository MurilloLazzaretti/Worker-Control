using System.Globalization;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;

namespace WorkerControl.Service;

/// <summary>
/// Runs the supervisor: reads the configuration, keeps it up to date, ticks a few times per
/// second and, when the service stops, stops the workers too.
/// </summary>
internal sealed class SupervisorService(IOptions<ServiceOptions> options, ILoggerFactory loggers, TimeProvider time) : BackgroundService
{
    public const string ConfigFile = "ConfigWorkers.json";

    /// <summary>
    /// A file of this name in the data folder when the service stops means: leave the workers
    /// running. They are found again on the next start.
    /// </summary>
    public const string DetachFile = "detach.flag";

    private static readonly TimeSpan StopMargin = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger = loggers.CreateLogger("WorkerControl");
    private readonly ILogger _events = loggers.CreateLogger("WorkerControl.Events");
    private readonly string _directory = options.Value.ResolveDataDirectory();
    private readonly TimeSpan _tick = TimeSpan.FromMilliseconds(Math.Max(20, options.Value.TickMilliseconds));
    private int _reloadRequested;
    private WorkerControlConfig? _config;
    private Supervisor? _supervisor;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        var config = await WaitForConfigAsync(stopping);
        if (config is null)
            return;
        _config = config;

        var state = new StateStore(_directory, _logger);
        using var broker = new ZapMQBroker(config.ZapMQHost, config.ZapMQPort, time, loggers.CreateLogger("WorkerControl.ZapMQ"), Administer);
        var supervisor = new Supervisor(new ProcessHost(), broker, time);
        supervisor.Event += Record;
        _supervisor = supervisor;

        supervisor.ApplyConfig(config);
        supervisor.Adopt(state.Load());
        _logger.LogInformation("Supervising {Groups} groups from {Directory}; ZapMQ at {Host}:{Port}",
            config.Groups.Count, _directory, config.ZapMQHost, config.ZapMQPort);

        using var watcher = WatchConfig();
        var savedVersion = -1L;
        var nextReload = time.GetUtcNow() + config.RateLoadConfig;

        while (!stopping.IsCancellationRequested)
        {
            if (Interlocked.Exchange(ref _reloadRequested, 0) == 1 || time.GetUtcNow() >= nextReload)
            {
                Reload(supervisor);
                nextReload = time.GetUtcNow() + _config.RateLoadConfig;
            }

            supervisor.Tick();
            Save(supervisor, state, ref savedVersion);

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
            _logger.LogWarning("Stopping without stopping the workers, as asked by {File}; they will be found again on the next start", DetachFile);
            return;
        }

        await StopWorkersAsync(supervisor, state);
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
                return ConfigReader.Parse(File.ReadAllText(Path.Combine(_directory, ConfigFile)));
            }
            catch (Exception error) when (error is IOException or ConfigException or UnauthorizedAccessException)
            {
                if (complained != error.Message)
                    _logger.LogError("{File} cannot be used: {Error}. Waiting for it to be fixed", ConfigFile, error.Message);
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
        WorkerControlConfig config;
        try
        {
            config = ConfigReader.Parse(ReadConfigText());
        }
        catch (Exception error) when (error is IOException or ConfigException or UnauthorizedAccessException)
        {
            _logger.LogError("{File} was refused and the configuration in use stays: {Error}", ConfigFile, error.Message);
            return;
        }

        var current = _config!;
        if (config == current || Same(config, current))
            return;

        if (config.ZapMQHost != current.ZapMQHost || config.ZapMQPort != current.ZapMQPort)
            _logger.LogWarning("The address of ZapMQ changed in {File}; it only takes effect when the service is restarted", ConfigFile);

        _config = config;
        supervisor.ApplyConfig(config);
    }

    private static bool Same(WorkerControlConfig left, WorkerControlConfig right) =>
        left with { Groups = [] } == right with { Groups = [] } && left.Groups.SequenceEqual(right.Groups);

    /// <summary>
    /// An editor may still hold the file for an instant after saving it.
    /// </summary>
    private string ReadConfigText()
    {
        var path = Path.Combine(_directory, ConfigFile);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    private FileSystemWatcher? WatchConfig()
    {
        try
        {
            var watcher = new FileSystemWatcher(_directory, ConfigFile)
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
            _logger.LogWarning("Changes to {File} will only be noticed periodically: {Error}", ConfigFile, error.Message);
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
    /// The two commands of 1.x.
    /// </summary>
    private JObject? Administer(string command)
    {
        var supervisor = _supervisor;
        if (supervisor is null)
            return null;

        switch (command)
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

    private void Record(SupervisorEvent e)
    {
        var level = e.Kind switch
        {
            EventKind.WorkerCrashed or EventKind.WorkerHung or EventKind.WorkerStartTimedOut or EventKind.WorkerStartFailed
                or EventKind.SafeStopTimedOut or EventKind.GroupUnstable => LogLevel.Warning,
            _ => LogLevel.Information
        };

        // As text: an enum would be written between quotes.
        var kind = e.Kind.ToString();
        if (e.ProcessId is { } pid)
            _events.Log(level, "{Kind} [{Group}] pid {ProcessId}: {Detail}", kind, e.Group, pid, e.Detail);
        else if (e.Group is not null)
            _events.Log(level, "{Kind} [{Group}]: {Detail}", kind, e.Group, e.Detail);
        else
            _events.Log(level, "{Kind}: {Detail}", kind, e.Detail);
    }
}
