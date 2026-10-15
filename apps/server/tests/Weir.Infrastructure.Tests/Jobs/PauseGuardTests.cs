using Microsoft.Extensions.Logging;
using Weir.Core.Jobs;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// The claim refuses work while paused. The worker asks the pause once more, separately, right after a claim, so a job claimed
/// by mistake goes back untouched instead of reaching a handler that changes a file.
/// </summary>
public sealed class PauseGuardTests : IDisposable
{
    private const string Remux = "processing.file.remux_pass.v1";
    private const string Scan = "processing.watched_folder.remux_scan_dispatch.v1";
    private static readonly DateTimeOffset T0 = JobsTestDatabase.T0;
    private readonly JobsTestDatabase _db = new();

    public PauseGuardTests()
    {
        _db.SeedSuiteSettings();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_job_the_claim_let_through_while_paused_is_put_back_unrun_with_its_attempt_given_back()
    {
        await _db.Store.EnqueueOrGetAsync("pass", Remux);
        _db.Pause(scanWhilePaused: true, until: T0.AddHours(1));
        var ran = false;
        var processor = _db.Processor([new DelegateHandler(Remux, _ => ran = true)]);

        // The claim is asked as of two hours on, when the pause has run out; the clock still says it is paused.
        var outcome = await processor.ProcessOneAsync("w", 3600, T0.AddHours(2));

        Assert.Equal(JobProcessOutcome.Idle, outcome);
        Assert.False(ran, "a handler must not run while processing is paused");
        var job = (await _db.Store.GetAsync(1))!;
        Assert.Equal(ProcessingJobStatus.Pending, job.Status);
        Assert.Equal(0, job.AttemptCount);
        Assert.Null(job.LeaseOwner);
    }

    [Fact]
    public async Task A_job_put_back_for_a_pause_leaves_the_media_file_exactly_as_it_was_and_says_so_in_the_log()
    {
        var film = _db.Join("library", "Film (2020)", "Film (2020).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(film)!);
        byte[] before = [.. "the film as its owner has it"u8];
        await File.WriteAllBytesAsync(film, before);
        var modified = File.GetLastWriteTimeUtc(film);
        await _db.Store.EnqueueOrGetAsync("pass", Remux);
        _db.Pause(scanWhilePaused: false, until: T0.AddHours(1));
        var logger = new ListLogger<ProcessingJobProcessor>();
        var processor = _db.Processor([new DelegateHandler(Remux, _ => File.WriteAllText(film, "cleaned"))], logger: logger);

        // The claim is asked as of two hours on, when the pause has run out; the clock still says it is paused.
        var outcome = await processor.ProcessOneAsync("w", 3600, T0.AddHours(2));

        Assert.Equal(JobProcessOutcome.Idle, outcome);
        Assert.Equal(before, await File.ReadAllBytesAsync(film));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(film));
        Assert.Equal([Path.Join(_db.Join("library"), "Film (2020)", "Film (2020).mkv")], Directory.EnumerateFiles(_db.Join("library"), "*", SearchOption.AllDirectories));
        var job = (await _db.Store.GetAsync(1))!;
        Assert.Equal((ProcessingJobStatus.Pending, 0), (job.Status, job.AttemptCount));
        var logged = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("while processing is paused, so it was put back without running", logged.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_job_that_reaches_its_handler_after_the_pause_ends_runs()
    {
        await _db.Store.EnqueueOrGetAsync("pass", Remux);
        _db.Pause(scanWhilePaused: true, until: T0.AddHours(1));
        _db.Clock.Advance(TimeSpan.FromHours(2));
        var ran = false;
        var processor = _db.Processor([new DelegateHandler(Remux, _ => ran = true)]);

        Assert.Equal(JobProcessOutcome.Processed, await processor.ProcessOneAsync("w", 3600));

        Assert.True(ran);
        Assert.Equal(1, (await _db.Store.GetAsync(1))!.AttemptCount);
    }

    [Fact]
    public async Task A_scan_still_runs_while_paused_when_the_pause_keeps_looking_for_files()
    {
        await _db.Store.EnqueueOrGetAsync("scan", Scan);
        _db.Pause(scanWhilePaused: true);
        var ran = false;
        var processor = _db.Processor([new DelegateHandler(Scan, _ => ran = true)]);

        Assert.Equal(JobProcessOutcome.Processed, await processor.ProcessOneAsync("w", 3600));

        Assert.True(ran);
    }
}
