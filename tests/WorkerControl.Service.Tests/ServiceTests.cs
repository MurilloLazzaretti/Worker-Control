using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace WorkerControl.Service.Tests;

/// <summary>
/// The service with real processes and a real ZapMQ server.
/// </summary>
[Collection("processes")]
public class ServiceTests
{
    [Fact]
    public async Task Starts_the_workers_and_reports_them_the_way_1x_did()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 2), rig.Group("Idle", 3, enabled: false));
        await rig.StartServiceAsync();

        var ids = await rig.WaitForWorkersAsync("Orders", 2);
        Assert.Equal(2, ids.Distinct().Count());

        var status = (await rig.CurrentWorkersAsync())!;
        var orders = status["WorkerGroups"]!.Single(group => (string?)group["Name"] == "Orders");
        Assert.True((bool)orders["Enabled"]!);
        Assert.Equal(2, (int)orders["TotalWorkers"]!);
        Assert.Equal(1000, (int)orders["MonitoringRate"]!);
        Assert.Equal(2000, (int)orders["TimeoutKeepAlive"]!);
        Assert.EndsWith("TestWorker" + (OperatingSystem.IsWindows() ? ".exe" : ""), (string?)orders["ApplicationFullPath"]);
        Assert.True(DateTime.TryParse((string?)orders["LastSyncConfig"], out _));
        Assert.Equal(JTokenType.Boolean, orders["Boost"]!["Enabled"]!.Type);
        Assert.True(DateTime.TryParse((string?)orders["Boost"]!["StartTime"], out _));
        Assert.All(orders["Workers"]!, worker => Assert.True(DateTime.TryParse((string?)worker["LastKeepAlive"], out _)));

        var idle = status["WorkerGroups"]!.Single(group => (string?)group["Name"] == "Idle");
        Assert.False((bool)idle["Enabled"]!);
        Assert.Empty(idle["Workers"]!);

        // The workers answer their keep-alives: the instant reported moves on.
        var first = (string?)orders["Workers"]![0]!["LastKeepAlive"];
        Assert.True(await Rig.Eventually(async () =>
        {
            var later = (await rig.CurrentWorkersAsync())?["WorkerGroups"]!.Single(group => (string?)group["Name"] == "Orders")["Workers"]!
                .FirstOrDefault(worker => (int)worker["ProcessId"]! == ids[0]);
            return later is not null && (string?)later["LastKeepAlive"] != first;
        }));
    }

    [Fact]
    public async Task A_worker_that_dies_is_replaced_without_waiting_for_a_keep_alive()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 2));
        await rig.StartServiceAsync();
        var before = await rig.WaitForWorkersAsync("Orders", 2);

        var clock = Stopwatch.StartNew();
        Rig.Kill(before[0]);

        List<int> after = [];
        Assert.True(await Rig.Eventually(async () =>
        {
            after = await rig.WorkersAsync("Orders");
            return after.Count == 2 && !after.Contains(before[0]) && after.All(Rig.IsRunning);
        }));
        Assert.Contains(before[1], after);
        Assert.InRange(clock.ElapsedMilliseconds, 0, 3000);
        Assert.Contains("WorkerCrashed [Orders] pid " + before[0], rig.LogText());
    }

    [Fact]
    public async Task A_worker_that_stops_answering_is_ended_and_replaced()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Stuck", 1, mode: "hang"));
        await rig.StartServiceAsync();
        var first = (await rig.WaitForWorkersAsync("Stuck", 1)).Single();

        // It answers once and then never again: timeout, a moment for the verdict, then gone.
        Assert.True(await Rig.Eventually(() => !Rig.IsRunning(first), 30000));
        Assert.True(await Rig.Eventually(async () => (await rig.WorkersAsync("Stuck")) is [var replacement] && replacement != first));
        Assert.Contains("WorkerHung [Stuck] pid " + first, rig.LogText());
    }

    [Fact]
    public async Task Changing_the_file_takes_effect_at_once_and_the_workers_in_excess_leave_in_order()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 3));
        await rig.StartServiceAsync();
        var three = await rig.WaitForWorkersAsync("Orders", 3);

        rig.WriteConfig(rig.Group("Orders", 1));

        var one = await rig.WaitForWorkersAsync("Orders", 1);
        Assert.Subset(three.ToHashSet(), one.ToHashSet());
        Assert.True(await Rig.Eventually(() => three.Count(Rig.IsRunning) == 1));
        // The service notices the exits on its next look, an instant later.
        Assert.True(await Rig.Eventually(() => rig.LogText().Split("WorkerStopped [Orders]").Length - 1 == 2));
        Assert.DoesNotContain("WorkerKilled", rig.LogText());

        // A broken file changes nothing.
        File.WriteAllText(Path.Combine(rig.Directory, "ConfigWorkers.json"), "{ this is not json");
        Assert.True(await Rig.Eventually(() => rig.LogText().Contains("was refused")));
        Assert.Equal(one, await rig.WorkersAsync("Orders"));
        Assert.True(Rig.IsRunning(one[0]));
    }

    [Fact]
    public async Task A_worker_that_ignores_the_safe_stop_is_ended_when_its_time_is_up()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Stubborn", 1, mode: "stubborn", more: "\"SafeStopTimeoutMs\": 2000"));
        await rig.StartServiceAsync();
        var worker = (await rig.WaitForWorkersAsync("Stubborn", 1)).Single();

        rig.WriteConfig(rig.Group("Stubborn", 1, mode: "stubborn", enabled: false, more: "\"SafeStopTimeoutMs\": 2000"));
        Assert.True(await rig.ReloadConfigAsync());

        await Task.Delay(1000);
        Assert.True(Rig.IsRunning(worker));
        Assert.True(await Rig.Eventually(() => !Rig.IsRunning(worker), 10000));
        Assert.Contains("SafeStopTimedOut [Stubborn] pid " + worker, rig.LogText());
    }

    [Fact]
    public async Task Stopping_the_service_stops_the_workers()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("A", 2), rig.Group("B", 1));
        await rig.StartServiceAsync();
        var workers = (await rig.WaitForWorkersAsync("A", 2)).Concat(await rig.WaitForWorkersAsync("B", 1)).ToList();

        await rig.StopServiceAsync();

        Assert.DoesNotContain(workers, Rig.IsRunning);
        Assert.Contains("WorkerStopped", rig.LogText());
        Assert.DoesNotContain("WorkerKilled", rig.LogText());
        Assert.Equal("[]", File.ReadAllText(Path.Combine(rig.Directory, "state.json")).Trim());
    }

    [Fact]
    public async Task Stopped_with_the_detach_file_it_leaves_the_workers_and_finds_them_again()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 2));
        await rig.StartServiceAsync();
        var before = await rig.WaitForWorkersAsync("Orders", 2);

        File.WriteAllText(Path.Combine(rig.Directory, "detach.flag"), "");
        await rig.StopServiceAsync();

        Assert.All(before, id => Assert.True(Rig.IsRunning(id)));
        Assert.False(File.Exists(Path.Combine(rig.Directory, "detach.flag")));

        await rig.StartServiceAsync();
        var after = await rig.WaitForWorkersAsync("Orders", 2);
        Assert.Equal(before.Order(), after.Order());
        await Task.Delay(1500);
        Assert.Equal(before.Order(), (await rig.WorkersAsync("Orders")).Order());
        Assert.Contains("WorkerAdopted [Orders]", rig.LogText());

        // A normal stop still takes them down.
        await rig.StopServiceAsync();
        Assert.DoesNotContain(before, Rig.IsRunning);
    }

    [Fact]
    public async Task A_restart_of_the_broker_ends_nobody()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 3));
        await rig.StartServiceAsync();
        var before = await rig.WaitForWorkersAsync("Orders", 3);

        // Long enough for several keep-alives to go unanswered.
        await rig.StopBrokerAsync();
        await Task.Delay(8000);
        Assert.All(before, id => Assert.True(Rig.IsRunning(id)));
        await rig.StartBrokerAsync();

        var after = await rig.WaitForWorkersAsync("Orders", 3, 30000);
        Assert.Equal(before.Order(), after.Order());
        await Task.Delay(6000);
        Assert.Equal(before.Order(), (await rig.WorkersAsync("Orders")).Order());
        var log = rig.LogText();
        Assert.DoesNotContain("WorkerHung", log);
        Assert.DoesNotContain("WorkerKilled", log);
        Assert.Contains("ZapMQ is not answering", log);
    }

    [Fact]
    public async Task Without_the_broker_the_workers_start_anyway()
    {
        await using var rig = new Rig();
        rig.WriteConfig(rig.Group("Orders", 2));
        await rig.StartServiceAsync();

        // Nobody can be asked yet, so the processes are counted directly.
        Assert.True(await Rig.Eventually(() => Mine(rig).Count == 2));
        await Task.Delay(4000);
        Assert.Equal(2, Mine(rig).Count);

        await rig.StartBrokerAsync();
        var ids = await rig.WaitForWorkersAsync("Orders", 2, 30000);
        Assert.Equal(Mine(rig).Order(), ids.Order());
    }

    [Fact]
    public async Task A_worker_that_keeps_dying_is_not_restarted_in_a_tight_loop()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Fragile", 1, mode: "crash:300", more: "\"CrashLimit\": 3, \"CrashWindowMs\": 10000"));
        await rig.StartServiceAsync();

        Assert.True(await Rig.Eventually(() => rig.LogText().Contains("GroupUnstable [Fragile]"), 20000));
        var started = Count(rig.LogText(), "WorkerStarted [Fragile]");
        await Task.Delay(3000);

        // Three quick failures, then a wait of 5 s before the next attempt.
        Assert.InRange(started, 3, 4);
        Assert.InRange(Count(rig.LogText(), "WorkerStarted [Fragile]"), started, started + 1);
    }

    private static int Count(string text, string what) => text.Split(what).Length - 1;

    /// <summary>
    /// The test workers started for this rig, found by the port in their command line.
    /// </summary>
    private static List<int> Mine(Rig rig)
    {
        var found = new List<int>();
        foreach (var process in Process.GetProcessesByName("TestWorker"))
        {
            try
            {
                var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "wmic" : "ps",
                    OperatingSystem.IsWindows() ? $"process where processid={process.Id} get commandline" : $"-o args= -p {process.Id}")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                using var query = Process.Start(info)!;
                var text = query.StandardOutput.ReadToEnd() + " ";
                query.WaitForExit(2000);
                if (text.Contains($" {rig.Port} "))
                    found.Add(process.Id);
            }
            catch (Exception)
            {
                // Gone while being looked at.
            }
        }
        return found;
    }
}
