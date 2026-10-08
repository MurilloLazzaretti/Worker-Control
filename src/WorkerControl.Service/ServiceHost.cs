using System.Reflection;
using Serilog;
using Serilog.Events;
using WorkerControl.Core;

namespace WorkerControl.Service;

public static class ServiceHost
{
    public static string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

    public static IHost Build(string[] args, Action<HostApplicationBuilder>? configure = null)
    {
        // A Windows service starts in System32, so everything is resolved from the executable folder.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        configure?.Invoke(builder);

        var section = builder.Configuration.GetSection(ServiceOptions.Section);
        builder.Services.Configure<ServiceOptions>(section);
        var options = section.Get<ServiceOptions>() ?? new ServiceOptions();

        builder.Services.AddWindowsService(service => service.ServiceName = "WorkerControlService");

        // Asking every worker to leave, and waiting for them, takes longer than the default.
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromMinutes(3));

        var logFile = Path.Combine(Path.GetFullPath(options.LogDirectory, options.ResolveDataDirectory()), "workercontrol-.log");
        var logLevel = Enum.TryParse<LogEventLevel>(options.LogLevel, ignoreCase: true, out var level) ? level : LogEventLevel.Information;
        builder.Services.AddSerilog(log => log
            .MinimumLevel.Is(logLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(
                logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: options.LogRetentionDays,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"),
            // The logger belongs to this host alone; a process that hosts something else as well
            // (the tests do) must not have one closing the log of the other.
            preserveStaticLogger: true,
            writeToProviders: true);

        builder.Services.AddSingleton(TimeProvider.System);
        // Who listens on which port, to tell what is behind the reverse proxy.
        if (!builder.Services.Any(service => service.ServiceType == typeof(Traffic.IMachineNetwork)))
        {
            if (OperatingSystem.IsWindows())
                builder.Services.AddSingleton<Traffic.IMachineNetwork, Traffic.WindowsMachineNetwork>();
            else
                builder.Services.AddSingleton<Traffic.IMachineNetwork, Traffic.NoMachineNetwork>();
        }
        // The services of the machine, which only Windows has. A test may put its own first.
        if (!builder.Services.Any(service => service.ServiceType == typeof(IServiceManager)))
        {
            if (OperatingSystem.IsWindows())
                builder.Services.AddSingleton<IServiceManager, WindowsServiceManager>();
            else
                builder.Services.AddSingleton<IServiceManager, NoServiceManager>();
        }
        builder.Services.AddHostedService<SupervisorService>();

        var host = builder.Build();
        var lifetimeLog = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WorkerControl");
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
            lifetimeLog.LogInformation("WorkerControl {Version} started", Version));
        return host;
    }
}
