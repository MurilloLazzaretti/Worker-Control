using Newtonsoft.Json.Linq;
using WorkerControl.Core;

namespace WorkerControl.Service;

/// <summary>
/// The administration contract of 2.0: requests with a <c>Command</c>, answered with
/// <c>Ok</c> and either what was asked or an <c>Error</c>.
/// </summary>
internal static class Admin
{
    public const int ContractVersion = 2;

    public static JObject Ok(Action<JObject>? more = null)
    {
        var answer = new JObject { ["Ok"] = true };
        more?.Invoke(answer);
        return answer;
    }

    public static JObject Error(string code, string message) => new()
    {
        ["Ok"] = false,
        ["Error"] = new JObject { ["Code"] = code, ["Message"] = message }
    };

    public static JObject Status(IReadOnlyList<GroupStatus> groups, DateTimeOffset startedAt, string zapMQHost, int zapMQPort, bool brokerHealthy,
        IReadOnlyDictionary<int, StoredHealth> lastHealth, IReadOnlyList<MonitoredServiceStatus> services) => Ok(answer =>
    {
        answer["Version"] = ServiceHost.Version;
        answer["Contract"] = ContractVersion;
        answer["Service"] = new JObject
        {
            ["StartedAt"] = startedAt,
            ["Machine"] = Environment.MachineName,
            ["ProcessId"] = Environment.ProcessId,
            ["ZapMQ"] = new JObject { ["Host"] = zapMQHost, ["Port"] = zapMQPort, ["Healthy"] = brokerHealthy }
        };
        answer["Groups"] = new JArray(groups.Select(group => Describe(group, lastHealth)));
        answer["Services"] = new JArray(services.Select(service => Describe(service, lastHealth)));
    });

    private static JObject Describe(MonitoredServiceStatus status, IReadOnlyDictionary<int, StoredHealth> lastHealth)
    {
        var service = status.Service;
        StoredHealth? health = null;
        if (service is { ProcessId: > 0 })
            lastHealth.TryGetValue(service.ProcessId, out health);
        return new JObject
        {
            ["Name"] = status.Config.Name,
            ["DisplayName"] = service?.DisplayName,
            ["ExecutablePath"] = service?.ExecutablePath,
            ["State"] = status.State.ToString(),
            ["StartType"] = service?.StartType,
            ["ProcessId"] = service is { ProcessId: > 0 } ? service.ProcessId : null,
            ["StartedAt"] = status.Process?.StartTime,
            ["ExitCode"] = status.State == ServiceState.Stopped ? service?.ExitCode : null,
            ["CpuPercent"] = health?.CpuPercent,
            ["MemoryBytes"] = status.Process?.MemoryBytes,
            ["Threads"] = status.Process?.Threads,
            ["Handles"] = status.Process?.Handles,
            ["AutoRestart"] = status.Config.AutoRestart,
            ["RestartingAt"] = status.RestartingAt,
            ["Restarting"] = status.Restarting,
            ["Unstable"] = status.Unstable,
            ["IsSupervisor"] = status.IsSupervisor,
            ["HasLog"] = status.Config.LogFiles is not null,
            ["Check"] = status.Config.Check is { } check
                ? new JObject { ["Target"] = check.Tcp ?? check.Url, ["Ok"] = status.CheckOk, ["Detail"] = status.CheckDetail, ["At"] = status.CheckedAt }
                : null
        };
    }

    /// <summary>
    /// The services of the machine, for whoever is choosing which to watch: the ones from the
    /// suggested folders first, then the rest, and those of Windows itself last.
    /// </summary>
    public static JObject Installed(IReadOnlyList<InstalledService> services, IReadOnlyList<string> suggestFrom, IReadOnlySet<string> watched)
    {
        var system = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows) }.Where(folder => folder.Length > 0).ToList();
        return Ok(answer =>
        {
            answer["SuggestFrom"] = new JArray(suggestFrom);
            answer["Services"] = new JArray(services
                .Select(service => (Service: service,
                    Suggested: ServiceCommandLine.IsUnder(service.ExecutablePath, suggestFrom),
                    System: ServiceCommandLine.IsUnder(service.ExecutablePath, system)))
                .OrderByDescending(item => item.Suggested)
                .ThenBy(item => item.System)
                .ThenBy(item => item.Service.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new JObject
                {
                    ["Name"] = item.Service.Name,
                    ["DisplayName"] = item.Service.DisplayName,
                    ["ExecutablePath"] = item.Service.ExecutablePath,
                    ["State"] = item.Service.State.ToString(),
                    ["StartType"] = item.Service.StartType,
                    ["Suggested"] = item.Suggested,
                    ["System"] = item.System,
                    ["Watched"] = watched.Contains(item.Service.Name)
                }));
        });
    }

    private static JObject Describe(GroupStatus group, IReadOnlyDictionary<int, StoredHealth> lastHealth)
    {
        var config = group.Config;
        return new JObject
        {
            ["Name"] = config.Name,
            ["Enabled"] = config.Enabled,
            ["ApplicationFullPath"] = config.ApplicationFullPath,
            ["TotalWorkers"] = config.TotalWorkers,
            ["DesiredWorkers"] = group.DesiredWorkers,
            ["BoostWorkers"] = group.BoostWorkers,
            ["ScaleWorkers"] = group.ScaleWorkers,
            ["Recycling"] = group.Recycling,
            ["Unstable"] = group.Unstable,
            ["MonitoringRate"] = (long)config.MonitoringRate.TotalMilliseconds,
            ["TimeoutKeepAlive"] = (long)config.TimeoutKeepAlive.TotalMilliseconds,
            ["LastSyncConfig"] = group.LastSyncConfig,
            ["Workers"] = new JArray(group.Workers.Select(worker =>
            {
                lastHealth.TryGetValue(worker.ProcessId, out var health);
                return new JObject
                {
                    ["ProcessId"] = worker.ProcessId,
                    ["State"] = worker.State.ToString(),
                    ["StartedAt"] = worker.StartedAt,
                    ["LastKeepAlive"] = worker.LastKeepAlive,
                    ["KeepAliveMs"] = worker.KeepAliveLatency?.TotalMilliseconds,
                    ["Adopted"] = worker.Adopted,
                    ["BeingReplaced"] = worker.BeingReplaced,
                    ["CpuPercent"] = health?.CpuPercent,
                    ["MemoryBytes"] = health?.MemoryBytes
                };
            }))
        };
    }

    public static JObject Events(IReadOnlyList<StoredEvent> events) => Ok(answer =>
        answer["Events"] = new JArray(events.Select(e => new JObject
        {
            ["Id"] = e.Id,
            ["At"] = e.At,
            ["Kind"] = e.Kind,
            ["Group"] = e.Group,
            ["ProcessId"] = e.ProcessId,
            ["Detail"] = e.Detail
        })));

    public static JObject Health(IReadOnlyList<StoredHealth> samples) => Ok(answer =>
        answer["Samples"] = new JArray(samples.Select(sample => new JObject
        {
            ["At"] = sample.At,
            ["Group"] = sample.Group,
            ["ProcessId"] = sample.ProcessId,
            ["State"] = sample.State,
            ["UptimeSeconds"] = sample.UptimeSeconds,
            ["CpuPercent"] = sample.CpuPercent,
            ["MemoryBytes"] = sample.MemoryBytes,
            ["KeepAliveMs"] = sample.KeepAliveMs
        })));
}
