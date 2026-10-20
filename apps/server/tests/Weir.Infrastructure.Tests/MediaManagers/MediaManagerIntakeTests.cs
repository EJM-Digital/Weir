using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// Taking in a media manager's word: who may speak, which workflow a hand-off belongs to, and what happens to a file a person
/// chose to keep when a manager sends it again. Real folders and a real database.
/// </summary>
public sealed class MediaManagerIntakeTests : IDisposable
{
    private const string Secret = "s3cret";

    private readonly MediaManagerFixture _fixture = new(("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", Secret));

    public void Dispose() => _fixture.Dispose();

    private string Watched => _fixture.Store.Home.Join("movies");

    private async Task<long> LibraryWithAsync(string relative, string content = "film")
    {
        var path = Path.Join(Watched, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return await _fixture.LibraryAsync("movie", Watched);
    }

    private async Task KeepAsync(long libraryId, string relative)
    {
        var fingerprint = SourceFiles.Fingerprint(Path.Join(Watched, relative));
        await _fixture.Db(async uow =>
        {
            await _fixture.SkipMarkers.SetAsync(uow, libraryId, relative, fingerprint.SizeBytes, fingerprint.ModifiedTimeNs);
            return true;
        });
    }

    private static MediaManagerImportEvent Webhook(string path, string? handoffId = null) => new()
    {
        SourceKey = "radarr",
        EventKind = MediaManagerImportEvent.Handoff,
        MediaScope = "movie",
        FilePath = path,
        HandoffId = handoffId,
    };

    [Fact]
    public void A_refusal_carries_the_status_the_route_answers_with()
    {
        Assert.Equal(400, new IntakeRefusedException().StatusCode);
        Assert.Equal(400, new IntakeRefusedException("plain").StatusCode);
        Assert.Equal(400, new IntakeRefusedException("wrapped", new InvalidOperationException()).StatusCode);
        var refusal = new IntakeRefusedException(401, "no");
        Assert.Equal(401, refusal.StatusCode);
        Assert.Equal("no", refusal.Message);
    }

    [Fact]
    public async Task The_instance_secret_lets_the_only_connection_of_a_kind_speak_and_lights_it_up()
    {
        var id = await _fixture.AddConnectionAsync("radarr");

        var identity = await _fixture.Db(uow => _fixture.Intake.AuthoriseAsync(uow, "radarr", Secret));

        Assert.True(identity.Authenticated);
        Assert.Equal(id, identity.ConnectionId);
    }

    [Fact]
    public async Task A_wrong_or_missing_secret_is_refused_with_a_401()
    {
        await _fixture.AddConnectionAsync("radarr");

        var wrong = await Assert.ThrowsAsync<IntakeRefusedException>(() => _fixture.Db(uow => _fixture.Intake.AuthoriseAsync(uow, "radarr", "nope")));
        var missing = await Assert.ThrowsAsync<IntakeRefusedException>(() => _fixture.Db(uow => _fixture.Intake.AuthoriseAsync(uow, "radarr", null)));

        Assert.Equal(401, wrong.StatusCode);
        Assert.Equal(401, missing.StatusCode);
    }

    [Fact]
    public async Task An_intake_that_is_not_watching_the_connections_still_answers_who_spoke()
    {
        var id = await _fixture.AddConnectionAsync("radarr");
        var intake = new MediaManagerIntake(
            _fixture.Store.Options, _fixture.Connections, _fixture.ConnectionStore, _fixture.Ledger, _fixture.Targets, _fixture.Jobs,
            _fixture.SkipMarkers, _fixture.Reporter, _fixture.Artwork, _fixture.Store.Clock);

        var identity = await _fixture.Db(uow => intake.AuthoriseAsync(uow, "radarr", Secret));

        Assert.Equal(id, identity.ConnectionId);
    }

    [Fact]
    public async Task A_hand_off_that_names_no_file_is_explained_against_the_library_of_its_scope()
    {
        var libraryId = await _fixture.LibraryAsync("movie", Watched);

        var (library, resolved) = await _fixture.Db(uow => MediaManagerIntake.LibraryForHandoffAsync(uow, Webhook(" ")));

        Assert.Equal(libraryId, library!.Id);
        Assert.NotNull(resolved);
    }

    [Fact]
    public async Task A_file_a_person_chose_to_keep_is_not_queued_again_when_a_manager_sends_it_without_a_hand_off()
    {
        var libraryId = await LibraryWithAsync("Film (2020)/Film.mkv");
        await KeepAsync(libraryId, "Film (2020)/Film.mkv");

        await _fixture.Db(uow => _fixture.Intake.EnqueueRefineAsync(uow, Webhook(Path.Join(Watched, "Film (2020)", "Film.mkv"))));

        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
        Assert.Equal(1, await _fixture.Store.Scalar(
            "SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_removal_kept' AND title LIKE '% handed Film.mkv to Weir again, but it is kept, so Weir left it alone'"));
    }

    [Fact]
    public async Task A_kept_file_that_has_changed_since_is_a_new_file_and_is_queued()
    {
        var libraryId = await LibraryWithAsync("Film (2020)/Film.mkv");
        await KeepAsync(libraryId, "Film (2020)/Film.mkv");
        await File.WriteAllTextAsync(Path.Join(Watched, "Film (2020)", "Film.mkv"), "a different, longer file");

        await _fixture.Db(uow => _fixture.Intake.EnqueueRefineAsync(uow, Webhook(Path.Join(Watched, "Film (2020)", "Film.mkv"))));

        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
    }

    [Fact]
    public async Task A_kept_file_that_is_gone_from_the_disk_can_no_longer_be_told_apart_so_it_is_not_kept()
    {
        var libraryId = await LibraryWithAsync("Film (2020)/Film.mkv");
        await KeepAsync(libraryId, "Film (2020)/Film.mkv");
        File.Delete(Path.Join(Watched, "Film (2020)", "Film.mkv"));

        await _fixture.Db(uow => _fixture.Intake.EnqueueRefineAsync(uow, Webhook(Path.Join(Watched, "Film (2020)", "Film.mkv"))));

        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
    }
}
