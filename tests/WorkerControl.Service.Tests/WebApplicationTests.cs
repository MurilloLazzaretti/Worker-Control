using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace WorkerControl.Service.Tests;

/// <summary>
/// Two web applications registered by the same name of the DNS, a monolith and one of micro
/// frontends: only one answers by it, and which one is changed through a file of the proxy.
/// </summary>
[Collection("processes")]
public class WebApplicationTests
{
    private sealed class PretendProxy(string root) : IProxyTool
    {
        public List<string> Asked { get; } = [];

        public (int Code, string Output) Run(string executable, string arguments, string directory)
        {
            Asked.Add(arguments.Split(' ')[0] == "-s" ? "reload" : "test");
            var bad = System.IO.Directory.EnumerateFiles(Path.Combine(root, "conf"), "*.conf", SearchOption.AllDirectories).Any(file => File.ReadAllText(file).Contains("BROKEN"));
            return bad ? (1, "nginx: [emerg] unknown directive \"BROKEN\"") : (0, "nginx: configuration file test is successful");
        }
    }

    private static void Write(string folder, string path, string text)
    {
        var file = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    private static string Json(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    private static async Task<JObject> App(Rig rig, string name, Func<JObject, bool> until)
    {
        JObject? app = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            app = ((JArray)(await rig.CommandAsync("Frontends"))!["Frontends"]!).OfType<JObject>().FirstOrDefault(item => (string?)item["Name"] == name);
            if (app is not null && until(app))
                return app;
            await Task.Delay(100);
        }
        throw new TimeoutException($"{name} did not get there: {app}");
    }

    private static (Rig Rig, string Proxy, string Portal, string Shell, PretendProxy Tool) Prepare()
    {
        var rig = new Rig();
        var proxy = Path.Combine(rig.Directory, "nginx");
        Write(proxy, "nginx.exe", "");
        Write(proxy, "conf/nginx.conf", "http {\n  server {\n    listen 80;\n    server_name web.test;\n    include zapmq/web-web.test.conf;\n  }\n}\n");
        var portal = Path.Combine(rig.Directory, "www", "portal");
        Write(portal, "index.html", "<html>monolith</html>");
        var shell = Path.Combine(rig.Directory, "www", "portal-mfe");
        var tool = new PretendProxy(proxy);
        rig.Register = services => services.AddSingleton<IProxyTool>(tool);
        rig.ExtraJson = $$"""
            , "Proxy": { "Config": {{Json(Path.Combine(proxy, "conf", "nginx.conf"))}} },
            "Frontends": [
              { "Name": "Portal", "Root": {{Json(portal)}}, "Mode": "monolith", "Address": "Web.Test" },
              { "Name": "Portal-MFE", "Root": {{Json(shell)}}, "Mode": "mfe", "Address": "web.test", "Active": false }
            ]
            """;
        rig.WriteConfig();
        return (rig, proxy, portal, shell, tool);
    }

    [Fact]
    public async Task The_application_that_answers_by_a_name_is_swapped_and_can_be_put_out_of_the_air()
    {
        var (rig, proxy, portal, shell, tool) = Prepare();
        await using var _ = rig;
        await rig.StartBrokerAsync();
        await rig.StartServiceAsync();
        var site = Path.Combine(proxy, "conf", "zapmq", "web-web.test.conf");

        var monolith = await App(rig, "Portal", app => app["Address"] is not null);
        Assert.Equal(("web.test", true, "files"), ((string?)monolith["Address"], (bool)monolith["Active"]!, (string?)monolith["Serve"]));
        var before = await rig.CommandAsync("WebSite", request => request["Address"] = "web.test");
        Assert.Equal((false, false, "include zapmq/web-web.test.conf;"), (before!.Value<bool>("Exists"), before.Value<bool>("Included"), before.Value<string>("Include")));

        JObject Switch(string command, string name) => rig.CommandAsync(command, request => { request["Name"] = name; request["By"] = "ana"; }).GetAwaiter().GetResult()!;

        // Nothing to serve yet: its files have not been brought.
        Assert.Equal("invalid-state", Switch("WebActivate", "Portal-MFE")["Error"]!.Value<string>("Code"));
        Write(shell, "index.html", "<html>shell</html>");

        tool.Asked.Clear();
        var activated = Switch("WebActivate", "Portal-MFE");
        Assert.True(activated.Value<bool>("Switched"), activated.ToString());
        Assert.Equal((true, true), (activated.Value<bool>("Included"), activated.Value<bool>("Reloaded")));
        Assert.Equal(["Portal"], activated["Deactivated"]!.Select(item => (string?)item));
        Assert.Equal(["test", "reload"], tool.Asked);
        var text = File.ReadAllText(site);
        Assert.Contains($"root \"{shell.Replace('\\', '/')}\";", text);
        Assert.Contains("/mfe/", text);
        Assert.Contains("try_files $uri $uri/ /index.html;", text);
        await App(rig, "Portal-MFE", app => (bool?)app["Active"] == true);
        Assert.False((bool)(await App(rig, "Portal", _ => true))["Active"]!);

        // What the proxy refuses changes nothing: neither the file nor which one answers.
        Write(proxy, "conf/other.conf", "BROKEN;");
        var refused = Switch("WebActivate", "Portal");
        Assert.False(refused.Value<bool>("Switched"), refused.ToString());
        Assert.Contains("unknown directive", refused.Value<string>("Output"));
        Assert.Equal(text, File.ReadAllText(site));
        File.Delete(Path.Combine(proxy, "conf", "other.conf"));

        var back = Switch("WebActivate", "Portal");
        Assert.True(back.Value<bool>("Switched"), back.ToString());
        Assert.Contains($"root \"{portal.Replace('\\', '/')}\";", File.ReadAllText(site));
        Assert.DoesNotContain("/mfe/", File.ReadAllText(site));
        await App(rig, "Portal", app => (bool?)app["Active"] == true);

        var off = Switch("WebDeactivate", "Portal");
        Assert.True(off.Value<bool>("Switched"), off.ToString());
        Assert.Contains("return 503", File.ReadAllText(site));
        await App(rig, "Portal", app => (bool?)app["Active"] == false);
        Assert.False((bool)(await App(rig, "Portal-MFE", _ => true))["Active"]!);
        Assert.Equal("invalid-state", Switch("WebDeactivate", "Portal")["Error"]!.Value<string>("Code"));
    }

    [Fact]
    public async Task An_application_keeps_its_kind_and_two_never_answer_by_the_same_name()
    {
        var (rig, _, _, _, _) = Prepare();
        await using var _ = rig;
        await rig.StartBrokerAsync();
        await rig.StartServiceAsync();
        await App(rig, "Portal", _ => true);

        async Task<JObject> Change(Action<JArray> change)
        {
            var environment = (JObject)(await rig.CommandAsync("GetEnvironment"))!["Config"]!;
            change((JArray)environment["Frontends"]!);
            return (await rig.CommandAsync("SetEnvironment", request => request["Config"] = environment))!;
        }

        var kind = await Change(list => list[0]!["Mode"] = "mfe");
        Assert.Equal("invalid-config", kind["Error"]!.Value<string>("Code"));
        Assert.Contains("stays one", kind["Error"]!.Value<string>("Message"));

        var both = await Change(list => list[1]!["Active"] = true);
        Assert.Equal("invalid-config", both["Error"]!.Value<string>("Code"));
        Assert.Contains("both active at web.test", both["Error"]!.Value<string>("Message"));

        // Another name, another application: that is how one becomes the other.
        var other = await Change(list => list.Add(new JObject { ["Name"] = "Portal-2", ["Root"] = list[0]!["Root"], ["Mode"] = "mfe", ["Address"] = "other.test" }));
        Assert.True(other.Value<bool>("Ok"), other.ToString());
    }

    [Fact]
    public async Task The_first_load_of_micro_frontends_makes_their_folder()
    {
        var (rig, _, _, shell, _) = Prepare();
        await using var _ = rig;
        await rig.StartBrokerAsync();
        await rig.StartServiceAsync();
        await App(rig, "Portal-MFE", _ => true);

        var target = await rig.CommandAsync("TransportTarget", request => { request["Kind"] = "frontend"; request["Name"] = "Portal-MFE"; });
        Assert.Equal("mfe", (string?)target!["Target"]!["Mode"]);

        var zip = Path.Combine(rig.Directory, "mfe.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var (path, text) in new[]
                     {
                         ("index.html", "<html>shell</html>"), ("assets/mf.manifest.json", "{ \"orders\": \"/mfe/orders/remoteEntry.js\" }"),
                         ("mfe/orders/remoteEntry.js", "orders"), ("mfe/orders/version.json", "{ \"versions\": [ { \"version\": \"1.0.2\" } ] }")
                     })
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(text);
            }

        var deployed = await rig.CommandAsync("TransportDeploy", request =>
        {
            request["Package"] = "p-1"; request["Item"] = 1; request["Kind"] = "frontend"; request["Name"] = "Portal-MFE"; request["File"] = zip; request["By"] = "ana";
        });
        Assert.True(deployed!.Value<bool>("Applied"), deployed.ToString());
        Assert.Equal("orders", File.ReadAllText(Path.Combine(shell, "mfe", "orders", "remoteEntry.js")));
        var app = await App(rig, "Portal-MFE", item => item["Modules"] is JArray { Count: > 0 });
        Assert.Equal("orders", (string?)app["Modules"]![0]!["Name"]);
        // Brought, not activated: the monolith still answers.
        Assert.False((bool)app["Active"]!);
    }
}
