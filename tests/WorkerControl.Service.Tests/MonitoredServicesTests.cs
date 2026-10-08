using System.Diagnostics;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;

namespace WorkerControl.Service.Tests;

/// <summary>
/// The Windows services that are watched, through the administration contract. The machine
/// is a pretend one; the processes behind the services are real.
/// </summary>
[Collection("processes")]
public class MonitoredServicesTests
{
    /// <summary>
    /// A machine whose services are whatever the test says. A running service has a real
    /// process behind it, so that there is something to measure.
    /// </summary>
    private sealed class Machine : IServiceManager, IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, InstalledService> _services = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Process> _processes = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Asked { get; } = [];

        public void Install(string name, string path, bool running = true)
        {
            lock (_gate)
            {
                _services[name] = new InstalledService(name, name + " Service", path, ServiceState.Stopped, "Automatic", 0, 0);
                if (running)
                    Run(name);
            }
        }

        public void Crash(string name)
        {
            lock (_gate)
            {
                End(name);
                _services[name] = _services[name] with { State = ServiceState.Stopped, ProcessId = 0, ExitCode = 1067 };
            }
        }

        public IReadOnlyList<InstalledService> List()
        {
            lock (_gate)
                return [.. _services.Values.Select(service => service with { ProcessId = 0, ExitCode = 0 })];
        }

        public InstalledService? Find(string name)
        {
            lock (_gate)
                return _services.GetValueOrDefault(name);
        }

        public void Start(string name)
        {
            lock (_gate)
            {
                Asked.Add("start " + name);
                Run(name);
            }
        }

        public void Stop(string name)
        {
            lock (_gate)
            {
                Asked.Add("stop " + name);
                End(name);
                _services[name] = _services[name] with { State = ServiceState.Stopped, ProcessId = 0, ExitCode = 0 };
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var name in _processes.Keys.ToList())
                    End(name);
            }
        }

        private void Run(string name)
        {
            // Anything that stays alive doing nothing.
            var command = OperatingSystem.IsWindows() ? new ProcessStartInfo("ping", "-n 600 127.0.0.1") : new ProcessStartInfo("sleep", "600");
            command.UseShellExecute = false;
            command.RedirectStandardOutput = true;
            var process = Process.Start(command)!;
            _processes[name] = process;
            _services[name] = _services[name] with { State = ServiceState.Running, ProcessId = process.Id, ExitCode = 0 };
        }

        private void End(string name)
        {
            if (_processes.Remove(name, out var process))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
                process.Dispose();
            }
        }
    }

    private static JToken Service(JObject status, string name) =>
        status["Services"]!.Single(service => (string?)service["Name"] == name);

    private static async Task<Rig> StartAsync(Machine machine, string services)
    {
        var rig = new Rig { Services = machine, ServicesJson = services };
        await rig.StartBrokerAsync();
        rig.WriteConfig();
        await rig.StartServiceAsync();
        return rig;
    }

    [Fact]
    public async Task Status_tells_how_each_watched_service_is()
    {
        using var machine = new Machine();
        machine.Install("Orders", "/apps/orders/orders");
        machine.Install("Billing", "/apps/billing/billing", running: false);
        await using var rig = await StartAsync(machine, """{ "Items": [ { "Name": "Orders" }, { "Name": "Billing", "AutoRestart": true }, { "Name": "Ghost" } ] }""");

        JObject status = null!;
        Assert.True(await Rig.Eventually(async () =>
        {
            status = (await rig.CommandAsync("Status"))!;
            return status is not null && Service(status, "Orders")["CpuPercent"]!.Type != JTokenType.Null;
        }));

        Assert.Equal(2, (int)status["Contract"]!);
        var orders = Service(status, "Orders");
        Assert.Equal("Running", (string?)orders["State"]);
        Assert.Equal("Orders Service", (string?)orders["DisplayName"]);
        Assert.Equal("/apps/orders/orders", (string?)orders["ExecutablePath"]);
        Assert.Equal(machine.Find("Orders")!.ProcessId, (int)orders["ProcessId"]!);
        Assert.True((long)orders["MemoryBytes"]! > 0);
        Assert.True((int)orders["Threads"]! > 0);
        Assert.InRange((DateTimeOffset)orders["StartedAt"]!, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.False((bool)orders["AutoRestart"]!);
        Assert.Equal(JTokenType.Null, orders["Check"]!.Type);

        var billing = Service(status, "Billing");
        Assert.Equal("Stopped", (string?)billing["State"]);
        Assert.Equal(JTokenType.Null, billing["ProcessId"]!.Type);
        Assert.True((bool)billing["AutoRestart"]!);
        Assert.Equal("Missing", (string?)Service(status, "Ghost")["State"]);

        // Measured like a worker, under the name of the service.
        var health = (await rig.CommandAsync("Health", request => request["Group"] = "service:Orders"))!;
        Assert.NotEmpty(health["Samples"]!);
        Assert.All(health["Samples"]!, sample => Assert.Equal(machine.Find("Orders")!.ProcessId, (int)sample["ProcessId"]!));
    }

    [Fact]
    public async Task What_is_asked_is_done_and_recorded_with_who_asked()
    {
        using var machine = new Machine();
        machine.Install("Orders", "/apps/orders/orders");
        await using var rig = await StartAsync(machine, """{ "Items": [ { "Name": "Orders" } ] }""");
        Assert.True(await Rig.Eventually(async () => (string?)Service((await rig.CommandAsync("Status"))!, "Orders")["State"] == "Running"));
        var before = machine.Find("Orders")!.ProcessId;

        var answer = await rig.CommandAsync("RestartService", request =>
        {
            request["Name"] = "Orders";
            request["By"] = "maria";
        });

        Assert.True((bool)answer!["Ok"]!, answer.ToString());
        Assert.True(await Rig.Eventually(() => machine.Find("Orders") is { State: ServiceState.Running } now && now.ProcessId != before));
        Assert.Equal(["stop Orders", "start Orders"], machine.Asked);

        JToken[] events = [];
        Assert.True(await Rig.Eventually(async () =>
        {
            events = [.. (await rig.CommandAsync("Events", request => request["Group"] = "service:Orders"))!["Events"]!];
            return events.Any(e => (string?)e["Kind"] == "MonitoredStarted");
        }));
        Assert.Contains(events, e => (string?)e["Kind"] == "ManualAction" && (string?)e["Detail"] == "restart (by maria)");
        Assert.Contains(events, e => (string?)e["Kind"] == "MonitoredStopped" && (string?)e["Detail"] == "stopped as asked");

        Assert.Equal("not-found", (string?)(await rig.CommandAsync("StopService", request => request["Name"] = "Unknown"))!["Error"]!["Code"]);
        Assert.Equal("invalid-request", (string?)(await rig.CommandAsync("StopService"))!["Error"]!["Code"]);
    }

    [Fact]
    public async Task A_service_set_up_for_it_comes_back_after_a_crash()
    {
        using var machine = new Machine();
        machine.Install("Orders", "/apps/orders/orders");
        await using var rig = await StartAsync(machine, """{ "Items": [ { "Name": "Orders", "AutoRestart": true } ] }""");
        Assert.True(await Rig.Eventually(async () => (string?)Service((await rig.CommandAsync("Status"))!, "Orders")["State"] == "Running"));

        machine.Crash("Orders");

        Assert.True(await Rig.Eventually(() => machine.Find("Orders")!.State == ServiceState.Running, 30000));
        Assert.Equal(["start Orders"], machine.Asked);
        Assert.Contains("MonitoredCrashed [service:Orders]", rig.LogText());
        Assert.Contains("MonitoredRestarting [service:Orders]", rig.LogText());
    }

    [Fact]
    public async Task The_services_of_the_machine_are_listed_with_the_suggested_ones_first()
    {
        using var machine = new Machine();
        machine.Install("Zebra", @"D:\Apps\zebra\zebra.exe", running: false);
        machine.Install("Alpha", @"C:\Other\alpha.exe", running: false);
        machine.Install("Orders", @"d:/apps/orders/orders.exe", running: false);
        await using var rig = await StartAsync(machine, """{ "SuggestFrom": ["D:\\Apps"], "Items": [ { "Name": "Orders" } ] }""");

        var answer = (await rig.CommandAsync("ListServices"))!;

        Assert.Equal(["Orders", "Zebra", "Alpha"], answer["Services"]!.Select(service => (string?)service["Name"]));
        Assert.Equal([true, true, false], answer["Services"]!.Select(service => (bool)service["Suggested"]!));
        Assert.Equal([true, false, false], answer["Services"]!.Select(service => (bool)service["Watched"]!));
        Assert.Equal(["D:\\Apps"], answer["SuggestFrom"]!.Select(folder => (string?)folder));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --service", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\Apps\\my app\\app.exe -k run", "C:\\Apps\\my app\\app.exe")]
    [InlineData("C:\\Apps\\app.EXE", "C:\\Apps\\app.EXE")]
    [InlineData("\\??\\C:\\Windows\\system32\\drivers\\x.exe", "C:\\Windows\\system32\\drivers\\x.exe")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_executable_is_taken_out_of_the_command_line_of_a_service(string? commandLine, string? expected) =>
        Assert.Equal(expected, ServiceCommandLine.Executable(commandLine));

    [Theory]
    [InlineData("D:\\Apps\\orders\\orders.exe", true)]
    [InlineData("d:/apps/orders/orders.exe", true)]
    [InlineData("D:\\AppsOther\\x.exe", false)]
    [InlineData("D:\\Apps", false)]
    [InlineData(null, false)]
    public void A_file_is_told_to_be_inside_a_folder_whatever_the_case_and_the_slashes(string? path, bool expected) =>
        Assert.Equal(expected, ServiceCommandLine.IsUnder(path, ["D:\\Apps\\"]));
}
