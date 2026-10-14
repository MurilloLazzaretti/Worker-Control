using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;

namespace WorkerControl.Service;

/// <summary>
/// The configuration on disk, in two files. <c>ConfigWorkers.json</c> is of the supervisor:
/// where the broker is, the groups, the services it looks after. <c>Environment.json</c> is of
/// the environment: the database, the transport, the traffic, the web applications and the
/// proxy. Whatever is written is validated first, goes into place in one step and leaves the
/// previous version beside it.
/// </summary>
internal sealed class ConfigFile(string directory)
{
    public const string Name = "ConfigWorkers.json";
    public const string EnvironmentName = "Environment.json";

    /// <summary>
    /// The sections that belong to the environment, and live in <see cref="EnvironmentName"/>.
    /// </summary>
    public static readonly string[] EnvironmentSections = ["Database", "Transport", "Traffic", "Frontends", "Proxy"];

    private readonly object _gate = new();

    public string Path { get; } = System.IO.Path.Combine(directory, Name);

    public string EnvironmentPath { get; } = System.IO.Path.Combine(directory, EnvironmentName);

    private static JObject Parse(string text)
    {
        try
        {
            return JObject.Parse(text, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
        }
        catch (JsonReaderException error)
        {
            throw new ConfigException("The file is not valid JSON: " + error.Message);
        }
    }

    private static string ReadText(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 5 && File.Exists(path))
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// The file of the environment; an empty object while there is none.
    /// </summary>
    public string ReadEnvironment() => File.Exists(EnvironmentPath) ? ReadText(EnvironmentPath) : "{}";

    /// <summary>
    /// The two files as one, which is what the configuration is read from. What the environment
    /// file has stands over the same section left in the other.
    /// </summary>
    public static string Merge(string workers, string environment)
    {
        var root = Parse(workers);
        foreach (var section in Parse(environment).Properties().Where(property => EnvironmentSections.Contains(property.Name)))
            root[section.Name] = section.Value.DeepClone();
        return root.ToString(Formatting.Indented);
    }

    public string ReadMerged() => Merge(Read(), ReadEnvironment());

    /// <summary>
    /// Moves to the file of the environment whatever of it is found in <c>ConfigWorkers.json</c>:
    /// left there by an older version, or written there by hand. True when something moved.
    /// </summary>
    public bool Separate()
    {
        lock (_gate)
        {
            var root = Parse(Read());
            var found = root.Properties().Where(property => EnvironmentSections.Contains(property.Name)).ToList();
            if (found.Count == 0)
                return false;
            var environment = Parse(ReadEnvironment());
            foreach (var section in found)
            {
                environment[section.Name] = section.Value.DeepClone();
                section.Remove();
            }
            // The environment first: with it written, nothing is lost whatever happens next.
            Replace(EnvironmentPath, environment.ToString(Formatting.Indented), keepPrevious: true);
            Replace(Path, root.ToString(Formatting.Indented), keepPrevious: true);
            return true;
        }
    }

    private static void Replace(string path, string json, bool keepPrevious)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        if (keepPrevious && File.Exists(path))
            File.Copy(path, path + ".bak", overwrite: true);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Replaces the file of the environment. Throws <see cref="ConfigException"/> when the
    /// configuration it would make would not be accepted.
    /// </summary>
    public void WriteEnvironment(string json)
    {
        var given = Parse(json);
        if (given.Properties().FirstOrDefault(property => !EnvironmentSections.Contains(property.Name)) is { } stranger)
            throw new ConfigException($"\"{stranger.Name}\" is not of the environment; it belongs in {Name}");
        lock (_gate)
        {
            ConfigReader.Parse(Merge(Read(), json));
            Replace(EnvironmentPath, given.ToString(Formatting.Indented), keepPrevious: true);
        }
    }

    /// <summary>
    /// An editor may still hold the file for an instant after saving it.
    /// </summary>
    public string Read() => ReadText(Path);

    /// <summary>
    /// Replaces the whole file. Throws <see cref="ConfigException"/> when the new contents
    /// would not be accepted.
    /// </summary>
    public void Write(string json) => Write(json, keepPrevious: true);

    private void Write(string json, bool keepPrevious)
    {
        lock (_gate)
        {
            // What it says of the environment stands over the file of the environment, where it goes next.
            var environment = Parse(ReadEnvironment());
            foreach (var section in Parse(json).Properties().Where(property => EnvironmentSections.Contains(property.Name)))
                environment[section.Name] = section.Value.DeepClone();
            ConfigReader.Parse(Merge(json, environment.ToString()));
            Replace(Path, json, keepPrevious);
            Separate();
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
            Separate();
            var root = Parse(ReadEnvironment());
            if (root["Database"] is not JObject database || database["Password"] is not JValue { Type: JTokenType.String } password)
                return false;
            var typed = (string)password!;
            if (typed.Length == 0 || Database.Secret.IsProtected(typed))
                return false;

            database["Password"] = protector.Protect(typed);
            Replace(EnvironmentPath, root.ToString(Formatting.Indented), keepPrevious: false);
            // A copy left by an earlier change may carry the same password.
            foreach (var previous in new[] { EnvironmentPath + ".bak", Path + ".bak" })
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
            if (EnvironmentSections.Contains(name))
            {
                Separate();
                var environment = Parse(ReadEnvironment());
                if (environment[name] is not JObject part)
                    environment[name] = part = new JObject();
                change(part);
                WriteEnvironment(environment.ToString(Formatting.Indented));
                return;
            }
            var root = Parse(Read());
            if (root[name] is not JObject section)
                root[name] = section = new JObject();
            change(section);
            Write(root.ToString(Formatting.Indented));
        }
    }

    /// <summary>
    /// Adds a group to the end of the list. False when there is one of that name already.
    /// </summary>
    public bool AddGroup(JObject group)
    {
        lock (_gate)
        {
            var root = JObject.Parse(Read(), new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
            if (root["WorkerGroups"] is not JArray list)
                root["WorkerGroups"] = list = [];
            if (list.OfType<JObject>().Any(item => string.Equals((string?)item["Name"], (string?)group["Name"], StringComparison.OrdinalIgnoreCase)))
                return false;
            list.Add(group);
            Write(root.ToString(Formatting.Indented));
            return true;
        }
    }

    public bool RemoveGroup(string group)
    {
        lock (_gate)
        {
            var root = JObject.Parse(Read(), new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
            var found = (root["WorkerGroups"] as JArray)?.OfType<JObject>().FirstOrDefault(item => (string?)item["Name"] == group);
            if (found is null)
                return false;
            found.Remove();
            Write(root.ToString(Formatting.Indented));
            return true;
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
