using System.IO.Compression;
using Newtonsoft.Json.Linq;

namespace WorkerControl.Service.Tests;

/// <summary>
/// A monolith replaced, in its own folder, by a whole application of micro frontends brought in a
/// package, and put back.
/// </summary>
[Collection("processes")]
public class FrontendModeTests
{
    private static string Read(string folder, string path) => File.ReadAllText(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)));

    private static void Write(string folder, string path, string text)
    {
        var file = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    private static async Task<JObject> App(Rig rig, string name, string mode)
    {
        JObject? app = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            app = ((JArray)(await rig.CommandAsync("Frontends"))!["Frontends"]!).OfType<JObject>().FirstOrDefault(item => (string?)item["Name"] == name);
            if ((string?)app?["Mode"] == mode)
                return app!;
            await Task.Delay(100);
        }
        throw new TimeoutException($"{name} is not {mode}: {app}");
    }

    [Fact]
    public async Task A_monolith_is_replaced_by_micro_frontends_and_put_back()
    {
        await using var rig = new Rig();
        var portal = Path.Combine(rig.Directory, "www", "portal");
        Write(portal, "index.html", "<html><base href=\"/\">monolith</html>");
        Write(portal, "main.js", "the monolith");
        Write(portal, "assets/env.json", "{ \"api\": \"http://qas/api\" }");
        Write(portal, "web.config", "<rewrite to index/>");
        rig.ExtraJson = $$""", "Frontends": [ { "Name": "Portal", "Root": {{System.Text.Json.JsonSerializer.Serialize(portal)}}, "Mode": "monolith" } ]""";
        rig.WriteConfig();
        await rig.StartBrokerAsync();
        await rig.StartServiceAsync();
        await App(rig, "Portal", "monolith");

        var target = await rig.CommandAsync("TransportTarget", request => { request["Kind"] = "frontend"; request["Name"] = "Portal"; });
        Assert.Equal("monolith", (string?)target!["Target"]!["Mode"]);

        // The whole application, as packed where it is made of micro frontends.
        var zip = Path.Combine(rig.Directory, "mfe.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var (path, text) in new[]
                     {
                         ("index.html", "<html><base href=\"/\">shell</html>"), ("main.js", "the shell"),
                         ("assets/mf.manifest.json", "{ \"orders\": \"/mfe/orders/remoteEntry.js\" }"),
                         ("mfe/orders/remoteEntry.js", "orders"), ("mfe/orders/version.json", "{ \"versions\": [ { \"version\": \"1.0.2\" } ] }")
                     })
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(text);
            }

        var deployed = await rig.CommandAsync("TransportDeploy", request =>
        {
            request["Package"] = "p-1"; request["Item"] = 1; request["Kind"] = "frontend"; request["Name"] = "Portal"; request["File"] = zip; request["Mode"] = "mfe"; request["By"] = "ana";
        });
        Assert.True(deployed!.Value<bool>("Applied"), deployed.ToString());
        Assert.Equal("mfe", (string?)deployed["Mode"]);
        // The files of the monolith went; those of the environment stayed.
        Assert.Equal(("the shell", "orders"), (Read(portal, "main.js"), Read(portal, "mfe/orders/remoteEntry.js")));
        Assert.Equal(("{ \"api\": \"http://qas/api\" }", "<rewrite to index/>"), (Read(portal, "assets/env.json"), Read(portal, "web.config")));
        // Registered as what it is now: its modules are known, each one a target of its own.
        var app = await App(rig, "Portal", "mfe");
        Assert.Equal("orders", (string?)app["Modules"]![0]!["Name"]);
        var targets = (await rig.CommandAsync("TransportTargets"))!["Targets"]!.Select(item => $"{item["Kind"]}:{item["Name"]}").ToList();
        Assert.Contains("frontend:orders", targets);

        // Back: the monolith, and registered as one again.
        var reverted = await rig.CommandAsync("TransportRevert", request =>
        {
            request["Package"] = "p-1"; request["Item"] = 1; request["Kind"] = "frontend"; request["Name"] = "Portal"; request["Backup"] = deployed.Value<string>("Backup");
            request["Mode"] = "monolith"; request["By"] = "ana";
        });
        Assert.True(reverted!.Value<bool>("Applied"), reverted.ToString());
        Assert.Equal("the monolith", Read(portal, "main.js"));
        Assert.False(File.Exists(Path.Combine(portal, "mfe", "orders", "remoteEntry.js")));
        await App(rig, "Portal", "monolith");
    }
}
