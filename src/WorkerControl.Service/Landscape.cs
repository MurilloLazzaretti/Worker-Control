using Newtonsoft.Json.Linq;
using WorkerControl.Service.Database;
using WorkerControl.Service.Traffic;
using WorkerControl.Service.Transport;

namespace WorkerControl.Service;

/// <summary>
/// What a drawing of the environment needs beyond the groups and the services: the
/// applications of the web server with how their sites stand, the web applications, and who
/// is connected to the database, process by process, so that each can be told apart.
/// </summary>
internal static class Landscape
{
    public static JObject Describe(TargetCatalog catalog, IWebServer web, IMachineNetwork network, IReadOnlyList<FrontendWatcher.App> frontends, DatabaseState? database) => Admin.Ok(answer =>
    {
        var states = web.States().ToDictionary(site => site.Name, StringComparer.OrdinalIgnoreCase);
        var ports = network.Sites().GroupBy(site => site.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().Port, StringComparer.OrdinalIgnoreCase);

        answer["Machine"] = Environment.MachineName;
        answer["Apis"] = new JArray(catalog.All().Where(target => target.Kind == "api").Select(target => new JObject
        {
            ["Name"] = target.Name,
            ["Folder"] = target.Paths.FirstOrDefault(),
            ["Version"] = target.Version,
            // Null where the web server does not say how a site stands.
            ["Sites"] = new JArray(target.Sites.Select(site => new JObject
            {
                ["Name"] = site,
                ["Port"] = ports.TryGetValue(site, out var port) ? port : null,
                ["Started"] = states.TryGetValue(site, out var state) ? state.Started && state.PoolStarted : null,
                ["ProcessIds"] = new JArray(state?.ProcessIds ?? [])
            }))
        }));
        answer["Frontends"] = new JArray(frontends.Select(app => new JObject
        {
            ["Name"] = app.Name, ["BaseUrl"] = app.BaseUrl, ["Problem"] = app.Problem, ["Modules"] = app.Modules.Count
        }));
        answer["Database"] = database is not { Configured: true }
            ? null
            : new JObject
            {
                ["Name"] = database.Name,
                ["Server"] = database.Server,
                ["Online"] = database.Online,
                ["Error"] = database.Error,
                // Null when the user of the connection may not read who is connected.
                ["Clients"] = database.Fast?.Clients is { } clients
                    ? new JArray(clients.GroupBy(client => (Host: client.Host.ToUpperInvariant(), client.ProcessId, client.Program)).Select(group => new JObject
                    {
                        ["Host"] = group.First().Host,
                        ["ProcessId"] = group.Key.ProcessId,
                        ["Program"] = group.Key.Program,
                        ["Sessions"] = group.Sum(client => client.Sessions),
                        ["Running"] = group.Sum(client => client.Running),
                        ["IdleSeconds"] = group.Where(client => client.IdleSeconds >= 0).Select(client => (int?)client.IdleSeconds).Min()
                    }))
                    : null
            };
    });
}
