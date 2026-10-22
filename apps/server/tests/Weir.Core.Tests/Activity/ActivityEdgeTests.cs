using Weir.Core.Activity;
using Weir.Core.Json;

namespace Weir.Core.Tests.Activity;

/// <summary>Activity facts from details that are not what the worker writes, and the recent-events body when the history is empty.</summary>
public sealed class ActivityEdgeTests
{
    [Fact]
    public void An_event_with_no_type_has_no_facts_of_its_own()
    {
        Assert.Equal(new ActivityFacts(null, null, null, null, null), ActivityClassifier.Classify(null!, null));
    }

    [Theory]
    [InlineData("99999999999999999999")]
    [InlineData("-99999999999999999999")]
    public void A_library_number_beyond_what_the_database_holds_is_left_out(string number)
    {
        var facts = ActivityClassifier.Classify("processing.file_processed", $$"""{"library_id": {{number}}}""");

        Assert.Null(facts.LibraryId);
    }

    [Fact]
    public void A_detail_that_starts_like_an_object_but_is_not_valid_json_gives_no_facts()
    {
        var facts = ActivityClassifier.Classify("something", "{not json");

        Assert.Equal(new ActivityFacts(null, null, null, null, null), facts);
    }

    [Fact]
    public void A_history_with_no_events_has_no_oldest_event_and_no_total()
    {
        var body = ActivityHistory.RecentOut([], hasMore: false, total: null, retentionDays: 30, oldestEventAt: null, new Dictionary<(long LibraryId, string Path), string>());

        Assert.IsType<WireNull>(body.Get("oldest_event_at"));
        Assert.Null(body.Get("total"));
    }
}
