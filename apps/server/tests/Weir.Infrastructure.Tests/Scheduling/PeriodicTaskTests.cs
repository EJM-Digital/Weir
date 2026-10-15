using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weir.Core.Configuration;
using Weir.Infrastructure.Scheduling;

namespace Weir.Infrastructure.Tests.Scheduling;

/// <summary>The periodic background task loop and its host.</summary>
public sealed class PeriodicTaskTests
{
    [Fact]
    public async Task A_task_that_runs_at_start_runs_straight_away_then_waits_the_interval()
    {
        var time = new FakeTimeProvider();
        var task = new CountingTask(runAtStart: true, interval: TimeSpan.FromHours(1));
        using var stop = new CancellationTokenSource();
        var loop = PeriodicTaskRunner.RunAsync(task, time, NullLogger.Instance, stop.Token);

        await Eventually.ThatAsync(() => task.Calls >= 1);
        // The loop is now parked in Task.Delay(Interval, time, ...): with a frozen fake clock and nothing
        // advancing it, a second call inside the interval is impossible rather than merely unobserved.
        Assert.Equal(1, task.Calls);

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_task_that_does_not_run_at_start_waits_one_interval_first()
    {
        // auth-session-cleanup: the first cleanup is an interval after start; startup has just cleaned up.
        // A fake clock makes this deterministic (real wall-clock timing races under load, #557):
        // PeriodicTaskRunner's very first statement awaits Task.Delay(task.Interval, time, ...), whose timer
        // is registered on `time` synchronously before that await yields, so by the time RunAsync returns
        // the task to us the timer already exists and advancing the fake clock is race-free.
        var time = new FakeTimeProvider();
        var task = new CountingTask(runAtStart: false, interval: TimeSpan.FromMinutes(10));
        using var stop = new CancellationTokenSource();
        var loop = PeriodicTaskRunner.RunAsync(task, time, NullLogger.Instance, stop.Token);

        Assert.Equal(0, task.Calls);

        time.Advance(task.Interval);
        await Eventually.ThatAsync(() => task.Calls >= 1);

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_task_sped_up_for_a_test_waits_that_many_times_less()
    {
        var time = new FakeTimeProvider();
        var task = new CountingTask(runAtStart: false, interval: TimeSpan.FromMinutes(10), cooldown: TimeSpan.FromMinutes(5));
        var sped = new SpedUpPeriodicTask(task, 10);
        using var stop = new CancellationTokenSource();
        var loop = PeriodicTaskRunner.RunAsync(sped, time, NullLogger.Instance, stop.Token);

        Assert.Equal((TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30)), (sped.Interval, sped.FailureCooldown));
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, task.Calls);
        time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.ThatAsync(() => task.Calls >= 1);

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_task_is_never_sped_up_below_a_tenth_of_a_second()
    {
        var sped = new SpedUpPeriodicTask(new CountingTask(runAtStart: true, interval: TimeSpan.FromSeconds(1)), 1000);

        Assert.Equal(TimeSpan.FromMilliseconds(100), sped.Interval);
    }

    [Fact]
    public async Task A_failed_run_is_logged_and_retried_after_the_cooldown()
    {
        var time = new FakeTimeProvider();
        var cooldown = TimeSpan.FromSeconds(20);
        var task = new CountingTask(runAtStart: true, interval: TimeSpan.FromHours(1), cooldown: cooldown, failFirst: true);
        var logger = new RecordingLogger<PeriodicTaskTests>();
        using var stop = new CancellationTokenSource();
        var loop = PeriodicTaskRunner.RunAsync(task, time, logger, stop.Token);

        await Eventually.ThatAsync(() => logger.Errors.Count >= 1);
        Assert.Equal(["counting tick failed"], logger.Errors);
        Assert.Equal(1, task.Calls);

        time.Advance(cooldown);
        await Eventually.ThatAsync(() => task.Calls >= 2);

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_task_with_a_label_is_listed_before_it_runs_and_its_run_is_recorded()
    {
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();
        var registry = new PeriodicTaskRegistry(time);
        var task = new CountingTask(runAtStart: false, interval: TimeSpan.FromMinutes(10), label: "Counting");
        using var stop = new CancellationTokenSource();
        var loop = PeriodicTaskRunner.RunAsync(task, time, NullLogger.Instance, stop.Token, registry);

        var planned = Assert.Single(registry.Snapshot());
        Assert.Equal(("Counting", start + task.Interval, null), (planned.Label, planned.NextRunAt, planned.LastOk));

        time.Advance(task.Interval);
        await Eventually.ThatAsync(() => registry.Snapshot().Single().LastOk == true);
        var ran = Assert.Single(registry.Snapshot());
        Assert.Equal((false, start + task.Interval, start + task.Interval + task.Interval), (ran.Running, ran.LastRunAt, ran.NextRunAt));

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_failed_run_is_listed_with_the_tasks_own_failure_message_and_the_cooldown_as_its_next_run()
    {
        var time = new FakeTimeProvider();
        var cooldown = TimeSpan.FromSeconds(20);
        var registry = new PeriodicTaskRegistry(time);
        var task = new CountingTask(runAtStart: true, interval: TimeSpan.FromHours(1), cooldown: cooldown, failFirst: true, label: "Counting");
        using var stop = new CancellationTokenSource();
        var loop = PeriodicTaskRunner.RunAsync(task, time, NullLogger.Instance, stop.Token, registry);

        await Eventually.ThatAsync(() => registry.Snapshot().Single().LastOk == false);

        var failed = Assert.Single(registry.Snapshot());
        Assert.Equal(("counting tick failed", time.GetUtcNow() + cooldown), (failed.LastError, failed.NextRunAt));

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_task_with_no_label_is_never_listed()
    {
        var time = new FakeTimeProvider();
        var registry = new PeriodicTaskRegistry(time);
        var task = new CountingTask(runAtStart: true, interval: TimeSpan.FromHours(1));
        using var stop = new CancellationTokenSource();
        var loop = PeriodicTaskRunner.RunAsync(task, time, NullLogger.Instance, stop.Token, registry);

        await Eventually.ThatAsync(() => task.Calls >= 1);

        Assert.Empty(registry.Snapshot());

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task The_host_runs_every_registered_task_once_however_often_it_is_added()
    {
        var first = new CountingTask(runAtStart: true, interval: TimeSpan.FromHours(1));
        var second = new CountingTask(runAtStart: true, interval: TimeSpan.FromHours(1));
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
            .AddSingleton(OptionsWithSpeedUp(null))
            .AddSingleton<IPeriodicTask>(first)
            .AddSingleton<IPeriodicTask>(second);
        services.AddSingleton<PeriodicTaskRegistry>();
        services.AddWeirPeriodicTasks();
        services.AddWeirPeriodicTasks();
        await using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(provider.GetServices<IHostedService>());

        await hosted.StartAsync(CancellationToken.None);
        await Eventually.ThatAsync(() => first.Calls >= 1 && second.Calls >= 1);
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_host_runs_its_tasks_at_the_pace_a_test_asked_for()
    {
        var time = new FakeTimeProvider();
        var task = new CountingTask(runAtStart: false, interval: TimeSpan.FromSeconds(30));
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(time)
            .AddSingleton(OptionsWithSpeedUp(10))
            .AddSingleton<IPeriodicTask>(task);
        services.AddSingleton<PeriodicTaskRegistry>();
        services.AddWeirPeriodicTasks();
        await using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(provider.GetServices<IHostedService>());
        var started = time.GetUtcNow();
        await hosted.StartAsync(CancellationToken.None);

        // The task's timer is made on the host's own thread, so the clock moves on a tenth of a second at a time until the task has run.
        await Eventually.ThatAsync(() =>
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            return task.Calls >= 1;
        });

        Assert.InRange(time.GetUtcNow() - started, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));
        await hosted.StopAsync(CancellationToken.None);
    }

    private static WeirOptions OptionsWithSpeedUp(int? speedUp)
    {
        var home = Path.Combine(Path.GetTempPath(), "weir-periodic-task-tests");
        var variables = new Dictionary<string, string> { ["WEIR_HOME"] = home };
        if (speedUp is { } times)
        {
            variables["WEIR_ENV"] = "development";
            variables["WEIR_PERIODIC_TASK_SPEEDUP"] = times.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return WeirOptionsLoader.Load(new RuntimeEnvironment(variables, OperatingSystem.IsWindows(), home, home));
    }

    private sealed class CountingTask(bool runAtStart, TimeSpan interval, TimeSpan? cooldown = null, bool failFirst = false, string? label = null) : IPeriodicTask
    {
        private int _calls;

        public int Calls => _calls;

        public string Name => "counting";

        public string? Label => label;

        public TimeSpan Interval => interval;

        public bool RunAtStart => runAtStart;

        public TimeSpan? FailureCooldown => cooldown;

        public string FailureMessage => "counting tick failed";

        public Task RunOnceAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            return failFirst && call == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        }
    }
}
