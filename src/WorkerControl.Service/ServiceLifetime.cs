using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace WorkerControl.Service;

/// <summary>
/// The service as Windows sees it, with one thing more than the one of .NET: it is told that the
/// machine is going down before the other services are (the preshutdown), and given the time to
/// stop the workers the same way a stop asked by hand does. At the plain shutdown every service
/// is told at once and has a few seconds: ZapMQ would be gone before the workers were asked to
/// leave, and what they were doing would be cut in the middle.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ServiceLifetime : WindowsServiceLifetime
{
    private const int ControlPreshutdown = 0x0F;
    private const int AcceptPreshutdown = 0x100;
    private const int StopPending = 3;
    private const int ServiceWin32OwnProcess = 0x10;

    /// <summary>
    /// How long Windows waits for the service at the preshutdown. The same as the host gives
    /// itself to stop: past that the workers that are left were ended anyway.
    /// </summary>
    private readonly TimeSpan _patience;
    private readonly ILogger _logger;
    private readonly bool _accepted;

    public ServiceLifetime(IHostEnvironment environment, IHostApplicationLifetime applicationLifetime, ILoggerFactory loggerFactory,
        IOptions<HostOptions> hostOptions, IOptions<WindowsServiceLifetimeOptions> serviceOptions)
        : base(environment, applicationLifetime, loggerFactory, hostOptions, serviceOptions)
    {
        _patience = hostOptions.Value.ShutdownTimeout;
        _logger = loggerFactory.CreateLogger("WorkerControl");
        // ServiceBase has no switch for the preshutdown; what it says it accepts is this field,
        // reported to Windows once the service has started.
        var accepted = typeof(System.ServiceProcess.ServiceBase).GetField("_acceptedCommands", BindingFlags.Instance | BindingFlags.NonPublic);
        if (accepted?.GetValue(this) is int commands)
        {
            accepted.SetValue(this, commands | AcceptPreshutdown);
            _accepted = true;
        }
    }

    protected override void OnStart(string[] args)
    {
        base.OnStart(args);
        if (!_accepted)
        {
            _logger.LogWarning("Windows will not tell the service ahead of a shutdown; the workers may be ended by it");
            return;
        }
        if (!SetPreshutdownTimeout(ServiceName, _patience))
            _logger.LogWarning("The time Windows waits for the service at a shutdown could not be set; it keeps its default");
    }

    protected override void OnCustomCommand(int command)
    {
        if (command != ControlPreshutdown)
        {
            base.OnCustomCommand(command);
            return;
        }

        _logger.LogInformation("Windows is shutting down: stopping the workers before it does");
        using var reporting = new CancellationTokenSource();
        // Windows takes a service that says nothing for a while as hung.
        var progress = Task.Run(async () =>
        {
            for (var checkpoint = 1; !reporting.IsCancellationRequested; checkpoint++)
            {
                Report(checkpoint);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), reporting.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });
        try
        {
            // The same as a stop asked by hand: it returns when the host has stopped.
            OnStop();
        }
        finally
        {
            reporting.Cancel();
            progress.Wait(TimeSpan.FromSeconds(1));
        }
    }

    private void Report(int checkpoint)
    {
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = StopPending,
            ControlsAccepted = 0,
            CheckPoint = checkpoint,
            WaitHint = 15_000
        };
        SetServiceStatus(ServiceHandle, ref status);
    }

    private static bool SetPreshutdownTimeout(string name, TimeSpan timeout)
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
            return false;
        try
        {
            var service = OpenServiceW(manager, name, ServiceChangeConfig);
            if (service == IntPtr.Zero)
                return false;
            try
            {
                var info = new PreshutdownInfo { Timeout = (uint)timeout.TotalMilliseconds };
                return ChangeServiceConfig2W(service, ServiceConfigPreshutdownInfo, ref info);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceConfigPreshutdownInfo = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PreshutdownInfo
    {
        public uint Timeout;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ChangeServiceConfig2W(IntPtr service, uint level, ref PreshutdownInfo info);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
