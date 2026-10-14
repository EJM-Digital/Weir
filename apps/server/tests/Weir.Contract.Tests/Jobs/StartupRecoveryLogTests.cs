using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Jobs;

/// <summary>
/// What start-up recovery says in the server's own log, and that the worker lanes start whatever it found. Start-up recovery runs
/// after the server is listening, so a failure in it must be logged loudly and must not leave the lanes waiting for ever.
/// </summary>
[ContractArea("jobs")]
public sealed class StartupRecoveryLogTests
{
    private const string Logger = "Weir.Infrastructure.Jobs.JobsStartupRecoveryService";

    [Fact]
    public async Task A_workflow_whose_saved_folder_breaks_the_folder_rules_is_named_in_a_warning_and_keeps_working()
    {
        await using var server = await WeirServer.StartNewAsync();
        string libraryName;
        await using (var database = await server.StopForDatabaseAsync())
        {
            libraryName = (string)SeedSql.Scalar(database.Connection, "SELECT name FROM libraries ORDER BY id LIMIT 1")!;
            SeedSql.Execute(database.Connection, "UPDATE libraries SET watched_folder = $home WHERE name = $name", ("$home", server.Home), ("$name", libraryName));
        }

        using var client = await server.CreateAdminClientAsync();

        Assert.Contains(
            $" WARNING {Logger} Workflow {libraryName}'s watched folder no longer meets Weir's folder rules: Weir can't use its own data folder as a watched folder. "
            + "Choose a different folder. It keeps working until the workflow is next saved.",
            server.LogText(),
            StringComparison.Ordinal);
        var libraries = await client.GetAsync($"{WeirClient.Api}/processing/libraries");
        Assert.Contains(libraries.Elements, library => (string?)library!["name"] == libraryName && (string?)library["watched_folder"] == server.Home);
    }

    [Fact]
    public async Task A_recovery_that_fails_is_logged_as_critical_and_the_worker_lanes_still_start()
    {
        await using var server = await WeirServer.StartNewAsync(new Dictionary<string, string> { ["WEIR_PROCESSING_WORKER_COUNT"] = "1" });
        await using (var database = await server.StopForDatabaseAsync())
        {
            // A job a dead process left leased, and a database that refuses to put it back in the queue.
            JobRows.Insert(
                database.Connection,
                "interrupted",
                "processing.file.remux_pass.v1",
                status: "leased",
                columns: [("lease_owner", "dead-host-1-w0"), ("lease_expires_at", "2999-01-01 00:00:00+00:00"), ("attempt_count", 1)]);
            SeedSql.Execute(
                database.Connection,
                "CREATE TRIGGER refuse_requeue BEFORE UPDATE ON jobs WHEN OLD.dedupe_key = 'interrupted' "
                + "BEGIN SELECT RAISE(ABORT, 'the database refused the requeue'); END");
        }

        using var client = await server.CreateAdminClientAsync();

        // Ready means the processing lane has started and is beating: it got past the gate recovery holds shut.
        var readiness = await client.GetAsync($"{WeirClient.Api}/system/readiness");
        Assert.True((bool)readiness.Fields["ready"]!, readiness.Text);
        var lane = Assert.Single(readiness.Fields["worker_health"]!.AsArray(), worker => (string?)worker!["module"] == "processing");
        Assert.Equal(1, (int)lane!["active_workers"]!);
        Assert.Contains(
            $" CRITICAL {Logger} Weir startup recovery failed; passes and scans will start without it once Weir is restarted.",
            server.LogText(),
            StringComparison.Ordinal);
        Assert.Equal("leased", (string?)(await JobsApi.AllJobsAsync(client)).Single(job => (string?)job["dedupe_key"] == "interrupted")["status"]);
    }
}
