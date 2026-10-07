using System.Collections.Concurrent;

namespace WorkerControl.Core;

/// <summary>
/// Keeps each group with the number of workers it should have, replaces the ones that go away
/// or stop answering, and stops the ones in excess. It does nothing on its own: whoever hosts
/// it calls <see cref="Tick"/> a few times per second.
/// </summary>
public sealed class Supervisor(IProcessHost host, IBroker broker, TimeProvider time)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan KillPatience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long after a keep-alive goes unanswered the worker is judged. A broker that failed
    /// in the meantime takes a moment to be noticed, and until then the silence cannot be
    /// blamed on the worker.
    /// </summary>
    public static readonly TimeSpan VerdictDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongestBackoff = TimeSpan.FromMinutes(5);

    private sealed class Worker(IWorkerProcess process, DateTimeOffset startedAt)
    {
        public IWorkerProcess Process { get; } = process;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public WorkerState State { get; set; } = WorkerState.Starting;
        public bool Adopted { get; init; }
        public bool Gone { get; set; }

        public bool KeepAlivePending { get; set; }
        public DateTimeOffset KeepAliveSentAt { get; set; }
        public DateTimeOffset NextKeepAliveAt { get; set; }
        public DateTimeOffset? LastAnswerAt { get; set; }

        /// <summary>Set while an unanswered keep-alive waits to be judged.</summary>
        public DateTimeOffset? VerdictAt { get; set; }

        /// <summary>When a worker being stopped is ended by force, or a kill is tried again.</summary>
        public DateTimeOffset Deadline { get; set; }
        public bool StopSent { get; set; }
        public EventKind? KilledFor { get; set; }

        public bool IsActive => State is WorkerState.Starting or WorkerState.Up;
    }

    private sealed class Group(GroupConfig config, DateTimeOffset now)
    {
        public GroupConfig Config { get; set; } = config;
        public List<Worker> Workers { get; } = [];
        public DateTimeOffset LastSync { get; set; } = now;
        public bool Removed { get; set; }
        public bool BoostActive { get; set; }

        public int QuickFailures { get; set; }
        public bool Unstable { get; set; }
        public DateTimeOffset NextStartAt { get; set; }
    }

    private readonly object _gate = new();
    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly List<Group> _groups = [];
    private WorkerControlConfig _config = new();
    private DateTimeOffset _batchStartedAt = DateTimeOffset.MinValue;
    private int _startedInBatch;
    private bool _stopping;
    private long _recordsVersion;

    /// <summary>
    /// Raised for everything worth recording. Handlers run inside the supervisor and must be
    /// quick.
    /// </summary>
    public event Action<SupervisorEvent>? Event;

    /// <summary>
    /// Changes whenever the set of workers under supervision does.
    /// </summary>
    public long RecordsVersion => Interlocked.Read(ref _recordsVersion);

    /// <summary>
    /// Puts a configuration in force. Groups that are no longer in it have their workers
    /// stopped; a new executable path applies to the workers started from now on.
    /// </summary>
    public void ApplyConfig(WorkerControlConfig config)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            _config = config;
            foreach (var group in _groups)
                group.Removed = true;

            foreach (var groupConfig in config.Groups)
            {
                var group = _groups.Find(existing => existing.Config.Name == groupConfig.Name);
                if (group is null)
                {
                    _groups.Add(new Group(groupConfig, now));
                    continue;
                }
                group.Config = groupConfig;
                group.LastSync = now;
                group.Removed = false;
            }
            Raise(EventKind.ConfigApplied, null, null, $"{config.Groups.Count} groups");
        }
    }

    /// <summary>
    /// Takes over the workers a previous run of the service left behind. Called once, after the
    /// first configuration and before the first tick.
    /// </summary>
    public void Adopt(IEnumerable<WorkerRecord> records)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            foreach (var record in records)
            {
                var group = _groups.Find(existing => existing.Config.Name == record.Group);
                var process = group is null ? null : host.Attach(record, group.Config);
                if (group is null || process is null)
                    continue;

                group.Workers.Add(new Worker(process, process.StartTime)
                {
                    State = WorkerState.Up,
                    Adopted = true,
                    NextKeepAliveAt = now
                });
                Raise(EventKind.WorkerAdopted, group, process.Id, "already running");
            }
            Interlocked.Increment(ref _recordsVersion);
        }
    }

    /// <summary>
    /// From now on every group wants no workers. The service keeps ticking until
    /// <see cref="IsIdle"/>.
    /// </summary>
    public void BeginStop()
    {
        lock (_gate)
            _stopping = true;
    }

    public bool IsIdle
    {
        get
        {
            lock (_gate)
                return _groups.All(group => group.Workers.Count == 0);
        }
    }

    /// <summary>
    /// Ends by force whatever is still running.
    /// </summary>
    public void KillAll()
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            foreach (var group in _groups)
                foreach (var worker in group.Workers.Where(worker => worker.State != WorkerState.Killing).ToList())
                    Kill(group, worker, EventKind.WorkerKilled, "the service is stopping", now);
        }
    }

    public IReadOnlyList<GroupStatus> GetStatus()
    {
        lock (_gate)
        {
            var timeOfDay = time.GetLocalNow().TimeOfDay;
            return _groups
                .Where(group => !group.Removed)
                .Select(group => new GroupStatus(
                    group.Config,
                    Desired(group, timeOfDay),
                    group.BoostActive,
                    group.Unstable,
                    group.LastSync,
                    group.Workers
                        .Select(worker => new WorkerStatus(worker.Process.Id, worker.State, worker.StartedAt, worker.LastAnswerAt, worker.Adopted))
                        .ToList()))
                .ToList();
        }
    }

    /// <summary>
    /// The workers to look for if the service restarts.
    /// </summary>
    public IReadOnlyList<WorkerRecord> GetRecords()
    {
        lock (_gate)
        {
            return _groups
                .SelectMany(group => group.Workers
                    .Where(worker => worker.IsActive)
                    .Select(worker => new WorkerRecord(group.Config.Name, worker.Process.Id, worker.Process.StartTime)))
                .ToList();
        }
    }

    public void Tick()
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            while (_inbox.TryDequeue(out var arrived))
                arrived();

            foreach (var group in _groups)
                foreach (var worker in group.Workers.ToList())
                    Inspect(group, worker, now);

            var timeOfDay = time.GetLocalNow().TimeOfDay;
            foreach (var group in _groups)
                Balance(group, now, timeOfDay);

            foreach (var group in _groups.Where(group => group.Removed && group.Workers.Count == 0).ToList())
            {
                _groups.Remove(group);
                Raise(EventKind.GroupRemoved, group, null, "no longer in the configuration");
            }
        }
    }

    private int Desired(Group group, TimeSpan timeOfDay) =>
        _stopping || group.Removed ? 0 : group.Config.DesiredWorkers(timeOfDay);

    private void Inspect(Group group, Worker worker, DateTimeOffset now)
    {
        if (worker.Process.HasExited)
        {
            Remove(group, worker, now);
            return;
        }

        var config = group.Config;
        switch (worker.State)
        {
            case WorkerState.Stopping:
                if (!worker.StopSent)
                    worker.StopSent = broker.SendSafeStop(worker.Process.Id);
                if (now >= worker.Deadline)
                {
                    Raise(EventKind.SafeStopTimedOut, group, worker.Process.Id, $"still running {config.SafeStopTimeout.TotalSeconds:0} s after the safe stop");
                    Kill(group, worker, EventKind.SafeStopTimedOut, "did not leave after the safe stop", now);
                }
                return;

            case WorkerState.Killing:
                if (now >= worker.Deadline)
                    Kill(group, worker, worker.KilledFor ?? EventKind.WorkerKilled, "still running after being ended", now);
                return;

            case WorkerState.Starting:
                // Without the broker nobody can answer; that is not the worker's fault.
                if (now - worker.StartedAt > config.StartupGrace && broker.HealthySince(now - config.StartupGrace))
                {
                    Raise(EventKind.WorkerStartTimedOut, group, worker.Process.Id, $"no keep-alive answered in {config.StartupGrace.TotalSeconds:0} s");
                    Kill(group, worker, EventKind.WorkerStartTimedOut, "never answered a keep-alive", now);
                    return;
                }
                break;
        }

        if (worker.VerdictAt is { } verdictAt)
        {
            if (now >= verdictAt)
                Judge(group, worker, now);
            return;
        }

        if (!worker.KeepAlivePending && now >= worker.NextKeepAliveAt)
            SendKeepAlive(group, worker, now);
    }

    /// <summary>
    /// A keep-alive went unanswered some moments ago. If the broker was fine all along, the
    /// worker is stuck.
    /// </summary>
    private void Judge(Group group, Worker worker, DateTimeOffset now)
    {
        worker.VerdictAt = null;

        // The question may never have reached the worker, or the answer may have been lost on
        // the way back. A worker is not ended for what the broker did.
        if (!broker.HealthySince(worker.KeepAliveSentAt))
        {
            worker.NextKeepAliveAt = now;
            return;
        }

        Raise(EventKind.WorkerHung, group, worker.Process.Id, $"no answer to the keep-alive in {group.Config.TimeoutKeepAlive.TotalSeconds:0} s");
        Kill(group, worker, EventKind.WorkerHung, "stopped answering", now);
    }

    private void SendKeepAlive(Group group, Worker worker, DateTimeOffset now)
    {
        worker.KeepAliveSentAt = now;
        var sent = broker.SendKeepAlive(
            worker.Process.Id,
            group.Config.TimeoutKeepAlive,
            () => _inbox.Enqueue(() => KeepAliveAnswered(group, worker, now)),
            () => _inbox.Enqueue(() => KeepAliveExpired(group, worker, now)));

        if (sent)
            worker.KeepAlivePending = true;
        else
            worker.NextKeepAliveAt = now + RetryDelay;
    }

    private void KeepAliveAnswered(Group group, Worker worker, DateTimeOffset sentAt)
    {
        if (worker.Gone || worker.KeepAliveSentAt != sentAt)
            return;

        var now = time.GetUtcNow();
        worker.KeepAlivePending = false;
        worker.LastAnswerAt = now;
        worker.NextKeepAliveAt = sentAt + group.Config.MonitoringRate;
        if (worker.State == WorkerState.Starting)
        {
            worker.State = WorkerState.Up;
            Raise(EventKind.WorkerUp, group, worker.Process.Id, $"answered after {(now - worker.StartedAt).TotalSeconds:0.0} s");
        }
    }

    private void KeepAliveExpired(Group group, Worker worker, DateTimeOffset sentAt)
    {
        if (worker.Gone || worker.KeepAliveSentAt != sentAt)
            return;

        var now = time.GetUtcNow();
        worker.KeepAlivePending = false;
        if (!worker.IsActive)
            return;

        if (worker.State == WorkerState.Starting)
        {
            // Still within its grace; whether that is over is checked on every tick.
            worker.NextKeepAliveAt = now;
            return;
        }

        worker.VerdictAt = now + VerdictDelay;
    }

    private void Kill(Group group, Worker worker, EventKind why, string detail, DateTimeOffset now)
    {
        var wasActive = worker.IsActive;
        worker.State = WorkerState.Killing;
        worker.KilledFor ??= why;
        worker.Deadline = now + KillPatience;
        try
        {
            worker.Process.Kill();
        }
        catch (Exception error)
        {
            Raise(EventKind.WorkerKilled, group, worker.Process.Id, $"could not be ended ({detail}): {error.Message}");
        }
        if (wasActive)
            Interlocked.Increment(ref _recordsVersion);
    }

    private void Remove(Group group, Worker worker, DateTimeOffset now)
    {
        group.Workers.Remove(worker);
        worker.Gone = true;
        Interlocked.Increment(ref _recordsVersion);

        var lifetime = now - worker.StartedAt;
        var lived = $"after {Describe(lifetime)}";
        switch (worker.State)
        {
            case WorkerState.Stopping:
                Raise(EventKind.WorkerStopped, group, worker.Process.Id, $"left {lived}");
                return;

            case WorkerState.Killing:
                Raise(EventKind.WorkerKilled, group, worker.Process.Id, $"ended by force {lived}");
                if (worker.KilledFor is EventKind.WorkerHung or EventKind.WorkerStartTimedOut)
                    CountFailure(group, worker, lifetime, now);
                return;

            default:
                Raise(EventKind.WorkerCrashed, group, worker.Process.Id, $"exit code {worker.Process.ExitCode?.ToString() ?? "unknown"}, {lived}");
                CountFailure(group, worker, lifetime, now);
                return;
        }
    }

    /// <summary>
    /// A worker that never got to answer, or that went away soon after starting, is a sign that
    /// starting another right away will only repeat the failure.
    /// </summary>
    private void CountFailure(Group group, Worker worker, TimeSpan lifetime, DateTimeOffset now)
    {
        if (worker.LastAnswerAt is not null && lifetime >= group.Config.CrashWindow)
            return;
        QuickFailure(group, now);
    }

    private void QuickFailure(Group group, DateTimeOffset now)
    {
        group.QuickFailures++;
        var beyond = group.QuickFailures - group.Config.CrashLimit;
        if (beyond < 0)
            return;

        var wait = TimeSpan.FromTicks(Math.Min(LongestBackoff.Ticks, FirstBackoff.Ticks << Math.Min(beyond, 10)));
        group.NextStartAt = now + wait;
        if (!group.Unstable)
        {
            group.Unstable = true;
            Raise(EventKind.GroupUnstable, group, null, $"{group.QuickFailures} quick failures in a row; waiting {wait.TotalSeconds:0} s before the next start");
        }
    }

    private void Balance(Group group, DateTimeOffset now, TimeSpan timeOfDay)
    {
        var config = group.Config;

        var boost = !_stopping && !group.Removed && config.Enabled && config.Boost.IsActive(timeOfDay);
        if (boost != group.BoostActive)
        {
            group.BoostActive = boost;
            Raise(boost ? EventKind.BoostStarted : EventKind.BoostEnded, group, null, $"{config.Boost.BoostWorkers} extra workers");
        }

        if (group.QuickFailures > 0 && group.Workers.Any(worker => worker.State == WorkerState.Up && now - worker.StartedAt >= config.CrashWindow))
        {
            group.QuickFailures = 0;
            if (group.Unstable)
            {
                group.Unstable = false;
                Raise(EventKind.GroupStable, group, null, "a worker stayed up");
            }
        }

        var desired = Desired(group, timeOfDay);
        var active = group.Workers.Where(worker => worker.IsActive).OrderBy(worker => worker.StartedAt).ToList();

        for (var missing = desired - active.Count; missing > 0; missing--)
        {
            if (now < group.NextStartAt || !TakeStartSlot(now))
                break;
            Start(group, now);
        }

        // The oldest leave first.
        foreach (var worker in active.Take(Math.Max(0, active.Count - desired)))
        {
            worker.State = WorkerState.Stopping;
            worker.Deadline = now + config.SafeStopTimeout;
            worker.StopSent = broker.SendSafeStop(worker.Process.Id);
            Interlocked.Increment(ref _recordsVersion);
            Raise(EventKind.SafeStopRequested, group, worker.Process.Id,
                worker.StopSent ? "asked to stop" : "asked to stop; the message could not be sent yet");
        }
    }

    /// <summary>
    /// Workers are started a few at a time, all groups together, so that a machine starting
    /// everything at once is not too busy for any of them to get ready.
    /// </summary>
    private bool TakeStartSlot(DateTimeOffset now)
    {
        if (now - _batchStartedAt >= _config.StartBatchInterval)
        {
            _batchStartedAt = now;
            _startedInBatch = 0;
        }
        if (_startedInBatch >= _config.StartBatchSize)
            return false;

        _startedInBatch++;
        return true;
    }

    private void Start(Group group, DateTimeOffset now)
    {
        IWorkerProcess process;
        try
        {
            process = host.Start(group.Config);
        }
        catch (Exception error)
        {
            Raise(EventKind.WorkerStartFailed, group, null, error.Message);
            QuickFailure(group, now);
            // Below the limit of failures there is no wait yet; this keeps the attempts from
            // coming one right after the other.
            if (group.NextStartAt < now + RetryDelay)
                group.NextStartAt = now + RetryDelay;
            return;
        }

        group.Workers.Add(new Worker(process, now) { NextKeepAliveAt = now });
        Interlocked.Increment(ref _recordsVersion);
        Raise(EventKind.WorkerStarted, group, process.Id, group.Config.ApplicationFullPath);
    }

    private void Raise(EventKind kind, Group? group, int? processId, string detail) =>
        Event?.Invoke(new SupervisorEvent(time.GetUtcNow(), kind, group?.Config.Name, processId, detail));

    private static string Describe(TimeSpan lifetime) =>
        lifetime.TotalMinutes < 2 ? $"{lifetime.TotalSeconds:0} s"
        : lifetime.TotalHours < 2 ? $"{lifetime.TotalMinutes:0} min"
        : $"{lifetime.TotalHours:0} h";
}
