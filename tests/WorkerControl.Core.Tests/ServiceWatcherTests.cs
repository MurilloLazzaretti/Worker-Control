using Microsoft.Extensions.Time.Testing;
using WorkerControl.Core;

namespace WorkerControl.Core.Tests;

/// <summary>
/// The Windows services that are watched without being started by the supervisor.
/// </summary>
public class ServiceWatcherTests
{
    private sealed class FakeServices : IServiceManager
    {
        private int _nextId = 5000;

        public Dictionary<string, InstalledService> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Asked { get; } = [];
        public string? Refuse { get; set; }

        /// <summary>A service that takes its time to stop, to see the watcher wait.</summary>
        public HashSet<string> SlowToStop { get; } = [];

        public void Install(string name, ServiceState state = ServiceState.Running) =>
            Installed[name] = new InstalledService(name, name + " (display)", $@"C:\apps\{name}\{name}.exe", state, "Automatic", state == ServiceState.Running ? _nextId++ : 0, 0);

        public void Crash(string name, int exitCode = 1067) =>
            Installed[name] = Installed[name] with { State = ServiceState.Stopped, ProcessId = 0, ExitCode = exitCode };

        public void StopOutside(string name) =>
            Installed[name] = Installed[name] with { State = ServiceState.Stopped, ProcessId = 0, ExitCode = 0 };

        public void FinishStopping(string name) => StopOutside(name);

        public IReadOnlyList<InstalledService> List() => [.. Installed.Values];

        public InstalledService? Find(string name) => Installed.GetValueOrDefault(name);

        public void Start(string name)
        {
            Asked.Add("start " + name);
            if (Refuse is not null)
                throw new InvalidOperationException(Refuse);
            Installed[name] = Installed[name] with { State = ServiceState.Running, ProcessId = _nextId++, ExitCode = 0 };
        }

        public void Stop(string name)
        {
            Asked.Add("stop " + name);
            if (Refuse is not null)
                throw new InvalidOperationException(Refuse);
            Installed[name] = SlowToStop.Contains(name)
                ? Installed[name] with { State = ServiceState.Stopping }
                : Installed[name] with { State = ServiceState.Stopped, ProcessId = 0, ExitCode = 0 };
        }
    }

    private sealed class FakeInspector(FakeTimeProvider time) : IProcessInspector
    {
        public ProcessInfo? Inspect(int processId) => new(time.GetUtcNow(), TimeSpan.FromSeconds(3), 80 * 1024 * 1024, 12, 340);
    }

    private sealed class FakeProbe : IServiceProbe
    {
        public bool Ok { get; set; } = true;
        public int Runs { get; private set; }

        public (bool Ok, string Detail) Check(ServiceCheck check)
        {
            Runs++;
            return Ok ? (true, "answered") : (false, "connection refused");
        }
    }

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-05T10:00:00Z"));
    private readonly FakeServices _services = new();
    private readonly FakeProbe _probe = new();
    private readonly List<SupervisorEvent> _events = [];
    private readonly ServiceWatcher _watcher;

    public ServiceWatcherTests()
    {
        _watcher = new ServiceWatcher(_services, new FakeInspector(_time), _probe, _time, ownProcessId: 1) { ChecksInline = true };
        _watcher.Event += _events.Add;
    }

    private void Watch(params MonitoredServiceConfig[] items) => _watcher.ApplyConfig(new ServicesConfig { Items = items });

    private void Pass(TimeSpan span)
    {
        // Ticked the way the service does it, a few times per second.
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += TimeSpan.FromMilliseconds(500))
        {
            _time.Advance(TimeSpan.FromMilliseconds(500));
            _watcher.Tick();
        }
    }

    private IEnumerable<string> Kinds => _events.Select(e => e.Kind.ToString());

    private MonitoredServiceStatus Status(string name) => _watcher.GetStatus().Single(status => status.Config.Name == name);

    [Fact]
    public void A_running_service_is_shown_with_its_process_and_raises_nothing()
    {
        _services.Install("Orders");
        Watch(new MonitoredServiceConfig { Name = "Orders" });

        _watcher.Tick();

        var status = Status("Orders");
        Assert.Equal(ServiceState.Running, status.State);
        Assert.Equal(12, status.Process!.Threads);
        Assert.Equal("Orders (display)", status.Service!.DisplayName);
        Assert.False(status.IsSupervisor);
        Assert.Empty(_events);
    }

    [Fact]
    public void A_service_that_is_not_installed_is_shown_as_missing()
    {
        Watch(new MonitoredServiceConfig { Name = "Ghost" });

        _watcher.Tick();

        Assert.Equal(ServiceState.Missing, Status("Ghost").State);
        Assert.Equal("not-found", _watcher.Start("Ghost").Code);
        Assert.Empty(_events);
    }

    [Fact]
    public void Stopping_with_an_error_is_a_crash_and_is_left_alone_by_default()
    {
        _services.Install("Orders");
        Watch(new MonitoredServiceConfig { Name = "Orders" });
        _watcher.Tick();

        _services.Crash("Orders", exitCode: 1067);
        Pass(TimeSpan.FromMinutes(10));

        var crash = Assert.Single(_events);
        Assert.Equal(EventKind.MonitoredCrashed, crash.Kind);
        Assert.Equal("service:Orders", crash.Group);
        Assert.Contains("exit code 1067", crash.Detail);
        Assert.Empty(_services.Asked);
        Assert.Equal(ServiceState.Stopped, Status("Orders").State);
    }

    [Fact]
    public void A_service_set_up_for_it_is_started_again_after_a_crash()
    {
        _services.Install("Orders");
        Watch(new MonitoredServiceConfig { Name = "Orders", AutoRestart = true });
        _watcher.Tick();
        Pass(TimeSpan.FromMinutes(2));

        _services.Crash("Orders");
        Pass(TimeSpan.FromSeconds(3));
        Assert.Empty(_services.Asked);
        Assert.NotNull(Status("Orders").RestartingAt);

        Pass(TimeSpan.FromSeconds(10));

        Assert.Equal(["start Orders"], _services.Asked);
        Assert.Equal(["MonitoredCrashed", "MonitoredRestarting", "MonitoredStarted"], Kinds);
        Assert.Equal(ServiceState.Running, Status("Orders").State);
    }

    [Fact]
    public void Crashing_in_a_row_makes_the_wait_longer_each_time_and_marks_it_unstable()
    {
        _services.Install("Orders");
        Watch(new MonitoredServiceConfig { Name = "Orders", AutoRestart = true });
        _watcher.Tick();

        var waits = new List<string>();
        for (var round = 0; round < 3; round++)
        {
            _services.Crash("Orders");
            Pass(TimeSpan.FromSeconds(2));
            waits.Add(_events.Last(e => e.Kind == EventKind.MonitoredRestarting).Detail);
            Pass(TimeSpan.FromSeconds(40));
            Assert.Equal(ServiceState.Running, Status("Orders").State);
        }

        Assert.Equal(["starting it again in 5 s", "starting it again in 10 s", "starting it again in 20 s"], waits);
        Assert.True(Status("Orders").Unstable);

        // Staying up for a while clears it.
        Pass(TimeSpan.FromMinutes(2));
        Assert.False(Status("Orders").Unstable);
    }

    [Fact]
    public void A_clean_stop_from_outside_is_never_undone()
    {
        _services.Install("Orders");
        Watch(new MonitoredServiceConfig { Name = "Orders", AutoRestart = true });
        _watcher.Tick();

        _services.StopOutside("Orders");
        Pass(TimeSpan.FromMinutes(10));

        var stop = Assert.Single(_events);
        Assert.Equal(EventKind.MonitoredStopped, stop.Kind);
        Assert.Equal("stopped, not through here", stop.Detail);
        Assert.Empty(_services.Asked);
    }

    [Fact]
    public void Asked_to_stop_it_stops_and_stays_stopped()
    {
        _services.Install("Orders");
        Watch(new MonitoredServiceConfig { Name = "Orders", AutoRestart = true });
        _watcher.Tick();

        Assert.True(_watcher.Stop("Orders").Ok);
        Pass(TimeSpan.FromMinutes(10));

        Assert.Equal(["stop Orders"], _services.Asked);
        Assert.Equal("stopped as asked", Assert.Single(_events).Detail);
        Assert.Equal("invalid-state", _watcher.Stop("Orders").Code);
    }

    [Fact]
    public void A_restart_waits_for_the_stop_and_then_starts()
    {
        _services.Install("Orders");
        _services.SlowToStop.Add("Orders");
        Watch(new MonitoredServiceConfig { Name = "Orders" });
        _watcher.Tick();
        var before = Status("Orders").Service!.ProcessId;

        Assert.True(_watcher.Restart("Orders").Ok);
        Pass(TimeSpan.FromSeconds(6));
        Assert.Equal(["stop Orders"], _services.Asked);
        Assert.True(Status("Orders").Restarting);

        _services.FinishStopping("Orders");
        Pass(TimeSpan.FromSeconds(6));

        Assert.Equal(["stop Orders", "start Orders"], _services.Asked);
        Assert.Equal(["MonitoredStopped", "MonitoredStarted"], Kinds);
        Assert.NotEqual(before, Status("Orders").Service!.ProcessId);
        Assert.False(Status("Orders").Restarting);
    }

    [Fact]
    public void A_service_that_does_not_stop_in_time_is_said_so_and_not_forced()
    {
        _services.Install("Orders");
        _services.SlowToStop.Add("Orders");
        Watch(new MonitoredServiceConfig { Name = "Orders", StopTimeout = TimeSpan.FromSeconds(10) });
        _watcher.Tick();

        _watcher.Restart("Orders");
        Pass(TimeSpan.FromSeconds(20));

        var late = Assert.Single(_events);
        Assert.Equal(EventKind.MonitoredStopTimedOut, late.Kind);
        Assert.Contains("the restart was given up", late.Detail);
        Assert.Equal(["stop Orders"], _services.Asked);

        // When it finally stops, nobody starts it behind the back of whoever is looking.
        _services.FinishStopping("Orders");
        Pass(TimeSpan.FromSeconds(10));
        Assert.Equal(["stop Orders"], _services.Asked);
    }

    [Fact]
    public void What_the_machine_refuses_is_told_to_whoever_asked()
    {
        _services.Install("Orders");
        Watch(new MonitoredServiceConfig { Name = "Orders" });
        _watcher.Tick();
        _services.Refuse = "Access is denied";

        var result = _watcher.Stop("Orders");

        Assert.False(result.Ok);
        Assert.Equal("failed", result.Code);
        Assert.Equal("Access is denied", result.Message);
        Assert.Equal("not-found", _watcher.Stop("Unknown").Code);
    }

    [Fact]
    public void The_service_of_the_supervisor_itself_cannot_be_acted_on()
    {
        _services.Install("Supervisor");
        _services.Installed["Supervisor"] = _services.Installed["Supervisor"] with { ProcessId = 1 };
        Watch(new MonitoredServiceConfig { Name = "Supervisor" });
        _watcher.Tick();

        Assert.True(Status("Supervisor").IsSupervisor);
        Assert.Equal("invalid-request", _watcher.Restart("Supervisor").Code);
        Assert.Empty(_services.Asked);
    }

    [Fact]
    public void A_check_that_starts_failing_and_then_recovers_is_told_once_each_way()
    {
        _services.Install("Orders");
        Watch(new MonitoredServiceConfig { Name = "Orders", Check = new ServiceCheck("localhost:9100", null) });
        _watcher.Tick();
        Assert.True(Status("Orders").CheckOk);

        _probe.Ok = false;
        Pass(TimeSpan.FromMinutes(1));
        Assert.False(Status("Orders").CheckOk);
        Assert.Equal("connection refused", Status("Orders").CheckDetail);

        _probe.Ok = true;
        Pass(TimeSpan.FromMinutes(1));

        Assert.Equal(["MonitoredCheckFailed", "MonitoredCheckRecovered"], Kinds);
        Assert.InRange(_probe.Runs, 8, 10);
    }

    [Fact]
    public void A_stopped_service_is_not_checked()
    {
        _services.Install("Orders", ServiceState.Stopped);
        Watch(new MonitoredServiceConfig { Name = "Orders", Check = new ServiceCheck(null, "http://localhost:9100/health") });

        Pass(TimeSpan.FromMinutes(1));

        Assert.Equal(0, _probe.Runs);
        Assert.Null(Status("Orders").CheckOk);
    }

    [Fact]
    public void Leaving_the_configuration_ends_the_watch()
    {
        _services.Install("Orders");
        _services.Install("Billing");
        Watch(new MonitoredServiceConfig { Name = "Orders" }, new MonitoredServiceConfig { Name = "Billing" });
        _watcher.Tick();

        Watch(new MonitoredServiceConfig { Name = "Billing" });
        _services.Crash("Orders");
        Pass(TimeSpan.FromSeconds(10));

        Assert.Equal(["Billing"], _watcher.GetStatus().Select(status => status.Config.Name));
        Assert.Empty(_events);
    }
}
