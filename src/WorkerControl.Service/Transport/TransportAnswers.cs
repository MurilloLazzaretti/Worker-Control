using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;

namespace WorkerControl.Service.Transport;

/// <summary>
/// What the service does for the carrying of changes that is about files: telling what can be
/// replaced on this machine, packing what is running, and replacing it by what a package brings.
/// </summary>
internal sealed class TransportWork(TargetCatalog catalog, Deployer deployer, Func<WorkerControlConfig?> config, string directory, TimeProvider time)
{
    private static JObject Describe(DeployTarget target) => new()
    {
        ["Kind"] = target.Kind,
        ["Name"] = target.Name,
        ["Paths"] = new JArray(target.Paths),
        ["Version"] = target.Version,
        ["Groups"] = new JArray(target.Groups),
        ["Service"] = target.Service,
        ["Sites"] = new JArray(target.Sites),
        ["Problem"] = target.Problem ?? (target.Paths.FirstOrDefault(path => !Directory.Exists(path)) is { } absent ? $"The folder {absent} does not exist" : null)
    };

    private static JArray Files(IEnumerable<FileEntry> files) =>
        new(files.Select(file => new JObject { ["Path"] = file.Path, ["Size"] = file.Size, ["Sha256"] = file.Sha256 }));

    public JObject Targets() => Admin.Ok(answer => answer["Targets"] = new JArray(catalog.All().Select(Describe)));

    /// <summary>
    /// One target and the files it has now, without what belongs to the environment.
    /// </summary>
    public JObject Target(string? kind, string? name)
    {
        if (catalog.Find(kind, name) is not { } target)
            return Admin.Error("not-found", $"There is no {kind} called {name} on this machine");
        var folder = target.Paths.FirstOrDefault(Directory.Exists);
        return Admin.Ok(answer =>
        {
            answer["Target"] = Describe(target);
            answer["Files"] = folder is null ? new JArray() : Files(FileSet.List(folder, target.Keep));
        });
    }

    /// <summary>
    /// Packs what the target is running now, to go into a package. The file is left where the
    /// panel, on the same machine, picks it up.
    /// </summary>
    public JObject Capture(string? kind, string? name)
    {
        if (catalog.Find(kind, name) is not { } target)
            return Admin.Error("not-found", $"There is no {kind} called {name} on this machine");
        if (target.Problem is not null)
            return Admin.Error("invalid-state", target.Problem);
        if (target.Paths.FirstOrDefault(Directory.Exists) is not { } folder)
            return Admin.Error("invalid-state", "The folder of this target does not exist on this machine");

        var staging = Path.Combine(directory, "out");
        Directory.CreateDirectory(staging);
        // What was packed long ago and never picked up.
        foreach (var old in Directory.EnumerateFiles(staging, "*.zip").Where(file => File.GetLastWriteTimeUtc(file) < time.GetUtcNow().UtcDateTime.AddDays(-1)))
            File.Delete(old);

        var file = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".zip");
        var files = FileSet.Zip(folder, target.Keep, file);
        if (files.Count == 0)
        {
            File.Delete(file);
            return Admin.Error("invalid-state", $"The folder {folder} has no file to carry");
        }
        using var stream = File.OpenRead(file);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return Admin.Ok(answer =>
        {
            answer["Target"] = Describe(target);
            answer["File"] = file;
            answer["Size"] = stream.Length;
            answer["Sha256"] = hash;
            answer["Files"] = Files(files);
        });
    }

    public JObject Deploy(string? kind, string? name, string? file, string package, int item, CancellationToken stopping)
    {
        if (catalog.Find(kind, name) is not { } target)
            return Admin.Error("not-found", $"There is no {kind} called {name} on this machine");
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
            return Admin.Error("invalid-request", "The files of the item are not where they were said to be");

        var result = deployer.DeployAsync(target, file, package, item, config()?.Transport.KeepVersions ?? 3, stopping).GetAwaiter().GetResult();
        return Admin.Ok(answer =>
        {
            answer["Applied"] = result.Ok;
            answer["Did"] = result.Ok ? "replaced" : null;
            answer["Problem"] = result.Error;
            answer["Replaced"] = result.Replaced;
            answer["Backup"] = result.Backup;
            answer["Messages"] = new JArray(result.Steps);
            answer["Added"] = result.Files?.Added;
            answer["Changed"] = result.Files?.Changed;
            answer["Removed"] = result.Files?.Removed;
        });
    }
}
