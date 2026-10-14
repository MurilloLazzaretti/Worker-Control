using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;
using WorkerControl.Service.Traffic;
using WorkerControl.Service.Transport;

namespace WorkerControl.Service.Tests;

/// <summary>
/// A folder of files that are only downloaded: each file is replaced on its own, its version
/// written beside it, and put back as it was when asked.
/// </summary>
public sealed class DownloadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("downloads-").FullName;
    private readonly PretendMachine _machine = new();
    private readonly string _folder;
    private readonly TargetCatalog _catalog;
    private readonly Deployer _deployer;

    public DownloadTests()
    {
        _folder = Path.Combine(_root, "www", "APKs");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "app-shop.apk"), "shop v1");
        File.WriteAllText(Path.Combine(_folder, "web.config"), "<configuration />");
        _machine.WebSites.Add(new WebSite("Download APKs", 9095, _folder));
        _machine.WebSites.Add(new WebSite("Api Orders 1", 9014, Directory.CreateDirectory(Path.Combine(_root, "www", "Orders")).FullName));
        var config = ConfigReader.Parse($$"""
            { "ZapMQHost": "localhost", "ZapMQPort": 5679, "WorkerGroups": [],
              "Downloads": [ { "Name": "APKs", "Root": {{System.Text.Json.JsonSerializer.Serialize(_folder)}} } ] }
            """);
        _catalog = new TargetCatalog(() => config, _machine, _machine, () => []);
        _deployer = new Deployer(_machine, _machine, _machine, TimeProvider.System, Path.Combine(_root, "transport"), NullLogger.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Zip(string name, string text)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry(name).Open()))
            writer.Write(text);
        return path;
    }

    private JArray Versions() => (JArray)JObject.Parse(File.ReadAllText(Path.Combine(_folder, "versions.json")))["apps"]!;

    [Fact]
    public async Task Each_file_is_a_target_replaced_on_its_own_with_its_version_and_put_back_when_asked()
    {
        // The site only serves the files: it is not an application, its files are.
        var targets = _catalog.All();
        Assert.DoesNotContain(targets, target => target.Kind == "api" && target.Name == "APKs");
        var shop = Assert.Single(targets, target => target.Kind == "download");
        Assert.Equal(("app-shop.apk", "app-shop.apk"), (shop.Name, shop.File));

        var replaced = await _deployer.DeployAsync(shop, Zip("app-shop.apk", "shop v2"), "pkg-12345678", 1, 3, CancellationToken.None, version: "2.0.0");

        Assert.True(replaced.Ok, replaced.Error);
        Assert.Equal("shop v2", File.ReadAllText(Path.Combine(_folder, "app-shop.apk")));
        Assert.Equal("<configuration />", File.ReadAllText(Path.Combine(_folder, "web.config")));
        var written = (JObject)Versions().Single();
        Assert.Equal(("app-shop", "app-shop.apk", "2.0.0", 7L), ((string?)written["name"], (string?)written["file"], (string?)written["version"], (long)written["size"]!));
        Assert.Equal("2.0.0", _catalog.Find("download", "app-shop.apk")!.Version);

        // A file that is not there yet is added the first time it comes.
        var fresh = _catalog.Find("download", "app-stock.apk");
        Assert.NotNull(fresh);
        var added = await _deployer.DeployAsync(fresh!, Zip("app-stock.apk", "stock v1"), "pkg-abcdefgh", 1, 3, CancellationToken.None, version: "1.0.0");
        Assert.True(added.Ok, added.Error);
        Assert.Equal(["app-shop.apk", "app-stock.apk"], Versions().Select(app => (string?)app["file"]));
        Assert.Null(_catalog.Find("download", "../outside.apk"));
        Assert.Null(_catalog.Find("download", "notes.txt"));
        Assert.Contains("only it", (await _deployer.DeployAsync(shop, Zip("other.apk", "x"), "pkg-x", 1, 3, CancellationToken.None)).Error);

        // Going back: the replaced one returns with its version, the added one goes away.
        var back = await _deployer.RevertAsync(shop, replaced.Backup!, "pkg-12345678", 1, CancellationToken.None);
        Assert.True(back.Ok, back.Error);
        Assert.Equal("shop v1", File.ReadAllText(Path.Combine(_folder, "app-shop.apk")));
        Assert.Null((string?)Versions().Single(app => (string?)app["file"] == "app-shop.apk")["version"]);
        var gone = await _deployer.RevertAsync(_catalog.Find("download", "app-stock.apk")!, added.Backup!, "pkg-abcdefgh", 1, CancellationToken.None);
        Assert.True(gone.Ok, gone.Error);
        Assert.False(File.Exists(Path.Combine(_folder, "app-stock.apk")));
        Assert.DoesNotContain(Versions(), app => (string?)app["file"] == "app-stock.apk");
    }
}
