using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// A file Weir found gone has a look booked for later. When the manager sends the release again and the file is back, one pass takes
/// the file over, and the look booked for the send it replaces has nothing left to do.
/// </summary>
public sealed class ResentHandoffLookTests
{
    private const string ReportPath = "/api/integrations/processors/events";

    private static MediaManagerImportEvent Handoff(string handoffId, string path) => new()
    {
        SourceKey = "deluno",
        EventKind = "handoff",
        MediaScope = "movie",
        FilePath = path,
        HandoffId = handoffId,
        CallbackPath = ReportPath,
        LibraryId = "lib-1",
    };

    [Fact]
    public async Task A_resend_taken_by_the_pass_still_running_leaves_no_look_booked_for_the_send_it_replaced()
    {
        using var fixture = new MediaManagerFixture();
        await fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        var watched = fixture.Store.Home.Join("movies");
        var path = Path.Join(watched, "Film", "film.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "x");
        await fixture.LibraryAsync("movie", watched);
        await fixture.Db(uow => fixture.Intake.EnqueueRefineAsync(uow, Handoff("first", path)));
        var running = Assert.Single(await fixture.Jobs.ListAsync());

        // The pass found the file gone, booked its second look, and has not yet finished when the file is back and the release is sent again.
        var look = ((WireObject)WireJsonParser.Parse(running.PayloadJson!)).Set("gone_looks", 1);
        await fixture.Db(uow => fixture.Jobs.EnqueueOrGetAsync(
            uow, $"{IntakeRules.RemuxPassJobKind}:gone-wait:{running.Id}", IntakeRules.RemuxPassJobKind, IntakeRules.PayloadJson(look), 3, runnerCost: null, priority: 0));
        await fixture.Store.Execute(
            $"UPDATE jobs SET status = 'leased' WHERE id = {running.Id}; " +
            "UPDATE jobs SET not_before = datetime('now', '+11 minutes') WHERE dedupe_key LIKE '%:gone-wait:%'");

        await fixture.Db(uow => fixture.Intake.EnqueueRefineAsync(uow, Handoff("again", path)));

        Assert.Equal(0, await fixture.Store.Scalar("SELECT count(*) FROM jobs WHERE status = 'pending' AND dedupe_key LIKE '%:gone-wait:%'"));
        Assert.Equal(1, await fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE id = {running.Id} AND dedupe_key LIKE '%again%'"));
    }
}
