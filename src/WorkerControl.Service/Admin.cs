using Newtonsoft.Json.Linq;
using WorkerControl.Core;

namespace WorkerControl.Service;

/// <summary>
/// The administration contract of 2.0: requests with a <c>Command</c>, answered with
/// <c>Ok</c> and either what was asked or an <c>Error</c>.
/// </summary>
internal static class Admin
{
    public const int ContractVersion = 1;

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
        IReadOnlyDictionary<int, StoredHealth> lastHealth) => Ok(answer =>
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
    });

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
