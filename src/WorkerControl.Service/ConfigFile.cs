using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;

namespace WorkerControl.Service;

/// <summary>
/// <c>ConfigWorkers.json</c> on disk. Whatever is written is validated first, goes into place
/// in one step and leaves the previous version beside it.
/// </summary>
internal sealed class ConfigFile(string directory)
{
    public const string Name = "ConfigWorkers.json";

    private readonly object _gate = new();

    public string Path { get; } = System.IO.Path.Combine(directory, Name);

    /// <summary>
    /// An editor may still hold the file for an instant after saving it.
    /// </summary>
    public string Read()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(Path);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Replaces the whole file. Throws <see cref="ConfigException"/> when the new contents
    /// would not be accepted.
    /// </summary>
    public void Write(string json)
    {
        ConfigReader.Parse(json);
        lock (_gate)
        {
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, json);
            if (File.Exists(Path))
                File.Copy(Path, Path + ".bak", overwrite: true);
            File.Move(temporary, Path, overwrite: true);
        }
    }

    /// <summary>
    /// Changes one group and leaves everything else in the file as it is. False when there is
    /// no such group.
    /// </summary>
    public bool ChangeGroup(string group, Action<JObject> change)
    {
        lock (_gate)
        {
            var root = JObject.Parse(Read(), new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
            var found = (root["WorkerGroups"] as JArray)?.OfType<JObject>().FirstOrDefault(item => (string?)item["Name"] == group);
            if (found is null)
                return false;
            change(found);
            Write(root.ToString(Formatting.Indented));
            return true;
        }
    }
}
