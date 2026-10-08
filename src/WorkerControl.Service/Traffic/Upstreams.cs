using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;

namespace WorkerControl.Service.Traffic;

/// <summary>
/// A process of this machine, as much of it as is needed to say who answers on a port.
/// </summary>
public sealed record MachineProcess(int ProcessId, string Name, string? Path);

/// <summary>
/// A site of the web server of the machine (IIS): the port it is bound to and the folder it
/// serves, which is where the application it hosts is.
/// </summary>
public sealed record WebSite(string Name, int Port, string PhysicalPath);

/// <summary>
/// What the machine knows about who listens where.
/// </summary>
public interface IMachineNetwork
{
    /// <summary>
    /// The process that listens on each TCP port.
    /// </summary>
    IReadOnlyDictionary<int, int> Listeners();

    MachineProcess? Process(int processId);

    IReadOnlyList<MachineProcess> Processes();

    IReadOnlyList<WebSite> Sites();
}

/// <summary>
/// Who answers behind an address the reverse proxy sends requests to.
/// </summary>
public sealed record UpstreamOwner(string Upstream, IReadOnlyList<MachineProcess> Processes, string? Site);

/// <summary>
/// Finds the processes behind the addresses the reverse proxy forwards to. A port is either
/// listened on by the application itself, or by the web server of the machine on behalf of a
/// site; in that case the application is whatever runs from the folder of the site.
/// </summary>
public static class UpstreamResolver
{
    /// <summary>
    /// The process of Windows that listens for every site of IIS.
    /// </summary>
    private const int SystemProcess = 4;

    public static IReadOnlyList<UpstreamOwner> Resolve(IEnumerable<string> upstreams, IMachineNetwork machine)
    {
        IReadOnlyDictionary<int, int>? listeners = null;
        IReadOnlyList<WebSite>? sites = null;
        IReadOnlyList<MachineProcess>? processes = null;
        var owners = new List<UpstreamOwner>();

        foreach (var upstream in upstreams.Distinct(StringComparer.Ordinal))
        {
            var colon = upstream.LastIndexOf(':');
            // Only what is on this machine can be looked into.
            if (colon <= 0 || !int.TryParse(upstream[(colon + 1)..], out var port) || !IsLocal(upstream[..colon]))
            {
                owners.Add(new UpstreamOwner(upstream, [], null));
                continue;
            }

            listeners ??= machine.Listeners();
            if (listeners.TryGetValue(port, out var owner) && owner > 0 && owner != SystemProcess && machine.Process(owner) is { } process)
            {
                owners.Add(new UpstreamOwner(upstream, [process], null));
                continue;
            }

            sites ??= machine.Sites();
            var site = sites.FirstOrDefault(candidate => candidate.Port == port);
            if (site is null)
            {
                owners.Add(new UpstreamOwner(upstream, [], null));
                continue;
            }

            processes ??= machine.Processes();
            var folder = site.PhysicalPath.Replace('/', '\\').TrimEnd('\\') + "\\";
            owners.Add(new UpstreamOwner(upstream,
                [.. processes.Where(candidate => candidate.Path is not null && candidate.Path.Replace('/', '\\').StartsWith(folder, StringComparison.OrdinalIgnoreCase))],
                site.Name));
        }
        return owners;
    }

    private static bool IsLocal(string host) =>
        host is "127.0.0.1" or "localhost" or "[::1]" or "::1" || string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The sites out of the configuration file of IIS (<c>applicationHost.config</c>).
    /// </summary>
    public static IReadOnlyList<WebSite> ReadSites(string xml)
    {
        var sites = new List<WebSite>();
        try
        {
            var document = XDocument.Parse(xml);
            foreach (var site in document.Descendants("site"))
            {
                var name = (string?)site.Attribute("name") ?? "";
                // The folder of the root of the site: its application "/" and, in it, the directory "/".
                var folder = site.Elements("application")
                    .Where(application => (string?)application.Attribute("path") == "/")
                    .SelectMany(application => application.Elements("virtualDirectory"))
                    .Where(directory => (string?)directory.Attribute("path") == "/")
                    .Select(directory => (string?)directory.Attribute("physicalPath"))
                    .FirstOrDefault();
                if (string.IsNullOrEmpty(folder))
                    continue;
                folder = Environment.ExpandEnvironmentVariables(folder);

                foreach (var binding in site.Descendants("binding"))
                {
                    // "address:port:host name"
                    var parts = ((string?)binding.Attribute("bindingInformation") ?? "").Split(':');
                    if (parts.Length >= 2 && int.TryParse(parts[^2], out var port))
                        sites.Add(new WebSite(name, port, folder));
                }
            }
        }
        catch (Exception error) when (error is System.Xml.XmlException or InvalidOperationException)
        {
            // A file that cannot be read tells of no sites.
        }
        return sites;
    }
}

/// <summary>
/// Where there is nothing to ask the machine.
/// </summary>
internal sealed class NoMachineNetwork : IMachineNetwork
{
    public IReadOnlyDictionary<int, int> Listeners() => new Dictionary<int, int>();

    public MachineProcess? Process(int processId) => null;

    public IReadOnlyList<MachineProcess> Processes() => [];

    public IReadOnlyList<WebSite> Sites() => [];
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsMachineNetwork : IMachineNetwork
{
    private const int ListenerTable = 3;
    private const int Ipv4 = 2;
    private const int Ipv6 = 23;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    public IReadOnlyDictionary<int, int> Listeners()
    {
        var listeners = new Dictionary<int, int>();
        // Rows of 24 bytes with the port at 8 and the process at 20; of 56 with them at 20 and 52.
        Read(Ipv4, rowSize: 24, portAt: 8, processAt: 20, listeners);
        Read(Ipv6, rowSize: 56, portAt: 20, processAt: 52, listeners);
        return listeners;
    }

    private static void Read(int family, int rowSize, int portAt, int processAt, Dictionary<int, int> listeners)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, ListenerTable, 0);
        if (size <= 0)
            return;
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, family, ListenerTable, 0) != 0)
                return;
            var count = Marshal.ReadInt32(table);
            for (var i = 0; i < count; i++)
            {
                var row = table + 4 + i * rowSize;
                // The port comes in the order of the network: its two bytes swapped.
                var raw = Marshal.ReadInt32(row, portAt);
                var port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                listeners.TryAdd(port, Marshal.ReadInt32(row, processAt));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    public MachineProcess? Process(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return Describe(process);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public IReadOnlyList<MachineProcess> Processes()
    {
        var all = new List<MachineProcess>();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                if (Describe(process) is { } described)
                    all.Add(described);
            }
        }
        return all;
    }

    private static MachineProcess? Describe(Process process)
    {
        try
        {
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // A process of the system that does not let its executable be seen.
            }
            return new MachineProcess(process.Id, process.ProcessName, path);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public IReadOnlyList<WebSite> Sites()
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "config", "applicationHost.config");
            return File.Exists(file) ? UpstreamResolver.ReadSites(File.ReadAllText(file)) : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
