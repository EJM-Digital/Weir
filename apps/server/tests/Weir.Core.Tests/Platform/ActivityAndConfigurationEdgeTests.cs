using Weir.Core.Activity;
using Weir.Core.Artwork;
using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Core.Notifications;
using Weir.Core.Time;

namespace Weir.Core.Tests.Platform;

/// <summary>Activity exports, poster addresses, release feeds and paths at their edges.</summary>
public sealed class ActivityAndConfigurationEdgeTests
{
    private static readonly Timestamp At = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));

    private static ActivityEventRow Row(string? trigger, string? result, long? library, string? path, string? detail) =>
        new(7, At, "processing.file_processed", "processing", "Film.mkv", detail, trigger, result, library, path, null);

    [Fact]
    public void A_history_that_reaches_back_shows_its_oldest_event()
    {
        var body = ActivityHistory.RecentOut([Row(null, null, null, null, null)], hasMore: true, total: 1, retentionDays: 30, At, new Dictionary<(long LibraryId, string Path), string>());

        Assert.Equal(At.ToWireText(), ((WireString)body.Get("oldest_event_at")!).Value);
    }

    [Fact]
    public void An_export_leaves_a_column_empty_when_the_event_has_nothing_for_it()
    {
        var csv = ActivityHistory.ExportCsv([Row(null, null, null, null, null), Row("schedule", "ok", 3, "Film.mkv", "done")]);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith(",Film.mkv,", lines[1], StringComparison.Ordinal);
        Assert.Contains(",schedule,ok,3,Film.mkv,Film.mkv,done", lines[2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://gateway.test/abcd1234.jpg")]
    [InlineData("http://gateway.test/abcd1234.jpg")]
    public void A_poster_on_the_gateway_over_either_web_scheme_is_taken(string address)
    {
        Assert.NotNull(ArtworkPosterSource.RefFromAnswerUrl(address, "gateway.test"));
    }

    [Theory]
    [InlineData("http://feed.example/releases.atom")]
    [InlineData("https://feed.example/releases.atom")]
    public void A_release_feed_over_either_web_scheme_is_accepted(string address)
    {
        Assert.Equal(address, WeirOptionsLoader.ParseReleasesFeedUrl(address));
    }

    [Theory]
    [InlineData("http://")]
    [InlineData("https://")]
    public void A_release_feed_with_no_host_refuses_the_start(string address)
    {
        Assert.Throws<WeirConfigurationException>(() => WeirOptionsLoader.ParseReleasesFeedUrl(address));
    }

    [Theory]
    [InlineData("//", false, "//")]
    [InlineData("///a", false, "/a")]
    [InlineData("//a", false, "//a")]
    [InlineData(@"\\", true, @"\\")]
    [InlineData(@"\\\a", true, @"\a")]
    [InlineData(@"//a/b", true, @"\\a\b")]
    public void A_path_with_two_leading_separators_keeps_them_only_when_a_name_follows(string path, bool windows, string expected)
    {
        Assert.Equal(expected, ValueParsing.NormalizeLexically(path, TestRuntime.Bare(windows)));
    }

    [Fact]
    public void An_address_with_no_fragment_or_query_is_compared_as_it_is()
    {
        Assert.Equal("https://example.com/hook", NotificationRules.NormalizeAddress("https://Example.com/hook/"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative/path.jpg")]
    [InlineData("https://other.test/abcd1234.jpg")]
    public void A_poster_address_that_is_not_on_the_gateway_is_judged_as_a_hand_off_address(string? address)
    {
        Assert.Null(ArtworkPosterSource.RefFromAnswerUrl(address, "gateway.test"));
    }

    [Theory]
    [InlineData("http:///releases.atom")]
    [InlineData("https:///releases.atom")]
    public void A_release_feed_address_with_no_host_refuses_the_start(string address)
    {
        Assert.Throws<WeirConfigurationException>(() => WeirOptionsLoader.ParseReleasesFeedUrl(address));
    }
}
