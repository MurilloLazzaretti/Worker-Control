namespace WorkerControl.Service.Database;

public sealed record InstanceInfo(string Server, string Product, string Version, string Level, string Edition);

public sealed record InstanceMachine(long UptimeSeconds, int Processors, long MemoryKb);

/// <summary>
/// How much of the machine the instance is using. The processor is in percent of all of it.
/// </summary>
public sealed record InstanceResources
{
    public int? CpuPercent { get; init; }
    public int? OtherCpuPercent { get; init; }
    public long? MemoryKb { get; init; }
    public long? TargetMemoryKb { get; init; }
    public long? PageLifeSeconds { get; init; }
    public long? Connections { get; init; }

    /// <summary>
    /// Counted since the instance started; the rate comes from two looks.
    /// </summary>
    public long? Batches { get; init; }
}

public sealed record DatabaseInfo(string Name, string State, string Recovery, string Access, bool ReadOnly, int Compatibility, bool System, long DataKb, long LogKb)
{
    public double? LogUsedPercent { get; init; }
}

public sealed record SessionGroup(string Program, string Host, string Login, string Database, int Sessions, int Running, int InTransaction);

/// <summary>
/// Something an application is doing on the instance right now, or a session that keeps another waiting.
/// </summary>
public sealed record Activity(int SessionId, string Status, string Command, long ElapsedMs, long CpuMs, long Reads, string WaitType, long WaitMs,
    int BlockedBy, string Database, string Program, string Host, string Login, int OpenTransactions, bool Running, string Text);

public sealed record Volume(string Mount, string Label, long TotalBytes, long FreeBytes);

/// <summary>
/// The last backups of a database, each as how many minutes ago it finished.
/// </summary>
public sealed record Backups(string Database, int? FullMinutes, int? DifferentialMinutes, int? LogMinutes);

public sealed record Job(string Name, bool Enabled, int? Outcome, int? LastRunMinutes, int? DurationSeconds, string Message);

public sealed record ExpensiveQuery(string Database, string Object, long Executions, double CpuMs, double ElapsedMs, long Reads, double MaxElapsedMs,
    int LastMinutes, bool TopCpu, bool TopTime, bool TopReads, string Text);

/// <summary>
/// What is looked at every few seconds. A part the user of the connection may not read is
/// left null and named in <see cref="Problems"/>.
/// </summary>
public sealed record FastSample
{
    public InstanceInfo? Instance { get; init; }
    public InstanceMachine? Machine { get; init; }
    public InstanceResources? Resources { get; init; }
    public IReadOnlyList<DatabaseInfo>? Databases { get; init; }
    public IReadOnlyList<SessionGroup>? Sessions { get; init; }
    public IReadOnlyList<Activity>? Activity { get; init; }
    public IReadOnlyDictionary<string, string> Problems { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// What changes slowly and is looked at every few minutes.
/// </summary>
public sealed record SlowSample
{
    public IReadOnlyList<Volume>? Volumes { get; init; }
    public IReadOnlyList<Backups>? Backups { get; init; }
    public IReadOnlyList<Job>? Jobs { get; init; }
    public IReadOnlyDictionary<string, string> Problems { get; init; } = new Dictionary<string, string>();
}

public sealed record DatabaseConnection(string Server, string? User, string? Password, bool Encrypt, bool TrustServerCertificate);

/// <summary>
/// Where the health of an instance comes from. A failure to connect is thrown; a part that
/// cannot be read is reported in the sample.
/// </summary>
public interface IDatabaseSource
{
    Task<FastSample> FastAsync(DatabaseConnection connection, CancellationToken stopping);

    Task<SlowSample> SlowAsync(DatabaseConnection connection, CancellationToken stopping);

    Task<IReadOnlyList<ExpensiveQuery>> ExpensiveAsync(DatabaseConnection connection, CancellationToken stopping);
}
