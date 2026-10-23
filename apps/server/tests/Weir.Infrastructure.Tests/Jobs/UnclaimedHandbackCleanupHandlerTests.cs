using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Jobs;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// #652: the Cleanup job for hand-back copies nobody claimed. It is off until a person switches it on, and it removes a
/// copy only when it is old enough, nobody said anything about it, and it is still exactly the file Weir wrote.
/// </summary>
public sealed class UnclaimedHandbackCleanupHandlerTests : IDisposable
{
    private readonly StoreFixture _store = new();
    private readonly ProcessingJobStore _jobs;
    private readonly OperatorSettingsStore _operatorSettings = new();
    private readonly HandbackStore _handback = new();
    private readonly UnclaimedHandbackCleanupHandler _handler;
    private readonly string _watched;
    private readonly string _output;

    public UnclaimedHandbackCleanupHandlerTests()
    {
        _store.Clock.Set(DateTimeOffset.UtcNow);
        _jobs = new ProcessingJobStore(_store.Database, _store.Clock);
        _handler = new UnclaimedHandbackCleanupHandler(_jobs, _operatorSettings, _handback, _store.Clock, NullLogger<UnclaimedHandbackCleanupHandler>.Instance);
        _watched = _store.Home.Join("downloads");
        _output = _store.Home.Join("hand-back");
        Directory.CreateDirectory(_watched);
        Directory.CreateDirectory(_output);
    }

    public void Dispose() => _store.Dispose();

    private async Task<long> LibraryAsync()
    {
        await _store.Execute(
            $"INSERT INTO libraries (id, name, media_type, watched_folder, output_folder) VALUES (70, 'Hand-back films', 'movie', '{_watched}', '{_output}')");
        return 70;
    }

    /// <summary>A copy Weir wrote <paramref name="daysAgo"/> days ago, recorded exactly as a pass records it.</summary>
    private async Task<string> HandedBackAsync(long library, string relative, int daysAgo, string? outputPath = null)
    {
        var copy = outputPath ?? Path.Join(_output, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        await File.WriteAllTextAsync(copy, "cleaned film " + relative);
        await _store.WithUnitOfWork(async uow =>
        {
            await _handback.RecordWrittenAsync(uow, library, relative, copy, _store.Clock.GetUtcNow().AddDays(-daysAgo));
            return 0;
        });
        return copy;
    }

    private Task RunAsync() =>
        _handler.HandleAsync(
            new JobWorkContext(1, PeriodicJobKinds.UnclaimedHandbackCleanup, "{\"media_scope\": \"movie\", \"trigger\": \"scheduled\"}", "test"),
            CancellationToken.None);

    [Fact]
    public async Task It_is_off_until_a_person_switches_it_on_and_waits_fourteen_days_by_default()
    {
        var enqueuer = new UnclaimedHandbackCleanupEnqueuer(_jobs, "movie");

        Assert.False(await enqueuer.IsEnabledAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromHours(6), await enqueuer.IntervalAsync(CancellationToken.None));

        await _store.WithUnitOfWork(uow => _operatorSettings.EnsureAsync(uow));
        Assert.False(await enqueuer.IsEnabledAsync(CancellationToken.None));
        Assert.Equal(14, await _store.Scalar("SELECT unclaimed_handback_window_days FROM operator_settings WHERE id = 1"));

        await _store.Execute("UPDATE operator_settings SET unclaimed_handback_cleanup_enabled = 1, unclaimed_handback_cleanup_interval_seconds = 3600 WHERE id = 1");
        Assert.True(await enqueuer.IsEnabledAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromHours(1), await enqueuer.IntervalAsync(CancellationToken.None));
    }

    [Fact]
    public async Task It_removes_only_an_old_unclaimed_copy_that_is_exactly_what_Weir_wrote()
    {
        var library = await LibraryAsync();
        var download = Path.Join(_watched, "Exact", "exact.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(download)!);
        await File.WriteAllTextAsync(download, "the original download");

        var exact = await HandedBackAsync(library, "Exact/exact.mkv", daysAgo: 20);
        var changed = await HandedBackAsync(library, "Changed/changed.mkv", daysAgo: 20);
        await File.AppendAllTextAsync(changed, " and something else wrote to it");
        var young = await HandedBackAsync(library, "Young/young.mkv", daysAgo: 3);
        var claimed = await HandedBackAsync(library, "Claimed/claimed.mkv", daysAgo: 20);
        await _store.Execute("UPDATE handbacks SET outcome = 'not-imported', outcome_by = 'Deluno' WHERE relative_path = 'Claimed/claimed.mkv'");
        // A row that names a file in the watched folder (never written by a pass, but the rule must still refuse it).
        var inWatched = await HandedBackAsync(library, "Strange/strange.mkv", daysAgo: 20, outputPath: Path.Join(_watched, "Strange", "strange.mkv"));

        await RunAsync();

        Assert.False(File.Exists(exact));
        Assert.True(File.Exists(download));
        Assert.True(File.Exists(changed));
        Assert.True(File.Exists(young));
        Assert.True(File.Exists(claimed));
        Assert.True(File.Exists(inWatched));
        Assert.Equal(1, await _store.Scalar("SELECT count(*) FROM handbacks WHERE released_at IS NOT NULL"));
        Assert.Equal(1, await _store.Scalar(
            "SELECT count(*) FROM handbacks WHERE relative_path = 'Exact/exact.mkv' AND release_note = 'No media manager imported it within 14 days, so Weir removed its copy.'"));
        Assert.Equal(1, await _store.Scalar(
            "SELECT count(*) FROM handbacks WHERE relative_path = 'Changed/changed.mkv' AND settled_at IS NOT NULL AND released_at IS NULL " +
            "AND release_note = 'Weir''s copy has changed since Weir wrote it, so Weir left it alone.'"));
        Assert.Equal(0, await _store.Scalar("SELECT count(*) FROM handbacks WHERE relative_path IN ('Young/young.mkv', 'Claimed/claimed.mkv') AND settled_at IS NOT NULL"));
        Assert.Equal(1, await _store.Scalar(
            "SELECT count(*) FROM activity_events WHERE event_type = 'processing.unclaimed_handback_cleanup_completed' " +
            "AND title = 'Removed 1 unclaimed hand-back copy (Movies)' AND detail LIKE '%\"removed\":1%'"));

        // A second run has nothing left to do: a settled copy is never looked at again.
        await RunAsync();
        Assert.True(File.Exists(changed));
        Assert.Equal(1, await _store.Scalar(
            "SELECT count(*) FROM activity_events WHERE title = 'Unclaimed hand-backs checked (Movies): nothing to remove'"));
    }

    [Fact]
    public async Task Several_old_copies_are_removed_together_and_one_somebody_else_already_removed_is_counted_as_gone()
    {
        var library = await LibraryAsync();
        var first = await HandedBackAsync(library, "One/one.mkv", daysAgo: 20);
        var second = await HandedBackAsync(library, "Two/two.mkv", daysAgo: 20);
        var vanished = await HandedBackAsync(library, "Three/three.mkv", daysAgo: 20);
        File.Delete(vanished);

        await RunAsync();

        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.Equal(1, await _store.Scalar(
            "SELECT count(*) FROM activity_events WHERE title = 'Removed 2 unclaimed hand-back copies (Movies)' AND detail LIKE '%\"already_gone\":1%'"));
        Assert.Equal(3, await _store.Scalar("SELECT count(*) FROM handbacks WHERE settled_at IS NOT NULL"));
    }

    [Fact]
    public async Task A_person_can_change_the_window()
    {
        var library = await LibraryAsync();
        var copy = await HandedBackAsync(library, "Film/film.mkv", daysAgo: 5);
        await _store.WithUnitOfWork(uow => _operatorSettings.EnsureAsync(uow));

        await RunAsync();
        Assert.True(File.Exists(copy));

        await _store.Execute("UPDATE operator_settings SET unclaimed_handback_window_days = 4 WHERE id = 1");
        await RunAsync();
        Assert.False(File.Exists(copy));
    }

    [WindowsFact("A file another program holds open can only be locked against deletion on Windows")]
    public async Task A_copy_in_use_is_kept_for_the_next_run_and_the_event_says_so()
    {
        var library = await LibraryAsync();
        var copy = await HandedBackAsync(library, "Film/film.mkv", daysAgo: 20);

        await using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await RunAsync();
        }

        Assert.True(File.Exists(copy));
        Assert.Equal(0, await _store.Scalar("SELECT count(*) FROM handbacks WHERE settled_at IS NOT NULL"));
        Assert.Equal(1, await _store.Scalar(
            "SELECT count(*) FROM activity_events WHERE detail LIKE '%\"result\":\"warning\"%' AND detail LIKE '%It tries again next run.%'"));

        await RunAsync();
        Assert.False(File.Exists(copy));
    }


    [Fact]
    public async Task The_television_run_names_television_and_queues_under_its_own_key()
    {
        await _store.Execute("DELETE FROM operator_settings");
        await _handler.HandleAsync(
            new JobWorkContext(1, PeriodicJobKinds.UnclaimedHandbackCleanup, "{\"media_scope\": \"tv\"}", "test"), CancellationToken.None);
        await new UnclaimedHandbackCleanupEnqueuer(_jobs, "tv").EnqueueOnceAsync(CancellationToken.None);
        await new UnclaimedHandbackCleanupEnqueuer(_jobs, "movie").EnqueueOnceAsync(CancellationToken.None);

        Assert.Equal(1, await _store.Scalar("SELECT count(*) FROM activity_events WHERE title = 'Unclaimed hand-backs checked (TV): nothing to remove' AND detail LIKE '%\"window_days\":14%'"));
        Assert.Equal(
            "processing.unclaimed_handback_cleanup:v1:movie,processing.unclaimed_handback_cleanup:v1:tv",
            await _store.ScalarText("SELECT group_concat(dedupe_key, ',') FROM (SELECT dedupe_key FROM jobs ORDER BY dedupe_key)"));
    }
}
