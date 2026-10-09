using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;
using WorkerControl.Service.Traffic;

namespace WorkerControl.Service.Transport;

/// <summary>
/// Putting on this machine an application it does not have yet, and taking one away: a group
/// of processes, the sites of an application of the web server, or a service. Nothing that is
/// there is ever written over, and what was made by a creation that did not get to its end is
/// taken away again.
/// </summary>
internal sealed partial class ApplicationWork(TargetCatalog catalog, Deployer deployer, IGroupSwitch groups, IServiceManager services, IWebServer web, IMachineNetwork network,
    Func<WorkerControlConfig?> config, ConfigFile file, Action reload, string directory, TimeProvider time, ILogger logger)
{
    public static readonly string[] Kinds = ["worker", "api", "service"];
    public const string DefaultSiteName = "{name} {n}";
    private const int MostInstances = 16;
    private const long BiggestConfig = 512 * 1024;

    /// <summary>
    /// For how long what was created is waited for to be up before it is said that it is not.
    /// </summary>
    public TimeSpan StartPatience { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(500);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex GoodName();

    [GeneratedRegex(@"\d+$")]
    private static partial Regex Number();

    private static bool Numbered(string folder) => int.TryParse(Path.GetFileName(folder.TrimEnd('\\', '/')), out _);

    /// <summary>
    /// The folder the applications of a kind are in: the one that was said, or the one most of them are in today.
    /// </summary>
    private string? BaseOf(string kind, IReadOnlyList<DeployTarget> targets)
    {
        if (config()?.Transport.CreateFolders.TryGetValue(kind, out var said) == true)
            return said;
        return targets.Where(target => target.Kind == kind)
            .SelectMany(target => target.Paths.Take(1))
            // The folder of an instance is inside the folder of the application.
            .Select(path => Path.GetDirectoryName(Numbered(path) ? Path.GetDirectoryName(path.TrimEnd('\\', '/')) ?? path : path.TrimEnd('\\', '/')))
            .Where(parent => !string.IsNullOrEmpty(parent))
            .GroupBy(parent => parent!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault()?.Key;
    }

    /// <summary>
    /// How the sites are named: as it was said, or as most of the ones there are.
    /// </summary>
    private string SiteName(IReadOnlyList<DeployTarget> targets)
    {
        if (config()?.Transport.SiteName is { Length: > 0 } said)
            return said;
        return targets.Where(target => target.Kind == "api")
            .SelectMany(target => target.Sites.Select(site => (Site: site, target.Name)))
            .Where(pair => pair.Site.Contains(pair.Name, StringComparison.OrdinalIgnoreCase) && Number().IsMatch(pair.Site))
            .Select(pair => Number().Replace(Regex.Replace(pair.Site, Regex.Escape(pair.Name), "{name}", RegexOptions.IgnoreCase), "{n}"))
            .GroupBy(template => template, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault()?.Key ?? DefaultSiteName;
    }

    /// <summary>
    /// What whoever is about to create an application needs to follow what is already here.
    /// </summary>
    public JObject Defaults()
    {
        var targets = catalog.All();
        var now = config();
        return Admin.Ok(answer =>
        {
            answer["Allowed"] = now?.Transport.AllowCreate ?? true;
            answer["Folders"] = new JObject(Kinds.Select(kind => new JProperty(kind, BaseOf(kind, targets))));
            answer["SiteName"] = SiteName(targets);
            answer["Sites"] = new JArray(network.Sites().OrderBy(site => site.Port).Select(site => new JObject { ["Name"] = site.Name, ["Port"] = site.Port, ["Path"] = site.PhysicalPath }));
            answer["Groups"] = new JArray((now?.Groups ?? []).Select(group => new JObject { ["Name"] = group.Name, ["Path"] = group.ApplicationFullPath, ["Workers"] = group.TotalWorkers }));
            answer["Services"] = new JArray(targets.Where(target => target.Kind == "service").Select(target => services.Find(target.Name)).OfType<InstalledService>()
                .Select(service => new JObject { ["Name"] = service.Name, ["DisplayName"] = service.DisplayName, ["Path"] = service.ExecutablePath, ["StartType"] = service.StartType }));
            answer["Listening"] = new JArray(network.Listeners().Keys.OrderBy(port => port));
        });
    }

    public sealed record ConfigText(string Path, string Content);

    public sealed record NewApplication(string Kind, string Name, string Folder, string Zip, string? Executable, int Instances, int Port, string? SiteName, string? DisplayName, string? StartType,
        IReadOnlyList<ConfigText> Configs);

    private sealed class Refused(string message) : Exception(message);

    private static string Full(string folder, string relative) => Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string SiteOf(string template, string name, int number) => template.Replace("{name}", name, StringComparison.OrdinalIgnoreCase).Replace("{n}", number.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Everything that would stop the creation, looked at before anything is touched.
    /// </summary>
    private List<FileEntry> Check(NewApplication wanted)
    {
        if (config()?.Transport.AllowCreate == false)
            throw new Refused("Creating applications is turned off on this machine (\"AllowCreate\" in \"Transport\")");
        if (!Kinds.Contains(wanted.Kind))
            throw new Refused("\"Kind\" must be worker, api or service");
        if (!GoodName().IsMatch(wanted.Name))
            throw new Refused("The name takes letters, digits, dot, dash and underscore, and starts with a letter or a digit");
        if (string.IsNullOrWhiteSpace(wanted.Folder) || !Path.IsPathFullyQualified(wanted.Folder))
            throw new Refused("The folder must be a full path on this machine");
        if (Directory.Exists(wanted.Folder) && Directory.EnumerateFileSystemEntries(wanted.Folder).Any())
            throw new Refused($"The folder {wanted.Folder} already has something in it");
        if (File.Exists(wanted.Folder))
            throw new Refused($"{wanted.Folder} is a file");
        if (!File.Exists(wanted.Zip))
            throw new Refused("The files of the application are not where they were said to be");

        List<FileEntry> files;
        try
        {
            files = FileSet.ListZip(wanted.Zip);
        }
        catch (Exception error) when (error is InvalidDataException or IOException)
        {
            throw new Refused("The files of the application could not be read: " + error.Message);
        }
        if (files.Count == 0)
            throw new Refused("The zip has no files");
        foreach (var text in wanted.Configs)
        {
            if (string.IsNullOrWhiteSpace(text.Path) || text.Path.Contains("..") || Path.IsPathRooted(text.Path))
                throw new Refused($"\"{text.Path}\" is not a path inside the folder of the application");
            if (Encoding.UTF8.GetByteCount(text.Content) > BiggestConfig)
                throw new Refused($"{text.Path} is too big to be written from here");
        }

        if (wanted.Kind != "api")
        {
            if (string.IsNullOrWhiteSpace(wanted.Executable) || !files.Any(entry => entry.Path.Equals(wanted.Executable.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                throw new Refused("The executable has to be one of the files of the zip");
        }

        switch (wanted.Kind)
        {
            case "worker":
                if (config()?.Groups.Any(group => group.Name.Equals(wanted.Name, StringComparison.OrdinalIgnoreCase)) == true)
                    throw new Refused($"There is already a group called {wanted.Name}");
                if (wanted.Instances is < 0 or > MostInstances)
                    throw new Refused($"A group is created with 0 to {MostInstances} processes");
                break;
            case "service":
                if (services.Find(wanted.Name) is not null)
                    throw new Refused($"There is already a service called {wanted.Name} on this machine");
                if (wanted.StartType is not (null or "" or "Automatic" or "Manual"))
                    throw new Refused("\"StartType\" must be Automatic or Manual");
                break;
            case "api":
            {
                if (wanted.Instances is < 1 or > MostInstances)
                    throw new Refused($"An application is created with 1 to {MostInstances} instances");
                if (wanted.Port is < 1 or > 65535 || wanted.Port + wanted.Instances - 1 > 65535)
                    throw new Refused("The first port must be between 1 and 65535, with room for one port for each instance");
                var template = wanted.SiteName is { Length: > 0 } said ? said : DefaultSiteName;
                if (!template.Contains("{name}", StringComparison.OrdinalIgnoreCase) || (wanted.Instances > 1 && !template.Contains("{n}", StringComparison.OrdinalIgnoreCase)))
                    throw new Refused("The name of the sites needs {name}, and {n} when there is more than one instance");
                if (template.IndexOfAny(['"', '/', '\\', '?', ';', ':', '@', '&', '=', '+', '$', ',', '|', '<', '>']) >= 0)
                    throw new Refused("The name of the sites has a character the web server does not take");
                var sites = network.Sites();
                var listening = network.Listeners();
                for (var number = 1; number <= wanted.Instances; number++)
                {
                    var port = wanted.Port + number - 1;
                    var site = SiteOf(template, wanted.Name, number);
                    if (sites.FirstOrDefault(other => other.Name.Equals(site, StringComparison.OrdinalIgnoreCase)) is not null)
                        throw new Refused($"There is already a site called {site}");
                    if (sites.FirstOrDefault(other => other.Port == port) is { } taken)
                        throw new Refused($"The port {port} is the one of the site {taken.Name}");
                    if (listening.ContainsKey(port))
                        throw new Refused($"Something on this machine already listens on the port {port}");
                }
                break;
            }
        }
        return files;
    }

    private async Task<bool> Until(Func<bool> done, CancellationToken stopping)
    {
        var until = time.GetUtcNow() + StartPatience;
        while (!done())
        {
            if (time.GetUtcNow() >= until || stopping.IsCancellationRequested)
                return false;
            await Task.Delay(Poll, time, CancellationToken.None);
        }
        return true;
    }

    private void Place(NewApplication wanted, string folder)
    {
        FileSet.Mirror(wanted.Zip, folder, []);
        foreach (var text in wanted.Configs)
        {
            var path = Full(folder, text.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.Content, new UTF8Encoding(false));
        }
    }

    /// <summary>
    /// Creates the application. When something goes wrong before it is all there, what was made is taken away.
    /// </summary>
    public JObject Create(NewApplication wanted, CancellationToken stopping)
    {
        try
        {
            Check(wanted);
        }
        catch (Refused refused)
        {
            return Admin.Error("invalid-request", refused.Message);
        }

        var steps = new List<string>();
        var undo = new Stack<(string What, Action Do)>();
        void Did(string step, Action? back = null, string? backName = null)
        {
            steps.Add(step);
            logger.LogInformation("New {Kind} {Name}: {Step}", wanted.Kind, wanted.Name, step);
            if (back is not null)
                undo.Push((backName ?? step, back));
        }

        string? warning = null;
        try
        {
            var existed = Directory.Exists(wanted.Folder);
            Directory.CreateDirectory(wanted.Folder);
            undo.Push(("the folder " + wanted.Folder, () =>
            {
                if (existed)
                    foreach (var entry in Directory.EnumerateFileSystemEntries(wanted.Folder))
                    {
                        if (Directory.Exists(entry))
                            Directory.Delete(entry, recursive: true);
                        else
                            File.Delete(entry);
                    }
                else
                    Directory.Delete(wanted.Folder, recursive: true);
            }));

            switch (wanted.Kind)
            {
                case "worker":
                {
                    Place(wanted, wanted.Folder);
                    Did($"put the files in {wanted.Folder}");
                    var added = file.AddGroup(new JObject
                    {
                        ["Name"] = wanted.Name, ["Enabled"] = true, ["ApplicationFullPath"] = Full(wanted.Folder, wanted.Executable!), ["TotalWorkers"] = wanted.Instances
                    });
                    if (!added)
                        throw new Refused($"There is already a group called {wanted.Name}");
                    Did($"added the group {wanted.Name} with {wanted.Instances} process(es)", () => { file.RemoveGroup(wanted.Name); reload(); }, "the group " + wanted.Name);
                    reload();
                    if (wanted.Instances > 0 && !Until(() => groups.Look(wanted.Name) is { Up: > 0 }, stopping).GetAwaiter().GetResult())
                        warning = "The group was created, but no process of it came up yet";
                    else
                        Did("the group is up");
                    break;
                }
                case "service":
                {
                    Place(wanted, wanted.Folder);
                    Did($"put the files in {wanted.Folder}");
                    var display = string.IsNullOrWhiteSpace(wanted.DisplayName) ? wanted.Name : wanted.DisplayName.Trim();
                    services.Create(wanted.Name, display, Full(wanted.Folder, wanted.Executable!), wanted.StartType is "Manual" ? "Manual" : "Automatic");
                    Did($"registered the service {wanted.Name}", () => services.Delete(wanted.Name), "the service " + wanted.Name);
                    file.ChangeSection("Services", section =>
                    {
                        if (section["Items"] is not JArray items)
                            section["Items"] = items = [];
                        if (!items.OfType<JObject>().Any(item => string.Equals((string?)item["Name"], wanted.Name, StringComparison.OrdinalIgnoreCase)))
                            items.Add(new JObject { ["Name"] = wanted.Name });
                    });
                    Did("it is now one of the services looked after", () => { Unwatch(wanted.Name); reload(); }, "the watching of " + wanted.Name);
                    reload();
                    try
                    {
                        services.Start(wanted.Name);
                        if (Until(() => services.Find(wanted.Name) is { State: ServiceState.Running }, stopping).GetAwaiter().GetResult())
                            Did("the service is running");
                        else
                            warning = "The service was created, but it is not running yet";
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        warning = "The service was created, but it refused to start: " + (error.InnerException?.Message ?? error.Message);
                    }
                    break;
                }
                case "api":
                {
                    var template = wanted.SiteName is { Length: > 0 } said ? said : DefaultSiteName;
                    for (var number = 1; number <= wanted.Instances; number++)
                    {
                        var folder = Path.Combine(wanted.Folder, number.ToString());
                        Place(wanted, folder);
                        Did($"put the files in {folder}");
                    }
                    for (var number = 1; number <= wanted.Instances; number++)
                    {
                        var site = SiteOf(template, wanted.Name, number);
                        var port = wanted.Port + number - 1;
                        web.CreateSite(site, port, Path.Combine(wanted.Folder, number.ToString()));
                        Did($"created the site {site} on the port {port}, with a pool of its own", () => web.DeleteSite(site), "the site " + site);
                    }
                    break;
                }
            }
        }
        catch (Exception error) when (error is Refused or IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("New {Kind} {Name} could not be created: {Error}", wanted.Kind, wanted.Name, error.Message);
            var left = new List<string>();
            while (undo.TryPop(out var back))
            {
                try
                {
                    back.Do();
                    steps.Add("took away " + back.What);
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    left.Add($"{back.What} ({again.Message})");
                }
            }
            return Admin.Ok(answer =>
            {
                answer["Created"] = false;
                answer["Problem"] = error.Message + (left.Count == 0 ? ". Nothing of it was left on the machine" : ". Left behind, to be taken away by hand: " + string.Join("; ", left));
                answer["Messages"] = new JArray(steps);
            });
        }

        return Admin.Ok(answer =>
        {
            answer["Created"] = true;
            answer["Warning"] = warning;
            answer["Messages"] = new JArray(steps);
        });
    }

    private void Unwatch(string service) => file.ChangeSection("Services", section =>
    {
        if (section["Items"] is JArray items)
            foreach (var item in items.OfType<JObject>().Where(item => string.Equals((string?)item["Name"], service, StringComparison.OrdinalIgnoreCase)).ToList())
                item.Remove();
    });

    /// <summary>
    /// Takes a folder out of the way to where what was removed is kept.
    /// </summary>
    private string Keep(string folder, string kept)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        try
        {
            Directory.Move(folder, kept);
        }
        catch (IOException)
        {
            // Another disk: copied, then taken away.
            FileSet.Copy(folder, kept);
            Directory.Delete(folder, recursive: true);
        }
        return kept;
    }

    /// <summary>
    /// Takes an application away from this machine: stops it, removes what makes it run and
    /// moves its folder to where what was removed is kept.
    /// </summary>
    public JObject Remove(string? kind, string? name, bool force, CancellationToken stopping)
    {
        if (config()?.Transport.AllowCreate == false)
            return Admin.Error("invalid-state", "Creating and removing applications is turned off on this machine (\"AllowCreate\" in \"Transport\")");
        if (kind is null || !Kinds.Contains(kind))
            return Admin.Error("invalid-request", "Only a worker, an api or a service is removed from here");
        if (catalog.Find(kind, name) is not { } target)
            return Admin.Error("not-found", $"There is no {kind} called {name} on this machine");
        if (target.Problem is not null)
            return Admin.Error("invalid-state", target.Problem);
        if (target.Service is not null && services.Find(target.Service) is { } own && own.ProcessId == Environment.ProcessId)
            return Admin.Error("invalid-state", "This service cannot remove itself");
        if (kind == "worker" && target.Groups.Count > 1)
            return Admin.Error("invalid-state", $"The groups {string.Join(", ", target.Groups)} run from the same folder: it is not removed from here");

        var steps = new List<string>();
        void Did(string step)
        {
            steps.Add(step);
            logger.LogInformation("Removing {Kind} {Name}: {Step}", kind, target.Name, step);
        }

        try
        {
            deployer.StopAsync(target, force, Did, stopping).GetAwaiter().GetResult();
        }
        catch (DeployFailed failed)
        {
            return Admin.Ok(answer =>
            {
                answer["Removed"] = false;
                answer["Problem"] = failed.Message;
                answer["Messages"] = new JArray(steps);
            });
        }

        var kept = Path.Combine(directory, "removed", kind, string.Concat(target.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')), time.GetUtcNow().ToString("yyyyMMdd-HHmmss"));
        try
        {
            switch (kind)
            {
                case "worker":
                    file.RemoveGroup(target.Name);
                    reload();
                    Did($"removed the group {target.Name}");
                    break;
                case "service":
                    services.Delete(target.Service!);
                    Did($"unregistered the service {target.Service}");
                    Unwatch(target.Service!);
                    reload();
                    break;
                case "api":
                    foreach (var site in target.Sites)
                    {
                        web.DeleteSite(site);
                        Did($"removed the site {site} and its pool");
                    }
                    break;
            }

            for (var index = 0; index < target.Paths.Count; index++)
            {
                var folder = target.Paths[index];
                if (!Directory.Exists(folder))
                    continue;
                Did($"moved {folder} to {Keep(folder, Path.Combine(kept, index.ToString()))}");
                // The folder of the application, once the last of its instances is gone.
                if (Numbered(folder) && Path.GetDirectoryName(folder.TrimEnd('\\', '/')) is { } parent && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    Directory.Delete(parent);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Admin.Ok(answer =>
            {
                answer["Removed"] = false;
                answer["Problem"] = "It was stopped, but not all of it was removed: " + error.Message;
                answer["Kept"] = Directory.Exists(kept) ? kept : null;
                answer["Messages"] = new JArray(steps);
            });
        }

        return Admin.Ok(answer =>
        {
            answer["Removed"] = true;
            answer["Kept"] = Directory.Exists(kept) ? kept : null;
            answer["Messages"] = new JArray(steps);
        });
    }
}
