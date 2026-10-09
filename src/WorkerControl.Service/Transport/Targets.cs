using System.Diagnostics;
using WorkerControl.Core;
using WorkerControl.Service.Traffic;

namespace WorkerControl.Service.Transport;

/// <summary>
/// Something on this machine a package may replace. The name is the one it has in every
/// environment; the paths are where it is here.
/// </summary>
public sealed record DeployTarget(string Kind, string Name, IReadOnlyList<string> Paths, IReadOnlyList<string> Keep)
{
    /// <summary>
    /// The groups of processes that run from its folder, for a worker.
    /// </summary>
    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>
    /// The service of Windows that runs from its folder, for a service.
    /// </summary>
    public string? Service { get; init; }

    /// <summary>
    /// The sites of the web server that serve it, for an api.
    /// </summary>
    public IReadOnlyList<string> Sites { get; init; } = [];
    public string? Version { get; init; }

    /// <summary>
    /// Why it cannot be replaced as it is known: no folder could be told, for one.
    /// </summary>
    public string? Problem { get; init; }
}

/// <summary>
/// What a package may replace on this machine, worked out from what the service already knows:
/// the groups of processes, the services it watches, the sites of the web server and the
/// modules of the web applications. The configuration may say otherwise, or add to it.
/// </summary>
internal sealed class TargetCatalog(Func<WorkerControlConfig?> config, IServiceManager services, IMachineNetwork network, Func<IReadOnlyList<FrontendWatcher.App>> frontends)
{
    private static string Folder(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SameFolder(string one, string other) => string.Equals(Folder(one), Folder(other), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What an executable says its version is. Null when it says nothing or cannot be read.
    /// </summary>
    internal static string? VersionOf(string? executable)
    {
        try
        {
            if (executable is null || !File.Exists(executable))
                return null;
            var info = FileVersionInfo.GetVersionInfo(executable);
            return string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion.Split('+')[0];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public IReadOnlyList<DeployTarget> All()
    {
        if (config() is not { } now)
            return [];
        var keep = now.Transport.Keep;
        var targets = new List<DeployTarget>();

        // Groups that run the same program from the same folder are stopped and started together.
        foreach (var group in now.Groups)
        {
            var folder = Path.GetDirectoryName(group.ApplicationFullPath) ?? "";
            targets.Add(new DeployTarget("worker", group.Name, folder.Length > 0 ? [folder] : [], keep)
            {
                Groups = [.. now.Groups.Where(other => folder.Length > 0 && SameFolder(Path.GetDirectoryName(other.ApplicationFullPath) ?? "", folder)).Select(other => other.Name)],
                Version = VersionOf(group.ApplicationFullPath),
                Problem = folder.Length == 0 ? "The folder of the program of the group could not be told" : null
            });
        }

        foreach (var watched in now.Services.Items)
        {
            var installed = services.Find(watched.Name);
            var executable = installed?.ExecutablePath;
            var wrapped = executable is not null && Path.GetFileNameWithoutExtension(executable).Equals("nssm", StringComparison.OrdinalIgnoreCase);
            var folder = executable is null || wrapped ? null : Path.GetDirectoryName(executable);
            targets.Add(new DeployTarget("service", watched.Name, folder is null ? [] : [folder], keep)
            {
                Service = watched.Name,
                Version = wrapped ? null : VersionOf(executable),
                Problem = installed is null ? "The service is not installed on this machine"
                    : wrapped ? "The service is run by a wrapper: say the folder of the program in \"Transport\".\"Targets\""
                    : folder is null ? "The folder of the service could not be told" : null
            });
        }

        // The sites that serve the same folder are one application. A folder that is only a
        // number is an instance of the application its parent folder is named after.
        var apps = frontends();
        var sites = network.Sites()
            .Where(site => !apps.Any(app => SameFolder(app.Root, site.PhysicalPath)))
            .GroupBy(site => ApplicationName(site.PhysicalPath), StringComparer.OrdinalIgnoreCase);
        foreach (var application in sites.Where(group => group.Key.Length > 0))
        {
            var paths = application.Select(site => Folder(site.PhysicalPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            targets.Add(new DeployTarget("api", application.Key, paths, keep)
            {
                Sites = [.. application.Select(site => site.Name).Distinct(StringComparer.OrdinalIgnoreCase)],
                Version = paths.Where(Directory.Exists).SelectMany(path => Directory.EnumerateFiles(path, "*.exe")).Select(VersionOf).FirstOrDefault(version => version is not null)
            });
        }

        foreach (var app in apps)
            foreach (var module in app.Modules)
            {
                var folder = Path.GetDirectoryName(Path.Combine(app.Root, module.Entry.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)));
                targets.Add(new DeployTarget("frontend", apps.Count > 1 ? $"{app.Name}/{module.Name}" : module.Name, folder is null ? [] : [folder], keep)
                {
                    Version = module.Versions.FirstOrDefault()?.Version ?? (module.Build is { } build ? "build " + build : null)
                });
            }

        // What the configuration says stands over what was worked out.
        foreach (var said in now.Transport.Targets)
        {
            var known = targets.FindIndex(target => target.Kind == said.Kind && target.Name.Equals(said.Name, StringComparison.OrdinalIgnoreCase));
            var before = known >= 0 ? targets[known] : new DeployTarget(said.Kind, said.Name, [], keep) { Service = said.Kind == "service" ? said.Name : null, Groups = said.Kind == "worker" ? [said.Name] : [] };
            var target = before with
            {
                Paths = said.Paths.Count > 0 ? [.. said.Paths.Select(Folder)] : before.Paths,
                Keep = said.Keep ?? before.Keep,
                Sites = said.Sites.Count > 0 ? said.Sites : before.Sites,
                Problem = said.Paths.Count > 0 ? null : before.Problem
            };
            if (known >= 0)
                targets[known] = target;
            else
                targets.Add(target);
        }

        return [.. targets.OrderBy(target => target.Kind, StringComparer.Ordinal).ThenBy(target => target.Name, StringComparer.OrdinalIgnoreCase)];
    }

    internal static string ApplicationName(string path)
    {
        var folder = new DirectoryInfo(Folder(path));
        return folder.Name.All(char.IsDigit) && folder.Parent is not null ? folder.Parent.Name : folder.Name;
    }

    public DeployTarget? Find(string? kind, string? name) =>
        All().FirstOrDefault(target => target.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) && target.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
