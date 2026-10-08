using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Win32;
using WorkerControl.Core;

namespace WorkerControl.Service;

/// <summary>
/// The services of this machine, through the service control manager of Windows.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceManager : IServiceManager
{
    public IReadOnlyList<InstalledService> List()
    {
        var services = new List<InstalledService>();
        foreach (var controller in ServiceController.GetServices())
        {
            using (controller)
            {
                try
                {
                    // The process is not asked for here: it is one more call per service, and
                    // whoever lists only wants to choose.
                    services.Add(new InstalledService(controller.ServiceName, controller.DisplayName, ExecutableOf(controller.ServiceName),
                        Map(controller.Status), StartTypeOf(controller), 0, 0));
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception)
                {
                    // Removed while being listed.
                }
            }
        }
        return services;
    }

    public InstalledService? Find(string name)
    {
        try
        {
            using var controller = new ServiceController(name);
            var status = Query(controller);
            return new InstalledService(controller.ServiceName, controller.DisplayName, ExecutableOf(controller.ServiceName),
                Map((ServiceControllerStatus)status.dwCurrentState), StartTypeOf(controller), (int)status.dwProcessId, (int)status.dwWin32ExitCode);
        }
        catch (InvalidOperationException error) when (error.InnerException is Win32Exception { NativeErrorCode: 1060 })
        {
            // ERROR_SERVICE_DOES_NOT_EXIST
            return null;
        }
    }

    public void Start(string name)
    {
        using var controller = new ServiceController(name);
        controller.Start();
    }

    public void Stop(string name)
    {
        using var controller = new ServiceController(name);
        controller.Stop();
    }

    private static ServiceState Map(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Running => ServiceState.Running,
        ServiceControllerStatus.Stopped => ServiceState.Stopped,
        ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => ServiceState.Starting,
        ServiceControllerStatus.StopPending => ServiceState.Stopping,
        ServiceControllerStatus.Paused or ServiceControllerStatus.PausePending => ServiceState.Paused,
        _ => ServiceState.Stopped
    };

    private static string StartTypeOf(ServiceController controller)
    {
        try
        {
            return controller.StartType.ToString();
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// The executable of a service, without the quotes and the arguments its command line
    /// may have.
    /// </summary>
    private static string? ExecutableOf(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            return ServiceCommandLine.Executable(key?.GetValue("ImagePath") as string);
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(SafeHandle service, int infoLevel, ref SERVICE_STATUS_PROCESS status, int size, out int needed);

    private static SERVICE_STATUS_PROCESS Query(ServiceController controller)
    {
        var status = new SERVICE_STATUS_PROCESS();
        if (!QueryServiceStatusEx(controller.ServiceHandle, 0, ref status, Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return status;
    }
}

/// <summary>
/// Where a machine has no services to speak of. Nothing is listed and nothing can be asked.
/// </summary>
internal sealed class NoServiceManager : IServiceManager
{
    public IReadOnlyList<InstalledService> List() => [];

    public InstalledService? Find(string name) => null;

    public void Start(string name) => throw new InvalidOperationException("Services can only be controlled on Windows");

    public void Stop(string name) => throw new InvalidOperationException("Services can only be controlled on Windows");
}

internal static class ServiceCommandLine
{
    /// <summary>
    /// The executable out of the command line a service is registered with:
    /// <c>"C:\Program Files\App\app.exe" --service</c> gives <c>C:\Program Files\App\app.exe</c>.
    /// </summary>
    public static string? Executable(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;
        var text = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : text.Trim('"');
        }
        // Without quotes, the path ends where the extension of the executable does.
        var extension = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        var path = extension > 0 ? text[..(extension + 4)] : text.Split(' ')[0];
        // The kernel writes some paths its own way.
        return path.StartsWith(@"\??\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>
    /// Whether a file is inside one of the folders, whatever the case and the kind of slash.
    /// </summary>
    public static bool IsUnder(string? path, IEnumerable<string> folders)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        var file = Normalize(path);
        return folders.Any(folder => file.StartsWith(Normalize(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string path) => path.Trim().Replace('/', '\\');
}

internal sealed class ProcessInspector : IProcessInspector
{
    public ProcessInfo? Inspect(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var started = process.StartTime.ToUniversalTime();
            var processor = process.TotalProcessorTime;
            var memory = process.WorkingSet64;
            var threads = process.Threads.Count;
            var handles = process.HandleCount;

            // What the process started counts as part of it: a service installed through a
            // wrapper is the wrapper, and the program that matters is its child.
            var children = new List<string>();
            foreach (var id in OperatingSystem.IsWindows() ? ProcessTree.Descendants(processId) : [])
            {
                try
                {
                    using var child = Process.GetProcessById(id);
                    // A number may have been given again to somebody who has nothing to do with this.
                    if (child.StartTime.ToUniversalTime() < started)
                        continue;
                    processor += child.TotalProcessorTime;
                    memory += child.WorkingSet64;
                    threads += child.Threads.Count;
                    handles += child.HandleCount;
                    children.Add(child.ProcessName);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Gone, or not ours to read.
                }
            }
            return new ProcessInfo(started, processor, memory, threads, handles, children.Count == 0 ? null : children);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Who started whom, among the processes of the machine.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProcessTree
{
    private const uint SnapProcess = 0x00000002;
    private static readonly IntPtr Invalid = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// The processes started by a process, and by those, and so on. Empty when it cannot be told.
    /// </summary>
    public static List<int> Descendants(int processId)
    {
        var childrenOf = new Dictionary<int, List<int>>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == Invalid)
            return [];
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
                return [];
            do
            {
                var parent = (int)entry.th32ParentProcessID;
                if (!childrenOf.TryGetValue(parent, out var list))
                    childrenOf[parent] = list = [];
                list.Add((int)entry.th32ProcessID);
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var found = new List<int>();
        var pending = new Queue<int>([processId]);
        var seen = new HashSet<int> { processId };
        while (pending.Count > 0)
        {
            foreach (var child in childrenOf.GetValueOrDefault(pending.Dequeue()) ?? [])
            {
                if (seen.Add(child))
                {
                    found.Add(child);
                    pending.Enqueue(child);
                }
            }
        }
        return found;
    }
}

/// <summary>
/// The checks a watched service may have: a port that takes a connection, or an address that
/// answers with success.
/// </summary>
internal sealed class ServiceProbe : IServiceProbe, IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http = new(new SocketsHttpHandler { ConnectTimeout = Patience }) { Timeout = Patience };

    public (bool Ok, string Detail) Check(ServiceCheck check)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            if (check.Tcp is { } address)
            {
                var colon = address.LastIndexOf(':');
                using var client = new TcpClient();
                if (!client.ConnectAsync(address[..colon], int.Parse(address[(colon + 1)..])).Wait(Patience))
                    return (false, $"{address} did not take a connection in {Patience.TotalSeconds:0} s");
                return (true, $"{address} took a connection in {watch.ElapsedMilliseconds} ms");
            }

            using var response = _http.GetAsync(check.Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode
                ? (true, $"answered {(int)response.StatusCode} in {watch.ElapsedMilliseconds} ms")
                : (false, $"answered {(int)response.StatusCode}");
        }
        catch (Exception error) when (error is AggregateException or SocketException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return (false, (error.InnerException ?? error).Message);
        }
    }

    public void Dispose() => _http.Dispose();
}
