using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace WorkerControl.Service.Tests;

/// <summary>
/// The configuration of the reverse proxy through the administration contract. The proxy is a
/// folder made for the test and a pretend executable that refuses a configuration with a
/// certain word in it.
/// </summary>
[Collection("processes")]
public class ProxyTests
{
    private sealed class PretendProxy(string root) : IProxyTool
    {
        public List<string> Asked { get; } = [];

        public (int Code, string Output) Run(string executable, string arguments, string directory)
        {
            Asked.Add(arguments.Split(' ')[0] == "-s" ? "reload" : "test");
            var bad = System.IO.Directory.EnumerateFiles(Path.Combine(root, "conf"), "*.conf", SearchOption.AllDirectories).Any(file => File.ReadAllText(file).Contains("BROKEN"));
            return bad ? (1, "nginx: [emerg] unknown directive \"BROKEN\" in conf/sites/orders.conf:2") : (0, "nginx: configuration file test is successful");
        }
    }

    [Fact]
    public async Task The_configuration_is_listed_read_written_only_when_the_proxy_takes_it_and_read_again()
    {
        await using var rig = new Rig();
        var root = Path.Combine(rig.Directory, "nginx");
        System.IO.Directory.CreateDirectory(Path.Combine(root, "conf", "sites"));
        System.IO.Directory.CreateDirectory(Path.Combine(root, "logs"));
        File.WriteAllText(Path.Combine(root, "nginx.exe"), "");
        File.WriteAllText(Path.Combine(root, "conf", "mime.types"), "types { }");
        File.WriteAllText(Path.Combine(root, "conf", "nginx.conf"), "http {\n  include mime.types;\n  # include old/*.conf;\n  include sites/*.conf;\n}\n");
        File.WriteAllText(Path.Combine(root, "conf", "sites", "orders.conf"), "upstream orders { server 127.0.0.1:9014; }\n");
        File.WriteAllText(Path.Combine(root, "conf", "sites", "notes.txt"), "not configuration");
        var proxy = new PretendProxy(root);
        rig.Register = services => services.AddSingleton<IProxyTool>(proxy);
        // Nobody says where the proxy is: it is where its access log is.
        rig.ExtraJson = $", \"Traffic\": {{ \"AccessLog\": {System.Text.Json.JsonSerializer.Serialize(Path.Combine(root, "logs", "access.log"))} }}";
        rig.WriteConfig();
        await rig.StartBrokerAsync();
        await rig.StartServiceAsync();

        var described = await rig.CommandAsync("Proxy");
        Assert.True(described!.Value<bool>("Configured"), described.ToString());
        Assert.Equal(["nginx.conf", "sites/orders.conf"], described["Files"]!.Select(file => (string?)file["Path"]));
        Assert.EndsWith("nginx.exe", described.Value<string>("Executable"));

        var opened = await rig.CommandAsync("ProxyFile", request => request["Path"] = "sites/orders.conf");
        Assert.StartsWith("upstream orders", opened!.Value<string>("Content"));
        JObject Write(string path, string content, string? sha = null) => rig.CommandAsync("SetProxyFile", request => { request["Path"] = path; request["Content"] = content; request["Sha256"] = sha; }).GetAwaiter().GetResult()!;

        // What the proxy refuses is not left on disk, and what it said comes back.
        var refused = Write("sites/orders.conf", "upstream orders {\n  BROKEN;\n}\n", opened.Value<string>("Sha256"));
        Assert.Equal((true, false), (refused.Value<bool>("Ok"), refused.Value<bool>("Saved")));
        Assert.Contains("unknown directive", refused.Value<string>("Output"));
        Assert.StartsWith("upstream orders { server", File.ReadAllText(Path.Combine(root, "conf", "sites", "orders.conf")));

        var saved = Write("sites/orders.conf", "upstream orders { server 127.0.0.1:9014; server 127.0.0.1:9015; }\n", opened.Value<string>("Sha256"));
        Assert.True(saved.Value<bool>("Saved"), saved.ToString());
        Assert.Contains("9015", File.ReadAllText(Path.Combine(root, "conf", "sites", "orders.conf")));
        Assert.StartsWith("upstream orders { server 127.0.0.1:9014; }", File.ReadAllText(saved.Value<string>("Backup")!));
        // Not the version that was opened; not a file of the configuration; not a file somewhere else.
        Assert.Equal("invalid-state", Write("sites/orders.conf", "x", opened.Value<string>("Sha256"))["Error"]!.Value<string>("Code"));
        Assert.Equal("not-found", Write("mime.types", "x")["Error"]!.Value<string>("Code"));
        Assert.Equal("not-found", Write("../nginx.exe", "x")["Error"]!.Value<string>("Code"));

        proxy.Asked.Clear();
        var reloaded = await rig.CommandAsync("ProxyReload");
        Assert.True(reloaded!.Value<bool>("Reloaded"));
        Assert.Equal(["test", "reload"], proxy.Asked);
        Assert.True((await rig.CommandAsync("ProxyTest"))!.Value<bool>("Valid"));
        // Without a service that is known, there is nothing to restart.
        Assert.Equal("invalid-state", (await rig.CommandAsync("ProxyRestart"))!["Error"]!.Value<string>("Code"));
    }

    [Fact]
    public async Task Without_a_proxy_that_is_known_it_says_so()
    {
        await using var rig = await Rig.StartAsync();

        Assert.False((await rig.CommandAsync("Proxy"))!.Value<bool>("Configured"));
        Assert.Equal("not-configured", (await rig.CommandAsync("ProxyTest"))!["Error"]!.Value<string>("Code"));
    }
}
