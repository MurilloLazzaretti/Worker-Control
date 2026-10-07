using Microsoft.Extensions.Time.Testing;
using WorkerControl.Core;

namespace WorkerControl.Core.Tests;

internal sealed class FakeProcess(int id, DateTimeOffset startTime) : IWorkerProcess
{
    public int Id { get; } = id;
    public DateTimeOffset StartTime { get; } = startTime;
    public bool HasExited { get; private set; }
    public int? ExitCode { get; private set; }
    public int Kills { get; private set; }

    /// <summary>A process that ignores being ended, to see the supervisor insist.</summary>
    public bool Unkillable { get; set; }

    public void Kill()
    {
        Kills++;
        if (!Unkillable)
            Exit(-1);
    }

    public void Exit(int code)
    {
        HasExited = true;
        ExitCode = code;
    }
}

internal sealed class FakeHost(FakeTimeProvider time) : IProcessHost
{
    private int _nextId = 1000;

    public List<FakeProcess> Started { get; } = [];
    public Dictionary<int, FakeProcess> Existing { get; } = [];
    public string? FailWith { get; set; }

    public IWorkerProcess Start(GroupConfig group)
    {
        if (FailWith is not null)
            throw new InvalidOperationException(FailWith);
        var process = new FakeProcess(_nextId++, time.GetUtcNow());
        Started.Add(process);
        return process;
    }

    public IWorkerProcess? Attach(WorkerRecord record, GroupConfig group) =>
        Existing.TryGetValue(record.ProcessId, out var process) && process.StartTime == record.StartTime && !process.HasExited
            ? process
            : null;

    public IEnumerable<FakeProcess> Running => Started.Where(process => !process.HasExited);
}

internal sealed class FakeBroker(FakeTimeProvider time) : IBroker
{
    public sealed record KeepAlive(int ProcessId, TimeSpan Timeout, Action Answered, Action Expired);

    private DateTimeOffset _lastFailure = DateTimeOffset.MinValue;

    public List<KeepAlive> Pending { get; } = [];
    public List<int> SafeStops { get; } = [];
    public bool Down { get; private set; }

    /// <summary>Workers that answer by themselves as soon as they are asked.</summary>
    public Func<int, bool> AutoAnswer { get; set; } = _ => false;

    public bool SendKeepAlive(int processId, TimeSpan timeout, Action answered, Action expired)
    {
        if (Down)
            return false;
        if (AutoAnswer(processId))
            answered();
        else
            Pending.Add(new KeepAlive(processId, timeout, answered, expired));
        return true;
    }

    public bool SendSafeStop(int processId)
    {
        if (Down)
            return false;
        SafeStops.Add(processId);
        return true;
    }

    public bool HealthySince(DateTimeOffset instant) => !Down && _lastFailure < instant;

    public void GoDown()
    {
        Down = true;
        _lastFailure = time.GetUtcNow();
    }

    public void ComeBack()
    {
        Down = false;
        _lastFailure = time.GetUtcNow();
    }

    public void Answer(int processId)
    {
        foreach (var keepAlive in Take(processId))
            keepAlive.Answered();
    }

    public void Expire(int processId)
    {
        foreach (var keepAlive in Take(processId))
            keepAlive.Expired();
    }

    public void AnswerAll()
    {
        foreach (var keepAlive in Pending.ToList())
        {
            Pending.Remove(keepAlive);
            keepAlive.Answered();
        }
    }

    private List<KeepAlive> Take(int processId)
    {
        var taken = Pending.Where(keepAlive => keepAlive.ProcessId == processId).ToList();
        Pending.RemoveAll(keepAlive => keepAlive.ProcessId == processId);
        return taken;
    }
}

/// <summary>
/// A supervisor with everything around it simulated, and the clock in the test's hands.
/// </summary>
internal sealed class Bench
{
    public FakeTimeProvider Time { get; }
    public FakeHost Host { get; }
    public FakeBroker Broker { get; }
    public Supervisor Supervisor { get; }
    public List<SupervisorEvent> Events { get; } = [];

    public Bench()
    {
        // A fixed morning, local time equal to UTC, so boost windows are easy to reason about.
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero));
        Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        Host = new FakeHost(Time);
        Broker = new FakeBroker(Time);
        Supervisor = new Supervisor(Host, Broker, Time);
        Supervisor.Event += Events.Add;
    }

    public static GroupConfig Group(string name = "Orders", int workers = 2, bool enabled = true) => new()
    {
        Name = name,
        Enabled = enabled,
        ApplicationFullPath = $"C:\\apps\\{name}.exe",
        TotalWorkers = workers
    };

    public void Apply(params GroupConfig[] groups) => Apply(new WorkerControlConfig { Groups = groups });

    public void Apply(WorkerControlConfig config) => Supervisor.ApplyConfig(config);

    public void Tick() => Supervisor.Tick();

    /// <summary>Lets time pass, ticking four times per second as the service does.</summary>
    public void Advance(TimeSpan span)
    {
        var step = TimeSpan.FromMilliseconds(250);
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += step)
        {
            Time.Advance(step);
            Tick();
        }
    }

    public void Advance(double seconds) => Advance(TimeSpan.FromSeconds(seconds));

    public GroupStatus Status(string name = "Orders") => Supervisor.GetStatus().Single(group => group.Config.Name == name);

    public IEnumerable<SupervisorEvent> Of(EventKind kind) => Events.Where(e => e.Kind == kind);
}
