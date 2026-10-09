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

    public static readonly string[] Kinds = ["worker", "service", "api", "frontend"];

    /// <summary>
    /// For how long what was left in the inbox has to be still before it is taken: a folder
    /// that is being copied is not a version yet.
    /// </summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(5);

    private string InboxRoot => Path.GetFullPath(config()?.Transport.Inbox ?? "inbox", directory);

    /// <summary>
    /// Where a new version of something of a kind is left: the folder said for the kind, or the
    /// one of the kind under the general inbox.
    /// </summary>
    private string InboxOf(string kind) =>
        config()?.Transport.Inboxes.TryGetValue(kind, out var said) == true ? Path.GetFullPath(said, directory) : Path.Combine(InboxRoot, kind);

    /// <summary>
    /// Makes the inbox be there, with a folder for each kind, so that whoever brings a new
    /// version finds where to leave it.
    /// </summary>
    public void PrepareInbox()
    {
        foreach (var kind in Kinds)
            Directory.CreateDirectory(InboxOf(kind));
    }

    /// <summary>
    /// What was left for a target: a folder with its name or a zip with its name, under the folder of its kind.
    /// </summary>
    private (string Path, bool IsZip)? Left(DeployTarget target)
    {
        var root = InboxOf(target.Kind);
        if (!Directory.Exists(root) || target.Name.IndexOfAny(['/', '\\']) >= 0)
            return null;
        var folder = Directory.EnumerateDirectories(root).FirstOrDefault(path => Path.GetFileName(path).Equals(target.Name, StringComparison.OrdinalIgnoreCase));
        if (folder is not null && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any())
            return (folder, false);
        var zip = Directory.EnumerateFiles(root, "*.zip").FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals(target.Name, StringComparison.OrdinalIgnoreCase));
        return zip is null ? null : (zip, true);
    }

    private static DateTime LastWrite((string Path, bool IsZip) left) =>
        left.IsZip ? File.GetLastWriteTimeUtc(left.Path) : Directory.EnumerateFiles(left.Path, "*", SearchOption.AllDirectories).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();

    public JObject Targets() => Admin.Ok(answer =>
    {
        answer["Inbox"] = InboxRoot;
        answer["Inboxes"] = new JObject(Kinds.Select(kind => new JProperty(kind, InboxOf(kind))));
        answer["Targets"] = new JArray(catalog.All().Select(target =>
        {
            var described = Describe(target);
            // What is waiting in the inbox for this target, if anything is.
            if (Left(target) is { } left)
                described["Incoming"] = new JObject
                {
                    ["Path"] = left.Path,
                    ["At"] = new DateTimeOffset(LastWrite(left), TimeSpan.Zero),
                    ["Files"] = left.IsZip ? null : Directory.EnumerateFiles(left.Path, "*", SearchOption.AllDirectories).Count()
                };
            return described;
        }));
        // What was left under a name that is no target of this machine: most likely a mistake in the name.
        var known = catalog.All().Select(target => (target.Kind, Name: target.Name.ToLowerInvariant())).ToHashSet();
        answer["Unmatched"] = new JArray(Kinds.Where(kind => Directory.Exists(InboxOf(kind))).SelectMany(kind =>
            Directory.EnumerateFileSystemEntries(InboxOf(kind))
                .Where(path => Directory.Exists(path) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .Where(path => !known.Contains((kind, (Directory.Exists(path) ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path)).ToLowerInvariant())))
                .Select(path => (JToken)path)));
    });

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
    public JObject Capture(string? kind, string? name, bool fromInbox = false)
    {
        if (catalog.Find(kind, name) is not { } target)
            return Admin.Error("not-found", $"There is no {kind} called {name} on this machine");
        if (fromInbox)
            return CaptureIncoming(target);
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

    /// <summary>
    /// Takes what was left in the inbox for a target, packed, and empties its place there: from
    /// now on it is in the hands of whoever asked for it.
    /// </summary>
    private JObject CaptureIncoming(DeployTarget target)
    {
        if (Left(target) is not { } left)
            return Admin.Error("not-found", $"Nothing was left in the inbox for {target.Kind} {target.Name}");
        if (time.GetUtcNow().UtcDateTime - LastWrite(left) < Settle)
            return Admin.Error("invalid-state", "What is in the inbox is still being written; wait for the copy to end");

        var staging = Path.Combine(directory, "out");
        Directory.CreateDirectory(staging);
        var file = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            if (left.IsZip)
                File.Move(left.Path, file);
            else
            {
                if (FileSet.Zip(left.Path, target.Keep, file).Count == 0)
                {
                    File.Delete(file);
                    return Admin.Error("invalid-state", $"The folder {left.Path} has no file to carry");
                }
                Directory.Delete(left.Path, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Admin.Error("invalid-state", "What is in the inbox could not be taken, most likely because it is still open somewhere: " + error.Message);
        }
        return Admin.Ok(answer =>
        {
            // The version the target says it is belongs to what runs, not to what was left to replace it.
            var described = Describe(target);
            described["Version"] = null;
            answer["Target"] = described;
            answer["File"] = file;
            answer["FromInbox"] = true;
        });
    }

    // ---------------------------------------------------------------- settings

    public JObject Settings() => Admin.Ok(answer =>
    {
        var transport = config()?.Transport ?? new TransportConfig();
        answer["Inboxes"] = new JObject(Kinds.Select(kind => new JProperty(kind, new JObject
        {
            ["Path"] = InboxOf(kind),
            ["Exists"] = Directory.Exists(InboxOf(kind)),
            // False while it is the folder of the kind under the general inbox, which nobody had to say.
            ["Said"] = transport.Inboxes.ContainsKey(kind)
        })));
        answer["Keep"] = new JArray(transport.Keep);
        answer["KeepVersions"] = transport.KeepVersions;
    });

    /// <summary>
    /// Says where the inbox of each kind is, and makes the folders. An empty path goes back to
    /// the folder of the kind under the general inbox.
    /// </summary>
    public JObject SetInboxes(JObject? inboxes, ConfigFile file)
    {
        if (inboxes is null)
            return Admin.Error("invalid-request", "\"Inboxes\" is required");
        var said = new Dictionary<string, string>();
        foreach (var item in inboxes.Properties())
        {
            var kind = item.Name.ToLowerInvariant();
            if (!Kinds.Contains(kind) || item.Value.Type is not (JTokenType.String or JTokenType.Null))
                return Admin.Error("invalid-request", "\"Inboxes\" takes worker, service, api and frontend, each with the path of a folder");
            var path = ((string?)item.Value ?? "").Trim();
            if (path.Length > 0 && !Path.IsPathFullyQualified(path))
                return Admin.Error("invalid-request", $"The inbox of {kind} must be a full path, like D:\\Apps\\inbox\\{kind}");
            said[kind] = path;
        }

        // Made before it is written down: a folder that cannot be made is not worth configuring.
        foreach (var (kind, path) in said.Where(pair => pair.Value.Length > 0))
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return Admin.Error("invalid-state", $"The folder {path} could not be made: {error.Message}");
            }
        }

        file.ChangeSection("Transport", transport =>
        {
            var kept = transport["Inboxes"] as JObject ?? new JObject();
            foreach (var (kind, path) in said)
            {
                if (path.Length == 0)
                    kept.Remove(kind);
                else
                    kept[kind] = path;
            }
            if (kept.Count == 0)
                transport.Remove("Inboxes");
            else
                transport["Inboxes"] = kept;
        });
        return Admin.Ok();
    }

    // ---------------------------------------------------------------- the files of the environment

    /// <summary>
    /// What can be read and written as text, and how big it may be.
    /// </summary>
    private static readonly string[] TextExtensions = [".json", ".config", ".xml", ".ini", ".txt", ".yml", ".yaml", ".env", ".properties", ".conf", ".js"];
    private const long MaxTextBytes = 1024 * 1024;

    /// <summary>
    /// The files of a target that belong to the environment and can be edited as text: the
    /// ones no package ever replaces.
    /// </summary>
    private static IEnumerable<string> Editable(DeployTarget target, string folder)
    {
        if (!Directory.Exists(folder))
            return [];
        var root = Path.GetFullPath(folder);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Where(path => FileSet.IsKept(path, target.Keep) && TextExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => new FileInfo(Path.Combine(root, path)).Length <= MaxTextBytes)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public JObject TargetFiles(string? kind, string? name)
    {
        if (catalog.Find(kind, name) is not { } target)
            return Admin.Error("not-found", $"There is no {kind} called {name} on this machine");
        return Admin.Ok(answer =>
        {
            answer["Target"] = Describe(target);
            answer["Files"] = new JArray(target.Paths.SelectMany((folder, instance) => Editable(target, folder).Select(path =>
            {
                var info = new FileInfo(Path.Combine(folder, path));
                return new JObject { ["Instance"] = instance, ["Folder"] = folder, ["Path"] = path, ["Size"] = info.Length, ["ModifiedAt"] = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) };
            })));
        });
    }

    /// <summary>
    /// The file of a target that was asked for, when it is one of the ones that can be edited.
    /// Nothing outside the folder of the target is ever reached this way.
    /// </summary>
    private (string? Full, JObject? Refused) Locate(DeployTarget target, int instance, string? path)
    {
        if (instance < 0 || instance >= target.Paths.Count)
            return (null, Admin.Error("invalid-request", "There is no such instance of this target"));
        var folder = target.Paths[instance];
        var wanted = (path ?? "").Replace('\\', '/').TrimStart('/');
        if (Editable(target, folder).FirstOrDefault(known => known.Equals(wanted, StringComparison.OrdinalIgnoreCase)) is not { } found)
            return (null, Admin.Error("not-found", "This is not a file of the environment that can be edited"));
        return (Path.Combine(folder, found.Replace('/', Path.DirectorySeparatorChar)), null);
    }

    public JObject ReadFile(string? kind, string? name, int instance, string? path)
    {
        if (catalog.Find(kind, name) is not { } target)
            return Admin.Error("not-found", $"There is no {kind} called {name} on this machine");
        var (full, refused) = Locate(target, instance, path);
        if (full is null)
            return refused!;
        var bytes = File.ReadAllBytes(full);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return Admin.Ok(answer =>
        {
            answer["Path"] = path;
            answer["Instance"] = instance;
            answer["Content"] = System.Text.Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            answer["Sha256"] = Sha(bytes);
            answer["ModifiedAt"] = new DateTimeOffset(File.GetLastWriteTimeUtc(full), TimeSpan.Zero);
        });
    }

    /// <summary>
    /// Writes a file of the environment, keeping the one that was there. It is only written
    /// over the version whoever edited it was looking at; and what runs from the folder is
    /// started again when that is asked for.
    /// </summary>
    public JObject WriteFile(string? kind, string? name, int instance, string? path, string? content, string? expected, bool restart, CancellationToken stopping)
    {
        if (catalog.Find(kind, name) is not { } target)
            return Admin.Error("not-found", $"There is no {kind} called {name} on this machine");
        if (content is null)
            return Admin.Error("invalid-request", "\"Content\" is required");
        var (full, refused) = Locate(target, instance, path);
        if (full is null)
            return refused!;

        var before = File.ReadAllBytes(full);
        if (!string.IsNullOrEmpty(expected) && !string.Equals(expected, Sha(before), StringComparison.OrdinalIgnoreCase))
            return Admin.Error("invalid-state", "The file was changed by somebody else since it was opened; open it again");
        if (System.Text.Encoding.UTF8.GetByteCount(content) > MaxTextBytes)
            return Admin.Error("invalid-request", "The file is too big to be written this way");
        if (Path.GetExtension(full).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var _ = System.Text.Json.JsonDocument.Parse(content, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (System.Text.Json.JsonException error)
            {
                return Admin.Error("invalid-request", "This is not valid JSON, and was not written: " + error.Message);
            }
        }

        var copy = Path.Combine(directory, "backup", "config", target.Kind, string.Concat(target.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')),
            $"{time.GetUtcNow():yyyyMMdd-HHmmss}", instance.ToString(), (path ?? "").Replace('\\', '/').TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllBytes(copy, before);

        // As it was: with the mark at the beginning or without it.
        var bom = before.Length >= 3 && before[0] == 0xEF && before[1] == 0xBB && before[2] == 0xBF;
        var bytes = new System.Text.UTF8Encoding(bom).GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(content)).ToArray();
        var aside = full + ".tmp";
        File.WriteAllBytes(aside, bytes);
        File.Move(aside, full, overwrite: true);

        IReadOnlyList<string> steps = [];
        string? problem = null;
        if (restart)
        {
            try
            {
                steps = deployer.RestartAsync(target, stopping).GetAwaiter().GetResult();
            }
            catch (DeployFailed failed)
            {
                problem = failed.Message;
            }
        }
        return Admin.Ok(answer =>
        {
            answer["Sha256"] = Sha(bytes);
            answer["Backup"] = copy;
            answer["Restarted"] = restart && problem is null;
            answer["RestartProblem"] = problem;
            answer["Messages"] = new JArray(steps);
        });
    }

    /// <summary>
    /// Puts back what a package replaced, from the copy that was kept then.
    /// </summary>
    public JObject Revert(string? kind, string? name, string? copy, string package, int item, bool force, CancellationToken stopping)
    {
        if (catalog.Find(kind, name) is not { } target)
            return Admin.Error("not-found", $"There is no {kind} called {name} on this machine");
        if (string.IsNullOrEmpty(copy))
            return Admin.Error("invalid-request", "\"Backup\" is required");

        var result = deployer.RevertAsync(target, copy, package, item, stopping, force).GetAwaiter().GetResult();
        return Admin.Ok(answer =>
        {
            answer["Applied"] = result.Ok;
            answer["Did"] = result.Ok ? "reverted" : null;
            answer["Problem"] = result.Error;
            answer["Backup"] = result.Backup;
            answer["Messages"] = new JArray(result.Steps);
        });
    }

    public JObject Deploy(string? kind, string? name, string? file, string package, int item, bool force, CancellationToken stopping)
    {
        if (catalog.Find(kind, name) is not { } target)
            return Admin.Error("not-found", $"There is no {kind} called {name} on this machine");
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
            return Admin.Error("invalid-request", "The files of the item are not where they were said to be");

        var result = deployer.DeployAsync(target, file, package, item, config()?.Transport.KeepVersions ?? 3, stopping, force).GetAwaiter().GetResult();
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
