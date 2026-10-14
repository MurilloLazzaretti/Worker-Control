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
    /// A name a file can have in the folder: no path, no wildcard, and of the kind the folder distributes.
    /// </summary>
    public static bool Fits(string file, string pattern) =>
        file.Length > 0 && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && file.IndexOfAny(['/', '\\', '*', '?']) < 0 && file != "." && file != ".."
        && System.Text.RegularExpressions.Regex.IsMatch(file, "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
