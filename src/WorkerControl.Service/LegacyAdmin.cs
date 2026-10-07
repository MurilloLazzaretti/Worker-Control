using System.Globalization;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;

namespace WorkerControl.Service;

/// <summary>
/// The answers of the administration queue in the 1.x format, which is what Management Studio
/// reads.
/// </summary>
internal static class LegacyAdmin
{
    public const string Queue = "WorkerControlAdmin";

    public static JObject CurrentWorkers(IReadOnlyList<GroupStatus> groups, CultureInfo culture)
    {
        var list = new JArray();
        foreach (var group in groups)
        {
            var config = group.Config;
            var workers = new JArray();
            // 1.x forgot a worker the moment it asked it to stop; only the ones in service appear.
            foreach (var worker in group.Workers.Where(worker => worker.State is WorkerState.Starting or WorkerState.Up))
            {
                workers.Add(new JObject
                {
                    ["ProcessId"] = worker.ProcessId,
                    ["LastKeepAlive"] = DateAndTime(worker.LastKeepAlive ?? worker.StartedAt, culture)
                });
            }

            list.Add(new JObject
            {
                ["Name"] = config.Name,
                ["Enabled"] = config.Enabled,
                ["ApplicationFullPath"] = config.ApplicationFullPath,
                // With the boost added while its window is open, as 1.x reported it.
                ["TotalWorkers"] = config.Enabled ? group.DesiredWorkers : config.TotalWorkers,
                ["MonitoringRate"] = (long)config.MonitoringRate.TotalMilliseconds,
                ["TimeoutKeepAlive"] = (long)config.TimeoutKeepAlive.TotalMilliseconds,
                ["LastSyncConfig"] = DateAndTime(group.LastSyncConfig, culture),
                ["Boost"] = new JObject
                {
                    ["Enabled"] = config.Boost.Enabled,
                    ["BoostWorkers"] = config.Boost.BoostWorkers,
                    ["StartTime"] = TimeOfDay(config.Boost.StartTime, culture),
                    ["EndTime"] = TimeOfDay(config.Boost.EndTime, culture)
                },
                ["Workers"] = workers
            });
        }
        return new JObject { ["WorkerGroups"] = list };
    }

    public static JObject Reloaded() => new() { ["Message"] = "OK" };

    /// <summary>
    /// Short date and long time of the machine, which is how 1.x wrote them and how Management
    /// Studio reads them back.
    /// </summary>
    private static string DateAndTime(DateTimeOffset instant, CultureInfo culture) =>
        instant.LocalDateTime.ToString("G", culture);

    private static string TimeOfDay(TimeSpan time, CultureInfo culture) =>
        DateTime.Today.Add(time).ToString("T", culture);
}
