using Microsoft.Extensions.Logging.Abstractions;
using WorkerControl.Core;

namespace WorkerControl.Service.Tests;

/// <summary>
/// The micro frontends of a machine, read from a folder made for the test.
/// </summary>
public sealed class FrontendWatcherTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("frontend-test-").FullName;
    private readonly string _data = Directory.CreateTempSubdirectory("frontend-data-").FullName;
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly Dictionary<string, int?> _answers = [];
    private readonly List<(string Url, string? Host)> _asked = [];
    private readonly List<SupervisorEvent> _events = [];
    private DateTime _clock = new(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_data, recursive: true);
    }

    private FrontendWatcher Watcher(string? baseUrl = "http://app.test", string? host = null)
    {
        var watcher = new FrontendWatcher(_data, (url, asked) =>
        {
            _asked.Add((url, asked));
            return _answers.TryGetValue(url, out var status) ? (status, status is null ? "connection refused" : null) : (200, null);
        }, _time, NullLogger.Instance);
        watcher.Event += _events.Add;
        watcher.ApplyConfig([new FrontendConfig { Name = "App", Root = _root, BaseUrl = baseUrl, Host = host }]);
        return watcher;
    }

    private void Manifest(params string[] modules)
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(Path.Combine(_root, "assets", "mf.manifest.json"),
            "{" + string.Join(",", modules.Select(module => $"\"{module}\": \"/mfe/{module}/remoteEntry.js\"")) + "}");
    }

    /// <summary>
    /// Publishes a module: its entry file, a couple of others and what it says about itself.
    /// </summary>
    private void Publish(string module, string? version = null, int build = 1)
    {
        var folder = Path.Combine(_root, "mfe", module);
        Directory.CreateDirectory(folder);
        _clock = _clock.AddHours(1);
        File.WriteAllText(Path.Combine(folder, "remoteEntry.js"), "// entry " + build);
        File.SetLastWriteTimeUtc(Path.Combine(folder, "remoteEntry.js"), _clock);
        File.WriteAllText(Path.Combine(folder, "main.js"), new string('x', 1000));
        File.WriteAllText(Path.Combine(folder, "version.json"), version is null
            ? $$"""{ "nome": "{{module}}", "build": {{build}}, "versions": [] }"""
            : $$"""{ "nome": "Módulo {{module}}", "build": {{build}}, "versions": [ { "version": "{{version}}", "date": "07/10/2026", "descriptions": ["Mudou isto", "E aquilo"] }, { "version": "0.9.0", "date": "01/09/2026", "descriptions": [] } ] }""");
    }

    private static FrontendWatcher.Module Module(FrontendWatcher watcher, string name) =>
        watcher.Snapshot().Apps.Single().Modules.Single(module => module.Name == name);

    [Fact]
    public void Each_module_of_the_manifest_is_shown_with_what_it_says_about_itself()
    {
        Manifest("orders", "billing");
        Publish("orders", "1.2.0", build: 7);
        Publish("billing");
        var watcher = Watcher();

        watcher.Scan();

        var app = watcher.Snapshot().Apps.Single();
        Assert.Null(app.Problem);
        Assert.Equal(["billing", "orders"], app.Modules.Select(module => module.Name));
        var orders = Module(watcher, "orders");
        Assert.Equal("Up", orders.State);
        Assert.True(orders.Online);
        Assert.Equal("Módulo orders", orders.Title);
        Assert.Equal(7, orders.Build);
        Assert.Equal("1.2.0", orders.Versions[0].Version);
        Assert.Equal(["Mudou isto", "E aquilo"], orders.Versions[0].Descriptions);
        Assert.Equal(2, orders.Versions.Count);
        Assert.Equal(3, orders.Files);
        Assert.True(orders.Bytes > 1000);
        Assert.NotNull(orders.PublishedAt);
        // A module may say nothing about its version.
        Assert.Empty(Module(watcher, "billing").Versions);
        Assert.Contains(("http://app.test/mfe/orders/remoteEntry.js", null), _asked);
    }

    [Fact]
    public void The_first_look_is_one_entry_and_not_a_publication_of_every_module()
    {
        Manifest("orders", "billing");
        Publish("orders");
        Publish("billing");
        var watcher = Watcher();

        watcher.Scan();
        watcher.Scan();

        var first = Assert.Single(watcher.Snapshot().Publications);
        Assert.Equal("first-seen", first.Kind);
        Assert.Equal(2, first.Count);
        Assert.Empty(_events);
    }

    [Fact]
    public void A_module_whose_entry_file_changed_was_published()
    {
        Manifest("orders");
        Publish("orders", "1.0.0", build: 1);
        var watcher = Watcher();
        watcher.Scan();

        Publish("orders", "1.1.0", build: 2);
        watcher.Scan();

        var publication = watcher.Snapshot().Publications[0];
        Assert.Equal(("orders", "published", "1.0.0", "1.1.0", 1, 2), (publication.Module, publication.Kind, publication.FromVersion, publication.ToVersion, publication.FromBuild, publication.ToBuild));
        var raised = Assert.Single(_events);
        Assert.Equal(EventKind.FrontendPublished, raised.Kind);
        Assert.Equal("frontend:App", raised.Group);
        Assert.Equal("orders: published, version 1.0.0 → 1.1.0, build 1 → 2", raised.Detail);
    }

    [Fact]
    public void What_was_known_survives_a_restart_of_the_service()
    {
        Manifest("orders");
        Publish("orders", "1.0.0");
        Watcher().Scan();

        Publish("orders", "1.0.0", build: 2);
        var again = Watcher();
        again.Scan();

        Assert.Equal(["published", "first-seen"], again.Snapshot().Publications.Select(publication => publication.Kind));
    }

    [Fact]
    public void A_module_that_does_not_answer_is_down_and_said_so_once()
    {
        Manifest("orders");
        Publish("orders");
        var watcher = Watcher();
        watcher.Scan();

        _answers["http://app.test/mfe/orders/remoteEntry.js"] = 404;
        watcher.Scan();
        watcher.Scan();
        Assert.Equal("Down", Module(watcher, "orders").State);
        Assert.Equal("Answered 404", Module(watcher, "orders").Problem);

        _answers["http://app.test/mfe/orders/remoteEntry.js"] = 200;
        watcher.Scan();

        Assert.Equal([EventKind.FrontendDown, EventKind.FrontendUp], _events.Select(e => e.Kind));
        Assert.Equal("Up", Module(watcher, "orders").State);
    }

    [Fact]
    public void A_module_of_the_manifest_that_is_not_on_disk_is_incomplete_and_a_folder_out_of_it_is_pointed_out()
    {
        Manifest("orders", "ghost");
        Publish("orders");
        Publish("forgotten");
        var watcher = Watcher();

        watcher.Scan();

        Assert.Equal("Incomplete", Module(watcher, "ghost").State);
        Assert.Equal(["forgotten"], watcher.Snapshot().Apps.Single().Orphans);
    }

    [Fact]
    public void Without_an_address_nothing_is_asked_and_the_site_can_be_named_apart_from_it()
    {
        Manifest("orders");
        Publish("orders");

        var silent = Watcher(baseUrl: null);
        silent.Scan();
        Assert.Empty(_asked);
        Assert.Null(Module(silent, "orders").Online);
        Assert.Equal("Up", Module(silent, "orders").State);

        Watcher(baseUrl: "http://127.0.0.1/", host: "app.test").Scan();
        Assert.Equal(("http://127.0.0.1/mfe/orders/remoteEntry.js", "app.test"), Assert.Single(_asked));
    }

    [Fact]
    public void A_folder_or_manifest_that_is_not_there_is_the_problem_of_the_application()
    {
        var watcher = Watcher();
        watcher.Scan();
        Assert.Contains("could not be read", watcher.Snapshot().Apps.Single().Problem);

        watcher.ApplyConfig([new FrontendConfig { Name = "App", Root = Path.Combine(_root, "nowhere") }]);
        watcher.Scan();
        Assert.Contains("does not exist", watcher.Snapshot().Apps.Single().Problem);
    }

    [Fact]
    public void The_application_around_the_modules_and_modules_that_come_and_go_are_publications_too()
    {
        Manifest("orders");
        Publish("orders");
        File.WriteAllText(Path.Combine(_root, "index.html"), "<html>1</html>");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "index.html"), _clock);
        var watcher = Watcher();
        watcher.Scan();

        File.SetLastWriteTimeUtc(Path.Combine(_root, "index.html"), _clock.AddHours(3));
        Manifest("billing");
        Publish("billing", "2.0.0");
        watcher.Scan();

        var kinds = watcher.Snapshot().Publications.Select(publication => (publication.Module, publication.Kind)).ToList();
        Assert.Contains(("billing", "new"), kinds);
        Assert.Contains(("orders", "removed"), kinds);
        Assert.Contains((FrontendWatcher.Shell, "published"), kinds);
        Assert.NotNull(watcher.Snapshot().Apps.Single().ShellAt);
    }
}
