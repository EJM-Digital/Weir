using System.Globalization;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>What a scan decides about a file it has seen before: left alone, put on hold, or queued again.</summary>
public sealed class WatchedFileDeciderTests : IDisposable
{
    private const string Film = "Film/film.mkv";

    private readonly ScanWorkflowWorld _world = ScanWorkflowWorld.StartAsync().GetAwaiter().GetResult();

    public void Dispose() => _world.Dispose();

    private static long ModifiedNs(string path) => (File.GetLastWriteTimeUtc(path) - DateTime.UnixEpoch).Ticks * 100;

    private Task SeenAsync(long library, string status, int bytes) =>
        _world.Store.Execute(
            "INSERT INTO files (library_id, relative_path, status, status_reason, size_bytes, last_seen_at) " +
            $"VALUES ({library}, '{Film}', '{status}', 'Seen before.', {bytes}, CURRENT_TIMESTAMP)");

    private Task BookedJobAsync(long library, string notBefore) =>
        _world.Store.Execute(
            "INSERT INTO jobs (dedupe_key, job_kind, status, payload_json, not_before) VALUES " +
            $"('booked-look', '{RemuxPassOutcomes.JobKind}', 'pending', '{{\"relative_media_path\":\"{Film}\",\"media_scope\":\"movie\",\"library_id\":{library}}}', '{notBefore}')");

    private static string InADay() => DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_failed_file_with_no_retry_time_is_only_marked_seen_and_not_queued()
    {
        var library = await _world.WorkflowAsync();
        _world.Write(Film, 10);
        await SeenAsync(library, ProcessingFileStatuses.ProcessingFailed, 10);

        await _world.ScanAsync(library);

        Assert.Empty(await _world.QueuedPathsAsync());
        Assert.Equal(ProcessingFileStatuses.ProcessingFailed, (await _world.RowAsync(library, Film))!.Status);
    }

    [Fact]
    public async Task A_file_with_a_pass_booked_for_later_is_on_hold_until_then_with_the_reason_it_was_given()
    {
        var library = await _world.WorkflowAsync();
        _world.Write(Film, 10);
        await SeenAsync(library, ProcessingFileStatuses.OnHold, 10);
        await BookedJobAsync(library, InADay());

        await _world.ScanAsync(library);

        var row = (await _world.RowAsync(library, Film))!;
        Assert.Equal((ProcessingFileStatuses.OnHold, "Seen before."), (row.Status, row.StatusReason));
        Assert.NotNull(row.HoldUntil);
    }

    [Fact]
    public async Task A_new_file_with_a_pass_booked_for_later_is_on_hold_for_the_booked_look()
    {
        var library = await _world.WorkflowAsync();
        _world.Write(Film, 10);
        await BookedJobAsync(library, InADay());

        await _world.ScanAsync(library);

        var row = (await _world.RowAsync(library, Film))!;
        Assert.Equal(ProcessingFileStatuses.OnHold, row.Status);
        Assert.Equal("Weir has another look at this file booked, and leaves it alone until then.", row.StatusReason);
        Assert.Equal(["Film/film.mkv"], await _world.QueuedPathsAsync());
    }

    [Fact]
    public async Task A_cleaned_movie_whose_original_is_to_be_removed_is_left_as_it_is_while_a_pass_for_it_is_queued()
    {
        var library = await _world.WorkflowAsync(settings => settings with { RemoveOriginalAfterSuccess = true });
        var path = _world.Write(Film, 10);
        await SeenAsync(library, ProcessingFileStatuses.Processed, 10);
        await _world.Store.Execute($"UPDATE files SET processed_source_size = 10, processed_source_mtime_ns = {ModifiedNs(path).ToString(CultureInfo.InvariantCulture)}");
        await _world.Store.Execute(
            $"INSERT INTO jobs (dedupe_key, job_kind, status, payload_json) VALUES ('queued-pass', '{RemuxPassOutcomes.JobKind}', 'pending', " +
            $"'{{\"relative_media_path\":\"{Film}\",\"media_scope\":\"movie\",\"library_id\":{library}}}')");

        await _world.ScanAsync(library);

        Assert.Equal(["Film/film.mkv"], await _world.QueuedPathsAsync());
        Assert.Equal(ProcessingFileStatuses.Processed, (await _world.RowAsync(library, Film))!.Status);
    }

    private const string BookedReason = "Weir has another look at this file booked, and leaves it alone until then.";

    /// <summary>A film a pass cleaned and recorded the fingerprint of, with the completion record and the output the interrupted removal waits on.</summary>
    private async Task<string> CleanedFilmAsync(long library, string relativePath, int bytes)
    {
        var path = _world.Write(relativePath, bytes);
        await _world.Store.Execute(
            "INSERT INTO files (library_id, relative_path, status, status_reason, size_bytes, last_seen_at, processed_source_size, processed_source_mtime_ns) " +
            $"VALUES ({library}, '{relativePath}', 'processed', 'Seen before.', {bytes}, CURRENT_TIMESTAMP, {bytes}, {ModifiedNs(path).ToString(CultureInfo.InvariantCulture)})");
        await WriteOutputAndCompletionAsync(library, relativePath, path);
        return path;
    }

    private async Task WriteOutputAndCompletionAsync(long library, string relativePath, string source)
    {
        var output = Path.Join(_world.Output, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllBytes(output, [1]);
        await ProcessingWatchedFolderScanDispatchJobHandlerTests.WriteCompletedPassEventAsync(_world.Store, library, relativePath, "movie", source, output);
    }

    [Fact]
    public async Task An_interrupted_movie_removal_takes_only_the_file_when_the_release_folder_holds_another_video()
    {
        var library = await _world.WorkflowAsync(settings => settings with { RemoveOriginalAfterSuccess = true });
        var path = await CleanedFilmAsync(library, Film, 10);
        var other = _world.Write("Film/extra.mkv", 12);

        await _world.ScanAsync(library);

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(other));
        var row = (await _world.RowAsync(library, Film))!;
        Assert.Equal(ProcessingFileStatuses.Processed, row.Status);
        Assert.StartsWith("The output was already complete.", row.StatusReason, StringComparison.Ordinal);
        Assert.DoesNotContain("release folder", row.StatusReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replaced_movie_whose_output_is_already_complete_has_its_old_failure_cleared_while_its_removal_is_finished()
    {
        var library = await _world.WorkflowAsync(settings => settings with { RemoveOriginalAfterSuccess = true });
        await SeenAsync(library, ProcessingFileStatuses.ProcessingFailed, 10);
        var path = _world.Write(Film, 11);
        await WriteOutputAndCompletionAsync(library, Film, path);

        await _world.ScanAsync(library);

        Assert.False(File.Exists(path));
        var row = (await _world.RowAsync(library, Film))!;
        Assert.Equal(ProcessingFileStatuses.Processed, row.Status);
        Assert.Equal(0, row.FailureAttempts);
        Assert.Empty(await _world.QueuedPathsAsync());
    }

    [Fact]
    public async Task An_interrupted_movie_removal_waits_while_the_file_is_still_settling()
    {
        var library = await _world.WorkflowAsync(settings => settings with { RemoveOriginalAfterSuccess = true, ReadyAfterSeconds = 3600 });
        var path = await CleanedFilmAsync(library, Film, 10);

        await _world.ScanAsync(library);

        Assert.True(File.Exists(path));
        Assert.Equal(ProcessingFileStatuses.Processed, (await _world.RowAsync(library, Film))!.Status);
    }

    [Fact]
    public async Task An_interrupted_movie_removal_waits_while_the_file_cannot_be_opened()
    {
        var library = await _world.WorkflowAsync(settings => settings with { RemoveOriginalAfterSuccess = true, SkipAccessTests = false });
        var path = await CleanedFilmAsync(library, Film, 10);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        await _world.ScanAsync(library);

        Assert.True(File.Exists(path));
        Assert.Equal(ProcessingFileStatuses.Processed, (await _world.RowAsync(library, Film))!.Status);
    }

    [Fact]
    public async Task A_cleaned_movie_with_no_completed_output_on_record_is_not_removed()
    {
        var library = await _world.WorkflowAsync(settings => settings with { RemoveOriginalAfterSuccess = true });
        var path = _world.Write(Film, 10);
        await SeenAsync(library, ProcessingFileStatuses.Processed, 10);
        await _world.Store.Execute($"UPDATE files SET processed_source_size = 10, processed_source_mtime_ns = {ModifiedNs(path).ToString(CultureInfo.InvariantCulture)}");

        await _world.ScanAsync(library);

        Assert.True(File.Exists(path));
        Assert.Equal(ProcessingFileStatuses.Processed, (await _world.RowAsync(library, Film))!.Status);
    }

    [Fact]
    public async Task A_waiting_file_with_a_pass_booked_for_later_goes_on_hold_for_the_booked_look()
    {
        var library = await _world.WorkflowAsync();
        _world.Write(Film, 10);
        await SeenAsync(library, ProcessingFileStatuses.Unprocessed, 10);
        await BookedJobAsync(library, InADay());

        await _world.ScanAsync(library);

        var row = (await _world.RowAsync(library, Film))!;
        Assert.Equal((ProcessingFileStatuses.OnHold, BookedReason), (row.Status, row.StatusReason));
    }

    [Fact]
    public async Task A_file_on_hold_with_no_reason_given_goes_on_hold_for_the_booked_look()
    {
        var library = await _world.WorkflowAsync();
        _world.Write(Film, 10);
        await SeenAsync(library, ProcessingFileStatuses.OnHold, 10);
        await _world.Store.Execute("UPDATE files SET status_reason = ''");
        await BookedJobAsync(library, InADay());

        await _world.ScanAsync(library);

        var row = (await _world.RowAsync(library, Film))!;
        Assert.Equal((ProcessingFileStatuses.OnHold, BookedReason), (row.Status, row.StatusReason));
    }
}
