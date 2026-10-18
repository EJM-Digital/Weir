using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// A pass tells the manager how a hand-off ended while its job is still leased: the job is only released after the report and the
/// downloaded-files scan. From the moment the ending is recorded the hand-off is finished, so the status says so (with the copy it
/// delivered) and the manager's answer is not refused as "not finished yet"; before that the job still holds it working.
/// </summary>
public sealed class HandoffStatusAfterReportTests
{
    private const string Folder = "Example.Movie.2024.1080p";
    private const string First = Folder + "/Example.Movie.2024.1080p.part1.mkv";
    private const string Second = Folder + "/Example.Movie.2024.1080p.part2.mkv";
    private const string Output = "/refined/Example.Movie.2024.1080p";

    private static async Task<MediaManagerFixture> ReceiveTwoFilesAsync()
    {
        var fixture = new MediaManagerFixture();
        var watched = fixture.Store.Home.Join("movies");
        Directory.CreateDirectory(Path.Join(watched, Folder));
        await File.WriteAllTextAsync(Path.Join(watched, First), "one");
        await File.WriteAllTextAsync(Path.Join(watched, Second), "two");
        await fixture.LibraryAsync("movie", watched);
        await fixture.Db(uow => fixture.Intake.EnqueueRefineAsync(uow, new MediaManagerImportEvent
        {
            SourceKey = "deluno",
            EventKind = "handoff",
            MediaScope = "movie",
            FilePath = Path.Join(watched, Folder),
            HandoffId = "release",
            LibraryId = "lib-1",
        }));
        return fixture;
    }

    private static async Task<HandoffStatus> StatusAsync(MediaManagerFixture fixture)
    {
        var row = (await fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "release"), commit: false))!;
        return await fixture.Db(uow => fixture.Ledger.CurrentStatusAsync(uow, row), commit: false);
    }

    [Fact]
    public async Task A_hand_off_the_pass_has_reported_is_finished_while_its_job_is_still_leased()
    {
        using var fixture = await ReceiveTwoFilesAsync();
        await fixture.Store.Execute(
            "UPDATE jobs SET status = 'leased'; " +
            $"UPDATE files SET status = 'processed' WHERE relative_path IN ('{First}', '{Second}'); " +
            "UPDATE media_manager_handoff_targets SET result = 'completed'");
        await fixture.Db(async uow =>
        {
            await fixture.Ledger.RecordOutcomeAsync(uow, "deluno", "release", HandoffLedgerRules.Completed, Output, "Done.", [Output]);
            return 0;
        });

        var status = await StatusAsync(fixture);

        Assert.Equal(HandoffLedgerRules.Completed, status.State);
        Assert.Equal(Output, status.OutputPath);
    }

    [Fact]
    public async Task A_file_with_its_result_but_no_ending_recorded_for_the_hand_off_yet_is_still_working()
    {
        using var fixture = await ReceiveTwoFilesAsync();
        await fixture.Store.Execute(
            "UPDATE jobs SET status = 'leased'; " +
            $"UPDATE files SET status = 'processed' WHERE relative_path IN ('{First}', '{Second}'); " +
            "UPDATE media_manager_handoff_targets SET result = 'completed'");

        var status = await StatusAsync(fixture);

        Assert.Equal(HandoffLedgerRules.Working, status.State);
        Assert.Null(status.OutputPath);
    }

    [Fact]
    public async Task A_leased_job_whose_file_has_not_been_reported_is_still_working()
    {
        using var fixture = await ReceiveTwoFilesAsync();
        await fixture.Store.Execute(
            "UPDATE jobs SET status = 'leased'; " +
            $"UPDATE files SET status = 'processed' WHERE relative_path IN ('{First}', '{Second}')");

        var status = await StatusAsync(fixture);

        Assert.Equal(HandoffLedgerRules.Working, status.State);
    }

    [Fact]
    public async Task One_file_reported_and_another_still_being_worked_leaves_the_hand_off_working()
    {
        using var fixture = await ReceiveTwoFilesAsync();
        await fixture.Store.Execute(
            "UPDATE jobs SET status = 'leased'; " +
            $"UPDATE files SET status = 'processed' WHERE relative_path = '{First}'; " +
            $"UPDATE media_manager_handoff_targets SET result = 'completed' WHERE relative_path = '{First}'");

        var status = await StatusAsync(fixture);

        Assert.Equal(HandoffLedgerRules.Working, status.State);
    }
}
