using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Scheduling;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Tests;

/// <summary>The real server hosts every periodic loop, started after crash recovery.</summary>
public sealed class PeriodicTasksStartupTests
{
    [Fact]
    public async Task The_server_hosts_every_periodic_loop_after_startup_recovery()
    {
        await using var server = await WeirTestServer.StartAsync();

        Assert.Equal(
            ["activity-latest-poll", "artwork-resolver", "auth-session-cleanup", "connection-usage-flush", "folder-checks", "media-manager-heartbeat", "media-manager-workflow-sync", "metrics-change", "platform-job-rows-retention", "processing-file-log-retention", "processing-library-mode-schedule", "processing-vanished-file-sweep", "processing-watched-folder-remux-scan-dispatch-enqueue", "readiness-changes", "suite-configuration-backup", "suite-log-retention", "suite-pause-expiry", "system-overview", "update-check", "update-work-state"],
            server.Services.GetServices<IPeriodicTask>().Select(task => task.Name).Order(StringComparer.Ordinal));
        var hosted = server.Services.GetServices<IHostedService>().ToList();
        Assert.Single(hosted.OfType<PeriodicTaskService>());
        Assert.True(hosted.FindIndex(service => service is JobsStartupRecoveryService) < hosted.FindIndex(service => service is PeriodicTaskService));
    }

    [Fact]
    public async Task The_server_runs_one_pulse_that_notes_when_the_whole_process_could_not_run()
    {
        await using var server = await WeirTestServer.StartAsync();

        var pulse = server.Services.GetRequiredService<ProcessPulse>();
        Assert.Same(pulse, Assert.Single(server.Services.GetServices<IHostedService>().OfType<ProcessPulse>()));
    }

    [Fact]
    public async Task An_enabled_configuration_backup_with_none_taken_is_written_soon_after_start()
    {
        await using var server = await WeirTestServer.StartAsync(
            prepareHome: home =>
            {
                var dbPath = Path.Join(home, "data", "weir.sqlite3");
                Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
                var database = new SqliteDatabase(dbPath);
                new SchemaMigrator(database).EnsureAtHead();
                using (var connection = database.Open())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "INSERT OR REPLACE INTO suite_settings (id, product_display_name, setup_wizard_state, app_timezone, log_retention_days, " +
                        "activity_retention_days, configuration_backup_enabled, configuration_backup_interval_hours, configuration_backup_preferred_time) " +
                        "VALUES (1, 'Weir', 'completed', 'UTC', 30, 90, 1, 6, '04:15')";
                    command.ExecuteNonQuery();
                }

                database.ClearPool();
            });

        var database = server.Services.GetRequiredService<SqliteDatabase>();
        await Eventually.ThatAsync(
            () =>
            {
                using var connection = database.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT (SELECT count(*) FROM suite_configuration_backup), (SELECT configuration_backup_last_run_at FROM suite_settings WHERE id = 1)";
                using var reader = command.ExecuteReader();
                reader.Read();
                return reader.GetInt64(0) == 1 && !reader.IsDBNull(1);
            },
            TimeSpan.FromSeconds(30),
            "No automatic configuration backup was written.");
    }
}
