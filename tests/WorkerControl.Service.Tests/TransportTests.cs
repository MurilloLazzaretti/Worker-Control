using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;
using WorkerControl.Service.Traffic;
using WorkerControl.Service.Transport;

namespace WorkerControl.Service.Tests;

/// <summary>
/// The files of what a package replaces: what is listed, packed, put in place and put back.
/// </summary>
public sealed class FileSetTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("transport-files-").FullName;
    private static readonly string[] Keep = ["appsettings*.json", "web.config", "*.db", "logs/"];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(string name, params (string Path, string Text)[] files)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (var (path, text) in files)
        {
            var file = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        return folder;
    }

    private static Dictionary<string, string> Read(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(file => Path.GetRelativePath(folder, file).Replace('\\', '/'), File.ReadAllText);

    [Theory]
    [InlineData("appsettings.json", true)]
    [InlineData("appsettings.Development.json", true)]
    [InlineData("sub/appsettings.json", true)]
    [InlineData("Web.Config", true)]
    [InlineData("data/traffic.db", true)]
    [InlineData("logs/app-2026.log", true)]
    [InlineData("bin/logs/x.txt", true)]
    [InlineData("App.dll", false)]
    [InlineData("catalogs/list.json", false)]
    [InlineData("dialogs/open.js", false)]
    public void What_belongs_to_the_environment_is_told_by_name_and_by_folder(string path, bool kept) =>
        Assert.Equal(kept, FileSet.IsKept(path, Keep));

    [Fact]
    public void A_folder_is_packed_without_what_belongs_to_the_environment()
    {
        var folder = Folder("app", ("App.exe", "v2"), ("lib/Core.dll", "core"), ("appsettings.json", "{secret}"), ("logs/today.log", "noise"), ("web.config", "<x/>"));
        var zip = Path.Combine(_root, "out", "app.zip");

        var packed = FileSet.Zip(folder, Keep, zip);

        Assert.Equal(["App.exe", "lib/Core.dll"], packed.Select(file => file.Path));
        Assert.Equal(packed, FileSet.ListZip(zip));
        Assert.Equal(packed, FileSet.List(folder, Keep));
        Assert.Equal(64, packed[0].Sha256.Length);
    }

    [Fact]
    public void A_folder_is_made_to_hold_what_the_package_brings_and_its_configuration()
    {
        var source = Folder("new", ("App.exe", "v2"), ("lib/Core.dll", "core"), ("lib/New.dll", "new"), ("appsettings.json", "{from dev}"));
        var zip = Path.Combine(_root, "new.zip");
        // Packed with everything, as a careless hand would: the configuration is still not put.
        ZipFile.CreateFromDirectory(source, zip);
        var target = Folder("installed", ("App.exe", "v1"), ("lib/Core.dll", "core"), ("lib/Old.dll", "old"), ("old/gone.txt", "x"), ("appsettings.json", "{of this place}"), ("logs/today.log", "kept"));

        var result = FileSet.Mirror(zip, target, Keep);

        Assert.Equal(new MirrorResult(Added: 1, Changed: 1, Removed: 2, Unchanged: 1), result);
        Assert.Equal(new Dictionary<string, string>
        {
            ["App.exe"] = "v2", ["lib/Core.dll"] = "core", ["lib/New.dll"] = "new", ["appsettings.json"] = "{of this place}", ["logs/today.log"] = "kept"
        }, Read(target));
        Assert.False(Directory.Exists(Path.Combine(target, "old")));
    }

    [Fact]
    public void A_package_with_a_file_that_would_land_outside_changes_nothing()
    {
        var zip = Path.Combine(_root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("App.exe").Open()))
                writer.Write("v2");
            using (var writer = new StreamWriter(archive.CreateEntry("../../outside.txt").Open()))
                writer.Write("gotcha");
        }
        var target = Folder("installed", ("App.exe", "v1"));

        Assert.Throws<InvalidDataException>(() => FileSet.Mirror(zip, target, Keep));
        Assert.Throws<InvalidDataException>(() => FileSet.ListZip(zip));

        Assert.Equal("v1", File.ReadAllText(Path.Combine(target, "App.exe")));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public void A_copy_puts_a_folder_back_exactly_as_it_was()
    {
        var folder = Folder("app", ("App.exe", "v1"), ("appsettings.json", "{mine}"), ("lib/Core.dll", "core"));
        var copy = Path.Combine(_root, "copy");
        FileSet.Copy(folder, copy);
        File.WriteAllText(Path.Combine(folder, "App.exe"), "v2");
        File.WriteAllText(Path.Combine(folder, "Extra.dll"), "x");
        File.Delete(Path.Combine(folder, "lib", "Core.dll"));

        FileSet.Restore(copy, folder);

        Assert.Equal(new Dictionary<string, string> { ["App.exe"] = "v1", ["appsettings.json"] = "{mine}", ["lib/Core.dll"] = "core" }, Read(folder));
    }
}

/// <summary>
/// A machine whose groups, services and sites are whatever the test says.
/// </summary>
internal sealed class PretendMachine : IGroupSwitch, IServiceManager, IWebServer, IMachineNetwork, IProcessEnder
{
    /// <summary>
    /// What is still running of each thing that did not stop, and what was ended by force.
    /// </summary>
    public List<int> Stuck { get; } = [];
    public List<int> Ended { get; } = [];
    public bool CannotBeEnded { get; set; }

    public bool End(int processId)
    {
        Ended.Add(processId);
        if (CannotBeEnded)
            return false;
        Stuck.Remove(processId);
        if (Stuck.Count == 0)
        {
            foreach (var name in Services.Keys.ToList())
                if (Services[name].State == ServiceState.Stopping)
                    Services[name] = Services[name] with { State = ServiceState.Stopped, ProcessId = 0 };
            foreach (var name in Groups.Keys.ToList())
                if (!Groups[name].Enabled)
                    Groups[name] = Groups[name] with { Processes = 0 };
        }
        return true;
    }

    public IReadOnlyList<int> ProcessIds(string group) => [.. Stuck];

    IReadOnlyList<int> IWebServer.ProcessIds(IReadOnlyList<string> sites) => [.. Stuck];

    public List<string> Did { get; } = [];
    public Dictionary<string, (bool Enabled, int Processes, int Up, int Desired)> Groups { get; } = [];
    public Dictionary<string, InstalledService> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<WebSite> WebSites { get; } = [];
    public bool NeverStops { get; set; }
    public bool NeverStarts { get; set; }
    public bool SlowToEnable { get; set; }

    public bool Enable(string group, bool enabled, string why)
    {
        Did.Add($"{(enabled ? "enable" : "disable")} {group}");
        if (!Groups.TryGetValue(group, out var now))
            return false;
        if (enabled && SlowToEnable)
            return true;
        Groups[group] = enabled ? (true, NeverStarts ? 0 : now.Desired, NeverStarts ? 0 : now.Desired, now.Desired) : (false, NeverStops ? now.Processes : 0, 0, now.Desired);
        return true;
    }

    public (bool Enabled, int Processes, int Up, int Desired)? Look(string group) => Groups.TryGetValue(group, out var now) ? now : null;

    public IReadOnlyList<InstalledService> List() => [.. Services.Values];

    public InstalledService? Find(string name) => Services.GetValueOrDefault(name);

    void IServiceManager.Start(string name)
    {
        Did.Add("start " + name);
        if (!NeverStarts)
            Services[name] = Services[name] with { State = ServiceState.Running, ProcessId = 4242 };
    }

    void IServiceManager.Stop(string name)
    {
        Did.Add("stop " + name);
        Services[name] = NeverStops ? Services[name] with { State = ServiceState.Stopping } : Services[name] with { State = ServiceState.Stopped, ProcessId = 0 };
    }

    void IWebServer.Stop(IReadOnlyList<string> sites) => Did.Add("offline " + string.Join("+", sites));

    void IWebServer.Start(IReadOnlyList<string> sites) => Did.Add("online " + string.Join("+", sites));

    public IReadOnlyDictionary<int, int> Listeners() => new Dictionary<int, int>();

    public MachineProcess? Process(int processId) => null;

    public IReadOnlyList<MachineProcess> Processes() => [];

    public IReadOnlyList<WebSite> Sites() => WebSites;
}

public sealed class DeployerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("transport-deploy-").FullName;
    private readonly PretendMachine _machine = new();
    private readonly Deployer _deployer;
    private static readonly string[] Keep = ["appsettings*.json"];

    public DeployerTests() =>
        _deployer = new Deployer(_machine, _machine, _machine, TimeProvider.System, Path.Combine(_root, "transport"), NullLogger.Instance)
        {
            StopPatience = TimeSpan.FromMilliseconds(300), StartPatience = TimeSpan.FromMilliseconds(300), EndPatience = TimeSpan.FromMilliseconds(300), Poll = TimeSpan.FromMilliseconds(20), Ender = _machine
        };

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Installed(string name, string version)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "App.exe"), version);
        File.WriteAllText(Path.Combine(folder, "appsettings.json"), "{of this place}");
        return folder;
    }

    private string Package(string version = "v2", bool bad = false)
    {
        var zip = Path.Combine(_root, $"package-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("App.exe").Open()))
            writer.Write(version);
        using (var writer = new StreamWriter(archive.CreateEntry("New.dll").Open()))
            writer.Write("new");
        if (bad)
            using (var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open()))
                writer.Write("x");
        return zip;
    }

    private Task<DeployResult> Deploy(DeployTarget target, string zip) => _deployer.DeployAsync(target, zip, "pkg-12345678", 1, 2, CancellationToken.None);

    [Fact]
    public async Task A_service_is_stopped_copied_replaced_and_started_in_that_order()
    {
        var folder = Installed("orders", "v1");
        _machine.Services["Orders"] = new InstalledService("Orders", "Orders", Path.Combine(folder, "App.exe"), ServiceState.Running, "Automatic", 4242, 0);
        var target = new DeployTarget("service", "Orders", [folder], Keep) { Service = "Orders" };

        var result = await Deploy(target, Package());

        Assert.True(result.Ok, result.Error);
        Assert.Equal(["stop Orders", "start Orders"], _machine.Did);
        Assert.Equal(("v2", "{of this place}"), (File.ReadAllText(Path.Combine(folder, "App.exe")), File.ReadAllText(Path.Combine(folder, "appsettings.json"))));
        Assert.True(File.Exists(Path.Combine(folder, "New.dll")));
        Assert.Equal(new MirrorResult(1, 1, 0, 0), result.Files);
        // What was there is kept, configuration and all, to go back to.
        Assert.Equal("v1", File.ReadAllText(Path.Combine(result.Backup!, "0", "App.exe")));
        Assert.True(File.Exists(Path.Combine(result.Backup!, "0", "appsettings.json")));
        Assert.Contains(result.Steps, step => step.StartsWith("replaced the files"));
    }

    [Fact]
    public async Task What_does_not_stop_is_not_touched()
    {
        var folder = Installed("orders", "v1");
        _machine.Services["Orders"] = new InstalledService("Orders", "Orders", Path.Combine(folder, "App.exe"), ServiceState.Running, "Automatic", 4242, 0);
        _machine.NeverStops = true;

        var result = await Deploy(new DeployTarget("service", "Orders", [folder], Keep) { Service = "Orders" }, Package());

        Assert.False(result.Ok);
        Assert.False(result.Replaced);
        Assert.Contains("did not stop", result.Error);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(folder, "App.exe")));
    }

    [Fact]
    public async Task The_groups_that_share_the_folder_stop_together_and_only_the_ones_that_were_on_come_back()
    {
        var folder = Installed("worker", "v1");
        _machine.Groups["Orders"] = (true, 2, 2, 2);
        _machine.Groups["OrdersNight"] = (false, 0, 0, 3);
        var target = new DeployTarget("worker", "Orders", [folder], Keep) { Groups = ["Orders", "OrdersNight"] };

        var result = await Deploy(target, Package());

        Assert.True(result.Ok, result.Error);
        Assert.Equal(["disable Orders", "enable Orders"], _machine.Did);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(folder, "App.exe")));
    }

    [Fact]
    public async Task A_group_that_does_not_stop_is_turned_on_again_and_left_as_it_was()
    {
        var folder = Installed("worker", "v1");
        _machine.Groups["Orders"] = (true, 2, 2, 2);
        _machine.NeverStops = true;

        var result = await Deploy(new DeployTarget("worker", "Orders", [folder], Keep) { Groups = ["Orders"] }, Package());

        Assert.False(result.Ok);
        Assert.Equal(["disable Orders", "enable Orders"], _machine.Did);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(folder, "App.exe")));
    }

    [Fact]
    public async Task What_was_replaced_and_does_not_come_up_is_said_to_be_replaced_and_failed()
    {
        var folder = Installed("worker", "v1");
        _machine.Groups["Orders"] = (true, 2, 2, 2);
        _machine.NeverStarts = true;

        var result = await Deploy(new DeployTarget("worker", "Orders", [folder], Keep) { Groups = ["Orders"] }, Package());

        Assert.False(result.Ok);
        Assert.True(result.Replaced);
        Assert.Contains("did not come up", result.Error);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(folder, "App.exe")));
        Assert.NotNull(result.Backup);
    }

    [Fact]
    public async Task Every_instance_of_an_application_is_replaced_with_its_sites_off_the_air()
    {
        var one = Installed(Path.Combine("api", "1"), "v1");
        var two = Installed(Path.Combine("api", "2"), "v1");
        var target = new DeployTarget("api", "api", [one, two], Keep) { Sites = ["Api 1", "Api 2"] };

        var result = await Deploy(target, Package());

        Assert.True(result.Ok, result.Error);
        Assert.Equal(["offline Api 1+Api 2", "online Api 1+Api 2"], _machine.Did);
        Assert.Equal(("v2", "v2"), (File.ReadAllText(Path.Combine(one, "App.exe")), File.ReadAllText(Path.Combine(two, "App.exe"))));
        Assert.Equal(new MirrorResult(2, 2, 0, 0), result.Files);
    }

    [Fact]
    public async Task A_bad_package_or_a_target_that_is_not_there_changes_nothing_and_stops_nothing()
    {
        var folder = Installed("orders", "v1");
        _machine.Services["Orders"] = new InstalledService("Orders", "Orders", Path.Combine(folder, "App.exe"), ServiceState.Running, "Automatic", 4242, 0);
        var target = new DeployTarget("service", "Orders", [folder], Keep) { Service = "Orders" };

        Assert.Contains("outside", (await Deploy(target, Package(bad: true))).Error);
        Assert.Contains("does not exist", (await Deploy(target with { Paths = [Path.Combine(_root, "nowhere")] }, Package())).Error);
        Assert.Contains("wrapper", (await Deploy(target with { Problem = "The service is run by a wrapper" }, Package())).Error);

        Assert.Empty(_machine.Did);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(folder, "App.exe")));
    }

    [Fact]
    public async Task What_a_package_replaced_is_put_back_from_the_copy_with_the_configuration_of_now()
    {
        var folder = Installed("orders", "v1");
        File.WriteAllText(Path.Combine(folder, "Old.dll"), "old");
        _machine.Services["Orders"] = new InstalledService("Orders", "Orders", Path.Combine(folder, "App.exe"), ServiceState.Running, "Automatic", 4242, 0);
        var target = new DeployTarget("service", "Orders", [folder], Keep) { Service = "Orders" };
        var applied = await Deploy(target, Package());
        Assert.False(File.Exists(Path.Combine(folder, "Old.dll")));
        // The configuration was changed after the package came in: that is of the environment and stays.
        File.WriteAllText(Path.Combine(folder, "appsettings.json"), "{changed since}");
        _machine.Did.Clear();

        var back = await _deployer.RevertAsync(target, applied.Backup!, "pkg-12345678", 1, CancellationToken.None);

        Assert.True(back.Ok, back.Error);
        Assert.Equal(["stop Orders", "start Orders"], _machine.Did);
        Assert.Equal(("v1", "old", "{changed since}"), (File.ReadAllText(Path.Combine(folder, "App.exe")), File.ReadAllText(Path.Combine(folder, "Old.dll")), File.ReadAllText(Path.Combine(folder, "appsettings.json"))));
        Assert.False(File.Exists(Path.Combine(folder, "New.dll")));
        // What was there before going back is kept too.
        Assert.Equal("v2", File.ReadAllText(Path.Combine(back.Backup!, "0", "App.exe")));

        // Only a copy made for this target, and one that is still there.
        Assert.Contains("not a copy kept for this target", (await _deployer.RevertAsync(target, folder, "pkg-12345678", 1, CancellationToken.None)).Error);
        Assert.Contains("not kept any more", (await _deployer.RevertAsync(target, applied.Backup! + "-gone", "pkg-12345678", 1, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task A_group_is_only_up_again_when_the_supervisor_has_turned_it_on()
    {
        var folder = Installed("worker", "v1");
        _machine.Groups["Orders"] = (true, 2, 2, 2);
        // Asked to be on, the supervisor still shows it off, as it does until it reads its file again: that is not up.
        _machine.SlowToEnable = true;

        var result = await Deploy(new DeployTarget("worker", "Orders", [folder], Keep) { Groups = ["Orders"] }, Package());

        Assert.False(result.Ok);
        Assert.Contains("did not come up", result.Error);
    }

    [Fact]
    public async Task What_does_not_stop_is_ended_by_force_only_when_that_was_asked_for()
    {
        var folder = Installed("stuck", "v1");
        _machine.Services["Orders"] = new InstalledService("Orders", "Orders", Path.Combine(folder, "App.exe"), ServiceState.Running, "Automatic", 2568, 0);
        _machine.NeverStops = true;
        _machine.Stuck.Add(2568);
        var service = new DeployTarget("service", "Orders", [folder], Keep) { Service = "Orders" };

        var left = await _deployer.DeployAsync(service, Package(), "pkg-12345678", 1, 3, CancellationToken.None);
        Assert.False(left.Ok);
        Assert.Contains("process 2568 is still there", left.Error);
        Assert.Empty(_machine.Ended);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(folder, "App.exe")));

        var forced = await _deployer.DeployAsync(service, Package(), "pkg-12345678", 1, 3, CancellationToken.None, force: true);
        Assert.True(forced.Ok, forced.Error);
        Assert.Equal([2568], _machine.Ended);
        Assert.Contains(forced.Steps, step => step.Contains("ended by force") && step.Contains("2568"));
        Assert.Equal("v2", File.ReadAllText(Path.Combine(folder, "App.exe")));

        // An application of the web server whose process lingers after it went off the air.
        var site = Installed("site", "v1");
        _machine.Stuck.Add(7001);
        var api = new DeployTarget("api", "Orders", [site], Keep) { Sites = ["Orders 1"] };
        var waiting = await _deployer.DeployAsync(api, Package(), "pkg-12345678", 2, 3, CancellationToken.None);
        Assert.False(waiting.Ok);
        Assert.Equal("online Orders 1", _machine.Did.Last());
        Assert.True((await _deployer.DeployAsync(api, Package(), "pkg-12345678", 2, 3, CancellationToken.None, force: true)).Ok);
        Assert.Equal([2568, 7001], _machine.Ended);

        // One that cannot be ended leaves everything as it was.
        _machine.Stuck.Add(7002);
        _machine.CannotBeEnded = true;
        var refused = await _deployer.DeployAsync(api, Package("v3"), "pkg-12345678", 3, 3, CancellationToken.None, force: true);
        Assert.Contains("could not be ended by force", refused.Error);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(site, "App.exe")));
    }

    [Fact]
    public async Task Only_the_latest_copies_are_kept()
    {
        var folder = Installed("site", "v1");
        var target = new DeployTarget("frontend", "orders", [folder], []);

        for (var version = 2; version <= 5; version++)
        {
            Assert.True((await Deploy(target, Package("v" + version))).Ok);
            await Task.Delay(1100);
        }

        Assert.Equal(2, Directory.GetDirectories(Path.Combine(_root, "transport", "backup", "frontend", "orders")).Length);
        // A module of the web application has nothing to stop.
        Assert.Empty(_machine.Did);
    }
}

public sealed class TargetCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("transport-targets-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Make(params string[] parts)
    {
        var folder = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void The_targets_are_worked_out_from_what_the_service_already_knows()
    {
        var workers = Make("workers", "orders");
        var machine = new PretendMachine();
        machine.Services["Sockets"] = new InstalledService("Sockets", "Sockets", Path.Combine(Make("services", "sockets"), "node.exe"), ServiceState.Running, "Automatic", 1, 0);
        machine.Services["Wrapped"] = new InstalledService("Wrapped", "Wrapped", Path.Combine(Make("tools"), "nssm.exe"), ServiceState.Running, "Automatic", 2, 0);
        machine.WebSites.AddRange([
            new WebSite("Api Orders 1", 9014, Make("www", "api", "Orders", "1")), new WebSite("Api Orders 2", 9015, Make("www", "api", "Orders", "2")),
            new WebSite("Api Stock 1", 9024, Make("www", "api", "Stock")), new WebSite("Api Stock 2", 9025, Path.Combine(_root, "www", "api", "Stock")),
            new WebSite("Web", 80, Make("www", "app"))]);
        var config = ConfigReader.Parse($$"""
            { "ZapMQHost": "localhost", "ZapMQPort": 5679,
              "WorkerGroups": [
                { "Name": "Orders", "ApplicationFullPath": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(workers, "Orders.exe"))}}, "TotalWorkers": 2 },
                { "Name": "OrdersNight", "ApplicationFullPath": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(workers, "Orders.exe"))}}, "TotalWorkers": 1 } ],
              "Services": { "Items": [ { "Name": "Sockets" }, { "Name": "Wrapped" }, { "Name": "Missing" } ] },
              "Transport": { "Targets": [ { "Kind": "service", "Name": "Wrapped", "Paths": [ {{System.Text.Json.JsonSerializer.Serialize(Make("services", "wrapped"))}} ], "Keep": [ "*.ini" ] } ] } }
            """);
        var web = new FrontendWatcher.App("App", Path.Combine(_root, "www", "app"), null, null, DateTimeOffset.UtcNow, null,
            [new FrontendWatcher.Module("orders", "/mfe/orders/remoteEntry.js", "Up", true, 200, null, "Orders", 7, [new FrontendWatcher.VersionEntry("1.4.0", null, [])], null, 3, 100)], []);
        var catalog = new TargetCatalog(() => config, machine, machine, () => [web]);

        var targets = catalog.All().ToDictionary(target => target.Kind + ":" + target.Name);

        Assert.Equal(["api:Orders", "api:Stock", "frontend:orders", "service:Missing", "service:Sockets", "service:Wrapped", "worker:Orders", "worker:OrdersNight"], targets.Keys.Order(StringComparer.Ordinal));
        // The two groups run the same program: they go together.
        Assert.Equal(["Orders", "OrdersNight"], targets["worker:Orders"].Groups);
        // The instances of an application are one target with every folder; the site of the web application is not an api.
        Assert.Equal(2, targets["api:Orders"].Paths.Count);
        Assert.Equal(["Api Orders 1", "Api Orders 2"], targets["api:Orders"].Sites);
        Assert.Single(targets["api:Stock"].Paths);
        Assert.Equal(["Api Stock 1", "Api Stock 2"], targets["api:Stock"].Sites);
        Assert.EndsWith(Path.Combine("mfe", "orders"), targets["frontend:orders"].Paths.Single());
        Assert.Equal("1.4.0", targets["frontend:orders"].Version);
        Assert.Contains("not installed", targets["service:Missing"].Problem);
        // A wrapper says nothing of where the program is; the configuration does.
        Assert.Null(targets["service:Wrapped"].Problem);
        Assert.EndsWith(Path.Combine("services", "wrapped"), targets["service:Wrapped"].Paths.Single());
        Assert.Equal(["*.ini"], targets["service:Wrapped"].Keep);
        Assert.Equal(TransportConfig.DefaultKeep, targets["service:Sockets"].Keep);
        Assert.NotNull(catalog.Find("API", "orders"));
        Assert.Null(catalog.Find("api", "Nothing"));
    }
}

/// <summary>
/// The carrying of files through the administration contract, with the service running.
/// </summary>
[Collection("processes")]
public class TransportAdminTests
{
    [Fact]
    public async Task What_runs_is_packed_and_what_a_package_brings_replaces_it()
    {
        await using var rig = new Rig();
        var orders = Path.Combine(rig.Directory, "services", "orders");
        var sockets = Path.Combine(rig.Directory, "services", "sockets");
        foreach (var (folder, version) in new[] { (orders, "v2"), (sockets, "v1") })
        {
            System.IO.Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "App.exe"), version);
            File.WriteAllText(Path.Combine(folder, "appsettings.json"), "{of " + Path.GetFileName(folder) + "}");
        }
        var machine = new PretendMachine();
        machine.Services["Orders"] = new InstalledService("Orders", "Orders", Path.Combine(orders, "App.exe"), ServiceState.Running, "Automatic", 4242, 0);
        machine.Services["Sockets"] = new InstalledService("Sockets", "Sockets", Path.Combine(sockets, "App.exe"), ServiceState.Running, "Automatic", 4243, 0);
        rig.Services = machine;
        rig.Register = services => services.AddSingleton<IWebServer>(machine);
        rig.ServicesJson = """{ "Items": [ { "Name": "Orders" }, { "Name": "Sockets" } ] }""";
        rig.WriteConfig();
        await rig.StartBrokerAsync();
        await rig.StartServiceAsync();

        var targets = (await rig.CommandAsync("TransportTargets"))!["Targets"]!;
        Assert.Equal(["service:Orders", "service:Sockets"], targets.Select(target => $"{target["Kind"]}:{target["Name"]}"));

        // What Orders is running, packed: without its configuration.
        var captured = await rig.CommandAsync("TransportCapture", request => { request["Kind"] = "service"; request["Name"] = "Orders"; });
        Assert.True(captured!.Value<bool>("Ok"), captured.ToString());
        Assert.Equal(["App.exe"], captured["Files"]!.Select(file => (string?)file["Path"]));
        var zip = captured.Value<string>("File")!;
        Assert.True(File.Exists(zip));

        // The same files put where Sockets is, as a package would.
        var before = await rig.CommandAsync("TransportTarget", request => { request["Kind"] = "service"; request["Name"] = "Sockets"; });
        Assert.NotEqual((string?)captured["Files"]![0]!["Sha256"], (string?)before!["Files"]![0]!["Sha256"]);
        var deployed = await rig.CommandAsync("TransportDeploy", request =>
        {
            request["Package"] = "p-1"; request["Item"] = 1; request["Kind"] = "service"; request["Name"] = "Sockets"; request["File"] = zip; request["By"] = "ana";
        });

        Assert.True(deployed!.Value<bool>("Applied"), deployed.ToString());
        Assert.Equal((0, 1, 0), (deployed.Value<int>("Added"), deployed.Value<int>("Changed"), deployed.Value<int>("Removed")));
        Assert.Equal(("v2", "{of sockets}"), (File.ReadAllText(Path.Combine(sockets, "App.exe")), File.ReadAllText(Path.Combine(sockets, "appsettings.json"))));
        Assert.Equal(["stop Sockets", "start Sockets"], machine.Did);
        Assert.StartsWith(Path.Combine(rig.Directory, "transport", "backup", "service", "Sockets"), deployed.Value<string>("Backup"));

        // A new version left in the inbox, under the kind and the name of the target, is taken from there.
        var inbox = (await rig.CommandAsync("TransportTargets"))!.Value<string>("Inbox")!;
        Assert.True(System.IO.Directory.Exists(Path.Combine(inbox, "api")));
        var left = Path.Combine(inbox, "service", "orders");
        System.IO.Directory.CreateDirectory(Path.Combine(left, "lib"));
        File.WriteAllText(Path.Combine(left, "App.exe"), "v3");
        File.WriteAllText(Path.Combine(left, "lib", "New.dll"), "new");
        File.WriteAllText(Path.Combine(left, "appsettings.json"), "{of the developer}");
        System.IO.Directory.CreateDirectory(Path.Combine(inbox, "service", "Typo"));
        var listed = await rig.CommandAsync("TransportTargets");
        Assert.Equal(3, listed!["Targets"]!.Single(target => (string?)target["Name"] == "Orders")["Incoming"]!.Value<int>("Files"));
        Assert.Null(listed["Targets"]!.Single(target => (string?)target["Name"] == "Sockets")["Incoming"]);
        Assert.EndsWith("Typo", (string?)listed["Unmatched"]!.Single());
        // Just written: it may still be being copied.
        var early = await rig.CommandAsync("TransportCapture", request => { request["Kind"] = "service"; request["Name"] = "Orders"; request["Incoming"] = true; });
        Assert.Equal("invalid-state", early!["Error"]!.Value<string>("Code"));
        foreach (var file in System.IO.Directory.EnumerateFiles(left, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
        var taken = await rig.CommandAsync("TransportCapture", request => { request["Kind"] = "service"; request["Name"] = "Orders"; request["Incoming"] = true; });
        Assert.True(taken!.Value<bool>("Ok"), taken.ToString());
        using (var incoming = ZipFile.OpenRead(taken.Value<string>("File")!))
            Assert.Equal(["App.exe", "lib/New.dll"], incoming.Entries.Select(entry => entry.FullName).Order());
        // Taken, it is no longer there; and what Orders runs was not touched.
        Assert.False(System.IO.Directory.Exists(left));
        Assert.Equal("v2", File.ReadAllText(Path.Combine(orders, "App.exe")));
        Assert.Equal("not-found", (await rig.CommandAsync("TransportCapture", request => { request["Kind"] = "service"; request["Name"] = "Orders"; request["Incoming"] = true; }))!["Error"]!.Value<string>("Code"));

        // The files of the environment can be read and written from the panel; the others cannot.
        var files = await rig.CommandAsync("TransportFiles", request => { request["Kind"] = "service"; request["Name"] = "Sockets"; });
        Assert.Equal(["appsettings.json"], files!["Files"]!.Select(file => (string?)file["Path"]));
        var opened = await rig.CommandAsync("TransportFile", request => { request["Kind"] = "service"; request["Name"] = "Sockets"; request["Path"] = "appsettings.json"; });
        Assert.Equal("{of sockets}", opened!.Value<string>("Content"));
        JObject Write(string path, string content, string? sha = null, bool restart = false) => rig.CommandAsync("SetTransportFile", request =>
        {
            request["Kind"] = "service"; request["Name"] = "Sockets"; request["Path"] = path; request["Content"] = content; request["Sha256"] = sha; request["Restart"] = restart; request["By"] = "ana";
        }).GetAwaiter().GetResult()!;
        // Not JSON, not written; not the version that was opened, not written; not a file of the environment, not reached.
        Assert.Equal("invalid-request", Write("appsettings.json", "{ broken", opened.Value<string>("Sha256"))["Error"]!.Value<string>("Code"));
        Assert.Equal("invalid-state", Write("appsettings.json", "{ \"a\": 1 }", "0000")["Error"]!.Value<string>("Code"));
        Assert.Equal("not-found", Write("App.exe", "x")["Error"]!.Value<string>("Code"));
        Assert.Equal("not-found", Write("../../ConfigWorkers.json", "{}")["Error"]!.Value<string>("Code"));
        Assert.Equal("{of sockets}", File.ReadAllText(Path.Combine(sockets, "appsettings.json")));
        machine.Did.Clear();
        var written = Write("appsettings.json", "{ \"Port\": 3001 }", opened.Value<string>("Sha256"), restart: true);
        Assert.True(written.Value<bool>("Ok"), written.ToString());
        Assert.Equal("{ \"Port\": 3001 }", File.ReadAllText(Path.Combine(sockets, "appsettings.json")));
        Assert.True(written.Value<bool>("Restarted"));
        Assert.Equal(["stop Sockets", "start Sockets"], machine.Did);
        // What was there before is kept.
        Assert.Equal("{of sockets}", File.ReadAllText(written.Value<string>("Backup")!));

        // The inbox of a kind can be put somewhere else, and the folder is made.
        var elsewhere = Path.Combine(rig.Directory, "drop", "apis");
        var settings = await rig.CommandAsync("SetTransportInboxes", request => request["Inboxes"] = new JObject { ["api"] = elsewhere });
        Assert.True(settings!.Value<bool>("Ok"), settings.ToString());
        Assert.True(System.IO.Directory.Exists(elsewhere));
        Assert.True(await Rig.Eventually(async () => (await rig.CommandAsync("TransportSettings"))!["Inboxes"]!["api"]!.Value<string>("Path") == elsewhere));
        var now = (await rig.CommandAsync("TransportSettings"))!["Inboxes"]!;
        Assert.Equal((true, false), (now["api"]!.Value<bool>("Said"), now["worker"]!.Value<bool>("Said")));
        Assert.Equal("invalid-request", (await rig.CommandAsync("SetTransportInboxes", request => request["Inboxes"] = new JObject { ["api"] = "relative/folder" }))!["Error"]!.Value<string>("Code"));
        Assert.Equal("invalid-request", (await rig.CommandAsync("SetTransportInboxes", request => request["Inboxes"] = new JObject { ["database"] = elsewhere }))!["Error"]!.Value<string>("Code"));

        Assert.Equal("not-found", (await rig.CommandAsync("TransportTarget", request => { request["Kind"] = "api"; request["Name"] = "Nothing"; }))!["Error"]!.Value<string>("Code"));
        Assert.Equal("invalid-request", (await rig.CommandAsync("TransportDeploy", request => { request["Package"] = "p"; request["Item"] = 1; request["Kind"] = "service"; request["Name"] = "Sockets"; request["File"] = "/nowhere.zip"; }))!["Error"]!.Value<string>("Code"));
    }
}
