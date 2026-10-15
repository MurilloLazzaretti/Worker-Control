using Newtonsoft.Json.Linq;
using WorkerControl.Core;

namespace WorkerControl.Service;

/// <summary>
/// Which web application answers by each name of the DNS. The name never changes: what changes
/// is what the reverse proxy serves by it, written in a file of its own that the server of the
/// site includes once. Activating one application rewrites that file, has the proxy test the
/// whole configuration and read it again, and leaves the others of the same name inactive.
/// </summary>
internal sealed class WebSwitch(Func<WorkerControlConfig?> config, ProxyWork proxy, ConfigFile file, Action reload, TimeProvider time)
{
    private FrontendConfig? Find(string? name) =>
        config()?.Frontends.FirstOrDefault(app => string.Equals(app.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string Quoted(string text) => "\"" + text.Replace("\\", "/").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// What the proxy does for a name, with the application that answers by it, or with none.
    /// </summary>
    public string Content(string address, FrontendConfig? app, string by)
    {
        var head = $"# Written by Worker Control: which web application answers at {address}.\n" +
                   "# Do not change it here: activate the application in the panel (Aplicação web).\n";
        if (app is null)
            return head + $"# None is active since {time.GetUtcNow():yyyy-MM-dd HH:mm} UTC ({by}).\n\n" +
                   "location / {\n    default_type text/html;\n    return 503 \"<html><body style='font-family:sans-serif;text-align:center;padding-top:15%'><h2>Sistema fora do ar</h2><p>Tente de novo em alguns minutos.</p></body></html>\";\n}\n";

        var text = head + $"# Active: {app.Name} ({(app.Mode == "mfe" ? "micro frontends" : "monolith")}) since {time.GetUtcNow():yyyy-MM-dd HH:mm} UTC ({by}).\n\n";
        if (app.Serve == "proxy")
            return text + $"location / {{\n    proxy_pass {app.ProxyTo};\n    proxy_http_version 1.1;\n    proxy_set_header Host $host;\n    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;\n}}\n";

        text += $"root {Quoted(Path.TrimEndingDirectorySeparator(app.Root))};\nindex index.html;\n\n" +
                "# The page and the files that say where everything else is are never kept by the browser.\n" +
                "location = /index.html {\n    add_header Cache-Control \"no-cache\";\n}\n" +
                "location ~ ^/assets/[^/]+\\.json$ {\n    add_header Cache-Control \"no-cache\";\n}\n";
        if (app.Mode == "mfe")
            text += "location ~ ^/mfe/[^/]+/(remoteEntry\\.js|version\\.json)$ {\n    add_header Cache-Control \"no-cache\";\n}\n";
        // An address of the application itself (a route of it) is answered with its page.
        return text + "\nlocation / {\n    try_files $uri $uri/ /index.html;\n}\n";
    }

    /// <summary>
    /// The application answers by its name from now on, and the others of the same name do not.
    /// </summary>
    public JObject Activate(string? name, string by) => Switch(name, by, activate: true);

    /// <summary>
    /// Nothing answers by the name of the application any more: the proxy says it is out of the air.
    /// </summary>
    public JObject Deactivate(string? name, string by) => Switch(name, by, activate: false);

    private JObject Switch(string? name, string by, bool activate)
    {
        if (Find(name) is not { } app)
            return Admin.Error("not-found", $"There is no web application called {name}");
        if (app.Address is not { } address)
            return Admin.Error("invalid-state", $"{app.Name} has no name of the DNS (\"Address\") to answer by");
        if (!activate && !app.Active)
            return Admin.Error("invalid-state", $"{app.Name} is not active");
        if (activate && app.Serve == "files" && !File.Exists(Path.Combine(app.Root, "index.html")))
            return Admin.Error("invalid-state", $"{app.Name} has no index.html in {app.Root}: bring its files first");

        var (_, _, before, problem) = proxy.Site(address);
        if (problem is not null)
            return Admin.Error("not-configured", problem);
        var written = proxy.WriteSite(address, Content(address, activate ? app : null, by));
        if (!written.Saved)
            return Admin.Ok(answer => { answer["Switched"] = false; answer["Output"] = written.Output; });

        var others = new List<string>();
        try
        {
            file.ChangeEnvironmentList("Frontends", list =>
            {
                foreach (var entry in list.OfType<JObject>().Where(item => string.Equals(((string?)item["Address"])?.Trim(), address, StringComparison.OrdinalIgnoreCase)))
                {
                    var mine = string.Equals((string?)entry["Name"], app.Name, StringComparison.OrdinalIgnoreCase);
                    // An application without "Active" is active.
                    if (!mine && entry["Active"]?.Value<bool?>() != false)
                        others.Add((string)entry["Name"]!);
                    entry["Active"] = mine && activate;
                }
            });
        }
        catch (ConfigException error)
        {
            // The registry did not take it: the proxy goes back to what it served.
            proxy.WriteSite(address, before ?? Content(address, null, by));
            return Admin.Ok(answer => { answer["Switched"] = false; answer["Output"] = error.Message; });
        }
        reload();
        return Admin.Ok(answer =>
        {
            answer["Switched"] = true;
            answer["Address"] = address;
            answer["Reloaded"] = written.Reloaded;
            answer["Included"] = written.Included;
            answer["File"] = written.Path;
            answer["Include"] = $"include {ProxyWork.SiteFile(address)};";
            answer["Deactivated"] = new JArray(others);
            answer["Output"] = written.Output;
        });
    }

    /// <summary>
    /// For a name: the file, whether the configuration includes it and the line that includes it.
    /// </summary>
    public JObject Site(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return Admin.Error("invalid-request", "\"Address\" is required");
        var (path, included, content, problem) = proxy.Site(address.Trim().ToLowerInvariant());
        if (problem is not null)
            return Admin.Error("not-configured", problem);
        return Admin.Ok(answer =>
        {
            answer["File"] = path;
            answer["Exists"] = content is not null;
            answer["Included"] = included;
            answer["Include"] = $"include {ProxyWork.SiteFile(address.Trim().ToLowerInvariant())};";
            answer["Content"] = content;
        });
    }
}
