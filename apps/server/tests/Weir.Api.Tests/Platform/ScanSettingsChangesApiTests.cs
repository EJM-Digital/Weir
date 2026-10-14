using System.Net;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// Saving the operator's scan settings reaches the running scan scheduler on its next tick, not at its periodic
/// re-read (#720).
/// </summary>
public sealed class ScanSettingsChangesApiTests
{
    private const string ScanKind = "processing.watched_folder.remux_scan_dispatch.v1";

    [Fact]
    public async Task Turning_periodic_scans_on_in_settings_queues_a_scan_straight_away()
    {
        await using var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", ApiTestClient.Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), ("WEIR_PROCESSING_WATCHER_ENABLED", "0")],
            prepareHome: MoviesLibraryWithPeriodicScansOff);
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();

        using var saved = await client.PutAsync(
            "/api/v1/processing/operator-settings",
            new
            {
                csrf_token = await client.CsrfAsync(),
                movie_schedule_enabled = true,
            });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        // Well inside the scheduler's InputsRefresh, so only the recorded change can explain the scan.
        await Eventually.ThatAsync(
            async () => await TestDatabase.ScalarAsync(server, $"SELECT count(*) FROM jobs WHERE job_kind = '{ScanKind}'") > 0,
            ProcessingWatchedFolderScanDispatchScheduleTask.InputsRefresh / 2,
            "No periodic scan was queued after periodic scans were switched on.");
    }

    /// <summary>The seeded Movies library with real folders, and its scope's periodic scans switched off.</summary>
    private static void MoviesLibraryWithPeriodicScansOff(string home)
    {
        var watched = Path.Join(home, "watched");
        var output = Path.Join(home, "output");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        var dbPath = Path.Join(home, "data", "weir.sqlite3");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var database = new SqliteDatabase(dbPath);
        new SchemaMigrator(database).EnsureAtHead();
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE libraries SET watched_folder = @watched, output_folder = @output, enabled = 1 WHERE media_type = 'movie'; " +
                "INSERT OR IGNORE INTO operator_settings (id) VALUES (1); " +
                "UPDATE operator_settings SET movie_schedule_enabled = 0 WHERE id = 1";
            command.Parameters.AddWithValue("@watched", watched);
            command.Parameters.AddWithValue("@output", output);
            command.ExecuteNonQuery();
        }

        database.ClearPool();
    }
}
