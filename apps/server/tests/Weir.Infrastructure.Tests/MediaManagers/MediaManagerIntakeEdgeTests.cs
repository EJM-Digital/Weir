using System.Globalization;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>Taking in a hand-off when there is no workflow for it, when the disk will not measure the file, and when its source was cleaned before.</summary>
public sealed class MediaManagerIntakeEdgeTests : IDisposable
{
    private const string Relative = "Film (2020)/Film.mkv";

    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private string Watched => _fixture.Store.Home.Join("movies");

    private string Source => Path.Join(Watched, "Film (2020)", "Film.mkv");

    private static MediaManagerImportEvent Webhook(string path, string? handoffId = null) => new()
    {
        SourceKey = "radarr",
        EventKind = MediaManagerImportEvent.Handoff,
        MediaScope = "movie",
        FilePath = path,
        HandoffId = handoffId,
    };

    private async Task<long> LibraryWithFileAsync(string outputFolder = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        await File.WriteAllTextAsync(Source, "film");
        var id = await _fixture.LibraryAsync("movie", Watched);
        await _fixture.Store.Execute($"UPDATE libraries SET output_folder = '{outputFolder.Replace("'", "''", StringComparison.Ordinal)}' WHERE id = {id}");
        return id;
    }

    private async Task CleanedBeforeAsync(long libraryId)
    {
        var copy = _fixture.Store.Home.Join("out", "Film.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        await File.WriteAllTextAsync(copy, "film");
        var modifiedNs = (File.GetLastWriteTimeUtc(Source) - DateTime.UnixEpoch).Ticks * 100;
        await _fixture.Store.Execute(
            "INSERT INTO handbacks (library_id, relative_path, output_path, output_size, output_mtime_ns, written_at, source_size, source_mtime_ns) VALUES " +
            $"({libraryId}, '{Relative}', '{copy.Replace("'", "''", StringComparison.Ordinal)}', 4, 1, '2026-10-07 04:00:00.000000', 4, {modifiedNs.ToString(CultureInfo.InvariantCulture)})");
    }

    [Fact]
    public async Task A_connection_secret_that_does_not_match_is_refused_when_the_instance_has_no_secret_to_fall_back_on()
    {
        var id = await _fixture.AddConnectionAsync("radarr");
        var row = (await _fixture.Db(uow => _fixture.ConnectionStore.GetAsync(uow, id)))!;
        await _fixture.Db(uow => _fixture.Connections.RotateWebhookSecretAsync(uow, row));

        var refused = await Assert.ThrowsAsync<IntakeRefusedException>(
            () => _fixture.Db(uow => _fixture.Intake.RequireSecretAsync(uow, "not-the-secret", "radarr")));

        Assert.Equal(401, refused.StatusCode);
    }

    [Fact]
    public async Task A_hand_off_naming_no_file_for_a_scope_with_no_workflow_is_refused()
    {
        await _fixture.Store.Execute("DELETE FROM libraries");

        var (library, resolved) = await _fixture.Db(uow => MediaManagerIntake.LibraryForHandoffAsync(uow, Webhook(" ")));
        var refused = await Assert.ThrowsAsync<IntakeRefusedException>(() => _fixture.Db(uow => _fixture.Intake.EnqueueRefineAsync(uow, Webhook(" ", "h-1"))));

        Assert.Null(library);
        Assert.False(resolved.Ok);
        Assert.Equal(400, refused.StatusCode);
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
    }

    [Fact]
    public async Task A_file_the_disk_will_not_measure_is_still_queued_and_its_size_is_left_unrecorded()
    {
        var libraryId = await LibraryWithFileAsync();
        var intake = new MediaManagerIntake(
            _fixture.Store.Options, _fixture.Connections, _fixture.ConnectionStore, _fixture.Ledger, _fixture.Targets, _fixture.Jobs,
            _fixture.SkipMarkers, _fixture.Reporter, _fixture.Artwork, _fixture.Store.Clock, _fixture.Activity)
        {
            FileSystem = new FaultingFileSystem().Fail(FileCall.GetLength, FaultingFileSystem.SharingViolation, pathEndsWith: "Film.mkv"),
        };

        await _fixture.Db(uow => intake.EnqueueRefineAsync(uow, Webhook(Source)));

        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
        Assert.Equal(0, await _fixture.Store.Scalar($"SELECT count(*) FROM files WHERE library_id = {libraryId} AND size_bytes > 0"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_source_that_can_no_longer_be_measured_is_not_taken_for_one_that_was_cleaned(bool pathEscapesTheFolder)
    {
        var libraryId = await LibraryWithFileAsync();
        await CleanedBeforeAsync(libraryId);
        var relative = Relative;
        if (pathEscapesTheFolder)
        {
            relative = "../outside.mkv";
            await _fixture.Store.Execute($"UPDATE handbacks SET relative_path = '{relative}'");
        }
        else
        {
            File.Delete(Source);
        }

        var earlier = await _fixture.Db(uow => CleanedSources.FindAsync(uow, libraryId, Watched, relative));

        Assert.Null(earlier);
    }

    [Fact]
    public async Task A_source_already_cleaned_and_sent_again_with_no_hand_off_id_is_left_alone_without_a_report()
    {
        var libraryId = await LibraryWithFileAsync();
        await CleanedBeforeAsync(libraryId);

        await _fixture.Db(uow => _fixture.Intake.EnqueueRefineAsync(uow, Webhook(Source)));

        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_skipped_repeat'"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("OUT")]
    public async Task A_source_already_cleaned_and_sent_again_is_answered_whether_or_not_the_workflow_names_an_output_folder(string outputFolder)
    {
        var libraryId = await LibraryWithFileAsync(outputFolder == "OUT" ? _fixture.Store.Home.Join("out") : outputFolder);
        await CleanedBeforeAsync(libraryId);

        await _fixture.Db(uow => _fixture.Intake.EnqueueRefineAsync(uow, Webhook(Source, "h-2")));

        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE handoff_id = 'h-2'"));
    }
}
