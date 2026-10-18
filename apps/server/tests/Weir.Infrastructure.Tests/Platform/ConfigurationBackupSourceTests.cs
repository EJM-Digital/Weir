using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Infrastructure.Settings;

namespace Weir.Infrastructure.Tests.Platform;

/// <summary>
/// Who made a configuration backup: a backup asked for by hand is listed as the newest and says so, and it never moves the
/// schedule, which stays anchored to the last backup the schedule itself made.
/// </summary>
public sealed class ConfigurationBackupSourceTests
{
    private static readonly DateTime ScheduledRun = new(2026, 1, 15, 2, 0, 0, DateTimeKind.Utc);

    private static ConfigurationBackups BackupsFor(StoreFixture fixture)
    {
        var suiteSettings = new SuiteSettingsStore(fixture.Users);
        var bundle = new ConfigurationBundleStore(
            suiteSettings,
            new ConfigurationBundleConnections(new Weir.Infrastructure.Notifications.NotificationChannelStore(), new Weir.Infrastructure.MediaManagers.MediaManagerConnectionStore()));
        return new ConfigurationBackups(fixture.Options, fixture.Clock, new IanaTimeZoneResolver(), suiteSettings, bundle);
    }

    private static async Task<StoreFixture> DailyAtTwoAsync()
    {
        var fixture = new StoreFixture();
        await fixture.Execute("UPDATE suite_settings SET configuration_backup_enabled = 1, configuration_backup_interval_hours = 24, configuration_backup_preferred_time = '02:00'");
        return fixture;
    }

    [Fact]
    public async Task A_backup_asked_for_by_hand_is_the_newest_and_says_it_was_by_hand()
    {
        using var fixture = await DailyAtTwoAsync();
        var backups = BackupsFor(fixture);
        Assert.Equal(1, await backups.RunTickAsync(fixture.Database, ScheduledRun));

        fixture.Clock.Set(new DateTimeOffset(2026, 1, 15, 15, 30, 0, TimeSpan.Zero));
        await fixture.WithUnitOfWork(uow => backups.CreateAsync(uow, BackupSource.Manual));

        var listed = await fixture.WithUnitOfWork(ConfigurationBackups.ListAsync);
        Assert.Equal(2, listed.Count);
        Assert.Equal(new DateTime(2026, 1, 15, 15, 30, 0, DateTimeKind.Utc), listed[0].CreatedAt.AsUtc);
        Assert.Equal(BackupSource.Manual, listed[0].MadeBy);
        Assert.Equal(BackupSource.Schedule, listed[1].MadeBy);
        Assert.Equal("manual", ((WireString)ConfigurationBackups.ItemOut(listed[0])["made_by"]).Value);
        Assert.Equal("schedule", ((WireString)ConfigurationBackups.ItemOut(listed[1])["made_by"]).Value);
    }

    [Fact]
    public async Task A_backup_by_hand_leaves_the_schedule_where_it_was()
    {
        using var fixture = await DailyAtTwoAsync();
        var backups = BackupsFor(fixture);
        Assert.Equal(1, await backups.RunTickAsync(fixture.Database, ScheduledRun));

        fixture.Clock.Set(new DateTimeOffset(2026, 1, 15, 15, 30, 0, TimeSpan.Zero));
        await fixture.WithUnitOfWork(uow => backups.CreateAsync(uow, BackupSource.Manual));

        Assert.Equal(1, await fixture.Scalar("SELECT count(*) FROM suite_settings WHERE configuration_backup_last_run_at = '2026-01-15 02:00:00.000000'"));
        Assert.Equal(0, await backups.RunTickAsync(fixture.Database, new DateTime(2026, 1, 16, 1, 59, 0, DateTimeKind.Utc)));
        Assert.Equal(1, await backups.RunTickAsync(fixture.Database, new DateTime(2026, 1, 16, 2, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(1, await fixture.Scalar("SELECT count(*) FROM suite_settings WHERE configuration_backup_last_run_at = '2026-01-16 02:00:00.000000'"));
    }

    [Fact]
    public async Task A_backup_made_before_the_source_was_kept_says_nothing_about_who_made_it()
    {
        using var fixture = await DailyAtTwoAsync();
        await fixture.Execute("INSERT INTO suite_configuration_backup (created_at, file_name, size_bytes) VALUES ('2026-01-01 00:00:00.000000', 'suite-configuration-20260101-000000.json', 2)");

        var listed = await fixture.WithUnitOfWork(ConfigurationBackups.ListAsync);

        Assert.Null(Assert.Single(listed).MadeBy);
        Assert.IsType<WireNull>(ConfigurationBackups.ItemOut(listed[0])["made_by"]);
    }
}
