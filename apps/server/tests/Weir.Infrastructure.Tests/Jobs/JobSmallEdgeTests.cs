using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>Small readings the job code relies on: stored times, the home folder, the do-nothing senders, and passes whose payload names no file.</summary>
public sealed class JobSmallEdgeTests
{
    [Fact]
    public void The_queue_metrics_that_go_nowhere_take_a_depth_and_an_event_and_keep_neither()
    {
        var exception = Record.Exception(() =>
        {
            NoJobQueueMetrics.Instance.RecordJobEvent("processing", "started");
            NoJobQueueMetrics.Instance.SetQueueDepth("processing", 3);
        });

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void A_stored_time_with_nothing_in_it_is_no_time(string stored)
    {
        Assert.Null(TimestampColumns.Parse(stored));
    }

    [Theory]
    [InlineData("~")]
    [InlineData("~/films")]
    [InlineData("~\\films")]
    public void A_path_starting_with_a_tilde_is_the_users_home(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var expanded = ProcessingLibraryFolders.ExpandForFilesystem(path);

        Assert.StartsWith(Path.GetFullPath(home), expanded, StringComparison.Ordinal);
        Assert.Equal(path.Length > 1, expanded.EndsWith("films", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pending_pass_whose_payload_names_no_file_is_not_counted_against_any_file()
    {
        using var db = new JobsTestDatabase();
        db.InsertRawJob("no-payload", RemuxPassOutcomes.JobKind);
        db.InsertRawJob("empty-object", RemuxPassOutcomes.JobKind, payloadJson: "{}");
        db.InsertRawJob("a-list", RemuxPassOutcomes.JobKind, payloadJson: "[1,2]");
        db.InsertRawJob("json5", RemuxPassOutcomes.JobKind, payloadJson: "{relative_media_path: 'Odd.mkv', library_id: 1,}");
        db.InsertRawJob("no-library", RemuxPassOutcomes.JobKind, payloadJson: "{\"relative_media_path\":\"Loose.mkv\"}");
        db.InsertRawJob("a-film", RemuxPassOutcomes.JobKind, payloadJson: "{\"relative_media_path\":\" Film.mkv \",\"library_id\":1}");
        db.InsertRawJob("a-show", RemuxPassOutcomes.JobKind, payloadJson: "{\"relative_media_path\":\"Show.mkv\",\"media_scope\":\"tv\",\"library_id\":1}");

        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        Assert.Equal(["Film.mkv"], await ActiveRemuxPasses.PathsAsync(uow, "movie", 1));
        Assert.Equal(["Show.mkv"], await ActiveRemuxPasses.PathsAsync(uow, "tv", 1));
    }
    [Fact]
    public async Task A_file_with_several_passes_booked_for_later_is_held_until_the_earliest_and_other_files_are_not_counted()
    {
        using var db = new JobsTestDatabase();
        var now = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        string At(int days) => TimestampColumns.Orm(now.AddDays(days));
        const string film = "{\"relative_media_path\":\"Film.mkv\",\"library_id\":1}";
        db.InsertRawJob("film-late", RemuxPassOutcomes.JobKind, payloadJson: film, notBefore: At(2));
        db.InsertRawJob("film-early", RemuxPassOutcomes.JobKind, payloadJson: film, notBefore: At(1));
        db.InsertRawJob("film-later", RemuxPassOutcomes.JobKind, payloadJson: film, notBefore: At(3));
        db.InsertRawJob("film-past", RemuxPassOutcomes.JobKind, payloadJson: film, notBefore: At(-1));
        db.InsertRawJob("film-date-only", RemuxPassOutcomes.JobKind, payloadJson: film, notBefore: "2026-10-22");
        db.InsertRawJob("show", RemuxPassOutcomes.JobKind, payloadJson: "{\"relative_media_path\":\"Show.mkv\",\"media_scope\":\"tv\",\"library_id\":1}", notBefore: At(1));
        db.InsertRawJob("other-library", RemuxPassOutcomes.JobKind, payloadJson: "{\"relative_media_path\":\"Other.mkv\",\"library_id\":2}", notBefore: At(1));
        db.InsertRawJob("names-no-file", RemuxPassOutcomes.JobKind, payloadJson: "{}", notBefore: At(1));

        await using var uow = await UnitOfWork.OpenAsync(db.Database);
        var held = await ActiveRemuxPasses.HeldBackStartsAsync(uow, "movie", 1, now);

        Assert.Equal(new Dictionary<string, DateTimeOffset> { ["Film.mkv"] = now.AddDays(1) }, held);
    }
}
