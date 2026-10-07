using System.ComponentModel;
using System.Diagnostics;
using WorkerControl.Core;

namespace WorkerControl.Service;

/// <summary>
/// Worker processes on the operating system.
/// </summary>
internal sealed class ProcessHost : IProcessHost
{
    public IWorkerProcess Start(GroupConfig group)
    {
        var path = group.ApplicationFullPath;
        var info = new ProcessStartInfo(path)
        {
            Arguments = group.Arguments,
            WorkingDirectory = group.WorkingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(path)) ?? "",
            UseShellExecute = false,
            // A console application gets a console of its own, hidden, as it did under 1.x.
            // Without one, a worker that waits on its console input would end right away.
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        var process = Process.Start(info) ?? throw new InvalidOperationException("The process was not created");
        return new WorkerProcess(process, DateTimeOffset.Now);
    }

    public IWorkerProcess? Attach(WorkerRecord record, GroupConfig group)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(record.ProcessId);
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            // The number alone is not enough: the system gives it to another process once the
            // first is gone. The instant of creation tells them apart.
            if (process.HasExited || (process.StartTime - record.StartTime.LocalDateTime).Duration() > TimeSpan.FromSeconds(1) || !RunsExecutable(process, group))
            {
                process.Dispose();
                return null;
            }
            return new WorkerProcess(process, record.StartTime);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            process.Dispose();
            return null;
        }
    }

    private static bool RunsExecutable(Process process, GroupConfig group)
    {
        var expected = Path.GetFullPath(group.ApplicationFullPath);
        try
        {
            if (process.MainModule?.FileName is { } actual)
                return string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            // The module list of some processes cannot be read; the name is the next best thing.
        }
        return string.Equals(process.ProcessName, Path.GetFileNameWithoutExtension(expected), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class WorkerProcess : IWorkerProcess
    {
        private readonly Process _process;

        public WorkerProcess(Process process, DateTimeOffset fallbackStartTime)
        {
            _process = process;
            Id = process.Id;
            try
            {
                StartTime = new DateTimeOffset(process.StartTime);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                // Gone before it could be asked.
                StartTime = fallbackStartTime;
            }
        }

        public int Id { get; }

        public DateTimeOffset StartTime { get; }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception)
                {
                    return true;
                }
            }
        }

        public int? ExitCode
        {
            get
            {
                try
                {
                    return _process.HasExited ? _process.ExitCode : null;
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception)
                {
                    return null;
                }
            }
        }

        public void Kill()
        {
            if (!HasExited)
                _process.Kill(entireProcessTree: true);
        }
    }
}
