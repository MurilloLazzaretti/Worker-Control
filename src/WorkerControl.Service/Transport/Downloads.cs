using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WorkerControl.Service.Transport;

/// <summary>
/// The list of what each file of a folder of downloads is, kept in the folder for whoever
/// downloads them: <c>{ "apps": [ { "name", "file", "version", "size", "sha256", "publishedAt" } ] }</c>.
/// </summary>
internal static class Downloads
{
    private static JArray Apps(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            // The dates are kept as they were written, not read into the clock of this machine.
            using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader)["apps"] is JArray apps ? apps : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The version written for each file, by the name of the file.
    /// </summary>
    public static Dictionary<string, string?> Versions(string folder, string versionsFile) =>
        Apps(Path.Combine(folder, versionsFile)).OfType<JObject>()
            .Where(app => app.Value<string>("file") is { Length: > 0 })
            .GroupBy(app => app.Value<string>("file")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value<string>("version"), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Writes what a file is now; with no file there any more, takes it out of the list.
    /// </summary>
    public static void Record(string folder, string versionsFile, string file, string? version, DateTimeOffset now)
    {
        var path = Path.Combine(folder, versionsFile);
        var apps = Apps(path);
        foreach (var old in apps.OfType<JObject>().Where(app => string.Equals(app.Value<string>("file"), file, StringComparison.OrdinalIgnoreCase)).ToList())
            old.Remove();
        var full = Path.Combine(folder, file);
        if (File.Exists(full))
        {
            using var stream = File.OpenRead(full);
            apps.Add(new JObject
            {
                ["name"] = Path.GetFileNameWithoutExtension(file),
                ["file"] = file,
                ["version"] = version,
                ["size"] = stream.Length,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(stream)),
                ["publishedAt"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)
            });
        }
        var sorted = new JArray(apps.OfType<JObject>().OrderBy(app => app.Value<string>("file"), StringComparer.OrdinalIgnoreCase));
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, new JObject { ["apps"] = sorted }.ToString(Formatting.Indented));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// The types the reverse proxy needs to say for the files it serves from a folder of downloads.
    /// </summary>
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["apk"] = "application/vnd.android.package-archive",
        ["aab"] = "application/octet-stream",
        ["ipa"] = "application/octet-stream",
        ["exe"] = "application/octet-stream",
        ["msi"] = "application/x-msi",
        ["zip"] = "application/zip",
        ["pdf"] = "application/pdf"
    };

    /// <summary>
    /// Makes the folder ready to be served: the folder itself and an empty list of versions,
    /// leaving alone whatever is there. Gives what the reverse proxy needs to serve it under
    /// <c>/name/</c>, to be put in its configuration by whoever looks after it.
    /// </summary>
    public static (bool Created, string Snippet) Prepare(string name, string root, string files, string versionsFile)
    {
        var created = !Directory.Exists(root);
        Directory.CreateDirectory(root);
        if (!File.Exists(Path.Combine(root, versionsFile)))
            File.WriteAllText(Path.Combine(root, versionsFile), new JObject { ["apps"] = new JArray() }.ToString(Formatting.Indented));

        var extension = Path.GetExtension(files).TrimStart('.');
        var type = Types.GetValueOrDefault(extension, "application/octet-stream");
        var folder = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/') + "/";
        var path = "/" + string.Concat(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')) + "/";
        var snippet = $$"""
            # Files to download: {{name}}
            location {{path}} {
                alias {{folder}};
                types {
                    {{type}} {{(extension is "" or "*" ? "bin" : extension)}};
                    application/json json;
                }
                default_type application/octet-stream;
                autoindex off;
                # Asked again each time, so that a new version and the list of versions are seen at once.
                add_header Cache-Control "no-cache";
            }
            """;
        return (created, snippet);
    }

    /// <summary>
    /// A name a file can have in the folder: no path, no wildcard, and of the kind the folder distributes.
    /// </summary>
    public static bool Fits(string file, string pattern) =>
        file.Length > 0 && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && file.IndexOfAny(['/', '\\', '*', '?']) < 0 && file != "." && file != ".."
        && System.Text.RegularExpressions.Regex.IsMatch(file, "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
