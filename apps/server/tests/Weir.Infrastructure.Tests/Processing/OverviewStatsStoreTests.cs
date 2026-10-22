using Weir.Core.Activity;
using Weir.Core.Time;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>The overview's counters, read from the details Weir recorded for each finished pass.</summary>
public sealed class OverviewStatsStoreTests : IDisposable
{
    private readonly StoreFixture _store = new();

    public void Dispose() => _store.Dispose();

    private async Task RecordAsync(string? detail, TimeSpan age = default)
    {
        var at = SqliteValues.ToSqlite(Timestamp.FromUtc(_store.Clock.GetUtcNow().UtcDateTime - age));
        await _store.WithUnitOfWork(async uow =>
        {
            await uow.ExecuteAsync(
                "INSERT INTO activity_events (created_at, event_type, module, title, detail) VALUES (@at, @type, 'processing', 'Finished', @detail)",
                ("@at", at), ("@type", ActivityEventTypes.ProcessingFileRemuxPassCompleted), ("@detail", detail));
            return 0;
        });
    }

    private Task<ProcessingOverviewStats> BuildAsync(int days = 30) =>
        _store.WithUnitOfWork(uow => new OverviewStatsStore().BuildAsync(uow, days, _store.Clock));

    [Fact]
    public async Task With_nothing_finished_every_counter_is_zero()
    {
        var stats = await BuildAsync();

        Assert.Equal(new ProcessingOverviewStats(30, 0, 0, 0, 0, 0, 0, 0), stats);
    }

    [Fact]
    public async Task A_window_of_less_than_a_day_is_a_day()
    {
        Assert.Equal(1, (await BuildAsync(0)).WindowDays);
    }

    [Fact]
    public async Task Written_outputs_count_and_their_sizes_give_the_space_saved()
    {
        await RecordAsync("""{"outcome":"live_output_written","source_size_bytes":1000,"output_size_bytes":400}""");
        await RecordAsync("""{"outcome":"live_output_written","source_size_bytes":"3000","output_size_bytes":"1600.0"}""");

        var stats = await BuildAsync();

        Assert.Equal(2, stats.OutputWrittenCount);
        Assert.Equal(2, stats.FilesProcessed);
        Assert.Equal(2000, stats.NetSpaceSavedBytes);
        Assert.Equal(50.0, stats.NetSpaceSavedPercent);
        Assert.Equal(100.0, stats.SuccessRatePercent);
    }

    [Fact]
    public async Task Sizes_given_as_fractions_are_cut_to_whole_bytes()
    {
        await RecordAsync("""{"outcome":"live_output_written","source_size_bytes":1000.9,"output_size_bytes":500.2}""");

        var stats = await BuildAsync();

        Assert.Equal(500, stats.NetSpaceSavedBytes);
    }

    [Fact]
    public async Task A_written_output_without_both_sizes_counts_but_adds_nothing_to_the_space_saved()
    {
        await RecordAsync("""{"outcome":"live_output_written","source_size_bytes":1000}""");
        await RecordAsync("""{"outcome":"live_output_written","output_size_bytes":10}""");
        await RecordAsync("""{"outcome":"live_output_written","source_size_bytes":"many","output_size_bytes":true}""");

        var stats = await BuildAsync();

        Assert.Equal(3, stats.OutputWrittenCount);
        Assert.Equal(0, stats.NetSpaceSavedBytes);
        Assert.Equal(0, stats.NetSpaceSavedPercent);
    }

    [Fact]
    public async Task A_file_that_needed_no_change_counts_as_already_optimised_only_when_it_was_copied()
    {
        await RecordAsync("""{"outcome":"live_skipped_not_required","output_copied_without_remux":true}""");
        await RecordAsync("""{"outcome":"live_skipped_not_required","output_copied_without_remux":false}""");
        await RecordAsync("""{"outcome":"live_skipped_not_required"}""");

        var stats = await BuildAsync();

        Assert.Equal(1, stats.AlreadyOptimizedCount);
        Assert.Equal(1, stats.FilesProcessed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2]")]
    [InlineData("""{"outcome":7}""")]
    [InlineData("""{"outcome":"something_else"}""")]
    [InlineData("{}")]
    public async Task Details_that_say_nothing_Weir_can_count_are_left_out(string? detail)
    {
        await RecordAsync(detail);

        var stats = await BuildAsync();

        Assert.Equal(0, stats.FilesProcessed);
        Assert.Equal(0, stats.OutputWrittenCount);
        Assert.Equal(0, stats.AlreadyOptimizedCount);
    }

    [Fact]
    public async Task Passes_older_than_the_window_are_left_out()
    {
        await RecordAsync("""{"outcome":"live_output_written"}""", age: TimeSpan.FromDays(40));
        await RecordAsync("""{"outcome":"live_output_written"}""", age: TimeSpan.FromDays(2));

        Assert.Equal(1, (await BuildAsync(30)).OutputWrittenCount);
        Assert.Equal(0, (await BuildAsync(1)).OutputWrittenCount);
    }
}
