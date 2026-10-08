namespace WorkerControl.Core;

/// <summary>
/// Windows services the machine runs that are not started by the supervisor, but are watched
/// by it: whether they are running, how they are doing, and when they stop.
/// </summary>
public sealed record ServicesConfig
{
    /// <summary>
    /// Folders whose services are offered first to whoever is choosing what to watch.
    /// </summary>
    public IReadOnlyList<string> SuggestFrom { get; init; } = [];

    public IReadOnlyList<MonitoredServiceConfig> Items { get; init; } = [];
}

public sealed record MonitoredServiceConfig
{
    /// <summary>
    /// The name of the service in Windows: the short one, not the one shown to people.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Starts the service again when it stops by itself with an error. Never when somebody
    /// stopped it.
    /// </summary>
    public bool AutoRestart { get; init; }

    /// <summary>
    /// How long a service asked to stop is waited for.
    /// </summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Where the service writes its log: a folder and a pattern for the file name.
    /// </summary>
    public string? LogFiles { get; init; }

    public ServiceCheck? Check { get; init; }
}

/// <summary>
/// Something a running service has to do to be considered well: accept a connection on a
/// port, or answer a request with success.
/// </summary>
public sealed record ServiceCheck(string? Tcp, string? Url);

public enum ServiceState
{
    /// <summary>There is no service of that name on the machine.</summary>
    Missing,
    Stopped,
    Starting,
    Stopping,
    Running,
    Paused
}

/// <summary>
/// A service as the machine tells it. The process id is zero and the exit code meaningless
/// while it is not running.
/// </summary>
public sealed record InstalledService(string Name, string DisplayName, string? ExecutablePath, ServiceState State, string StartType, int ProcessId, int ExitCode);

public interface IServiceManager
{
    /// <summary>
    /// Every service installed on the machine.
    /// </summary>
    IReadOnlyList<InstalledService> List();

    /// <summary>
    /// One service, or null when there is none of that name.
    /// </summary>
    InstalledService? Find(string name);

    /// <summary>
    /// Asks the service to start. Does not wait for it. Throws when the request is refused.
    /// </summary>
    void Start(string name);

    /// <summary>
    /// Asks the service to stop. Does not wait for it. Throws when the request is refused.
    /// </summary>
    void Stop(string name);
}

/// <summary>
/// A process and everything it started, measured together: a service is often only what
/// starts the program that does the work. <see cref="Children"/> are the names of the
/// processes it started, when there are any.
/// </summary>
public sealed record ProcessInfo(DateTimeOffset StartTime, TimeSpan ProcessorTime, long MemoryBytes, int Threads, int Handles, IReadOnlyList<string>? Children = null, IReadOnlyList<int>? ChildIds = null);

public interface IProcessInspector
{
    /// <summary>
    /// Null when the process is gone or cannot be read.
    /// </summary>
    ProcessInfo? Inspect(int processId);
}

public interface IServiceProbe
{
    /// <summary>
    /// Runs the check. May take a few seconds.
    /// </summary>
    (bool Ok, string Detail) Check(ServiceCheck check);
}

public sealed record MonitoredServiceStatus(
    MonitoredServiceConfig Config,
    InstalledService? Service,
    ProcessInfo? Process,
    bool? CheckOk,
    string? CheckDetail,
    DateTimeOffset? CheckedAt,
    DateTimeOffset? RestartingAt,
    bool Restarting,
    bool Unstable,
    bool IsSupervisor)
{
    public ServiceState State => Service?.State ?? ServiceState.Missing;
}

public sealed record ServiceActionResult(bool Ok, string? Code = null, string? Message = null)
{
    public static readonly ServiceActionResult Done = new(true);

    public static ServiceActionResult Refused(string code, string message) => new(false, code, message);
}

/// <summary>
/// Watches the services of the configuration: notices when one stops or starts, starts again
/// the ones set up for that, runs their checks and carries out what somebody asks (start,
/// stop, restart). Like the supervisor, it only moves when ticked and never waits.
/// </summary>
public sealed class ServiceWatcher(IServiceManager manager, IProcessInspector inspector, IServiceProbe probe, TimeProvider time, int ownProcessId)
{
    /// <summary>
    /// What the group of an event or of a health measurement starts with when it is about a
    /// service and not about a group of workers.
    /// </summary>
    public const string GroupPrefix = "service:";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan FirstRestartDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan LongestRestartDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A service that ran for this long before stopping is not counted as failing in a row.
    /// </summary>
    public static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);

    private sealed class Watched(MonitoredServiceConfig config)
    {
        public MonitoredServiceConfig Config { get; set; } = config;
        public bool Seen { get; set; }
        public InstalledService? Last { get; set; }
        public ProcessInfo? Process { get; set; }
        public DateTimeOffset? UpSince { get; set; }

        /// <summary>A stop was asked for through here and has not happened yet.</summary>
        public bool StopAsked { get; set; }

        /// <summary>Once stopped, it is to be started again.</summary>
        public bool Restarting { get; set; }
        public DateTimeOffset StopDeadline { get; set; }

        public int Failures { get; set; }
        public DateTimeOffset? RestartAt { get; set; }

        public bool? CheckOk { get; set; }
        public string? CheckDetail { get; set; }
        public DateTimeOffset? CheckedAt { get; set; }
        public bool Checking { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Watched> _services = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _next = DateTimeOffset.MinValue;

    public event Action<SupervisorEvent>? Event;

    /// <summary>
    /// Runs the checks inside <see cref="Tick"/> instead of beside it. For the tests.
    /// </summary>
    public bool ChecksInline { get; set; }

    public void ApplyConfig(ServicesConfig config)
    {
        lock (_gate)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in config.Items)
            {
                wanted.Add(item.Name);
                if (_services.TryGetValue(item.Name, out var watched))
                {
                    if (watched.Config.Check != item.Check)
                        (watched.CheckOk, watched.CheckDetail, watched.CheckedAt) = (null, null, null);
                    if (!item.AutoRestart)
                        watched.RestartAt = null;
                    watched.Config = item;
                }
                else
                {
                    _services[item.Name] = new Watched(item);
                }
            }
            foreach (var gone in _services.Keys.Where(name => !wanted.Contains(name)).ToList())
                _services.Remove(gone);
            // The new ones are looked at right away.
            _next = DateTimeOffset.MinValue;
        }
    }

    public void Tick()
    {
        List<SupervisorEvent> raised = [];
        List<(Watched Watched, ServiceCheck Check)> checks = [];
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (now < _next)
                return;
            _next = now + Interval;

            foreach (var watched in _services.Values)
                Observe(watched, now, raised, checks);
        }

        foreach (var e in raised)
            Event?.Invoke(e);
        foreach (var (watched, check) in checks)
        {
            if (ChecksInline)
                RunCheck(watched, check);
            else
                _ = Task.Run(() => RunCheck(watched, check));
        }
    }

    public IReadOnlyList<MonitoredServiceStatus> GetStatus()
    {
        lock (_gate)
        {
            return _services.Values
                .OrderBy(watched => watched.Config.Name, StringComparer.OrdinalIgnoreCase)
                .Select(watched => new MonitoredServiceStatus(
                    watched.Config,
                    watched.Last,
                    watched.Process,
                    watched.CheckOk,
                    watched.CheckDetail,
                    watched.CheckedAt,
                    watched.RestartAt,
                    watched.Restarting,
                    Unstable: watched.Failures >= 3,
                    IsSupervisor: IsOwn(watched.Last)))
                .ToList();
        }
    }

    public ServiceActionResult Start(string name) => Act(name, (watched, current, now) =>
    {
        if (current.State is ServiceState.Running or ServiceState.Starting)
            return ServiceActionResult.Refused("invalid-state", $"The service \"{name}\" is already running");
        manager.Start(current.Name);
        watched.RestartAt = null;
        watched.StopAsked = false;
        watched.Restarting = false;
        return ServiceActionResult.Done;
    });

    public ServiceActionResult Stop(string name) => Act(name, (watched, current, now) =>
    {
        if (current.State == ServiceState.Stopped)
            return ServiceActionResult.Refused("invalid-state", $"The service \"{name}\" is already stopped");
        manager.Stop(current.Name);
        watched.RestartAt = null;
        watched.Restarting = false;
        watched.StopAsked = true;
        watched.StopDeadline = now + watched.Config.StopTimeout;
        return ServiceActionResult.Done;
    });

    public ServiceActionResult Restart(string name) => Act(name, (watched, current, now) =>
    {
        watched.RestartAt = null;
        if (current.State == ServiceState.Stopped)
        {
            manager.Start(current.Name);
            return ServiceActionResult.Done;
        }
        manager.Stop(current.Name);
        watched.StopAsked = true;
        watched.Restarting = true;
        watched.StopDeadline = now + watched.Config.StopTimeout;
        return ServiceActionResult.Done;
    });

    private ServiceActionResult Act(string name, Func<Watched, InstalledService, DateTimeOffset, ServiceActionResult> action)
    {
        lock (_gate)
        {
            if (!_services.TryGetValue(name, out var watched))
                return ServiceActionResult.Refused("not-found", $"The service \"{name}\" is not being watched");
            try
            {
                var current = manager.Find(watched.Config.Name);
                if (current is null)
                    return ServiceActionResult.Refused("not-found", $"There is no service named \"{name}\" on this machine");
                if (IsOwn(current))
                    return ServiceActionResult.Refused("invalid-request", "This is the service of the Worker Control itself");
                var result = action(watched, current, time.GetUtcNow());
                // What was asked shows on the next look, which is brought forward.
                _next = DateTimeOffset.MinValue;
                return result;
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or TimeoutException)
            {
                return ServiceActionResult.Refused("failed", (error.InnerException ?? error).Message);
            }
        }
    }

    private bool IsOwn(InstalledService? service) => service is { ProcessId: > 0 } && service.ProcessId == ownProcessId;

    // Under the gate.
    private void Observe(Watched watched, DateTimeOffset now, List<SupervisorEvent> raised, List<(Watched, ServiceCheck)> checks)
    {
        var name = watched.Config.Name;
        void Raise(EventKind kind, string detail, int? processId = null) =>
            raised.Add(new SupervisorEvent(now, kind, GroupPrefix + name, processId, detail));

        InstalledService? current;
        try
        {
            current = manager.Find(name);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // Not being able to look is not the service having changed.
            watched.CheckDetail ??= error.Message;
            return;
        }

        var before = watched.Last;
        var state = current?.State ?? ServiceState.Missing;
        var wasUp = before is { State: ServiceState.Running or ServiceState.Paused or ServiceState.Stopping or ServiceState.Starting };
        var isDown = state is ServiceState.Stopped or ServiceState.Missing;

        if (!watched.Seen)
        {
            watched.Seen = true;
            if (state == ServiceState.Running)
                watched.UpSince = now;
        }
        else if (wasUp && isDown)
        {
            var ran = watched.UpSince is { } since ? now - since : TimeSpan.Zero;
            watched.UpSince = null;
            if (watched.StopAsked)
            {
                Raise(EventKind.MonitoredStopped, "stopped as asked");
                watched.Failures = 0;
            }
            else if (current is { ExitCode: not 0 })
            {
                watched.Failures = ran >= StableAfter ? 1 : watched.Failures + 1;
                Raise(EventKind.MonitoredCrashed, $"stopped by itself with exit code {current.ExitCode}, after {Describe(ran)}", before!.ProcessId > 0 ? before.ProcessId : null);
                if (watched.Config.AutoRestart)
                {
                    var delay = TimeSpan.FromTicks(Math.Min(LongestRestartDelay.Ticks, FirstRestartDelay.Ticks << Math.Min(watched.Failures - 1, 10)));
                    watched.RestartAt = now + delay;
                    Raise(EventKind.MonitoredRestarting, $"starting it again in {Describe(delay)}");
                }
            }
            else
            {
                // A clean stop: somebody stopped it some other way, or it ended on its own accord.
                Raise(EventKind.MonitoredStopped, state == ServiceState.Missing ? "the service is no longer installed" : "stopped, not through here");
                watched.Failures = 0;
            }
            watched.StopAsked = false;
        }
        else if (state == ServiceState.Running && (before?.State != ServiceState.Running || before.ProcessId != current!.ProcessId))
        {
            watched.UpSince = now;
            watched.RestartAt = null;
            Raise(EventKind.MonitoredStarted, before?.State == ServiceState.Running ? "restarted, not through here" : "running", current!.ProcessId);
        }

        if (state == ServiceState.Running && watched.Failures > 0 && watched.UpSince is { } up && now - up >= StableAfter)
            watched.Failures = 0;

        // What was asked for and is still owed.
        if (watched.Restarting && state == ServiceState.Stopped)
        {
            watched.Restarting = false;
            watched.StopAsked = false;
            TryStart(name, Raise);
        }
        else if (watched.StopAsked && !isDown && now > watched.StopDeadline)
        {
            Raise(EventKind.MonitoredStopTimedOut, $"asked to stop {Describe(watched.Config.StopTimeout)} ago and still {state.ToString().ToLowerInvariant()}" + (watched.Restarting ? "; the restart was given up" : ""));
            watched.StopAsked = false;
            watched.Restarting = false;
        }

        if (watched.RestartAt is { } at && now >= at)
        {
            watched.RestartAt = null;
            if (state == ServiceState.Stopped)
                TryStart(name, Raise);
        }

        watched.Last = current;
        watched.Process = state == ServiceState.Running && current!.ProcessId > 0 ? inspector.Inspect(current.ProcessId) : null;

        if (state != ServiceState.Running)
        {
            (watched.CheckOk, watched.CheckDetail, watched.CheckedAt) = (null, null, null);
        }
        else if (watched.Config.Check is { } check && !watched.Checking && (watched.CheckedAt is null || now - watched.CheckedAt >= CheckInterval))
        {
            watched.Checking = true;
            checks.Add((watched, check));
        }
    }

    private void TryStart(string name, Action<EventKind, string, int?> raise)
    {
        try
        {
            manager.Start(name);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or TimeoutException)
        {
            raise(EventKind.MonitoredActionFailed, "could not be started: " + (error.InnerException ?? error).Message, null);
        }
    }

    private void RunCheck(Watched watched, ServiceCheck check)
    {
        (bool Ok, string Detail) result;
        try
        {
            result = probe.Check(check);
        }
        catch (Exception error)
        {
            result = (false, error.Message);
        }

        SupervisorEvent? raised = null;
        lock (_gate)
        {
            watched.Checking = false;
            // The answer of a check that is no longer the one of the service is of no use.
            if (watched.Config.Check != check || watched.Last?.State != ServiceState.Running)
                return;
            var now = time.GetUtcNow();
            if (watched.CheckOk != false && !result.Ok)
                raised = new SupervisorEvent(now, EventKind.MonitoredCheckFailed, GroupPrefix + watched.Config.Name, null, result.Detail);
            else if (watched.CheckOk == false && result.Ok)
                raised = new SupervisorEvent(now, EventKind.MonitoredCheckRecovered, GroupPrefix + watched.Config.Name, null, result.Detail);
            (watched.CheckOk, watched.CheckDetail, watched.CheckedAt) = (result.Ok, result.Detail, now);
        }
        if (raised is not null)
            Event?.Invoke(raised);
    }

    private static string Describe(TimeSpan span) =>
        span.TotalSeconds < 90 ? $"{span.TotalSeconds:0} s" : span.TotalMinutes < 90 ? $"{span.TotalMinutes:0} min" : $"{span.TotalHours:0.#} h";
}
