using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.SystemReadings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.SystemReadings;

/// <summary>The readings and rates at their edges: a figure the system could not give, a window with no time in it, no saved settings yet.</summary>
public sealed class ReadingRulesEdgeTests
{
    [Theory]
    [InlineData(null, 100L, null)]
    [InlineData(50L, null, null)]
    [InlineData(50L, 0L, null)]
    [InlineData(50L, 200L, 25d)]
    public void Memory_in_use_is_a_share_of_the_total_only_when_both_are_known(long? used, long? total, double? expected)
    {
        var reading = new MachineReading(null, used, total, null, null, null);

        Assert.Equal(expected, reading.MemoryPercent);
    }

    [Fact]
    public void Memory_in_a_sample_is_a_share_of_the_total_only_when_both_are_known()
    {
        var at = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        var unread = StatsNow.Unread(at, 4);

        Assert.Null(StatsSample.Of(unread).Point.MemoryPercent);
        Assert.Null(StatsSample.Of(unread with { MemoryTotalBytes = 0, MemoryUsedBytes = 5 }).Point.MemoryPercent);
        Assert.Null(StatsSample.Of(unread with { MemoryTotalBytes = 100 }).Point.MemoryPercent);
        Assert.Equal(40d, StatsSample.Of(unread with { MemoryTotalBytes = 100, MemoryUsedBytes = 40 }).Point.MemoryPercent);
    }

    [Fact]
    public void A_disk_that_went_backwards_or_did_not_move_has_no_busy_share()
    {
        var before = new DiskCounters(0, 0, IdleTime: 100, QueryTime: 100);

        Assert.Null(HostRates.BusyPercent(before, before));
        Assert.Null(HostRates.BusyPercent(before, new DiskCounters(0, 0, IdleTime: 50, QueryTime: 200)));
        Assert.Equal(75d, HostRates.BusyPercent(before, new DiskCounters(0, 0, IdleTime: 150, QueryTime: 300)));
    }

    [Fact]
    public void No_volumes_is_the_empty_snapshot()
    {
        Assert.Same(DiskSnapshot.Empty, DiskSnapshot.FromVolumes(new Dictionary<string, DiskCounters>()));
        Assert.NotNull(DiskSnapshot.FromVolumes(new Dictionary<string, DiskCounters> { ["C:"] = new(1, 2, 3, 4) }).Machine);
    }

    [Fact]
    public void A_drive_reader_counts_the_work_files_itself_when_it_is_not_told_how()
    {
        var reader = new DriveReader(new FakeHostReadingSource(), new FreeSpaceForecast(new FakeTimeProvider()), new FakeTimeProvider());

        Assert.Empty(reader.Read([]));
    }

    [Fact]
    public async Task Before_any_setting_is_saved_the_files_at_once_are_the_workers_it_started_with()
    {
        using var store = new StoreFixture();
        await store.Execute("DELETE FROM operator_settings");
        var changes = new DataChangePublisher();
        var source = new DatabaseWorkSource(store.Database, new LibraryStore(changes), new OperatorSettingsStore(changes), store.Options);

        var setup = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(store.Options.ProcessingWorkerCount, setup.Slots);
    }
}
