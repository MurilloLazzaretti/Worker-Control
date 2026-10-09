using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace WorkerControl.Service.Transport;

/// <summary>
/// One file of a folder, by its path from the folder with forward slashes.
/// </summary>
public sealed record FileEntry(string Path, long Size, string Sha256);

/// <summary>
/// What replacing the files of a folder did.
/// </summary>
public sealed record MirrorResult(int Added, int Changed, int Removed, int Unchanged);

/// <summary>
/// The files of a published folder: listing them, packing them and putting the files of a
/// package in their place. What belongs to the environment is left alone in every one of these.
/// </summary>
internal static class FileSet
{
    /// <summary>
    /// True when the path is one of what is kept: a file name with wildcards (<c>appsettings*.json</c>),
    /// a path from the folder with wildcards (<c>config/*.json</c>), or a folder at any depth,
    /// written with a slash at the end (<c>logs/</c>).
    /// </summary>
    public static bool IsKept(string path, IReadOnlyList<string> keep)
    {
        path = path.Replace('\\', '/').TrimStart('/');
        var name = path[(path.LastIndexOf('/') + 1)..];
        foreach (var raw in keep)
        {
            var pattern = raw.Replace('\\', '/').Trim();
            if (pattern.Length == 0)
                continue;
            if (pattern.EndsWith('/'))
            {
                var folder = pattern.TrimStart('/');
                if (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || path.Contains("/" + folder, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (Like(pattern.Contains('/') ? path : name, pattern.TrimStart('/')))
                return true;
        }
        return false;
    }

    private static bool Like(string text, string pattern) =>
        Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Hash(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

    private static IEnumerable<(string Full, string Relative)> Files(string folder)
    {
        var root = Path.GetFullPath(folder);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => (file, Path.GetRelativePath(root, file).Replace('\\', '/')))
            .OrderBy(pair => pair.Item2, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The files of a folder that are not kept, each with what tells one version of it from another.
    /// </summary>
    public static List<FileEntry> List(string folder, IReadOnlyList<string> keep)
    {
        var entries = new List<FileEntry>();
        foreach (var (full, relative) in Files(folder))
        {
            if (IsKept(relative, keep))
                continue;
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            entries.Add(new FileEntry(relative, stream.Length, Hash(stream)));
        }
        return entries;
    }

    /// <summary>
    /// Packs the files of a folder that are not kept, at the root of the zip.
    /// </summary>
    public static List<FileEntry> Zip(string folder, IReadOnlyList<string> keep, string zipPath)
    {
        var entries = new List<FileEntry>();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (full, relative) in Files(folder))
        {
            if (IsKept(relative, keep))
                continue;
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
            entry.LastWriteTime = File.GetLastWriteTime(full);
            using (var target = entry.Open())
                stream.CopyTo(target);
            stream.Position = 0;
            entries.Add(new FileEntry(relative, stream.Length, Hash(stream)));
        }
        return entries;
    }

    /// <summary>
    /// The files a zip carries. One that would land outside the folder it is put in makes the
    /// whole zip be refused.
    /// </summary>
    public static List<FileEntry> ListZip(string zipPath)
    {
        var entries = new List<FileEntry>();
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries.Where(entry => !entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\')))
        {
            var path = Safe(entry.FullName);
            using var stream = entry.Open();
            entries.Add(new FileEntry(path, entry.Length, Hash(stream)));
        }
        return [.. entries.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)];
    }

    private static string Safe(string name)
    {
        var path = name.Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(part => part is ".." or "."))
            throw new InvalidDataException($"The package carries a file that would land outside its folder: {name}");
        return path;
    }

    /// <summary>
    /// Makes a folder hold exactly the files of a zip: what is new is put, what changed is
    /// replaced, what the zip does not have is taken away. What is kept is never touched,
    /// whether the zip has it or not.
    /// </summary>
    public static MirrorResult Mirror(string zipPath, string folder, IReadOnlyList<string> keep)
    {
        var root = Path.GetFullPath(folder);
        Directory.CreateDirectory(root);
        var before = List(root, keep).ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var brought = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int added = 0, changed = 0, unchanged = 0;

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            // Looked at whole before anything is written: a bad zip changes nothing.
            foreach (var entry in zip.Entries)
                Safe(entry.FullName);

            foreach (var entry in zip.Entries.Where(entry => !entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\')))
            {
                var path = Safe(entry.FullName);
                if (IsKept(path, keep))
                    continue;
                brought.Add(path);
                var target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

                if (before.TryGetValue(path, out var had) && had.Size == entry.Length)
                {
                    using var incoming = entry.Open();
                    if (Hash(incoming) == had.Sha256)
                    {
                        unchanged++;
                        continue;
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                if (had is null)
                    added++;
                else
                    changed++;
            }
        }

        var removed = 0;
        foreach (var gone in before.Keys.Where(path => !brought.Contains(path)))
        {
            File.Delete(Path.Combine(root, gone.Replace('/', Path.DirectorySeparatorChar)));
            removed++;
        }
        // The folders that were left with nothing in them, the deepest first.
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);

        return new MirrorResult(added, changed, removed, unchanged);
    }

    /// <summary>
    /// A copy of a whole folder, configuration and all, to go back to.
    /// </summary>
    public static void Copy(string from, string to)
    {
        var root = Path.GetFullPath(from);
        Directory.CreateDirectory(to);
        foreach (var (full, relative) in Files(root))
        {
            var target = Path.Combine(to, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(full, target, overwrite: true);
        }
    }

    /// <summary>
    /// Puts the files of a folder back as a copy of it has them, leaving alone what belongs to
    /// the environment: the configuration stays as it is now, whatever the copy has of it.
    /// </summary>
    public static MirrorResult RestoreKeeping(string copy, string folder, IReadOnlyList<string> keep)
    {
        var root = Path.GetFullPath(folder);
        Directory.CreateDirectory(root);
        var wanted = List(copy, keep).ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var now = List(root, keep).ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        int added = 0, changed = 0, removed = 0, unchanged = 0;

        foreach (var (path, entry) in wanted)
        {
            if (now.TryGetValue(path, out var has) && has.Sha256 == entry.Sha256)
            {
                unchanged++;
                continue;
            }
            var target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(copy, path.Replace('/', Path.DirectorySeparatorChar)), target, overwrite: true);
            if (has is null)
                added++;
            else
                changed++;
        }
        foreach (var gone in now.Keys.Where(path => !wanted.ContainsKey(path)))
        {
            File.Delete(Path.Combine(root, gone.Replace('/', Path.DirectorySeparatorChar)));
            removed++;
        }
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        return new MirrorResult(added, changed, removed, unchanged);
    }

    /// <summary>
    /// Puts a folder back as a copy of it was: every file of the copy, and nothing else.
    /// </summary>
    public static void Restore(string copy, string folder)
    {
        var root = Path.GetFullPath(folder);
        var kept = Files(copy).Select(pair => pair.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(root))
            foreach (var (full, relative) in Files(root).ToList())
                if (!kept.Contains(relative))
                    File.Delete(full);
        Copy(copy, root);
    }
}
