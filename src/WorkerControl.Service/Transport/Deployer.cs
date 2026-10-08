using System.Diagnostics;
using WorkerControl.Core;

namespace WorkerControl.Service.Transport;

/// <summary>
/// Turning the groups of processes off and on, as the supervisor does it.
/// </summary>
public interface IGroupSwitch
{
    /// <summary>
    /// False when there is no such group.
    /// </summary>
    bool Enable(string group, bool enabled, string why);

    /// <summary>
    /// How the group stands: whether it is on, how many processes it has and how many of them
    /// are up, and how many it is supposed to have. Null when there is no such group.
    /// </summary>
    (bool Enabled, int Processes, int Up, int Desired)? Look(string group);
}

/// <summary>
/// Taking the sites of the web server off the air and back, so that the files they hold are let go.
/// </summary>
public interface IWebServer
{
    void Stop(IReadOnlyList<string> sites);

    void Start(IReadOnlyList<string> sites);
}

/// <summary>
/// How replacing one target went. With it replaced and not answering afterwards, the files
/// are the new ones and <c>Ok</c> is false.
/// </summary>
public sealed record DeployResult(bool Ok, string? Error, IReadOnlyList<string> Steps, MirrorResult? Files, string? Backup, bool Replaced);

public sealed class DeployFailed(string message) : Exception(message);

/// <summary>
/// Replaces what runs from a folder by what a package brings: stops whatever runs from it,
/// keeps a copy of the folder as it is, puts the new files in place leaving the configuration
/// alone, starts it again and waits for it to be up. If the files cannot all be put, the copy
/// is put back before anything is started.
/// </summary>
internal sealed class Deployer(IGroupSwitch groups, IServiceManager services, IWebServer web, TimeProvider time, string directory, ILogger logger)
{
    public TimeSpan StopPatience { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan StartPatience { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(500);

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    private string BackupRoot(DeployTarget target) => Path.Combine(directory, "backup", target.Kind, Safe(target.Name));

    public async Task<DeployResult> DeployAsync(DeployTarget target, string zipPath, string package, int item, int keepVersions, CancellationToken stopping)
    {
        var steps = new List<string>();
        void Did(string step)
        {
            steps.Add(step);
            logger.LogInformation("Package {Package}, item {Item}, {Kind} {Name}: {Step}", package, item, target.Kind, target.Name, step);
        }

        if (target.Problem is not null)
            return new DeployResult(false, target.Problem, steps, null, null, false);
        if (target.Paths.Count == 0)
            return new DeployResult(false, "No folder is known for this target on this machine", steps, null, null, false);
        if (target.Paths.FirstOrDefault(path => !Directory.Exists(path)) is { } absent)
            return new DeployResult(false, $"The folder {absent} does not exist on this machine", steps, null, null, false);
        if (target.Service is not null && services.Find(target.Service) is { } own && own.ProcessId == Environment.ProcessId)
            return new DeployResult(false, "This service cannot replace itself", steps, null, null, false);

        List<FileEntry> brought;
        try
        {
            brought = FileSet.ListZip(zipPath);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new DeployResult(false, "The files of the item cannot be read: " + error.Message, steps, null, null, false);
        }
        if (brought.Count == 0)
            return new DeployResult(false, "The item carries no file", steps, null, null, false);

        var backup = Path.Combine(BackupRoot(target), $"{time.GetUtcNow():yyyyMMdd-HHmmss}-{Safe(package)[..Math.Min(8, package.Length)]}");
        IReadOnlyList<string> wereOn = [];
        MirrorResult? files = null;
        try
        {
            wereOn = await Stop(target, Did, stopping);

            for (var index = 0; index < target.Paths.Count; index++)
            {
                FileSet.Copy(target.Paths[index], Path.Combine(backup, index.ToString()));
                Did($"kept a copy of {target.Paths[index]}");
            }

            var replaced = 0;
            try
            {
                foreach (var path in target.Paths)
                {
                    var result = FileSet.Mirror(zipPath, path, target.Keep);
                    files = files is null ? result : new MirrorResult(files.Added + result.Added, files.Changed + result.Changed, files.Removed + result.Removed, files.Unchanged + result.Unchanged);
                    replaced++;
                    Did($"replaced the files of {path}: {result.Added} new, {result.Changed} changed, {result.Removed} removed");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Half of the new files is worse than all of the old ones.
                for (var index = 0; index <= Math.Min(replaced, target.Paths.Count - 1); index++)
                {
                    FileSet.Restore(Path.Combine(backup, index.ToString()), target.Paths[index]);
                    Did($"put {target.Paths[index]} back as it was");
                }
                await StartQuietly(target, wereOn, Did, stopping);
                return new DeployResult(false, "The files could not all be replaced, and the folder was put back as it was: " + error.Message, steps, null, backup, false);
            }
        }
        catch (DeployFailed failed)
        {
            // It did not stop: nothing was touched. Whatever was turned off is turned on again.
            await StartQuietly(target, wereOn, Did, stopping);
            return new DeployResult(false, failed.Message, steps, null, null, false);
        }

        try
        {
            await Start(target, wereOn, Did, stopping);
        }
        catch (DeployFailed failed)
        {
            return new DeployResult(false, "The files were replaced, but it did not come up again: " + failed.Message, steps, files, backup, true);
        }

        Prune(target, keepVersions);
        return new DeployResult(true, null, steps, files, backup, true);
    }

    private async Task StartQuietly(DeployTarget target, IReadOnlyList<string> wereOn, Action<string> did, CancellationToken stopping)
    {
        try
        {
            await Start(target, wereOn, did, stopping);
        }
        catch (DeployFailed failed)
        {
            did("it did not come up again: " + failed.Message);
        }
    }

    private async Task Until(Func<bool> done, TimeSpan patience, string otherwise, CancellationToken stopping)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > patience)
                throw new DeployFailed(otherwise);
            await Task.Delay(Poll, stopping);
        }
    }

    /// <summary>
    /// Stops whatever runs from the folder. Gives the groups that were on, to turn those on again and no other.
    /// </summary>
    private async Task<IReadOnlyList<string>> Stop(DeployTarget target, Action<string> did, CancellationToken stopping)
    {
        switch (target.Kind)
        {
            case "worker":
            {
                var on = target.Groups.Where(group => groups.Look(group) is { Enabled: true }).ToList();
                foreach (var group in on)
                    groups.Enable(group, false, "a package is replacing its program");
                try
                {
                    await Until(() => target.Groups.All(group => groups.Look(group) is null or { Processes: 0 }), StopPatience, "The processes of the group did not stop in time", stopping);
                }
                catch (DeployFailed)
                {
                    // Nothing was touched: what was turned off goes back on.
                    foreach (var group in on)
                        groups.Enable(group, true, "the package could not replace its program");
                    throw;
                }
                did(on.Count == 0 ? "the group was already off" : $"stopped {string.Join(", ", on)}");
                return on;
            }
            case "service":
            {
                var service = services.Find(target.Service!) ?? throw new DeployFailed($"The service {target.Service} is not installed on this machine");
                if (service.State == ServiceState.Stopped)
                {
                    did("the service was already stopped");
                    return [];
                }
                try
                {
                    services.Stop(target.Service!);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    throw new DeployFailed("The service refused to stop: " + error.Message);
                }
                await Until(() => services.Find(target.Service!) is null or { State: ServiceState.Stopped }, StopPatience, "The service did not stop in time", stopping);
                did($"stopped the service {target.Service}");
                return [target.Service!];
            }
            case "api":
                if (target.Sites.Count == 0)
                    throw new DeployFailed("No site of the web server is known for this application");
                try
                {
                    web.Stop(target.Sites);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
                {
                    try
                    {
                        // Whatever part of it did stop goes back on the air.
                        web.Start(target.Sites);
                    }
                    catch (Exception again) when (again is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
                    {
                    }
                    throw new DeployFailed("The web server did not take the application off the air: " + error.Message);
                }
                did($"took {string.Join(", ", target.Sites)} off the air");
                return target.Sites;
            default:
                return [];
        }
    }

    private async Task Start(DeployTarget target, IReadOnlyList<string> wereOn, Action<string> did, CancellationToken stopping)
    {
        if (wereOn.Count == 0)
            return;
        switch (target.Kind)
        {
            case "worker":
                foreach (var group in wereOn)
                    groups.Enable(group, true, "a package replaced its program");
                await Until(() => wereOn.All(group => groups.Look(group) is { Desired: 0 } or { Up: > 0 }), StartPatience, "The processes of the group did not come up in time", stopping);
                did($"started {string.Join(", ", wereOn)}");
                break;
            case "service":
                try
                {
                    services.Start(target.Service!);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    throw new DeployFailed("The service refused to start: " + error.Message);
                }
                await Until(() => services.Find(target.Service!) is { State: ServiceState.Running }, StartPatience, "The service did not start in time", stopping);
                did($"started the service {target.Service}");
                break;
            case "api":
                try
                {
                    web.Start(wereOn);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
                {
                    throw new DeployFailed("The web server did not put the application on the air: " + error.Message);
                }
                did($"put {string.Join(", ", wereOn)} on the air");
                break;
        }
    }

    /// <summary>
    /// Only the latest copies of each target are kept.
    /// </summary>
    private void Prune(DeployTarget target, int keepVersions)
    {
        try
        {
            var root = BackupRoot(target);
            if (!Directory.Exists(root))
                return;
            foreach (var old in Directory.EnumerateDirectories(root).OrderByDescending(path => path, StringComparer.Ordinal).Skip(Math.Max(1, keepVersions)))
                Directory.Delete(old, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The older copies of {Kind} {Name} could not be removed: {Error}", target.Kind, target.Name, error.Message);
        }
    }
}

/// <summary>
/// The web server of Windows, through its own command line tool. The sites are taken off the
/// air by stopping the application pools they run in, which is what lets go of their files.
/// </summary>
internal sealed class IisWebServer : IWebServer
{
    private static string Tool => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "appcmd.exe");

    public void Stop(IReadOnlyList<string> sites)
    {
        foreach (var pool in Pools(sites))
            Run("stop", "apppool", pool, alreadyDone: "already stopped");
    }

    public void Start(IReadOnlyList<string> sites)
    {
        foreach (var pool in Pools(sites))
            Run("start", "apppool", pool, alreadyDone: "already started");
        foreach (var site in sites)
            Run("start", "site", site, alreadyDone: "already started");
    }

    private static List<string> Pools(IReadOnlyList<string> sites)
    {
        var pools = new List<string>();
        foreach (var site in sites)
        {
            var (code, output) = Execute($"list app /site.name:\"{site}\" /text:applicationPool");
            if (code != 0)
                throw new InvalidOperationException($"The site {site} could not be read: {output.Trim()}");
            pools.AddRange(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return [.. pools.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static void Run(string verb, string what, string name, string alreadyDone)
    {
        var (code, output) = Execute($"{verb} {what} \"{name}\"");
        if (code != 0 && !output.Contains(alreadyDone, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"appcmd {verb} {what} {name}: {output.Trim()}");
    }

    private static (int Code, string Output) Execute(string arguments)
    {
        if (!File.Exists(Tool))
            throw new InvalidOperationException("The command line tool of the web server (appcmd.exe) is not on this machine");
        using var process = Process.Start(new ProcessStartInfo(Tool, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("appcmd.exe could not be started");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("appcmd.exe did not answer in a minute");
        }
        return (process.ExitCode, output);
    }
}

internal sealed class NoWebServer : IWebServer
{
    public void Stop(IReadOnlyList<string> sites) => throw new InvalidOperationException("There is no web server to command on this system");

    public void Start(IReadOnlyList<string> sites) => throw new InvalidOperationException("There is no web server to command on this system");
}
