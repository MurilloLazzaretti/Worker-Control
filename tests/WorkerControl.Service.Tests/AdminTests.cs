using Newtonsoft.Json.Linq;

namespace WorkerControl.Service.Tests;

/// <summary>
/// The administration contract of 2.0 and what depends on it, against the real service.
/// </summary>
[Collection("processes")]
public class AdminTests
{
    private static JToken Group(JObject status, string name) =>
        status["Groups"]!.Single(group => (string?)group["Name"] == name);

    [Fact]
    public async Task Status_tells_the_state_of_the_service_its_groups_and_its_workers()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 2), rig.Group("Idle", 1, enabled: false));
        await rig.StartServiceAsync();
        var ids = await rig.WaitForWorkersAsync("Orders", 2);

        JObject status = null!;
        Assert.True(await Rig.Eventually(async () =>
        {
            status = (await rig.CommandAsync("Status"))!;
            return status is not null && Group(status, "Orders")["Workers"]!.All(worker => (string?)worker["State"] == "Up" && worker["MemoryBytes"]!.Type != JTokenType.Null);
        }));

        Assert.True((bool)status["Ok"]!);
        Assert.Equal(ServiceHost.Version, (string?)status["Version"]);
        Assert.Equal(2, (int)status["Contract"]!);
        Assert.Equal(Environment.ProcessId, (int)status["Service"]!["ProcessId"]!);
        Assert.Equal(rig.Port, (int)status["Service"]!["ZapMQ"]!["Port"]!);

        var orders = Group(status, "Orders");
        Assert.Equal(2, (int)orders["TotalWorkers"]!);
        Assert.Equal(2, (int)orders["DesiredWorkers"]!);
        Assert.Equal(0, (int)orders["BoostWorkers"]!);
        Assert.False((bool)orders["Unstable"]!);
        Assert.Equal(ids.Order(), orders["Workers"]!.Select(worker => (int)worker["ProcessId"]!).Order());
        Assert.All(orders["Workers"]!, worker =>
        {
            Assert.True((long)worker["MemoryBytes"]! > 1024 * 1024);
            Assert.InRange((double)worker["KeepAliveMs"]!, 0, 5000);
            Assert.False((bool)worker["Adopted"]!);
        });

        var idle = Group(status, "Idle");
        Assert.False((bool)idle["Enabled"]!);
        Assert.Equal(0, (int)idle["DesiredWorkers"]!);
        Assert.Empty(idle["Workers"]!);
    }

    [Fact]
    public async Task The_number_of_workers_of_a_group_can_be_changed_and_stays_changed_in_the_file()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 1), rig.Group("Other", 1));
        await rig.StartServiceAsync();
        await rig.WaitForWorkersAsync("Orders", 1);

        var answer = await rig.CommandAsync("SetGroupWorkers", request =>
        {
            request["Group"] = "Orders";
            request["TotalWorkers"] = 3;
            request["By"] = "ana";
        });

        Assert.True((bool)answer!["Ok"]!);
        await rig.WaitForWorkersAsync("Orders", 3);
        var file = JObject.Parse(rig.ConfigText());
        Assert.Equal(3, (int)file["WorkerGroups"]![0]!["TotalWorkers"]!);
        Assert.Equal(1, (int)file["WorkerGroups"]![1]!["TotalWorkers"]!);
        Assert.Equal("localhost " + rig.Port + " normal", (string?)file["WorkerGroups"]![0]!["Arguments"]);
        Assert.True(File.Exists(Path.Combine(rig.Directory, "ConfigWorkers.json.bak")));
        Assert.Contains("ManualAction [Orders]: workers set to 3 (by ana)", rig.LogText());

        Assert.Equal("not-found", (string?)(await rig.CommandAsync("SetGroupWorkers", request =>
        {
            request["Group"] = "Nope";
            request["TotalWorkers"] = 1;
        }))!["Error"]!["Code"]);
        Assert.Equal("invalid-request", (string?)(await rig.CommandAsync("SetGroupWorkers", request => request["Group"] = "Orders"))!["Error"]!["Code"]);
    }

    [Fact]
    public async Task A_group_can_be_disabled_and_enabled_again()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 2));
        await rig.StartServiceAsync();
        var before = await rig.WaitForWorkersAsync("Orders", 2);

        Assert.True((bool)(await rig.CommandAsync("SetGroupEnabled", request =>
        {
            request["Group"] = "Orders";
            request["Enabled"] = false;
        }))!["Ok"]!);
        Assert.True(await Rig.Eventually(() => !before.Any(Rig.IsRunning)));

        Assert.True((bool)(await rig.CommandAsync("SetGroupEnabled", request =>
        {
            request["Group"] = "Orders";
            request["Enabled"] = true;
        }))!["Ok"]!);
        var after = await rig.WaitForWorkersAsync("Orders", 2);
        Assert.Empty(after.Intersect(before));
    }

    [Fact]
    public async Task A_worker_restarted_on_request_only_leaves_when_its_substitute_is_up()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 1));
        await rig.StartServiceAsync();
        var old = (await rig.WaitForWorkersAsync("Orders", 1)).Single();

        Assert.True((bool)(await rig.CommandAsync("RestartWorker", request => request["ProcessId"] = old))!["Ok"]!);

        var seen = new HashSet<int> { old };
        Assert.True(await Rig.Eventually(async () =>
        {
            var now = await rig.WorkersAsync("Orders");
            // At no moment is the group without a worker in service.
            Assert.NotEmpty(now);
            seen.UnionWith(now);
            return now is [var only] && only != old && !Rig.IsRunning(old);
        }));
        Assert.Equal(2, seen.Count);
        // The service notices the exit on its next look, an instant later.
        Assert.True(await Rig.Eventually(() => rig.LogText().Contains("WorkerStopped [Orders] pid " + old)));
        Assert.Equal("not-found", (string?)(await rig.CommandAsync("RestartWorker", request => request["ProcessId"] = 4242424))!["Error"]!["Code"]);
    }

    [Fact]
    public async Task A_group_restarted_on_request_gets_all_its_workers_replaced()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 2));
        await rig.StartServiceAsync();
        var before = await rig.WaitForWorkersAsync("Orders", 2);

        Assert.True((bool)(await rig.CommandAsync("RestartGroup", request => request["Group"] = "Orders"))!["Ok"]!);

        Assert.True(await Rig.Eventually(async () =>
        {
            var now = await rig.WorkersAsync("Orders");
            return now.Count == 2 && !now.Intersect(before).Any() && now.All(Rig.IsRunning) && !before.Any(Rig.IsRunning);
        }, 30000));
        Assert.Contains("RecycleFinished [Orders]", rig.LogText());
    }

    [Fact]
    public async Task What_happened_and_how_the_workers_are_doing_can_be_asked_for()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 2));
        await rig.StartServiceAsync();
        var ids = await rig.WaitForWorkersAsync("Orders", 2);
        Rig.Kill(ids[0]);
        await rig.WaitForWorkersAsync("Orders", 2);

        var events = (await rig.CommandAsync("Events"))!;
        var kinds = events["Events"]!.Select(e => (string?)e["Kind"]).ToList();
        Assert.Contains("ServiceStarted", kinds);
        Assert.Contains("ConfigApplied", kinds);
        Assert.Equal(3, kinds.Count(kind => kind == "WorkerStarted"));
        var crash = events["Events"]!.Single(e => (string?)e["Kind"] == "WorkerCrashed");
        Assert.Equal(ids[0], (int)crash["ProcessId"]!);
        Assert.Equal("Orders", (string?)crash["Group"]);
        // Most recent first.
        Assert.True((long)events["Events"]![0]!["Id"]! > (long)events["Events"]!.Last()["Id"]!);

        var onlyCrashes = (await rig.CommandAsync("Events", request => request["Kind"] = "WorkerCrashed"))!;
        Assert.Single(onlyCrashes["Events"]!);
        Assert.Single((await rig.CommandAsync("Events", request => request["Limit"] = 1))!["Events"]!);

        JObject health = null!;
        Assert.True(await Rig.Eventually(async () =>
        {
            health = (await rig.CommandAsync("Health", request => request["ProcessId"] = ids[1]))!;
            return health["Samples"]!.Count() >= 3;
        }));
        Assert.All(health["Samples"]!, sample =>
        {
            Assert.Equal(ids[1], (int)sample["ProcessId"]!);
            Assert.Equal("Orders", (string?)sample["Group"]);
            Assert.True((long)sample["MemoryBytes"]! > 0);
        });
        // The second measurement on has a processor share, between two readings.
        Assert.Contains(health["Samples"]!, sample => sample["CpuPercent"]!.Type != JTokenType.Null);
        Assert.True((double)health["Samples"]!.Last()["UptimeSeconds"]! > (double)health["Samples"]!.First()["UptimeSeconds"]!);
        Assert.True(File.Exists(Path.Combine(rig.Directory, "workercontrol.db")));
    }

    [Fact]
    public async Task The_whole_configuration_can_be_read_and_replaced_and_a_bad_one_is_refused()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 1));
        await rig.StartServiceAsync();
        await rig.WaitForWorkersAsync("Orders", 1);

        var read = (await rig.CommandAsync("GetConfig"))!;
        Assert.Equal("Orders", (string?)read["Config"]!["WorkerGroups"]![0]!["Name"]);
        Assert.Equal(rig.ConfigText(), (string?)read["Text"]);

        var before = rig.ConfigText();
        var bad = (JObject)read["Config"]!.DeepClone();
        bad["WorkerGroups"]![0]!["TotalWorkers"] = -5;
        var refused = (await rig.CommandAsync("SetConfig", request => request["Config"] = bad))!;
        Assert.False((bool)refused["Ok"]!);
        Assert.Equal("invalid-config", (string?)refused["Error"]!["Code"]);
        Assert.Contains("cannot be less than 0", (string?)refused["Error"]!["Message"]);
        Assert.Equal(before, rig.ConfigText());

        var good = (JObject)read["Config"]!.DeepClone();
        good["WorkerGroups"]![0]!["TotalWorkers"] = 2;
        Assert.True((bool)(await rig.CommandAsync("SetConfig", request => request["Config"] = good))!["Ok"]!);
        await rig.WaitForWorkersAsync("Orders", 2);

        Assert.Equal("unknown-command", (string?)(await rig.CommandAsync("Dance"))!["Error"]!["Code"]);
    }

    [Fact]
    public async Task Asked_to_stop_leaving_the_workers_it_does_and_finds_them_on_the_next_start()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 2));
        await rig.StartServiceAsync();
        var before = await rig.WaitForWorkersAsync("Orders", 2);

        Assert.True((bool)(await rig.CommandAsync("DetachAndStop"))!["Ok"]!);
        Assert.True(await Rig.Eventually(() => rig.LogText().Contains("ServiceStopped: the workers were left running")));
        Assert.All(before, id => Assert.True(Rig.IsRunning(id)));

        await rig.StopServiceAsync();
        await rig.StartServiceAsync();
        Assert.Equal(before.Order(), (await rig.WaitForWorkersAsync("Orders", 2)).Order());
    }

    [Fact]
    public async Task A_queue_piling_up_brings_extra_workers()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        var backlog = "Backlog" + Guid.NewGuid().ToString("N");
        rig.WriteConfig(rig.Group("Orders", 1, more: $$"""
            "QueueScaling": { "Queue": "{{backlog}}", "PendingPerWorker": 5, "MaxWorkers": 3, "CooldownMs": 600000 }
            """));
        await rig.StartServiceAsync();
        await rig.WaitForWorkersAsync("Orders", 1);

        // Nobody consumes this queue: what is published stays waiting.
        for (var n = 0; n < 12; n++)
            Assert.True(rig.Publish(backlog, new { n }));

        await rig.WaitForWorkersAsync("Orders", 3, 30000);
        var status = (await rig.CommandAsync("Status"))!;
        Assert.Equal(2, (int)Group(status, "Orders")["ScaleWorkers"]!);
        Assert.Equal(3, (int)Group(status, "Orders")["DesiredWorkers"]!);
        Assert.Contains("ScaleChanged [Orders]", rig.LogText());
    }
}
