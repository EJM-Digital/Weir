using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Jobs;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>What a pass records on a file's row when the path is odd, the row is missing or the record is too long to keep whole.</summary>
public sealed class RemuxPassFileStateEdgeTests : IDisposable
{
    private const string Path = "Heat (1995)/heat.mkv";
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly JobsTestDatabase _db = new();

    public RemuxPassFileStateEdgeTests()
    {
        _db.Execute("INSERT INTO libraries (id, name, media_type, watched_folder, display_order) VALUES (1, 'Movies', 'movie', '/in', 1), (2, 'Kids', 'movie', '/kids', 2)");
        _db.Execute($"INSERT INTO files (id, library_id, relative_path) VALUES (10, 1, '{Path}'), (20, 2, '{Path}')");
    }

    public void Dispose() => _db.Dispose();

    private async Task InAsync(Func<UnitOfWork, Task> work)
    {
        var uow = await UnitOfWork.OpenAsync(_db.Database);
        await using (uow.ConfigureAwait(false))
        {
            await work(uow);
            await uow.CommitAsync();
        }
    }

    private static MeasuredMediaFacts Facts(long? libraryId, string? codec = "hevc", IReadOnlyList<string>? audio = null) =>
        new(Path, 1920, 1080, codec, 2, 1, 7200.5, audio, 10, libraryId);

    private string? Text(string sql) => _db.Scalar(sql)?.ToString();

    [Fact]
    public async Task A_note_for_a_file_with_no_row_is_dropped_and_no_row_is_made()
    {
        await InAsync(uow => RemuxPassFileState.AppendStatusReasonAsync(uow, 1, "Other/other.mkv", "Waiting."));

        Assert.Equal("0", Text("SELECT count(*) FROM files WHERE relative_path = 'Other/other.mkv'"));
    }

    [Fact]
    public async Task A_note_is_added_after_the_reason_the_file_already_has()
    {
        _db.Execute("UPDATE files SET status_reason = 'Waiting for Deluno.' WHERE id = 10");

        await InAsync(uow => RemuxPassFileState.AppendStatusReasonAsync(uow, 1, Path, "Retrying soon."));

        Assert.Equal("Waiting for Deluno. Retrying soon.", Text("SELECT status_reason FROM files WHERE id = 10"));
        Assert.Equal(string.Empty, Text("SELECT coalesce(status_reason, '') FROM files WHERE id = 20"));
    }

    [Fact]
    public async Task Measured_facts_go_on_the_row_of_the_workflow_that_measured_them_and_nothing_else()
    {
        await InAsync(uow => RemuxPassFileState.RecordMeasuredMediaFactsAsync(uow, Facts(2, audio: ["AC3", " ", "EAC3"])));

        Assert.Equal("hevc|1920|1080|2|1|10|ac3,eac3", Text("SELECT video_codec || '|' || video_width || '|' || video_height || '|' || audio_track_count || '|' || subtitle_track_count || '|' || video_bit_depth || '|' || audio_codecs FROM files WHERE id = 20"));
        Assert.Equal(string.Empty, Text("SELECT coalesce(video_codec, '') FROM files WHERE id = 10"));
    }

    [Fact]
    public async Task Measured_facts_with_no_workflow_or_nothing_measured_change_no_row()
    {
        await InAsync(uow => RemuxPassFileState.RecordMeasuredMediaFactsAsync(uow, Facts(libraryId: null)));
        await InAsync(uow => RemuxPassFileState.RecordMeasuredMediaFactsAsync(uow, new MeasuredMediaFacts(Path, null, null, null, null, null, null, null, null, 1)));

        Assert.Equal("0", Text("SELECT count(*) FROM files WHERE video_codec IS NOT NULL OR video_width IS NOT NULL"));
    }

    [Fact]
    public async Task A_collision_decision_with_no_workflow_changes_no_row_and_one_with_a_workflow_is_kept_on_that_row_only()
    {
        var decision = new CollisionDecision("replace", "replaced", "/out/heat.mkv", true, "A better copy replaced it.");

        await InAsync(uow => RemuxPassFileState.RecordOutputCollisionAsync(uow, Path, decision, libraryId: null));
        Assert.Equal("0", Text("SELECT count(*) FROM files WHERE output_collision_policy IS NOT NULL"));

        await InAsync(uow => RemuxPassFileState.RecordOutputCollisionAsync(uow, Path, decision, libraryId: 1));
        Assert.Equal("replace|replaced|A better copy replaced it.", Text("SELECT output_collision_policy || '|' || output_collision_action || '|' || output_collision_reason FROM files WHERE id = 10"));
        Assert.Equal("1", Text("SELECT count(*) FROM files WHERE output_collision_policy IS NOT NULL"));
    }

    [Fact]
    public async Task A_record_too_long_to_keep_is_kept_shortened_and_says_so()
    {
        var detail = new WireObject().Set("outcome", "processed").Set("library_id", 1L).Set("log", new string('x', FileLogStore.MaxDetailChars + 10));

        await InAsync(uow => RemuxPassFileState.RecordFileLogAsync(uow, Path, "Pass finished", detail, Now));

        var kept = Text("SELECT detail_json FROM file_logs")!;
        Assert.True(kept.Length <= FileLogStore.MaxDetailChars + 1000);
        Assert.Contains("\"truncated\":true", kept, StringComparison.Ordinal);
        Assert.Contains("was shortened when it was saved", kept, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"processed\"", kept, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_of_a_path_outside_the_watched_folder_is_still_recorded_with_no_size_and_no_fingerprint()
    {
        var library = await LibraryAsync(1);

        await InAsync(uow => RemuxPassFileState.RecordFailureAsync(uow, library, "../escape/film.mkv", ProcessingFailureClasses.Execution, "The tool failed.", Now));

        Assert.Equal("processing_failed|0", Text("SELECT status || '|' || size_bytes FROM files WHERE relative_path = '../escape/film.mkv'"));
        Assert.Equal(string.Empty, Text("SELECT coalesce(fingerprint_size_bytes, '') FROM files WHERE relative_path = '../escape/film.mkv'"));
    }

    private async Task<Weir.Core.Processing.ProcessingLibraryRecord> LibraryAsync(long id)
    {
        Weir.Core.Processing.ProcessingLibraryRecord? library = null;
        await InAsync(async uow => library = await new LibraryStore().GetAsync(uow, id));
        return library!;
    }
}
