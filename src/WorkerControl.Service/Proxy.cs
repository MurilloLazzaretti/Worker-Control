using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using WorkerControl.Core;

namespace WorkerControl.Service;

/// <summary>
/// Running the executable of the reverse proxy, to test its configuration and to make it read it again.
/// </summary>
public interface IProxyTool
{
    (int Code, string Output) Run(string executable, string arguments, string directory);
}

internal sealed class ProxyTool : IProxyTool
{
    public (int Code, string Output) Run(string executable, string arguments, string directory)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, arguments)
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        }) ?? throw new InvalidOperationException("The executable of the proxy could not be started");
        // It says what it has to say on the error output, whether it went well or not.
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("The proxy did not answer in half a minute");
        }
        return (process.ExitCode, (output + error.GetAwaiter().GetResult()).Trim());
    }
}

/// <summary>
/// The configuration of the reverse proxy (NGINX) seen and changed from the panel. A file is
/// only left changed when the proxy itself says the configuration is still good; and nothing
/// is put in force until somebody asks for it to be read again.
/// </summary>
internal sealed partial class ProxyWork(Func<WorkerControlConfig?> config, IServiceManager services, IProxyTool tool, string directory, TimeProvider time)
{
    private const long MaxBytes = 2 * 1024 * 1024;

    private sealed record Place(string Root, string Config, string? Executable, string? Service);

    /// <summary>
    /// Where the proxy is: as the configuration says, or worked out from the access log it
    /// writes, which is in the folder of logs beside the one of its configuration.
    /// </summary>
    private Place? Where()
    {
        var now = config();
        var said = now?.Proxy;
        var root = said?.Config is { } file ? Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(file)))
            : said?.Executable is { } executable ? Path.GetDirectoryName(Path.GetFullPath(executable))
            : now?.Traffic?.AccessLog is { } log ? Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(log)))
            : null;
        if (root is null)
            return null;
        var main = said?.Config ?? Path.Combine(root, "conf", "nginx.conf");
        var program = said?.Executable ?? new[] { "nginx.exe", "nginx" }.Select(name => Path.Combine(root, name)).FirstOrDefault(File.Exists);
        var service = said?.Service ?? now?.Services.Items.Select(item => item.Name).FirstOrDefault(name => name.Contains("nginx", StringComparison.OrdinalIgnoreCase));
        return new Place(root, Path.GetFullPath(main), program, service);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// The main file and every file it includes, by their path from the folder of the configuration.
    /// </summary>
    private static List<string> Files(Place place)
    {
        var folder = Path.GetDirectoryName(place.Config)!;
        var found = new List<string>();
        var pending = new Queue<string>([place.Config]);
        while (pending.Count > 0 && found.Count < 200)
        {
            var file = pending.Dequeue();
            if (!File.Exists(file) || found.Contains(file, StringComparer.OrdinalIgnoreCase))
                continue;
            found.Add(file);
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }
            foreach (Match include in Include().Matches(Comment().Replace(text, "")))
            {
                // Relative to the folder of the main file, as the proxy reads it; with wildcards in the name of the file.
                var pattern = include.Groups[1].Value.Trim().Trim('"', '\'');
                var full = Path.IsPathFullyQualified(pattern) ? pattern : Path.Combine(folder, pattern.Replace('/', Path.DirectorySeparatorChar));
                var parent = Path.GetDirectoryName(full);
                if (parent is null || !Directory.Exists(parent))
                    continue;
                foreach (var match in Directory.EnumerateFiles(parent, Path.GetFileName(full)).Order(StringComparer.OrdinalIgnoreCase))
                    // The lists of types and the like are part of the proxy, not of what is configured in it.
                    if (Path.GetExtension(match).Equals(".conf", StringComparison.OrdinalIgnoreCase))
                        pending.Enqueue(match);
            }
        }
        return found;
    }

    private static string Relative(Place place, string file) => Path.GetRelativePath(Path.GetDirectoryName(place.Config)!, file).Replace('\\', '/');

    public JObject Describe()
    {
        if (Where() is not { } place)
            return Admin.Ok(answer => answer["Configured"] = false);
        var service = place.Service is null ? null : services.Find(place.Service);
        return Admin.Ok(answer =>
        {
            answer["Configured"] = true;
            answer["Root"] = place.Root;
            answer["Config"] = place.Config;
            answer["Executable"] = place.Executable;
            answer["Service"] = place.Service is null ? null : new JObject { ["Name"] = place.Service, ["State"] = service?.State.ToString() ?? "Missing" };
            answer["Problem"] = !File.Exists(place.Config) ? $"The file {place.Config} does not exist" : place.Executable is null ? "The executable of the proxy was not found beside its configuration" : null;
            answer["Files"] = new JArray(Files(place).Select(file =>
            {
                var info = new FileInfo(file);
                return new JObject { ["Path"] = Relative(place, file), ["Size"] = info.Length, ["ModifiedAt"] = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), ["Main"] = file.Equals(place.Config, StringComparison.OrdinalIgnoreCase) };
            }));
        });
    }

    private (Place? Place, string? File, JObject? Refused) Locate(string? path)
    {
        if (Where() is not { } place)
            return (null, null, Admin.Error("not-configured", "It is not known where the proxy is on this machine"));
        var wanted = (path ?? "").Replace('\\', '/');
        var file = Files(place).FirstOrDefault(known => Relative(place, known).Equals(wanted, StringComparison.OrdinalIgnoreCase));
        return file is null ? (place, null, Admin.Error("not-found", "This is not a file of the configuration of the proxy")) : (place, file, null);
    }

    public JObject Read(string? path)
    {
        var (_, file, refused) = Locate(path);
        if (file is null)
            return refused!;
        var bytes = File.ReadAllBytes(file);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return Admin.Ok(answer =>
        {
            answer["Path"] = path;
            answer["Content"] = System.Text.Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            answer["Sha256"] = Sha(bytes);
        });
    }

    /// <summary>
    /// Asks the proxy whether its configuration, as it is on disk, is good.
    /// </summary>
    private static (bool Ok, string Output) Check(Place place, IProxyTool tool)
    {
        if (place.Executable is null)
            return (false, "The executable of the proxy was not found, so the configuration cannot be tested");
        var (code, output) = tool.Run(place.Executable, $"-t -p \"{place.Root}\" -c \"{place.Config}\"", place.Root);
        return (code == 0, output);
    }

    public JObject Test()
    {
        if (Where() is not { } place)
            return Admin.Error("not-configured", "It is not known where the proxy is on this machine");
        var (ok, output) = Check(place, tool);
        return Admin.Ok(answer =>
        {
            answer["Valid"] = ok;
            answer["Output"] = output;
        });
    }

    /// <summary>
    /// Writes a file of the configuration and asks the proxy whether the whole of it is still
    /// good. If it is not, the file goes back to what it was and what the proxy said is given.
    /// </summary>
    public JObject Write(string? path, string? content, string? expected)
    {
        var (place, file, refused) = Locate(path);
        if (file is null)
            return refused!;
        if (content is null)
            return Admin.Error("invalid-request", "\"Content\" is required");
        if (System.Text.Encoding.UTF8.GetByteCount(content) > MaxBytes)
            return Admin.Error("invalid-request", "The file is too big to be written this way");

        var before = File.ReadAllBytes(file);
        if (!string.IsNullOrEmpty(expected) && !string.Equals(expected, Sha(before), StringComparison.OrdinalIgnoreCase))
            return Admin.Error("invalid-state", "The file was changed by somebody else since it was opened; open it again");

        var copy = Path.Combine(directory, "backup", "proxy", $"{time.GetUtcNow():yyyyMMdd-HHmmss}", Relative(place!, file).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllBytes(copy, before);

        // Written as it was, with the mark at the beginning or without it: the proxy does not take the mark.
        var bom = before.Length >= 3 && before[0] == 0xEF && before[1] == 0xBB && before[2] == 0xBF;
        var bytes = new System.Text.UTF8Encoding(bom).GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(content)).ToArray();
        File.WriteAllBytes(file, bytes);

        var (ok, output) = Check(place!, tool);
        if (!ok)
        {
            File.WriteAllBytes(file, before);
            return Admin.Ok(answer =>
            {
                answer["Saved"] = false;
                answer["Output"] = output;
            });
        }
        return Admin.Ok(answer =>
        {
            answer["Saved"] = true;
            answer["Output"] = output;
            answer["Sha256"] = Sha(bytes);
            answer["Backup"] = copy;
        });
    }

    /// <summary>
    /// Makes the proxy read its configuration again without stopping. It is tested first: a
    /// proxy asked to read a bad configuration keeps the one it has, and says nothing.
    /// </summary>
    public JObject Reload()
    {
        if (Where() is not { } place)
            return Admin.Error("not-configured", "It is not known where the proxy is on this machine");
        var (ok, output) = Check(place, tool);
        if (!ok)
            return Admin.Ok(answer =>
            {
                answer["Reloaded"] = false;
                answer["Output"] = output;
            });
        var (code, said) = tool.Run(place.Executable!, $"-s reload -p \"{place.Root}\" -c \"{place.Config}\"", place.Root);
        return Admin.Ok(answer =>
        {
            answer["Reloaded"] = code == 0;
            answer["Output"] = code == 0 ? output : said;
        });
    }

    /// <summary>
    /// Stops the service of the proxy and starts it again. Everything behind it is out of
    /// reach for the moment that takes, so it is not done with a configuration that is not good.
    /// </summary>
    public async Task<JObject> RestartAsync(CancellationToken stopping)
    {
        if (Where() is not { } place)
            return Admin.Error("not-configured", "It is not known where the proxy is on this machine");
        if (place.Service is null || services.Find(place.Service) is null)
            return Admin.Error("invalid-state", "The Windows service of the proxy is not known; say it in \"Proxy\".\"Service\"");
        var (ok, output) = Check(place, tool);
        if (!ok)
            return Admin.Ok(answer =>
            {
                answer["Restarted"] = false;
                answer["Output"] = output;
            });

        async Task<bool> Until(ServiceState state)
        {
            for (var attempt = 0; attempt < 120; attempt++)
            {
                if (services.Find(place.Service) is { } now && now.State == state)
                    return true;
                await Task.Delay(500, stopping);
            }
            return false;
        }

        try
        {
            if (services.Find(place.Service) is { State: not ServiceState.Stopped })
            {
                services.Stop(place.Service);
                if (!await Until(ServiceState.Stopped))
                    return Admin.Error("failed", "The service of the proxy did not stop in time");
            }
            services.Start(place.Service);
            var up = await Until(ServiceState.Running);
            return Admin.Ok(answer =>
            {
                answer["Restarted"] = up;
                answer["Output"] = up ? output : "The service of the proxy did not start in time";
            });
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Admin.Error("failed", "The service of the proxy refused: " + error.Message);
        }
    }

    [GeneratedRegex(@"^\s*include\s+([^;]+);", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Include();

    [GeneratedRegex(@"#[^\n]*")]
    private static partial Regex Comment();
}
