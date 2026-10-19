using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weir.Core.Updates;
using Weir.Infrastructure.Http;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.Http;

/// <summary>
/// The release lookup against GitHub as it answered when it was recorded: the public release feed first, and the releases API when
/// the feed cannot be read. The recordings (<c>fixtures/recorded/github-releases</c>) are GitHub's own bytes, so a change in the
/// shape of either answer shows here the next time they are recorded again.
/// </summary>
public sealed class GitHubReleaseRecordingTests
{
    private const string Newest = "1.0.0-rc.18";
    private const string Older = "1.0.0-rc.17";

    private static GitHubReleaseCatalogClient ClientOn(RecordedGitHub github) =>
        new(new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero)), NullLogger<GitHubReleaseCatalogClient>.Instance, github);

    [Fact]
    public async Task The_recorded_feed_names_the_newest_release_to_a_copy_that_is_behind()
    {
        using var github = new RecordedGitHub();

        var latest = await ClientOn(github).FetchLatestAsync(Older, CancellationToken.None);

        Assert.Equal(Newest, latest?.Version);
        Assert.Equal([ReleaseCatalog.ReleasesFeedUrl], github.Asked);
    }

    [Fact]
    public async Task The_recorded_feed_shows_a_copy_that_is_the_newest_that_it_is()
    {
        using var github = new RecordedGitHub();

        var latest = await ClientOn(github).FetchLatestAsync(Newest, CancellationToken.None);

        Assert.Equal(Newest, latest?.Version);
    }

    [Fact]
    public async Task The_recorded_releases_API_answers_when_the_feed_cannot_be_read_and_carries_the_Windows_installer()
    {
        using var github = new RecordedGitHub { FeedIsDown = true };

        var latest = await ClientOn(github).FetchLatestAsync(Older, CancellationToken.None);

        Assert.Equal(Newest, latest?.Version);
        Assert.Equal([ReleaseCatalog.ReleasesFeedUrl, ReleaseCatalog.ReleasesUrl], github.Asked);
        Assert.Contains(latest!.Assets, asset => asset.Name == "Weir-win-Setup.exe");
    }

    [Fact]
    public async Task The_feed_and_the_API_name_the_same_newest_release()
    {
        using var viaFeed = new RecordedGitHub();
        using var viaApi = new RecordedGitHub { FeedIsDown = true };

        var fromFeed = await ClientOn(viaFeed).FetchLatestAsync(Older, CancellationToken.None);
        var fromApi = await ClientOn(viaApi).FetchLatestAsync(Older, CancellationToken.None);

        Assert.Equal(fromFeed?.Version, fromApi?.Version);
        Assert.Equal(fromFeed?.HtmlUrl, fromApi?.HtmlUrl);
    }

    [Fact]
    public async Task A_refusal_from_the_API_after_a_feed_that_cannot_be_read_is_a_failed_lookup()
    {
        using var github = new RecordedGitHub { FeedIsDown = true, ApiIsRefusing = true };

        var failure = await Assert.ThrowsAsync<ReleaseFetchException>(() => ClientOn(github).FetchLatestAsync(Older, CancellationToken.None));

        Assert.Equal(403, failure.StatusCode);
    }
}
