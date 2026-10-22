using System.Xml;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// Where the tray looks for updates: the public release feed and the release's download folder, and GitHub's API only when
/// the release feed cannot be read.
/// </summary>
public sealed class ReleaseFeedSourceTests
{
    private const string Repo = ProjectLocation.RepositoryUrl;
    private const string Atom = Repo + "/releases.atom";
    private const string ApiReleases = ProjectLocation.ApiRepositoryUrl + "/releases";

    private static string Feed(params string[] tags) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><feed xmlns=\"http://www.w3.org/2005/Atom\">"
        + string.Concat(tags.Select(tag =>
            $"<entry><id>tag:github.com,2008:Repository/1/{tag}</id><updated>2026-10-05T08:30:00Z</updated>"
            + $"<link rel=\"alternate\" type=\"text/html\" href=\"{Repo}/releases/tag/{tag}\"/><title>{tag}</title></entry>"))
        + "</feed>";

    private static string VelopackFeed(string version) =>
        $$"""{"Assets":[{"PackageId":"Weir","Version":"{{version}}","Type":"Full","FileName":"Weir-{{version}}-full.nupkg","SHA1":"C33E643DDB836DBC23C195F2F91F5E23D4250B05","SHA256":"C7C368EBE5BC6DC6CB2B3AE045E97DA38C4034D2A9074FA3D3FAD22F288C9A59","Size":179244524}]}""";

    [Fact]
    public void Tags_are_ordered_by_version_precedence_not_by_their_place_in_the_feed_and_a_stable_install_gets_no_pre_release()
    {
        var feed = Feed("v1.0.0-rc.2", "v1.0.0-rc.10", "v0.9.4", "untagged-1b2c3d4e", "nightly", "v0.9.5");

        Assert.Equal(["v1.0.0-rc.10", "v1.0.0-rc.2", "v0.9.5", "v0.9.4"], ReleaseFeedSource.TagsNewestFirst(feed, includePreReleases: true));
        Assert.Equal(["v0.9.5", "v0.9.4"], ReleaseFeedSource.TagsNewestFirst(feed, includePreReleases: false));
    }

    [Fact]
    public void A_stable_release_is_newer_than_its_own_release_candidates()
    {
        Assert.Equal(["v1.0.0", "v1.0.0-rc.3", "v0.9.0"], ReleaseFeedSource.TagsNewestFirst(Feed("v1.0.0-rc.3", "v1.0.0", "v0.9.0"), includePreReleases: true));
    }

    [Fact]
    public void No_qualifying_tag_is_none()
    {
        Assert.Empty(ReleaseFeedSource.TagsNewestFirst(Feed("v1.0.0-rc.1", "untagged-abc"), includePreReleases: false));
        Assert.Empty(ReleaseFeedSource.TagsNewestFirst(Feed(), includePreReleases: true));
    }

    [Fact]
    public void Text_that_is_not_a_feed_is_refused()
    {
        Assert.ThrowsAny<XmlException>(() => ReleaseFeedSource.TagsNewestFirst("<html>not a feed", includePreReleases: true));
    }

    [Fact]
    public void A_pre_release_install_is_offered_pre_releases_and_a_stable_install_is_not()
    {
        var feed = Feed("v1.0.0-rc.2", "v0.9.5");

        Assert.Equal("v1.0.0-rc.2", NewestFor("1.0.0-rc.1", feed));
        Assert.Equal("v0.9.5", NewestFor("0.9.4", feed));

        static string NewestFor(string running, string feed) =>
            ReleaseFeedSource.TagsNewestFirst(feed, UpdateChannel.IncludesPreReleases(running))[0];
    }

    [Fact]
    public async Task A_newest_release_whose_windows_files_are_not_attached_yet_gives_way_to_the_next_older_one()
    {
        var web = new FakeWeb
        {
            [Atom] = Feed("v1.0.0-rc.10", "v1.0.0-rc.9", "v1.0.0-rc.8"),
            [Repo + "/releases/download/v1.0.0-rc.9/releases.win.json"] = VelopackFeed("1.0.0-rc.9"),
        };
        var source = UpdateChannel.SourceFor(Repo, "1.0.0-rc.8", web);

        var feed = await source.GetReleaseFeed(NullVelopackLogger.Instance, "Weir", "win");
        await source.DownloadReleaseEntry(NullVelopackLogger.Instance, feed.Assets[0], "package.nupkg", _ => { });

        Assert.Equal("1.0.0-rc.9", Assert.Single(feed.Assets).Version.ToString());
        Assert.Equal(Repo + "/releases/download/v1.0.0-rc.9/Weir-1.0.0-rc.9-full.nupkg", Assert.Single(web.Downloaded));
        Assert.DoesNotContain(web.Asked, url => url.StartsWith("https://api.github.com/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_no_release_has_its_windows_files_the_feed_is_empty_and_the_api_is_not_asked()
    {
        var web = new FakeWeb { [Atom] = Feed("v1.0.0-rc.10", "v1.0.0-rc.9") };

        var feed = await UpdateChannel.SourceFor(Repo, "1.0.0-rc.8", web).GetReleaseFeed(NullVelopackLogger.Instance, "Weir", "win");

        Assert.Empty(feed.Assets);
        Assert.DoesNotContain(web.Asked, url => url.StartsWith("https://api.github.com/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_newest_release_is_read_from_its_download_folder_and_the_api_is_never_asked()
    {
        var web = new FakeWeb
        {
            [Atom] = Feed("v1.0.0-rc.10", "v1.0.0-rc.9"),
            [Repo + "/releases/download/v1.0.0-rc.10/releases.win.json"] = VelopackFeed("1.0.0-rc.10"),
        };
        var source = UpdateChannel.SourceFor(Repo, "1.0.0-rc.9", web);

        var feed = await source.GetReleaseFeed(NullVelopackLogger.Instance, "Weir", "win");
        await source.DownloadReleaseEntry(NullVelopackLogger.Instance, feed.Assets[0], "package.nupkg", _ => { });

        Assert.Equal("Weir-1.0.0-rc.10-full.nupkg", Assert.Single(feed.Assets).FileName);
        Assert.Equal(Repo + "/releases/download/v1.0.0-rc.10/Weir-1.0.0-rc.10-full.nupkg", Assert.Single(web.Downloaded));
        Assert.DoesNotContain(web.Asked, url => url.StartsWith("https://api.github.com/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stable_install_reads_the_newest_stable_release_not_a_newer_candidate()
    {
        var web = new FakeWeb
        {
            [Atom] = Feed("v1.1.0-rc.1", "v1.0.0"),
            [Repo + "/releases/download/v1.0.0/releases.win.json"] = VelopackFeed("1.0.0"),
        };

        var feed = await UpdateChannel.SourceFor(Repo, "0.9.0", web).GetReleaseFeed(NullVelopackLogger.Instance, "Weir", "win");

        Assert.Equal("1.0.0", Assert.Single(feed.Assets).Version.ToString());
    }

    [Fact]
    public async Task A_feed_with_no_release_for_the_channel_is_an_empty_feed_and_the_api_is_not_asked()
    {
        var web = new FakeWeb { [Atom] = Feed("v1.0.0-rc.1") };

        var feed = await UpdateChannel.SourceFor(Repo, "0.9.0", web).GetReleaseFeed(NullVelopackLogger.Instance, "Weir", "win");

        Assert.Empty(feed.Assets);
        Assert.Equal([Atom], web.Asked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_release_feed_that_cannot_be_read_leaves_the_api_to_answer(bool feedIsNotXml)
    {
        const string AssetUrl = Repo + "/releases/download/v0.9.5/releases.win.json";
        var web = new FakeWeb
        {
            [ApiReleases] = $$"""[{"name":"v0.9.5","prerelease":false,"published_at":"2026-10-01T10:00:00Z","assets":[{"url":"{{ApiReleases}}/assets/7","name":"releases.win.json","browser_download_url":"{{AssetUrl}}","content_type":"application/json"}]}]""",
            [AssetUrl] = VelopackFeed("0.9.5"),
        };
        if (feedIsNotXml)
        {
            web[Atom] = "<html>not a feed";
        }

        var feed = await UpdateChannel.SourceFor(Repo, "0.9.0", web).GetReleaseFeed(NullVelopackLogger.Instance, "Weir", "win");

        Assert.Equal("0.9.5", Assert.Single(feed.Assets).Version.ToString());
        Assert.Equal(Atom, web.Asked[0]);
        Assert.Contains(web.Asked, url => url.StartsWith(ApiReleases, StringComparison.Ordinal));
    }

    [Fact]
    public void An_entry_whose_link_is_not_a_release_tag_is_left_out()
    {
        var feed = "<feed xmlns=\"http://www.w3.org/2005/Atom\"><entry><link rel=\"alternate\" href=\"" + Repo + "/discussions/4\"/></entry><entry><title>no link</title></entry></feed>";

        Assert.Empty(ReleaseFeedSource.TagsNewestFirst(feed, includePreReleases: true));
    }

    /// <summary>A GitHub that answers each address from the test (a missing one is a 404) and records what was asked.</summary>
    private sealed class FakeWeb : IFileDownloader
    {
        private readonly Dictionary<string, string> _pages = [];

        public List<string> Asked { get; } = [];

        public List<string> Downloaded { get; } = [];

        public string this[string url]
        {
            set => _pages[url] = value;
        }

        public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
        {
            Downloaded.Add(url);
            return Task.CompletedTask;
        }

        public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            System.Text.Encoding.UTF8.GetBytes(await DownloadString(url, headers, timeout));

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Asked.Add(url);
            var page = _pages.FirstOrDefault(known => url == known.Key || url.StartsWith(known.Key + "?", StringComparison.Ordinal));
            return page.Key is null
                ? throw new HttpRequestException($"Response status code does not indicate success: 404 (Not Found) for {url}.", null, System.Net.HttpStatusCode.NotFound)
                : Task.FromResult(page.Value);
        }
    }
}
