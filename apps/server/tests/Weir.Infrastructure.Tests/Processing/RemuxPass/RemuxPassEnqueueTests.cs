using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>Queueing one pass by hand: which refusals the person is given, and how a pass already waiting for the same file is reused.</summary>
public sealed class RemuxPassEnqueueTests : IDisposable
{
    private const string Kind = "processing.file.remux_pass.v1";

    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private Task<ProcessingJob> EnqueueAsync(string path, string scope = "movie", long? libraryId = null, bool passThrough = false) =>
        _fixture.Db(uow => RemuxPassEnqueue.EnqueueManualAsync(uow, _fixture.Jobs, _fixture.Libraries, path, scope, libraryId, passThrough));

    private Task<long> WatchedMovieLibraryAsync() => _fixture.LibraryAsync("movie", _fixture.Store.Home.Join("movies"));

    private async Task<long> InsertPendingAsync(string? payloadJson, long? libraryId = null, string status = "pending")
    {
        var payload = payloadJson is null ? "NULL" : "'" + payloadJson.Replace("'", "''", StringComparison.Ordinal) + "'";
        await _fixture.Store.Execute(
            $"INSERT INTO jobs (dedupe_key, job_kind, payload_json, status, not_before) VALUES ('{Guid.NewGuid():N}', '{Kind}', {payload}, '{status}', '2030-01-01 00:00:00')");
        return await _fixture.Store.Scalar("SELECT max(id) FROM jobs");
    }

    private static WireObject Payload(string json) => (WireObject)WireJsonParser.Parse(json);

    [Fact]
    public void A_refusal_carries_the_status_the_route_answers_with()
    {
        Assert.Equal(400, new RemuxPassEnqueueException().StatusCode);
        Assert.Equal(400, new RemuxPassEnqueueException("plain").StatusCode);
        Assert.Equal(400, new RemuxPassEnqueueException("wrapped", new InvalidOperationException()).StatusCode);
        var refusal = new RemuxPassEnqueueException(404, "gone");
        Assert.Equal(404, refusal.StatusCode);
        Assert.Equal("gone", refusal.Message);
    }

    [Fact]
    public async Task The_entry_points_refuse_a_missing_argument()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => RemuxPassEnqueue.EnqueueManualAsync(null!, _fixture.Jobs, _fixture.Libraries, "a", "movie", null, false));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _fixture.Db(uow => RemuxPassEnqueue.EnqueueManualAsync(uow, null!, _fixture.Libraries, "a", "movie", null, false)));
    }

    [Fact]
    public async Task A_workflow_that_no_longer_exists_is_a_404_that_says_to_refresh()
    {
        var refusal = await Assert.ThrowsAsync<RemuxPassEnqueueException>(() => EnqueueAsync("film.mkv", libraryId: 9999));

        Assert.Equal(404, refusal.StatusCode);
        Assert.Contains("no longer exists", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("movie", "Movies watched folder")]
    [InlineData("tv", "TV watched folder")]
    public async Task A_scope_whose_workflow_watches_no_folder_is_refused_in_plain_words(string scope, string expected)
    {
        var refusal = await Assert.ThrowsAsync<RemuxPassEnqueueException>(() => EnqueueAsync("film.mkv", scope));

        Assert.Equal(400, refusal.StatusCode);
        Assert.StartsWith(expected, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workflow_whose_watched_folder_is_only_spaces_is_refused_too()
    {
        var libraryId = await _fixture.LibraryAsync("movie", "   ");

        var refusal = await Assert.ThrowsAsync<RemuxPassEnqueueException>(() => EnqueueAsync("film.mkv", libraryId: libraryId));

        Assert.Equal(400, refusal.StatusCode);
    }

    [Fact]
    public async Task A_pass_is_queued_for_the_file_the_person_named_in_the_workflow_they_chose()
    {
        var libraryId = await WatchedMovieLibraryAsync();

        var job = await EnqueueAsync("  Film (2020)/Film.mkv  ", libraryId: libraryId);

        Assert.Equal(Kind, job.JobKind);
        var payload = Payload(job.PayloadJson!);
        Assert.Equal("Film (2020)/Film.mkv", ((WireString)payload.Get("relative_media_path")!).Value);
        Assert.Equal(libraryId, ((WireInteger)payload.Get("library_id")!).Value);
        Assert.Equal("manual", ((WireString)payload.Get("trigger")!).Value);
        Assert.False(((WireBool)payload.Get("pass_through_unchanged")!).Value);
    }

    [Fact]
    public async Task With_no_workflow_named_the_one_that_covers_the_scope_is_used()
    {
        var libraryId = await WatchedMovieLibraryAsync();

        var job = await EnqueueAsync("film.mkv");

        Assert.Equal(libraryId, ((WireInteger)Payload(job.PayloadJson!).Get("library_id")!).Value);
    }

    [Fact]
    public async Task Asking_for_pass_through_with_nothing_waiting_queues_a_new_pass()
    {
        await WatchedMovieLibraryAsync();

        var job = await EnqueueAsync("film.mkv", passThrough: true);

        Assert.True(((WireBool)Payload(job.PayloadJson!).Get("pass_through_unchanged")!).Value);
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task Pass_through_turns_a_pass_already_waiting_for_the_file_instead_of_queueing_a_second()
    {
        var libraryId = await WatchedMovieLibraryAsync();
        var waiting = await InsertPendingAsync(
            $"{{\"relative_media_path\":\"film.mkv\",\"library_id\":{libraryId},\"handoff_id\":\"h-7\",\"trigger\":\"scan\"}}", libraryId);

        var job = await EnqueueAsync("film.mkv", libraryId: libraryId, passThrough: true);

        Assert.Equal(waiting, job.Id);
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM jobs"));
        var storedJson = (await _fixture.Store.ScalarText($"SELECT payload_json FROM jobs WHERE id = {waiting}"))!;
        var stored = Payload(storedJson);
        Assert.Equal("h-7", ((WireString)stored.Get("handoff_id")!).Value);
        Assert.True(((WireBool)stored.Get("pass_through_unchanged")!).Value);
        Assert.Equal("manual", ((WireString)stored.Get("trigger")!).Value);
        Assert.Equal(0, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE id = {waiting} AND not_before IS NOT NULL"));
        Assert.Equal(storedJson, job.PayloadJson);
    }

    [Fact]
    public async Task Asking_for_a_normal_pass_leaves_the_waiting_one_alone_and_queues_another()
    {
        var libraryId = await WatchedMovieLibraryAsync();
        var waiting = await InsertPendingAsync($"{{\"relative_media_path\":\"film.mkv\",\"library_id\":{libraryId}}}", libraryId);

        var job = await EnqueueAsync("film.mkv", libraryId: libraryId);

        Assert.NotEqual(waiting, job.Id);
        Assert.Equal(2, await _fixture.Store.Scalar("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task A_waiting_pass_for_the_same_file_in_another_workflow_is_not_turned()
    {
        var libraryId = await WatchedMovieLibraryAsync();
        await InsertPendingAsync($"{{\"relative_media_path\":\"film.mkv\",\"library_id\":{libraryId + 40}}}");

        var job = await EnqueueAsync("film.mkv", libraryId: libraryId, passThrough: true);

        Assert.Equal(2, await _fixture.Store.Scalar("SELECT count(*) FROM jobs"));
        Assert.Equal(libraryId, ((WireInteger)Payload(job.PayloadJson!).Get("library_id")!).Value);
    }

    [Fact]
    public async Task The_oldest_waiting_pass_for_the_file_is_the_one_turned()
    {
        var libraryId = await WatchedMovieLibraryAsync();
        var first = await InsertPendingAsync("""{"relative_media_path":"film.mkv"}""");
        await InsertPendingAsync("""{"relative_media_path":"film.mkv"}""");

        var job = await EnqueueAsync("film.mkv", libraryId: libraryId, passThrough: true);

        Assert.Equal(first, job.Id);
    }

    [Fact]
    public async Task A_waiting_pass_with_no_workflow_recorded_is_turned_for_any_workflow()
    {
        var libraryId = await WatchedMovieLibraryAsync();
        var unrecorded = await InsertPendingAsync("""{"relative_media_path":"film.mkv","library_id":null}""");

        var job = await EnqueueAsync("film.mkv", libraryId: libraryId, passThrough: true);

        Assert.Equal(unrecorded, job.Id);
    }

    [Fact]
    public async Task Waiting_passes_that_cannot_be_the_one_asked_about_are_passed_over()
    {
        var libraryId = await WatchedMovieLibraryAsync();
        await InsertPendingAsync(null);
        await InsertPendingAsync("""["film.mkv"]""");
        await InsertPendingAsync($"{{\"count\":{new string('1', WireJsonParser.MaxIntegerDigits + 1)},\"relative_media_path\":\"film.mkv\"}}");
        await InsertPendingAsync("""{"library_id":1}""");
        await InsertPendingAsync("""{"relative_media_path":7}""");
        await InsertPendingAsync("""{"relative_media_path":"other.mkv"}""");
        await InsertPendingAsync("""{"relative_media_path":"film.mkv"}""", status: "completed");
        await InsertPendingAsync($"{{\"relative_media_path\":\"film.mkv\",\"library_id\":\"{libraryId}\"}}");
        var before = await _fixture.Store.Scalar("SELECT count(*) FROM jobs");

        var job = await EnqueueAsync("film.mkv", libraryId: libraryId, passThrough: true);

        Assert.Equal(before + 1, await _fixture.Store.Scalar("SELECT count(*) FROM jobs"));
        Assert.True(job.Id > before);
    }

    [Fact]
    public async Task A_pass_with_no_file_named_is_never_turned_into_another()
    {
        var libraryId = await WatchedMovieLibraryAsync();
        var waiting = await InsertPendingAsync("""{"relative_media_path":""}""");

        var job = await EnqueueAsync("   ", libraryId: libraryId, passThrough: true);

        Assert.NotEqual(waiting, job.Id);
    }
}
