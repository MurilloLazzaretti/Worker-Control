using WorkerControl.Core;

namespace WorkerControl.Core.Tests;

/// <summary>
/// What makes the number of workers of a group vary: boost windows, the queue, and the
/// replacement of workers one by one.
/// </summary>
public class ScalingTests
{
    private static DateTime At(DayOfWeek day, string time)
    {
        // 2026-01-04 is a Sunday.
        var date = new DateTime(2026, 1, 4).AddDays((int)day);
        return date + TimeSpan.Parse(time);
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, "09:00:00", 2)]
    [InlineData(DayOfWeek.Monday, "07:59:59", 0)]
    [InlineData(DayOfWeek.Saturday, "09:00:00", 0)]
    // The night window starts on Friday and is still Friday's after midnight.
    [InlineData(DayOfWeek.Friday, "23:00:00", 5)]
    [InlineData(DayOfWeek.Saturday, "01:00:00", 5)]
    [InlineData(DayOfWeek.Saturday, "23:00:00", 0)]
    [InlineData(DayOfWeek.Friday, "01:00:00", 0)]
    // Both windows are open: they do not add up.
    [InlineData(DayOfWeek.Friday, "22:30:00", 5)]
    public void Boost_windows_follow_the_days_of_the_week_and_the_largest_one_wins(DayOfWeek day, string time, int expected)
    {
        var group = Bench.Group() with
        {
            BoostWindows =
            [
                new BoostWindow { Workers = 2, StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(18, 0, 0), Days = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday } },
                new BoostWindow { Workers = 5, StartTime = new TimeSpan(22, 0, 0), EndTime = new TimeSpan(2, 0, 0), Days = new HashSet<DayOfWeek> { DayOfWeek.Friday } },
                new BoostWindow { Workers = 3, StartTime = new TimeSpan(22, 15, 0), EndTime = new TimeSpan(22, 45, 0), Days = new HashSet<DayOfWeek> { DayOfWeek.Friday } }
            ]
        };

        Assert.Equal(expected, group.BoostWorkersAt(At(day, time)));
    }

    [Fact]
    public void The_window_of_1x_and_the_new_ones_work_together()
    {
        var group = Bench.Group() with
        {
            Boost = new BoostConfig { Enabled = true, BoostWorkers = 4, StartTime = new TimeSpan(9, 0, 0), EndTime = new TimeSpan(10, 0, 0) },
            BoostWindows = [new BoostWindow { Workers = 1, StartTime = new TimeSpan(9, 30, 0), EndTime = new TimeSpan(11, 0, 0) }]
        };

        Assert.Equal(4, group.BoostWorkersAt(At(DayOfWeek.Monday, "09:45:00")));
        Assert.Equal(1, group.BoostWorkersAt(At(DayOfWeek.Monday, "10:30:00")));
        Assert.Equal(0, group.BoostWorkersAt(At(DayOfWeek.Monday, "11:00:00")));
    }

    [Fact]
    public void A_queue_piling_up_brings_extra_workers_up_to_the_limit_of_the_group()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(Bench.Group(workers: 2) with
        {
            QueueScaling = new QueueScalingConfig { Queue = "Orders", PendingPerWorker = 50, MaxWorkers = 5, Cooldown = TimeSpan.FromMinutes(2) }
        });
        bench.Advance(5);
        Assert.Equal(2, bench.Host.Running.Count());

        bench.Queues.Pending["Orders"] = 49;
        bench.Advance(5);
        Assert.Equal(2, bench.Host.Running.Count());

        bench.Queues.Pending["Orders"] = 120;
        bench.Advance(5);
        Assert.Equal(4, bench.Host.Running.Count());
        Assert.Equal(2, bench.Status().ScaleWorkers);
        Assert.Equal(4, bench.Status().DesiredWorkers);

        // Far more than the group may have.
        bench.Queues.Pending["Orders"] = 5000;
        bench.Advance(5);
        Assert.Equal(5, bench.Host.Running.Count());
        Assert.Equal(3, bench.Status().ScaleWorkers);
    }

    [Fact]
    public void The_extra_workers_only_leave_some_time_after_the_queue_stops_asking_for_them()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(Bench.Group(workers: 1) with
        {
            QueueScaling = new QueueScalingConfig { Queue = "Orders", PendingPerWorker = 10, MaxWorkers = 4, Cooldown = TimeSpan.FromMinutes(2) }
        });
        bench.Queues.Pending["Orders"] = 30;
        bench.Advance(10);
        Assert.Equal(4, bench.Host.Running.Count());

        bench.Queues.Pending["Orders"] = 0;
        bench.Advance(TimeSpan.FromSeconds(110));
        Assert.Empty(bench.Broker.SafeStops);

        // A burst in the meantime starts the wait again.
        bench.Queues.Pending["Orders"] = 30;
        bench.Advance(1);
        bench.Queues.Pending["Orders"] = 0;
        bench.Advance(TimeSpan.FromSeconds(110));
        Assert.Empty(bench.Broker.SafeStops);

        bench.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(3, bench.Broker.SafeStops.Count);
        Assert.Equal(0, bench.Status().ScaleWorkers);
    }

    [Fact]
    public void While_the_broker_cannot_tell_how_full_the_queue_is_nothing_changes()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(Bench.Group(workers: 1) with
        {
            QueueScaling = new QueueScalingConfig { Queue = "Orders", PendingPerWorker = 10, MaxWorkers = 3, Cooldown = TimeSpan.FromSeconds(10) }
        });
        bench.Queues.Pending["Orders"] = 20;
        bench.Advance(10);
        Assert.Equal(3, bench.Host.Running.Count());

        bench.Queues.Pending["Orders"] = null;
        bench.Advance(120);
        Assert.Equal(3, bench.Host.Running.Count());
        Assert.Empty(bench.Broker.SafeStops);
    }

    [Fact]
    public void A_scheduled_recycle_replaces_the_workers_one_by_one_without_leaving_the_group_short()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        // The clock starts at 10:00; a time already past today must not fire at start.
        bench.Apply(Bench.Group(workers: 3) with { Recycle = new RecycleConfig { Time = new TimeSpan(9, 0, 0) } });
        bench.Advance(60);
        var original = bench.Host.Started.ToList();
        Assert.Equal(3, original.Count);
        Assert.Empty(bench.Of(EventKind.RecycleStarted));

        bench.Apply(Bench.Group(workers: 3) with { Recycle = new RecycleConfig { Time = new TimeSpan(10, 30, 0) } });
        bench.Advance(TimeSpan.FromMinutes(29.5));

        var least = int.MaxValue;
        for (var step = 0; step < 240 && original.Any(process => !process.HasExited); step++)
        {
            bench.Advance(0.25);
            // A worker asked to stop leaves a moment later.
            foreach (var process in bench.Host.Running.Where(process => bench.Broker.SafeStops.Contains(process.Id)).ToList())
                process.Exit(0);
            least = Math.Min(least, bench.Status().Workers.Count(worker => worker.State is WorkerState.Starting or WorkerState.Up));
        }
        bench.Advance(1);

        Assert.Single(bench.Of(EventKind.RecycleStarted));
        Assert.Single(bench.Of(EventKind.RecycleFinished));
        Assert.All(original, process => Assert.True(process.HasExited));
        Assert.Equal(6, bench.Host.Started.Count);
        Assert.Equal(3, bench.Host.Running.Count());
        Assert.True(least >= 3, $"the group got down to {least} workers");
        Assert.Empty(bench.Of(EventKind.WorkerCrashed));
        Assert.False(bench.Status().Recycling);

        // Not again on the same day.
        bench.Advance(TimeSpan.FromHours(2));
        Assert.Single(bench.Of(EventKind.RecycleStarted));
    }

    [Fact]
    public void A_worker_is_only_replaced_once_its_substitute_is_up()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(Bench.Group(workers: 1));
        bench.Advance(5);
        var old = bench.Host.Started.Single();

        // The substitute is slow to answer.
        bench.Broker.AutoAnswer = pid => pid == old.Id;
        Assert.True(bench.Supervisor.RestartWorker(old.Id));
        bench.Advance(20);

        Assert.Equal(2, bench.Host.Started.Count);
        Assert.Empty(bench.Broker.SafeStops);
        Assert.True(bench.Status().Workers.Single(worker => worker.ProcessId == old.Id).BeingReplaced);

        bench.Broker.AnswerAll();
        bench.Advance(1);
        Assert.Equal([old.Id], bench.Broker.SafeStops);
        Assert.False(bench.Supervisor.RestartWorker(424242));
    }

    [Fact]
    public void A_group_can_be_restarted_on_request()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(Bench.Group("A", 2), Bench.Group("B", 1));
        bench.Advance(5);
        var original = bench.Host.Started.ToList();

        Assert.True(bench.Supervisor.RestartGroup("A"));
        Assert.False(bench.Supervisor.RestartGroup("Nope"));
        for (var step = 0; step < 80; step++)
        {
            bench.Advance(0.25);
            foreach (var process in bench.Host.Running.Where(process => bench.Broker.SafeStops.Contains(process.Id)).ToList())
                process.Exit(0);
        }

        Assert.True(original[0].HasExited);
        Assert.True(original[1].HasExited);
        Assert.False(original[2].HasExited);
        Assert.Equal(2, bench.Status("A").Workers.Count);
        Assert.Equal(5, bench.Host.Started.Count);
    }

    [Fact]
    public void Health_is_measured_for_the_workers_in_service()
    {
        var bench = new Bench();
        bench.Apply(Bench.Group(workers: 2));
        bench.Tick();
        bench.Tick();
        bench.Advance(1.5);
        bench.Broker.Answer(bench.Host.Started[0].Id);
        bench.Advance(10);

        var samples = bench.Supervisor.SampleHealth();

        Assert.Equal(2, samples.Count);
        var up = samples.Single(sample => sample.ProcessId == bench.Host.Started[0].Id);
        Assert.Equal(WorkerState.Up, up.State);
        Assert.Equal("Orders", up.Group);
        Assert.InRange(up.Uptime.TotalSeconds, 11, 13);
        Assert.InRange(up.KeepAliveLatency!.Value.TotalSeconds, 1, 2.5);
        Assert.Equal(64 * 1024 * 1024, up.Usage!.Value.MemoryBytes);
        Assert.Null(samples.Single(sample => sample != up).KeepAliveLatency);
    }
}
