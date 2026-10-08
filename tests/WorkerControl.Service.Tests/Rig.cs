using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using WorkerControl.Service;
using ZapMQ;
using ZapMQ.Server;

namespace WorkerControl.Service.Tests;

/// <summary>
/// Everything a test needs around the service: a real ZapMQ server, a folder with a
/// <c>ConfigWorkers.json</c>, the service itself and a client that administers it the way
/// Management Studio does.
/// </summary>
public sealed class Rig : IAsyncDisposable
{
    private static readonly string WorkerPath = FindWorker();

    private WebApplication? _broker;
    private IHost? _service;
    private readonly ZapMQWrapper _admin;
    private readonly HashSet<int> _seen = [];

    public Rig()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Directory = System.IO.Directory.CreateTempSubdirectory("workercontrol-test-").FullName;
        _admin = new ZapMQWrapper("localhost", Port);
        Http = new HttpClient { BaseAddress = new Uri($"http://localhost:{Port}/") };
    }

    public int Port { get; }

    /// <summary>
    /// The services of the machine as the service under test is to see them. Set before it
    /// starts; without it, the ones the machine really has (none, outside Windows).
    /// </summary>
    public WorkerControl.Core.IServiceManager? Services { get; set; }

    public string Directory { get; }

    public HttpClient Http { get; }

    public static async Task<Rig> StartAsync(params string[] groups)
    {
        var environment = new Rig();
        await environment.StartBrokerAsync();
        environment.WriteConfig(groups);
        await environment.StartServiceAsync();
        return environment;
    }

    /// <summary>
    /// One group of the configuration file. Times are short so the tests do not take long.
    /// </summary>
    public string Group(string name, int workers, string mode = "normal", bool enabled = true, string more = "") => $$"""
        {
          "Enabled": {{(enabled ? "true" : "false")}},
          "Name": "{{name}}",
          "ApplicationFullPath": {{Newtonsoft.Json.JsonConvert.ToString(WorkerPath)}},
          "Arguments": "localhost {{Port}} {{mode}}",
          "TotalWorkers": {{workers}},
          "MonitoringRate": 1000,
          "TimeoutKeepAlive": 2000,
          "Boost": { "Enabled": false, "BoostWorkers": 0, "StartTime": "00:00:00", "EndTime": "00:00:00" }
          {{(more.Length > 0 ? "," + more : "")}}
        }
        """;

    public void WriteConfig(params string[] groups) => File.WriteAllText(Path.Combine(Directory, "ConfigWorkers.json"), $$"""
        {
          "ZapMQHost": "localhost",
          "ZapMQPort": {{Port}},
          "RateLoadConfig": 180000,
          "StartBatchSize": 10,
          "StartBatchIntervalMs": 100,
          "WorkerGroups": [ {{string.Join(",", groups)}} ]
          {{(ServicesJson is null ? "" : ", \"Services\": " + ServicesJson)}}
        }
        """);

    /// <summary>
    /// The "Services" section of the configuration file, when the test wants one.
    /// </summary>
    public string? ServicesJson { get; set; }

    public async Task StartBrokerAsync()
    {
        _broker = ServerHost.Build([], builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ZapMQ:Port"] = Port.ToString(),
            ["ZapMQ:LogDirectory"] = Path.Combine(Directory, "zapmq-logs"),
            // Only the messaging port matters here, and many servers run side by side.
            ["ZapMQ:Panel:Enabled"] = "false",
            ["ZapMQ:QueueDefinitionsFile"] = Path.Combine(Directory, "queues.json")
        }));
        await _broker.StartAsync();
    }

    public async Task StopBrokerAsync()
    {
        if (_broker is null)
            return;
        await _broker.StopAsync();
        await _broker.DisposeAsync();
        _broker = null;
    }

    public async Task StartServiceAsync()
    {
        _service = ServiceHost.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkerControl:DataDirectory"] = Directory,
                ["WorkerControl:TickMilliseconds"] = "50",
                ["WorkerControl:HealthSampleSeconds"] = "1"
            });
            if (Services is not null)
                builder.Services.AddSingleton(Services);
        });
        await _service.StartAsync();

        // A host that is run, as the real service is, stops when the application asks to.
        // Here it is only started, so the same has to be arranged by hand.
        var service = _service;
        service.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
            Task.Run(() => service.StopAsync()));
    }

    public async Task StopServiceAsync()
    {
        if (_service is null)
            return;
        await _service.StopAsync();
        _service.Dispose();
        _service = null;
    }

    /// <summary>
    /// The answer to <c>CurrentWorkers</c>, asked over ZapMQ as Management Studio asks. Null
    /// when nobody answered.
    /// </summary>
    public async Task<JObject?> CurrentWorkersAsync()
    {
        var answer = new TaskCompletionSource<JObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = _admin.OnRPCExpired;
        _admin.OnRPCExpired = _ => answer.TrySetResult(null);
        if (!_admin.SendRPCMessage("WorkerControlAdmin", new { Message = "CurrentWorkers" }, message => answer.TrySetResult(message.Response as JObject), 3000))
            return null;
        var result = await answer.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _admin.OnRPCExpired = previous;
        return result;
    }

    /// <summary>
    /// A command of the 2.0 contract, sent over ZapMQ. Null when nobody answered.
    /// </summary>
    public async Task<JObject?> CommandAsync(string command, Action<JObject>? more = null)
    {
        var request = new JObject { ["Command"] = command, ["Version"] = 1 };
        more?.Invoke(request);
        var answer = new TaskCompletionSource<JObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_admin.SendRPCMessage("WorkerControlAdmin", request, message => answer.TrySetResult(message.Response as JObject), 5000))
            return null;
        var first = await Task.WhenAny(answer.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        return first == answer.Task ? await answer.Task : null;
    }

    /// <summary>
    /// A publisher of its own, to fill queues the way an application would.
    /// </summary>
    public bool Publish(string queue, object body) => _admin.SendMessage(queue, body);

    public string ConfigText() => File.ReadAllText(Path.Combine(Directory, "ConfigWorkers.json"));

    public async Task<bool> ReloadConfigAsync()
    {
        var answer = new TaskCompletionSource<JObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_admin.SendRPCMessage("WorkerControlAdmin", new { Message = "ReloadConfig" }, message => answer.TrySetResult(message.Response as JObject), 3000))
            return false;
        var result = await answer.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (string?)result?["Message"] == "OK";
    }

    /// <summary>
    /// The process ids the service reports for a group.
    /// </summary>
    public async Task<List<int>> WorkersAsync(string group)
    {
        var status = await CurrentWorkersAsync();
        var found = status?["WorkerGroups"]?.FirstOrDefault(item => (string?)item["Name"] == group);
        var ids = found?["Workers"]?.Select(worker => (int)worker["ProcessId"]!).ToList() ?? [];
        foreach (var id in ids)
            _seen.Add(id);
        return ids;
    }

    /// <summary>
    /// Waits until the group reports that many workers and all of them are running.
    /// </summary>
    public async Task<List<int>> WaitForWorkersAsync(string group, int count, int timeoutMs = 20000)
    {
        List<int> ids = [];
        var ok = await Eventually(async () =>
        {
            ids = await WorkersAsync(group);
            return ids.Count == count && ids.All(IsRunning);
        }, timeoutMs);
        Assert.True(ok, $"group {group}: expected {count} running workers, the service reports [{string.Join(", ", ids)}]");
        return ids;
    }

    public static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static void Kill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    public static async Task<bool> Eventually(Func<Task<bool>> condition, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return true;
            await Task.Delay(100);
        }
        return await condition();
    }

    public static Task<bool> Eventually(Func<bool> condition, int timeoutMs = 20000) =>
        Eventually(() => Task.FromResult(condition()), timeoutMs);

    public string LogText()
    {
        var folder = Path.Combine(Directory, "logs");
        return System.IO.Directory.Exists(folder)
            ? string.Concat(System.IO.Directory.GetFiles(folder).Select(file =>
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }))
            : "";
    }

    public async ValueTask DisposeAsync()
    {
        await StopServiceAsync();
        _admin.StopThreads();
        await StopBrokerAsync();
        Http.Dispose();
        // Nothing a test started may outlive it, whatever went wrong.
        foreach (var id in _seen)
            Kill(id);
        foreach (var stray in Process.GetProcessesByName("TestWorker"))
        {
            try
            {
                if (stray.StartInfo.Arguments.Contains(Port.ToString()) || CommandLine(stray).Contains($" {Port} "))
                    stray.Kill();
            }
            catch (Exception)
            {
                // Not ours, or already gone.
            }
        }
        // Set to look at what a failed test left behind.
        if (System.Environment.GetEnvironmentVariable("WORKERCONTROL_TEST_KEEP") == "1")
            return;
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A log file may still be held for an instant.
        }
    }

    private static string CommandLine(Process process)
    {
        try
        {
            var info = new ProcessStartInfo("ps", $"-o args= -p {process.Id}") { RedirectStandardOutput = true, UseShellExecute = false };
            using var ps = Process.Start(info)!;
            var text = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(2000);
            return text + " ";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string FindWorker()
    {
        // tests/WorkerControl.Service.Tests/bin/<configuration>/<framework>/ → tests/TestWorker/bin/<configuration>/<framework>/
        var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var framework = here.Name;
        var configuration = here.Parent!.Name;
        var folder = Path.Combine(here.Parent.Parent!.Parent!.Parent!.FullName, "TestWorker", "bin", configuration, framework);
        var name = OperatingSystem.IsWindows() ? "TestWorker.exe" : "TestWorker";
        var path = Path.Combine(folder, name);
        return File.Exists(path) ? path : throw new FileNotFoundException("The test worker was not built", path);
    }
}
