namespace WorkerControl.Core;

/// <summary>
/// A worker process, as much of it as the supervisor needs.
/// </summary>
public interface IWorkerProcess
{
    int Id { get; }

    /// <summary>
    /// When the operating system created the process. With the id, it tells one process from
    /// another that later got the same number.
    /// </summary>
    DateTimeOffset StartTime { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    /// <summary>
    /// Ends the process at once, without asking.
    /// </summary>
    void Kill();

    /// <summary>
    /// Processor time used so far and memory in use. Null when it cannot be read.
    /// </summary>
    ProcessUsage? GetUsage();
}

public readonly record struct ProcessUsage(TimeSpan ProcessorTime, long MemoryBytes);

/// <summary>
/// How many messages wait in a queue of the broker.
/// </summary>
public interface IQueueMonitor
{
    /// <summary>
    /// Null when the broker cannot tell (unreachable, or a version that does not count).
    /// </summary>
    int? PendingMessages(string queue);
}

public interface IProcessHost
{
    /// <summary>
    /// Starts one worker of the group. Throws when the process cannot be created.
    /// </summary>
    IWorkerProcess Start(GroupConfig group);

    /// <summary>
    /// Finds a worker started by a previous run of the service. Null when that process is gone
    /// or is not the one recorded.
    /// </summary>
    IWorkerProcess? Attach(WorkerRecord record, GroupConfig group);
}

/// <summary>
/// The messages the supervisor exchanges with the workers through the broker.
/// </summary>
public interface IBroker
{
    /// <summary>
    /// Asks a worker whether it is alive. Exactly one of the callbacks is called later, from
    /// any thread. False when the question could not even be sent.
    /// </summary>
    bool SendKeepAlive(int processId, TimeSpan timeout, Action answered, Action expired);

    /// <summary>
    /// Tells a worker to finish what it is doing and leave. False when it could not be sent.
    /// </summary>
    bool SendSafeStop(int processId);

    /// <summary>
    /// Whether messages have been going to the broker and coming back, without a failure,
    /// since the given instant. An unanswered keep-alive only means a stuck worker when this
    /// holds.
    /// </summary>
    bool HealthySince(DateTimeOffset instant);
}

/// <summary>
/// What is kept on disk about a worker, enough to find it again after the service restarts.
/// </summary>
public sealed record WorkerRecord(string Group, int ProcessId, DateTimeOffset StartTime);

public enum WorkerState
{
    /// <summary>Created; has not answered a keep-alive yet.</summary>
    Starting,

    /// <summary>Has answered at least one keep-alive.</summary>
    Up,

    /// <summary>Was asked to stop and has not left yet.</summary>
    Stopping,

    /// <summary>Was ended by force and the process has not gone yet.</summary>
    Killing
}

public enum EventKind
{
    ConfigApplied,
    WorkerStarted,
    WorkerStartFailed,
    WorkerAdopted,
    WorkerUp,
    WorkerCrashed,
    WorkerHung,
    WorkerStartTimedOut,
    SafeStopRequested,
    SafeStopTimedOut,
    WorkerStopped,
    WorkerKilled,
    GroupUnstable,
    GroupStable,
    GroupRemoved,
    BoostStarted,
    BoostEnded,
    ScaleChanged,
    RecycleStarted,
    RecycleFinished,

    // Raised by the service, not by the supervisor.
    ServiceStarted,
    ServiceStopped,
    ConfigRefused,
    ManualAction
}

public sealed record SupervisorEvent(DateTimeOffset At, EventKind Kind, string? Group, int? ProcessId, string Detail);

public sealed record WorkerStatus(
    int ProcessId,
    WorkerState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastKeepAlive,
    TimeSpan? KeepAliveLatency,
    bool Adopted,
    bool BeingReplaced);

/// <summary>
/// A group as it is now. The number of workers it wants is the base plus what the boost and
/// the queue are asking for.
/// </summary>
public sealed record GroupStatus(
    GroupConfig Config,
    int DesiredWorkers,
    int BoostWorkers,
    int ScaleWorkers,
    bool Recycling,
    bool Unstable,
    DateTimeOffset LastSyncConfig,
    IReadOnlyList<WorkerStatus> Workers)
{
    public bool BoostActive => BoostWorkers > 0;
}

/// <summary>
/// One measurement of one worker.
/// </summary>
public sealed record HealthSample(
    string Group,
    int ProcessId,
    WorkerState State,
    TimeSpan Uptime,
    ProcessUsage? Usage,
    TimeSpan? KeepAliveLatency);
