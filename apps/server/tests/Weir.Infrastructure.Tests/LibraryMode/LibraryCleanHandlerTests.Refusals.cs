using System.Globalization;
using System.Text.Json;
using Weir.Core.Activity;
using Weir.Core.Jobs;
using Weir.Core.Library;
using Weir.Core.LibraryMode;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Core.Rules;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Tests.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>Every reason a clean job leaves a file alone, and the failures after a committed clean that must never undo it.</summary>
public sealed partial class LibraryCleanHandlerTests
{
    private sealed class ThrowingNotifier : ILibraryFileChangeNotifier
    {
        public int Calls { get; private set; }

        public Task NotifyAsync(LibraryFileChange change, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("the manager notifier broke");
        }
    }

    private sealed class ThrowingRemovedTrackStore : IRemovedTrackStore
    {
        public int Calls { get; private set; }

        public Task RecordAsync(RemovedTrackFileKey key, IReadOnlyList<RemovedTrackRecord> tracks, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("the store broke");
        }

        public Task<IReadOnlyList<RemovedTrackRecord>> GetAsync(RemovedTrackFileKey key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<RemovedTrackFileKey, IReadOnlyList<RemovedTrackRecord>>> GetAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedLinkCount(int? count) : IHardlinkInspector
    {
        public int? LinkCount(string path) => count;
    }

    private sealed class UnreadableLinkCount : IHardlinkInspector
    {
        public int? LinkCount(string path) => throw new UnauthorizedAccessException("denied");
    }

    private static Task HandleAsync(LibraryCleanHandler handler, string payload) =>
        handler.HandleAsync(new JobWorkContext(1, LibraryModeJobKinds.CleanKind, payload, "test"), CancellationToken.None);

    private static string Payload(long libraryId, string path) =>
        $$"""{"library_id":{{libraryId.ToString(CultureInfo.InvariantCulture)}},"path":{{JsonSerializer.Serialize(path)}},"trigger":"manual","confirm_final_removal":true}""";

    private async Task<List<string>> TitlesAsync(string eventType) =>
        await _fixture.Db(uow => uow.QueryAsync(
            "SELECT title FROM activity_events WHERE event_type = $type ORDER BY id", reader => reader.GetString(0), ("$type", eventType)));

    [Fact]
    public void The_handler_cannot_be_built_without_any_of_its_parts() =>
        ConstructorGuards.AssertEachRequiredPartIsRefused<LibraryCleanHandler>(() => HandlerArguments());

    [Fact]
    public async Task A_job_with_no_context_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Handler().HandleAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json {")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    [InlineData("""{"library_id":1,"path":""}""")]
    public async Task A_job_that_names_no_file_fails_in_plain_words(string payload)
    {
        await HandleAsync(Handler(), payload);

        Assert.Equal(["This job's payload has no file path."], await TitlesAsync(LibraryActivityEventTypes.FileFailed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public async Task A_job_for_a_workflow_that_is_gone_fails_in_plain_words(long libraryId)
    {
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        await HandleAsync(Handler(), Payload(libraryId, path));

        Assert.Equal(["This workflow no longer exists."], await TitlesAsync(LibraryActivityEventTypes.FileFailed));
        Assert.Empty(_media.Calls);
    }

    [Fact]
    public async Task A_file_other_names_still_share_is_skipped_before_anything_is_read()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        await HandleAsync(Handler(new FixedLinkCount(2)), Payload(library, path));

        Assert.Single(await TitlesAsync(LibraryActivityEventTypes.FileSkipped));
        Assert.Empty(_media.Calls);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_link_count_that_cannot_be_read_does_not_stop_the_clean_from_looking_at_the_file()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        await HandleAsync(Handler(new UnreadableLinkCount()), Payload(library, path));

        Assert.Equal(["This file already matches the workflow's rules; nothing to clean."], await TitlesAsync(LibraryActivityEventTypes.FileSkipped));
        Assert.NotEmpty(_media.Probed);
    }

    [Fact]
    public async Task A_file_with_no_video_track_fails_with_the_reason_the_planner_gives()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = """{"format":{"duration":"100.0"},"streams":[{"index":0,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"eng"}}]}""";

        await HandleAsync(Handler(), Payload(library, path));

        Assert.Equal(["This file has no video track that Weir could find."], await TitlesAsync(LibraryActivityEventTypes.FileFailed));
        Assert.Empty(_media.Remuxes);
    }

    [Fact]
    public async Task A_file_whose_track_details_make_no_sense_fails_in_plain_words_and_is_left_untouched()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = """{"format":{"duration":"100.0"},"streams":[{"index":0,"codec_type":5}]}""";

        await HandleAsync(Handler(), Payload(library, path));

        Assert.Equal([ToolFailureText.UnusableTrackData], await TitlesAsync(LibraryActivityEventTypes.FileFailed));
        Assert.Empty(_media.Remuxes);
    }

    [Fact]
    public async Task A_committed_clean_stays_cleaned_when_recording_the_removed_tracks_or_telling_the_manager_fails()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        var notifier = new ThrowingNotifier();
        var store = new ThrowingRemovedTrackStore();

        var jobId = await EnqueueCleanAsync(library, path, confirmFinalRemoval: true);

        await RunCleanAsync(jobId, Handler(notifier: notifier, removedTracks: store));

        Assert.Equal(1, store.Calls);
        Assert.Equal(1, notifier.Calls);
        Assert.Single(await TitlesAsync(LibraryActivityEventTypes.FileCleaned));
        Assert.Empty(await TitlesAsync(LibraryActivityEventTypes.FileFailed));
        Assert.NotEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
    }

    [WindowsFact("Only Windows refuses to replace a file another program holds open.")]
    public async Task A_file_held_open_is_tried_again_after_five_fifteen_and_sixty_minutes_then_given_up_on()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        var jobId = await EnqueueCleanAsync(library, path, confirmFinalRemoval: true);
        var waits = new List<int>();
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        foreach (var _ in Enumerable.Range(0, 3))
        {
            var before = _fixture.Store.Clock.GetUtcNow();
            await RunCleanAsync(jobId);
            var booked = TimestampColumns.Parse(await _fixture.Db(uow => uow.ScalarAsync($"SELECT not_before FROM jobs WHERE id = {jobId}")))!.Value;
            waits.Add((int)(booked - before).TotalMinutes);
            _fixture.Store.Clock.SetUtcNow(booked);
        }

        Assert.Equal([5, 15, 60], waits);
        Assert.Empty(await TitlesAsync(LibraryActivityEventTypes.FileFailed));

        await RunCleanAsync(jobId);

        Assert.Equal(["This file was still in use by another program after three tries, so Weir left it unchanged."], await TitlesAsync(LibraryActivityEventTypes.FileFailed));
    }
}
