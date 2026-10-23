using Weir.Core.Activity;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Activity;

/// <summary>The history filters and probes on a database with nothing in it, or with one file whose library is not known.</summary>
public sealed class ActivityHistoryEdgeTests
{
    private readonly ActivityHistoryStore _history = new();

    [Fact]
    public async Task The_system_view_leaves_out_the_modules_that_have_their_own_view()
    {
        using var fixture = new StoreFixture();
        await fixture.Execute(
            "INSERT INTO activity_events (created_at, event_type, module, title) VALUES " +
            "('2026-01-01 00:00:01', 'a.1', 'processing', 'a pass'), ('2026-01-01 00:00:02', 'a.2', 'auth', 'a sign-in'), ('2026-01-01 00:00:03', 'a.3', 'system', 'a start')");

        var page = await fixture.WithUnitOfWork(uow => _history.ListRecentAsync(uow, new ActivityFilter(Module: " System "), limit: 10, beforeId: null));
        var count = await fixture.WithUnitOfWork(uow => _history.CountAsync(uow, new ActivityFilter(Module: "system")));

        Assert.Equal(["a start", "a sign-in"], page.Items.Select(row => row.Title));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task An_empty_history_has_no_oldest_event_and_no_latest_id()
    {
        using var fixture = new StoreFixture();

        Assert.Null(await fixture.WithUnitOfWork(uow => _history.OldestCreatedAtAsync(uow)));
        Assert.Null(await _history.LatestIdAsync(fixture.Database));
    }

    [Fact]
    public async Task Updating_only_the_title_keeps_the_type_and_the_detail()
    {
        using var fixture = new StoreFixture();
        var writer = new SqliteActivityWriter(fixture.Database);
        var id = await writer.RecordAsync(new ActivityEventDraft(ActivityEventTypes.ProcessingFileProcessingProgress, "processing", "Processing movie.mkv", "{\"percent\":10}"));

        await fixture.WithUnitOfWork(uow => SqliteActivityWriter.UpdateAsync(uow, id, title: "Processed movie.mkv"));

        Assert.Equal(1, await fixture.Scalar(
            $"SELECT count(*) FROM activity_events WHERE id = {id} AND title = 'Processed movie.mkv' AND detail = '{{\"percent\":10}}' AND event_type = '{ActivityEventTypes.ProcessingFileProcessingProgress}'"));
    }
}
