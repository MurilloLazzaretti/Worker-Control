using WorkerControl.Core;

namespace WorkerControl.Core.Tests;

public class SupervisorTests
{
    [Fact]
    public void Starts_the_workers_a_group_asks_for()
    {
        var bench = new Bench();
        bench.Apply(Bench.Group(workers: 3));

        bench.Tick();

        Assert.Equal(3, bench.Host.Started.Count);
        Assert.All(bench.Status().Workers, worker => Assert.Equal(WorkerState.Starting, worker.State));
        Assert.Equal(3, bench.Of(EventKind.WorkerStarted).Count());
    }

    [Fact]
    public void Starts_a_few_at_a_time_across_all_groups()
    {
        var bench = new Bench();
        bench.Apply(new WorkerControlConfig
        {
            StartBatchSize = 4,
            StartBatchInterval = TimeSpan.FromSeconds(2),
            Groups = [Bench.Group("A", 5), Bench.Group("B", 5)]
        });

        bench.Tick();
        Assert.Equal(4, bench.Host.Started.Count);

        bench.Advance(1.75);
        Assert.Equal(4, bench.Host.Started.Count);

        bench.Advance(0.25);
        Assert.Equal(8, bench.Host.Started.Count);

        bench.Advance(2);
        Assert.Equal(10, bench.Host.Started.Count);

        bench.Advance(10);
        Assert.Equal(10, bench.Host.Started.Count);
    }

    [Fact]
    public void A_worker_is_up_once_it_answers_and_is_asked_again_every_monitoring_rate()
    {
        var bench = new Bench();
        bench.Apply(Bench.Group(workers: 1));
        bench.Tick();
        var pid = bench.Host.Started.Single().Id;
        bench.Tick();
        Assert.Single(bench.Broker.Pending);

        bench.Broker.Answer(pid);
        bench.Tick();
        Assert.Equal(WorkerState.Up, bench.Status().Workers.Single().State);
        Assert.Single(bench.Of(EventKind.WorkerUp));
        Assert.Empty(bench.Broker.Pending);

        bench.Advance(29);
        Assert.Empty(bench.Broker.Pending);
        bench.Advance(1);
        Assert.Single(bench.Broker.Pending);
    }

    [Fact]
    public void A_worker_that_goes_away_is_replaced_at_once()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(Bench.Group(workers: 2));
        bench.Advance(120);
        var first = bench.Host.Started[0];

        first.Exit(3);
        bench.Tick();

        Assert.Equal(3, bench.Host.Started.Count);
        Assert.Equal(2, bench.Host.Running.Count());
        var crash = Assert.Single(bench.Of(EventKind.WorkerCrashed));
        Assert.Equal(first.Id, crash.ProcessId);
        Assert.Contains("exit code 3", crash.Detail);
    }

    [Fact]
    public void A_worker_that_stops_answering_is_ended_and_replaced()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(Bench.Group(workers: 1));
        bench.Advance(120);
        var worker = bench.Host.Started.Single();

        bench.Broker.AutoAnswer = _ => false;
        bench.Advance(30);
        bench.Broker.Expire(worker.Id);
        bench.Tick();
        Assert.False(worker.HasExited);

        // The verdict waits a moment, in case it was the broker that failed.
        bench.Advance(Supervisor.VerdictDelay + TimeSpan.FromSeconds(0.5));
        Assert.True(worker.HasExited);
        Assert.Equal(1, worker.Kills);
        Assert.Single(bench.Of(EventKind.WorkerHung));
        Assert.Equal(2, bench.Host.Started.Count);
    }

    [Fact]
    public void A_new_worker_is_not_ended_for_being_slow_to_answer_the_first_time()
    {
        var bench = new Bench();
        bench.Apply(Bench.Group(workers: 1) with { TimeoutKeepAlive = TimeSpan.FromSeconds(15), StartupGrace = TimeSpan.FromSeconds(60) });
        bench.Tick();
        var worker = bench.Host.Started.Single();

        // Three keep-alives go unanswered while the worker is still getting ready.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            bench.Advance(15);
            bench.Broker.Expire(worker.Id);
            bench.Tick();
            Assert.False(worker.HasExited);
            Assert.Single(bench.Broker.Pending);
        }

        bench.Broker.Answer(worker.Id);
        bench.Tick();
        Assert.Equal(WorkerState.Up, bench.Status().Workers.Single().State);
        Assert.Single(bench.Host.Started);
    }

    [Fact]
    public void A_new_worker_that_never_answers_is_ended_when_its_grace_is_over()
    {
        var bench = new Bench();
        bench.Apply(Bench.Group(workers: 1) with { StartupGrace = TimeSpan.FromSeconds(60) });
        bench.Tick();
        var worker = bench.Host.Started.Single();

        bench.Advance(60);
        Assert.False(worker.HasExited);

        bench.Advance(0.5);
        Assert.True(worker.HasExited);
        Assert.Single(bench.Of(EventKind.WorkerStartTimedOut));
    }

    [Fact]
    public void Nobody_is_ended_for_what_the_broker_did()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(Bench.Group(workers: 2));
        bench.Advance(120);

        // Keep-alives are out when the broker goes away; they expire unanswered.
        bench.Broker.AutoAnswer = _ => false;
        bench.Advance(30);
        Assert.Equal(2, bench.Broker.Pending.Count);
        foreach (var worker in bench.Host.Started)
            bench.Broker.Expire(worker.Id);
        // The failure of the broker is only noticed after the keep-alives expired.
        bench.Advance(2);
        bench.Broker.GoDown();
        bench.Advance(300);

        Assert.Equal(2, bench.Host.Running.Count());
        Assert.Equal(2, bench.Host.Started.Count);
        Assert.Empty(bench.Of(EventKind.WorkerHung));

        // Back again, the workers are asked and judged normally.
        bench.Broker.ComeBack();
        bench.Broker.AutoAnswer = _ => true;
        bench.Advance(60);
        Assert.Equal(2, bench.Host.Running.Count());
        Assert.Empty(bench.Of(EventKind.WorkerHung));
    }

    [Fact]
    public void Without_the_broker_workers_still_start_and_are_still_replaced_when_they_go_away()
    {
        var bench = new Bench();
        bench.Broker.GoDown();
        bench.Apply(Bench.Group(workers: 2));

        bench.Advance(300);
        Assert.Equal(2, bench.Host.Running.Count());
        Assert.Empty(bench.Of(EventKind.WorkerStartTimedOut));

        bench.Host.Started[0].Exit(1);
        bench.Advance(1);
        Assert.Equal(2, bench.Host.Running.Count());
        Assert.Equal(3, bench.Host.Started.Count);
    }

    [Fact]
    public void Workers_in_excess_are_asked_to_stop_oldest_first_and_leave()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(new WorkerControlConfig { StartBatchSize = 1, StartBatchInterval = TimeSpan.FromSeconds(1), Groups = [Bench.Group(workers: 3)] });
        bench.Advance(10);
        var oldest = bench.Host.Started[0];

        bench.Apply(new WorkerControlConfig { Groups = [Bench.Group(workers: 2)] });
        bench.Tick();

        Assert.Equal([oldest.Id], bench.Broker.SafeStops);
        Assert.Equal(WorkerState.Stopping, bench.Status().Workers.Single(worker => worker.ProcessId == oldest.Id).State);

        oldest.Exit(0);
        bench.Tick();
        Assert.Single(bench.Of(EventKind.WorkerStopped));
        Assert.Equal(2, bench.Status().Workers.Count);
        Assert.Equal(3, bench.Host.Started.Count);
    }

    [Fact]
    public void A_worker_that_ignores_the_safe_stop_is_ended_when_the_time_is_up()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(Bench.Group(workers: 1) with { SafeStopTimeout = TimeSpan.FromSeconds(30) });
        bench.Advance(5);
        var worker = bench.Host.Started.Single();

        bench.Apply(Bench.Group(workers: 1, enabled: false) with { SafeStopTimeout = TimeSpan.FromSeconds(30) });
        bench.Advance(29.5);
        Assert.False(worker.HasExited);

        bench.Advance(1);
        Assert.True(worker.HasExited);
        Assert.Single(bench.Of(EventKind.SafeStopTimedOut));
        Assert.Single(bench.Of(EventKind.WorkerKilled));
        Assert.Empty(bench.Status().Workers);
        Assert.Single(bench.Host.Started);
    }

    [Fact]
    public void A_worker_being_stopped_does_not_count_so_its_replacement_starts_already()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(Bench.Group("A", 2));
        bench.Advance(5);

        // The group shrinks and grows back while the stopped worker is still leaving.
        bench.Apply(Bench.Group("A", 1));
        bench.Tick();
        bench.Apply(Bench.Group("A", 2));
        bench.Tick();

        Assert.Equal(3, bench.Host.Started.Count);
        Assert.Equal(3, bench.Status("A").Workers.Count);
        Assert.Equal(1, bench.Status("A").Workers.Count(worker => worker.State == WorkerState.Stopping));
    }

    [Fact]
    public void The_safe_stop_is_sent_again_until_the_broker_takes_it()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(Bench.Group(workers: 1));
        bench.Advance(5);

        bench.Broker.GoDown();
        bench.Apply(Bench.Group(workers: 1, enabled: false));
        bench.Advance(5);
        Assert.Empty(bench.Broker.SafeStops);

        bench.Broker.ComeBack();
        bench.Advance(1);
        Assert.Equal([bench.Host.Started.Single().Id], bench.Broker.SafeStops);
    }

    [Fact]
    public void A_group_taken_out_of_the_configuration_has_its_workers_stopped()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(Bench.Group("A", 1), Bench.Group("B", 1));
        bench.Advance(5);

        bench.Apply(Bench.Group("A", 1));
        bench.Tick();
        var removed = bench.Host.Started[1];
        Assert.Equal([removed.Id], bench.Broker.SafeStops);

        removed.Exit(0);
        bench.Advance(1);
        Assert.Single(bench.Of(EventKind.GroupRemoved));
        Assert.Equal(["A"], bench.Supervisor.GetStatus().Select(group => group.Config.Name));
        Assert.Equal(2, bench.Host.Started.Count);
    }

    [Fact]
    public void A_group_whose_workers_keep_failing_waits_longer_and_longer_between_attempts()
    {
        var bench = new Bench();
        bench.Apply(new WorkerControlConfig
        {
            StartBatchInterval = TimeSpan.Zero,
            Groups = [Bench.Group(workers: 1) with { CrashLimit = 3, CrashWindow = TimeSpan.FromSeconds(60) }]
        });

        // Every worker dies right after starting; the replacement starts on the same tick.
        void Die()
        {
            foreach (var process in bench.Host.Running.ToList())
                process.Exit(1);
            bench.Tick();
        }

        bench.Tick();
        Die();
        Die();
        Assert.Equal(3, bench.Host.Started.Count);
        Assert.False(bench.Status().Unstable);

        Die();
        Assert.Equal(3, bench.Host.Started.Count);
        Assert.True(bench.Status().Unstable);
        Assert.Single(bench.Of(EventKind.GroupUnstable));

        // 5 s before the fourth attempt, 10 s before the fifth.
        bench.Advance(4.5);
        Assert.Equal(3, bench.Host.Started.Count);
        bench.Advance(0.75);
        Assert.Equal(4, bench.Host.Started.Count);
        Die();
        bench.Advance(9);
        Assert.Equal(4, bench.Host.Started.Count);
        bench.Advance(1.25);
        Assert.Equal(5, bench.Host.Started.Count);

        // One that stays up puts the group back to normal.
        bench.Broker.AutoAnswer = _ => true;
        bench.Broker.AnswerAll();
        bench.Advance(61);
        Assert.False(bench.Status().Unstable);
        Assert.Single(bench.Of(EventKind.GroupStable));
        Assert.Equal(5, bench.Host.Started.Count);
    }

    [Fact]
    public void An_executable_that_cannot_be_started_is_not_tried_in_a_tight_loop()
    {
        var bench = new Bench();
        bench.Host.FailWith = "The system cannot find the file specified";
        bench.Apply(Bench.Group(workers: 2));

        bench.Advance(60);

        var failures = bench.Of(EventKind.WorkerStartFailed).ToList();
        Assert.InRange(failures.Count, 4, 12);
        Assert.All(failures, failure => Assert.Contains("cannot find the file", failure.Detail));
        Assert.True(bench.Status().Unstable);

        bench.Host.FailWith = null;
        bench.Broker.AutoAnswer = _ => true;
        bench.Advance(400);
        Assert.Equal(2, bench.Host.Running.Count());
    }

    [Fact]
    public void A_boost_window_adds_workers_and_takes_them_back()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        var boost = new BoostConfig { Enabled = true, BoostWorkers = 2, StartTime = new TimeSpan(10, 30, 0), EndTime = new TimeSpan(10, 45, 0) };
        bench.Apply(Bench.Group(workers: 1) with { Boost = boost });

        bench.Advance(60);
        Assert.Single(bench.Host.Running);
        Assert.False(bench.Status().BoostActive);

        bench.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(3, bench.Host.Running.Count());
        Assert.True(bench.Status().BoostActive);
        Assert.Equal(3, bench.Status().DesiredWorkers);
        Assert.Single(bench.Of(EventKind.BoostStarted));

        bench.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(2, bench.Broker.SafeStops.Count);
        Assert.Single(bench.Of(EventKind.BoostEnded));
        Assert.Equal(1, bench.Status().DesiredWorkers);
    }

    [Theory]
    [InlineData("22:00:00", "02:00:00", "23:30:00", true)]
    [InlineData("22:00:00", "02:00:00", "01:59:59", true)]
    [InlineData("22:00:00", "02:00:00", "02:00:00", false)]
    [InlineData("22:00:00", "02:00:00", "12:00:00", false)]
    [InlineData("08:00:00", "18:00:00", "08:00:00", true)]
    [InlineData("08:00:00", "18:00:00", "18:00:00", false)]
    [InlineData("08:00:00", "08:00:00", "08:00:00", false)]
    public void A_boost_window_may_cross_midnight(string start, string end, string now, bool active)
    {
        var boost = new BoostConfig { Enabled = true, BoostWorkers = 1, StartTime = TimeSpan.Parse(start), EndTime = TimeSpan.Parse(end) };

        Assert.Equal(active, boost.IsActive(TimeSpan.Parse(now)));
    }

    [Fact]
    public void Workers_left_by_a_previous_run_are_taken_over_instead_of_duplicated()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        var born = bench.Time.GetUtcNow().AddHours(-3);
        var alive = new FakeProcess(501, born);
        var other = new FakeProcess(502, born);
        bench.Host.Existing[501] = alive;
        // 502 exists, but it is not the process that was recorded: it only got the same number.
        bench.Host.Existing[502] = other;

        bench.Apply(Bench.Group(workers: 2));
        bench.Supervisor.Adopt([
            new WorkerRecord("Orders", 501, born),
            new WorkerRecord("Orders", 502, born.AddMinutes(1)),
            new WorkerRecord("Orders", 503, born),
            new WorkerRecord("Gone", 504, born)
        ]);
        bench.Advance(5);

        Assert.Single(bench.Of(EventKind.WorkerAdopted));
        Assert.Single(bench.Host.Started);
        var workers = bench.Status().Workers;
        Assert.Equal(2, workers.Count);
        Assert.Contains(workers, worker => worker.ProcessId == 501 && worker.Adopted && worker.State == WorkerState.Up);
        Assert.False(other.HasExited);
    }

    [Fact]
    public void An_adopted_worker_that_does_not_answer_is_ended_like_any_other()
    {
        var bench = new Bench();
        var born = bench.Time.GetUtcNow().AddHours(-3);
        var stuck = new FakeProcess(501, born);
        bench.Host.Existing[501] = stuck;
        bench.Apply(Bench.Group(workers: 1));
        bench.Supervisor.Adopt([new WorkerRecord("Orders", 501, born)]);

        bench.Tick();
        bench.Advance(15);
        bench.Broker.Expire(501);
        bench.Advance(Supervisor.VerdictDelay + TimeSpan.FromSeconds(0.5));

        Assert.True(stuck.HasExited);
        Assert.Single(bench.Of(EventKind.WorkerHung));
    }

    [Fact]
    public void Stopping_asks_everyone_to_leave_and_waits_for_them()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(Bench.Group("A", 2), Bench.Group("B", 1));
        bench.Advance(5);

        bench.Supervisor.BeginStop();
        bench.Tick();
        Assert.Equal(3, bench.Broker.SafeStops.Count);
        Assert.False(bench.Supervisor.IsIdle);

        foreach (var process in bench.Host.Started)
            process.Exit(0);
        bench.Tick();
        Assert.True(bench.Supervisor.IsIdle);
        Assert.Equal(3, bench.Host.Started.Count);
        Assert.Empty(bench.Supervisor.GetRecords());
    }

    [Fact]
    public void A_process_that_survives_being_ended_is_ended_again()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(Bench.Group(workers: 1));
        bench.Advance(5);
        var worker = bench.Host.Started.Single();
        worker.Unkillable = true;

        bench.Supervisor.KillAll();
        Assert.Equal(1, worker.Kills);

        bench.Advance(5.25);
        Assert.Equal(2, worker.Kills);
    }

    [Fact]
    public void The_records_follow_the_workers_under_supervision()
    {
        var bench = new Bench();
        bench.Broker.AutoAnswer = _ => true;
        bench.Apply(Bench.Group(workers: 2));
        var before = bench.Supervisor.RecordsVersion;

        bench.Advance(1);
        Assert.NotEqual(before, bench.Supervisor.RecordsVersion);
        Assert.Equal(
            bench.Host.Started.Select(process => new WorkerRecord("Orders", process.Id, process.StartTime)),
            bench.Supervisor.GetRecords());

        var steady = bench.Supervisor.RecordsVersion;
        bench.Advance(120);
        Assert.Equal(steady, bench.Supervisor.RecordsVersion);

        bench.Host.Started[0].Exit(1);
        bench.Tick();
        Assert.NotEqual(steady, bench.Supervisor.RecordsVersion);
    }
}
