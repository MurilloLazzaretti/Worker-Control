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
    public void Write(string json) => Write(json, keepPrevious: true);

    private void Write(string json, bool keepPrevious)
    {
        ConfigReader.Parse(json);
        lock (_gate)
        {
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, json);
            if (keepPrevious && File.Exists(Path))
                File.Copy(Path, Path + ".bak", overwrite: true);
            File.Move(temporary, Path, overwrite: true);
        }
    }

    /// <summary>
    /// Replaces a password of the database that was typed in the file by one only this machine
    /// reads back. The file as it was is not kept beside it: it had the password in it. True
    /// when the file was changed.
    /// </summary>
    public bool ProtectSecrets(Database.ISecretProtector protector)
    {
        if (!protector.Available)
            return false;
        lock (_gate)
        {
            var root = JObject.Parse(Read(), new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
            if (root["Database"] is not JObject database || database["Password"] is not JValue { Type: JTokenType.String } password)
                return false;
            var typed = (string)password!;
            if (typed.Length == 0 || Database.Secret.IsProtected(typed))
                return false;

            database["Password"] = protector.Protect(typed);
            Write(root.ToString(Formatting.Indented), keepPrevious: false);
            // A copy left by an earlier change may carry the same password.
            var previous = Path + ".bak";
            if (File.Exists(previous) && File.ReadAllText(previous).Contains(JsonConvert.ToString(typed), StringComparison.Ordinal))
                File.Delete(previous);
            return true;
        }
    }

    /// <summary>
    /// Changes one group and leaves everything else in the file as it is. False when there is
    /// no such group.
    /// </summary>
    /// <summary>
    /// Changes one section at the root of the file, making it when it is not there, and leaves
    /// everything else as it is.
    /// </summary>
    public void ChangeSection(string name, Action<JObject> change)
    {
        lock (_gate)
        {
            var root = JObject.Parse(Read(), new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
            if (root[name] is not JObject section)
                root[name] = section = new JObject();
            change(section);
            Write(root.ToString(Formatting.Indented));
        }
    }

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
