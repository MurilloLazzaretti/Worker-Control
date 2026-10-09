using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;
using WorkerControl.Service.Traffic;
using WorkerControl.Service.Transport;

namespace WorkerControl.Service.Tests;

/// <summary>
/// Creating on the machine an application it does not have, and taking one away.
/// </summary>
public sealed class ApplicationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("applications-").FullName;
    private readonly PretendMachine _machine = new();
    private readonly ConfigFile _file;
    private readonly ApplicationWork _work;
    private WorkerControlConfig? _config;

    public ApplicationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        _file = new ConfigFile(Path.Combine(_root, "data"));
        File.WriteAllText(_file.Path, """{ "ZapMQHost": "localhost", "ZapMQPort": 5679, "WorkerGroups": [] }""");
        Reload();
        var catalog = new TargetCatalog(() => _config, _machine, _machine, () => []);
        var transport = Path.Combine(_root, "data", "transport");
        var deployer = new Deployer(_machine, _machine, _machine, TimeProvider.System, transport, NullLogger.Instance)
        {
            StopPatience = TimeSpan.FromMilliseconds(300), StartPatience = TimeSpan.FromMilliseconds(300), EndPatience = TimeSpan.FromMilliseconds(300), Poll = TimeSpan.FromMilliseconds(20), Ender = _machine
        };
        _work = new ApplicationWork(catalog, deployer, _machine, _machine, _machine, _machine, () => _config, _file, Reload, transport, TimeProvider.System, NullLogger.Instance)
        {
            StartPatience = TimeSpan.FromMilliseconds(300), Poll = TimeSpan.FromMilliseconds(20)
        };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// What the supervisor does when its file changes: it reads it again and runs what it says.
    /// </summary>
    private void Reload()
    {
        _config = ConfigReader.Parse(_file.Read());
        foreach (var group in _config.Groups.Where(group => !_machine.Groups.ContainsKey(group.Name)))
            _machine.Groups[group.Name] = _machine.NeverStarts ? (true, 0, 0, group.TotalWorkers) : (true, group.TotalWorkers, group.TotalWorkers, group.TotalWorkers);
        foreach (var gone in _machine.Groups.Keys.Where(name => _config.Groups.All(group => group.Name != name)).ToList())
            _machine.Groups.Remove(gone);
    }

    private string Zip(params (string Path, string Text)[] files)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
        return path;
    }

    private string Published() => Zip(("App.exe", "v1"), ("lib/Core.dll", "core"), ("appsettings.json", "{as published}"));

    private ApplicationWork.NewApplication New(string kind, string name, string? folder = null, int instances = 2, int port = 19100) =>
        new(kind, name, folder ?? Path.Combine(_root, kind, name), Published(), kind == "api" ? null : "App.exe", instances, port, "Shop Api {name} {n}", "The " + name, "Automatic",
            [new ApplicationWork.ConfigText("appsettings.json", "{of this place}")]);

    private JObject Root() => JObject.Parse(_file.Read());

    [Fact]
    public void A_group_is_created_with_its_files_and_comes_up_and_is_taken_away_again()
    {
        var wanted = New("worker", "Orders");

        var created = _work.Create(wanted, CancellationToken.None);

        Assert.True((bool)created["Created"]!, (string?)created["Problem"]);
        Assert.Null((string?)created["Warning"]);
        Assert.Equal(("v1", "core", "{of this place}"), (File.ReadAllText(Path.Combine(wanted.Folder, "App.exe")), File.ReadAllText(Path.Combine(wanted.Folder, "lib", "Core.dll")), File.ReadAllText(Path.Combine(wanted.Folder, "appsettings.json"))));
        var group = Assert.Single(Root()["WorkerGroups"]!);
        Assert.Equal(("Orders", true, Path.Combine(wanted.Folder, "App.exe"), 2), ((string?)group["Name"], (bool)group["Enabled"]!, (string?)group["ApplicationFullPath"], (int)group["TotalWorkers"]!));

        // Nothing that is there is written over: not the name, not the folder.
        Assert.Contains("already a group", (string?)_work.Create(New("worker", "orders", Path.Combine(_root, "other")), CancellationToken.None)["Error"]!["Message"]);
        Assert.Contains("already has something", (string?)_work.Create(New("worker", "Other", wanted.Folder), CancellationToken.None)["Error"]!["Message"]);

        var removed = _work.Remove("worker", "Orders", false, CancellationToken.None);

        Assert.True((bool)removed["Removed"]!, (string?)removed["Problem"]);
        Assert.Empty(Root()["WorkerGroups"]!);
        Assert.False(Directory.Exists(wanted.Folder));
        // What was there is kept, not thrown away.
        Assert.Equal("v1", File.ReadAllText(Path.Combine((string)removed["Kept"]!, "0", "App.exe")));
    }

    [Fact]
    public void A_group_that_does_not_come_up_is_created_all_the_same_and_said()
    {
        _machine.NeverStarts = true;

        var created = _work.Create(New("worker", "Slow"), CancellationToken.None);

        Assert.True((bool)created["Created"]!);
        Assert.Contains("no process of it came up", (string?)created["Warning"]);
        Assert.Single(Root()["WorkerGroups"]!);
    }

    [Fact]
    public void An_application_of_the_web_server_gets_a_folder_a_site_and_a_pool_for_each_instance()
    {
        var wanted = New("api", "Orders");

        var created = _work.Create(wanted, CancellationToken.None);

        Assert.True((bool)created["Created"]!, (string?)created["Problem"]);
        Assert.Equal(["site Shop Api Orders 1 19100", "site Shop Api Orders 2 19101"], _machine.Did);
        Assert.Equal([Path.Combine(wanted.Folder, "1"), Path.Combine(wanted.Folder, "2")], _machine.WebSites.Select(site => site.PhysicalPath));
        Assert.All(_machine.WebSites, site => Assert.Equal("{of this place}", File.ReadAllText(Path.Combine(site.PhysicalPath, "appsettings.json"))));

        // What is there tells how the next one is to be named and where it goes.
        var defaults = _work.Defaults();
        Assert.Equal(("Shop Api {name} {n}", Path.Combine(_root, "api")), ((string?)defaults["SiteName"], (string?)defaults["Folders"]!["api"]));
        Assert.Equal([19100, 19101], defaults["Sites"]!.Select(site => (int)site["Port"]!));

        // A port or a name that is taken stops it before anything is made.
        _machine.Listening[19200] = 77;
        Assert.Contains("19101 is the one of the site", (string?)_work.Create(New("api", "Billing", port: 19101), CancellationToken.None)["Error"]!["Message"]);
        Assert.Contains("already listens on the port 19200", (string?)_work.Create(New("api", "Billing", port: 19199), CancellationToken.None)["Error"]!["Message"]);
        Assert.False(Directory.Exists(Path.Combine(_root, "api", "Billing")));

        _machine.Did.Clear();
        var removed = _work.Remove("api", "Orders", false, CancellationToken.None);

        Assert.True((bool)removed["Removed"]!, (string?)removed["Problem"]);
        Assert.Equal(["offline Shop Api Orders 1+Shop Api Orders 2", "unsite Shop Api Orders 1", "unsite Shop Api Orders 2"], _machine.Did);
        Assert.False(Directory.Exists(wanted.Folder));
        Assert.True(File.Exists(Path.Combine((string)removed["Kept"]!, "1", "App.exe")));
    }

    [Fact]
    public void The_instances_of_an_application_may_share_one_folder_as_the_ones_there_are_do()
    {
        var wanted = New("api", "Orders") with { Shared = true };

        var created = _work.Create(wanted, CancellationToken.None);

        Assert.True((bool)created["Created"]!, (string?)created["Problem"]);
        Assert.Equal([wanted.Folder, wanted.Folder], _machine.WebSites.Select(site => site.PhysicalPath));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(wanted.Folder, "App.exe")));
        Assert.False(Directory.Exists(Path.Combine(wanted.Folder, "1")));
        // Most of what is there shares a folder: the next one is offered the same.
        Assert.True((bool)_work.Defaults()["SharedFolder"]!);

        var removed = _work.Remove("api", "Orders", false, CancellationToken.None);
        Assert.True((bool)removed["Removed"]!, (string?)removed["Problem"]);
        Assert.Empty(_machine.WebSites);
        Assert.False(Directory.Exists(wanted.Folder));
    }

    [Fact]
    public void What_was_made_by_a_creation_that_fails_half_way_is_taken_away()
    {
        _machine.SiteThatFails = "Shop Api Orders 2";
        var wanted = New("api", "Orders");

        var created = _work.Create(wanted, CancellationToken.None);

        Assert.False((bool)created["Created"]!);
        Assert.Contains("the web server said no", (string?)created["Problem"]);
        Assert.Contains("Nothing of it was left", (string?)created["Problem"]);
        Assert.Equal(["site Shop Api Orders 1 19100", "unsite Shop Api Orders 1"], _machine.Did);
        Assert.Empty(_machine.WebSites);
        Assert.False(Directory.Exists(wanted.Folder));
    }

    [Fact]
    public void A_service_is_registered_looked_after_and_started_and_is_taken_away_again()
    {
        var wanted = New("service", "Billing");

        var created = _work.Create(wanted, CancellationToken.None);

        Assert.True((bool)created["Created"]!, (string?)created["Problem"]);
        Assert.Null((string?)created["Warning"]);
        Assert.Equal(["register Billing Automatic", "start Billing"], _machine.Did);
        Assert.Equal((Path.Combine(wanted.Folder, "App.exe"), "The Billing", ServiceState.Running), (_machine.Services["Billing"].ExecutablePath, _machine.Services["Billing"].DisplayName, _machine.Services["Billing"].State));
        Assert.Equal("Billing", (string?)Assert.Single(Root()["Services"]!["Items"]!)["Name"]);
        Assert.Contains("already a service", (string?)_work.Create(New("service", "billing", Path.Combine(_root, "elsewhere")), CancellationToken.None)["Error"]!["Message"]);

        _machine.Did.Clear();
        var removed = _work.Remove("service", "Billing", false, CancellationToken.None);

        Assert.True((bool)removed["Removed"]!, (string?)removed["Problem"]);
        Assert.Equal(["stop Billing", "unregister Billing"], _machine.Did);
        Assert.Empty(Root()["Services"]!["Items"]!);
        Assert.False(Directory.Exists(wanted.Folder));
    }

    [Fact]
    public void What_is_asked_wrong_or_turned_off_changes_nothing()
    {
        string? Refusal(ApplicationWork.NewApplication wanted) => (string?)_work.Create(wanted, CancellationToken.None)["Error"]?["Message"];

        Assert.Contains("letters, digits", Refusal(New("worker", "bad name")));
        Assert.Contains("full path", Refusal(New("worker", "Orders", "relative/folder")));
        Assert.Contains("one of the files of the zip", Refusal(New("worker", "Orders") with { Executable = "Other.exe" }));
        Assert.Contains("needs {name}", Refusal(New("api", "Orders") with { SiteName = "Fixed" }));
        Assert.Contains("not a path inside", Refusal(New("worker", "Orders") with { Configs = [new ApplicationWork.ConfigText("../outside.json", "{}")] }));
        Assert.Equal("not-found", (string?)_work.Remove("worker", "Nobody", false, CancellationToken.None)["Error"]!["Code"]);
        Assert.Equal("invalid-request", (string?)_work.Remove("frontend", "shop", false, CancellationToken.None)["Error"]!["Code"]);

        File.WriteAllText(_file.Path, """{ "ZapMQHost": "localhost", "ZapMQPort": 5679, "WorkerGroups": [], "Transport": { "AllowCreate": false } }""");
        Reload();
        Assert.Contains("turned off", Refusal(New("worker", "Orders")));
        Assert.False((bool)_work.Defaults()["Allowed"]!);
        Assert.False(Directory.Exists(Path.Combine(_root, "worker")));
    }
}
