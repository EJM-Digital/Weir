using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// A pass tells the manager how a file ended while its job is still leased: the job is only released after the report and the
/// downloaded-files scan. From the report on, the file is finished for the hand-off, so the status says so and the manager's answer
/// is not refused as "not finished yet".
/// </summary>
public sealed class HandoffStatusAfterReportTests
{
    private const string Folder = "Example.Movie.2024.1080p";
    private const string First = Folder + "/Example.Movie.2024.1080p.part1.mkv";
    private const string Second = Folder + "/Example.Movie.2024.1080p.part2.mkv";

    private static async Task<(MediaManagerFixture Fixture, HandoffLedgerRow Row)> ReceiveTwoFilesAsync()
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
        var row = (await fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "release")))!;
        return (fixture, row);
    }

    [Fact]
    public async Task A_file_the_pass_has_reported_is_finished_while_its_job_is_still_leased()
    {
        var (fixture, row) = await ReceiveTwoFilesAsync();
        using (fixture)
        {
            await fixture.Store.Execute(
                "UPDATE jobs SET status = 'leased'; " +
                $"UPDATE files SET status = 'processed' WHERE relative_path IN ('{First}', '{Second}'); " +
                "UPDATE media_manager_handoff_targets SET result = 'completed'");

            var status = await fixture.Db(uow => fixture.Ledger.CurrentStatusAsync(uow, row));

            Assert.Equal(HandoffLedgerRules.Completed, status.State);
        }
    }

    [Fact]
    public async Task A_leased_job_whose_file_has_not_been_reported_is_still_working()
    {
        var (fixture, row) = await ReceiveTwoFilesAsync();
        using (fixture)
        {
            await fixture.Store.Execute(
                "UPDATE jobs SET status = 'leased'; " +
                $"UPDATE files SET status = 'processed' WHERE relative_path IN ('{First}', '{Second}')");

            var status = await fixture.Db(uow => fixture.Ledger.CurrentStatusAsync(uow, row));

            Assert.Equal(HandoffLedgerRules.Working, status.State);
        }
    }

    [Fact]
    public async Task One_file_reported_and_another_still_being_worked_leaves_the_hand_off_working()
    {
        var (fixture, row) = await ReceiveTwoFilesAsync();
        using (fixture)
        {
            await fixture.Store.Execute(
                "UPDATE jobs SET status = 'leased'; " +
                $"UPDATE files SET status = 'processed' WHERE relative_path = '{First}'; " +
                $"UPDATE media_manager_handoff_targets SET result = 'completed' WHERE relative_path = '{First}'");

            var status = await fixture.Db(uow => fixture.Ledger.CurrentStatusAsync(uow, row));

            Assert.Equal(HandoffLedgerRules.Working, status.State);
        }
    }
}
