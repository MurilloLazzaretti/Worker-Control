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

    /// <summary>
    /// For one file of a folder of downloads: its name in the folder, and the file that says the versions.
    /// </summary>
    public string? File { get; init; }
    public string? VersionsFile { get; init; }
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
            string? problem = null;
            var serviceKeep = keep;
            if (wrapped)
            {
                // The wrapper keeps what it runs, and from where.
                var (program, directory) = services.Wrapped(watched.Name);
                executable = program;
                folder = directory ?? (program is null ? null : Path.GetDirectoryName(program));
                if (folder is null)
                    problem = "The service is run by a wrapper that does not say which program it runs: say the folder in \"Transport\".\"Targets\"";
                else if (Shared(folder))
                {
                    // A runtime installed for everybody (node, java) is not the folder of the application; the script it is given is.
                    var script = ScriptOf(services.WrappedArguments(watched.Name));
                    if (script is not null && !Shared(Path.GetDirectoryName(script)!))
                        folder = Path.GetDirectoryName(script);
                    else
                    {
                        problem = $"The service runs {program} from {folder}, which is not the folder of an application: say it in \"Transport\".\"Targets\"";
                        folder = null;
                    }
                }
                else if (program is not null && Path.GetFileNameWithoutExtension(program).Equals("nginx", StringComparison.OrdinalIgnoreCase))
                    // The reverse proxy: its configuration, its pages in use and what it writes are of the environment.
                    serviceKeep = [.. keep, "conf/", "temp/", "logs/", "*.pid"];
            }
            targets.Add(new DeployTarget("service", watched.Name, folder is null ? [] : [folder], serviceKeep)
            {
                Service = watched.Name,
                Version = VersionOf(executable),
                Problem = installed is null ? "The service is not installed on this machine"
                    : problem ?? (folder is null ? "The folder of the service could not be told" : null)
            });
        }

        // The sites that serve the same folder are one application. A folder that is only a
        // number is an instance of the application its parent folder is named after.
        var apps = frontends();
        // A site that only serves files to download is not an application: its files are.
        var sites = network.Sites()
            .Where(site => !apps.Any(app => SameFolder(app.Root, site.PhysicalPath)) && !now.Downloads.Any(area => SameFolder(area.Root, site.PhysicalPath)))
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
        {
            static string? VersionOf(FrontendWatcher.Module module) => module.Versions.FirstOrDefault()?.Version ?? (module.Build is { } build ? "build " + build : null);

            // A monolith is replaced whole: its folder, leaving alone what belongs to the environment.
            if (app.Mode == "monolith")
            {
                targets.Add(new DeployTarget("frontend", app.Name, [Folder(app.Root)], keep)
                {
                    Version = app.Modules.Select(VersionOf).FirstOrDefault(),
                    Problem = app.Problem
                });
                continue;
            }

            var moduleFolders = new List<string>();
            foreach (var module in app.Modules)
            {
                var entry = module.Entry.Replace('\\', '/').TrimStart('/');
                var folder = Path.GetDirectoryName(Path.Combine(app.Root, entry.Replace('/', Path.DirectorySeparatorChar)));
                targets.Add(new DeployTarget("frontend", apps.Count > 1 ? $"{app.Name}/{module.Name}" : module.Name, folder is null ? [] : [folder], keep)
                {
                    Version = VersionOf(module)
                });
                if (entry.LastIndexOf('/') is var slash and > 0)
                    moduleFolders.Add(entry[..(slash + 1)]);
            }

            // The shell: the folder of the application without the modules it loads, nor the list of them, which are of the environment.
            targets.Add(new DeployTarget("frontend", apps.Count > 1 ? $"{app.Name}/{FrontendWatcher.ShellTarget}" : FrontendWatcher.ShellTarget, [Folder(app.Root)],
                [.. keep, .. moduleFolders.Distinct(StringComparer.OrdinalIgnoreCase), app.Manifest.Replace('\\', '/').TrimStart('/')])
            {
                Problem = app.Problem is null ? null : "The modules of the application could not be read, so what to leave alone in its folder is not known: " + app.Problem
            });
        }

        foreach (var area in now.Downloads)
        {
            if (!Directory.Exists(area.Root))
                continue;
            var versions = Downloads.Versions(area.Root, area.VersionsFile);
            foreach (var path in Directory.EnumerateFiles(area.Root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.GetFileName(path);
                if (!Downloads.Fits(file, area.Files) || file.Equals(area.VersionsFile, StringComparison.OrdinalIgnoreCase))
                    continue;
                targets.Add(DownloadTarget(area, file, now.Downloads.Count > 1, versions.GetValueOrDefault(file)));
            }
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

    /// <summary>
    /// The first file the arguments of a program name with a full path, and that is there.
    /// </summary>
    internal static string? ScriptOf(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return null;
        var tokens = System.Text.RegularExpressions.Regex.Matches(arguments, "\"([^\"]+)\"|(\\S+)").Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
        return tokens.FirstOrDefault(token => Path.IsPathFullyQualified(token) && File.Exists(token));
    }

    /// <summary>
    /// A folder of the system or of what is installed for everybody, where no application of the environment lives.
    /// </summary>
    private static bool Shared(string folder)
    {
        var full = Folder(folder);
        return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows }
            .Select(Environment.GetFolderPath)
            .Where(path => path.Length > 0)
            .Any(path => full.StartsWith(Folder(path), StringComparison.OrdinalIgnoreCase));
    }

    internal static string ApplicationName(string path)
    {
        var folder = new DirectoryInfo(Folder(path));
        return folder.Name.All(char.IsDigit) && folder.Parent is not null ? folder.Parent.Name : folder.Name;
    }

    private static DeployTarget DownloadTarget(DownloadConfig area, string file, bool many, string? version) =>
        new("download", many ? $"{area.Name}/{file}" : file, [Folder(area.Root)], []) { File = file, VersionsFile = area.VersionsFile, Version = version };

    public DeployTarget? Find(string? kind, string? name)
    {
        var found = All().FirstOrDefault(target => target.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) && target.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (found is not null || !string.Equals(kind, "download", StringComparison.OrdinalIgnoreCase) || name is null || config() is not { } now)
            return found;
        // A file that is not in the folder yet: it is added the first time it is put there.
        var slash = name.IndexOf('/');
        var area = now.Downloads.Count == 1 && slash < 0 ? now.Downloads[0]
            : slash > 0 ? now.Downloads.FirstOrDefault(candidate => candidate.Name.Equals(name[..slash], StringComparison.OrdinalIgnoreCase)) : null;
        var file = slash > 0 ? name[(slash + 1)..] : name;
        return area is not null && Directory.Exists(area.Root) && Downloads.Fits(file, area.Files) && !file.Equals(area.VersionsFile, StringComparison.OrdinalIgnoreCase)
            ? DownloadTarget(area, file, now.Downloads.Count > 1, null)
            : null;
    }
}
