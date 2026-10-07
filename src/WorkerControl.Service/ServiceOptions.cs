namespace WorkerControl.Service;

/// <summary>
/// Settings of the service itself, from the optional <c>WorkerControl</c> section of
/// <c>appsettings.json</c>. What it supervises is in <c>ConfigWorkers.json</c>.
/// </summary>
public sealed class ServiceOptions
{
    public const string Section = "WorkerControl";

    /// <summary>
    /// Folder of <c>ConfigWorkers.json</c> and <c>state.json</c>. Empty means the folder of the
    /// executable.
    /// </summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>
    /// Folder of the daily log files, relative to the data folder unless it is a full path.
    /// </summary>
    public string LogDirectory { get; set; } = "logs";

    public int LogRetentionDays { get; set; } = 30;

    /// <summary>
    /// Verbose, Debug, Information, Warning or Error.
    /// </summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// How often the supervisor looks at its workers.
    /// </summary>
    public int TickMilliseconds { get; set; } = 250;

    /// <summary>
    /// How often the health of the workers is measured.
    /// </summary>
    public int HealthSampleSeconds { get; set; } = 30;

    public string ResolveDataDirectory() =>
        string.IsNullOrWhiteSpace(DataDirectory) ? AppContext.BaseDirectory : Path.GetFullPath(DataDirectory, AppContext.BaseDirectory);
}
