using Weir.Core.Updates;

namespace Weir.Core.Tests.Updates;

public sealed class ReleaseFeedTests
{
    private static string Entry(string tag, string updated = "2026-10-05T08:30:00Z", string title = "Weir") =>
        $"<entry><id>tag:github.com,2008:Repository/1/{tag}</id><updated>{updated}</updated>"
        + $"<link rel=\"alternate\" type=\"text/html\" href=\"{ProjectLocation.RepositoryUrl}/releases/tag/{tag}\"/><title>{title}</title></entry>";

    private static string Feed(params string[] entries) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><feed xmlns=\"http://www.w3.org/2005/Atom\"><title>Releases</title>{string.Concat(entries)}</feed>";

    [Fact]
    public void A_release_is_read_from_its_tag_link_title_and_updated_time()
    {
        var release = Assert.Single(ReleaseFeed.Parse(Feed(Entry("v1.0.0-rc.10", "2026-10-08T21:04:05Z", "Weir 1.0.0-rc.10"))));

        Assert.Equal("v1.0.0-rc.10", release.TagName);
        Assert.Equal("1.0.0-rc.10", release.Version);
        Assert.Equal("Weir 1.0.0-rc.10", release.ReleaseName);
        Assert.Equal($"{ProjectLocation.RepositoryUrl}/releases/tag/v1.0.0-rc.10", release.HtmlUrl);
        Assert.Equal("2026-10-08T21:04:05Z", release.PublishedAt?.ToWireText());
        Assert.True(release.Prerelease);
        Assert.False(release.Draft);
    }

    [Fact]
    public void A_release_from_the_feed_offers_the_installer_at_its_download_address()
    {
        var release = Assert.Single(ReleaseFeed.Parse(Feed(Entry("v3.4.0"))));

        var installer = Assert.Single(release.Assets);
        Assert.Same(installer, release.WindowsInstallerAsset());
        Assert.Equal("Weir-win-Setup.exe", installer.Name);
        Assert.Equal($"{ProjectLocation.RepositoryUrl}/releases/download/v3.4.0/Weir-win-Setup.exe", installer.BrowserDownloadUrl);
        Assert.Null(installer.ApiUrl);
    }

    [Theory]
    [InlineData("v3.4.0", false)]
    [InlineData("v3.4.0-rc.1", true)]
    [InlineData("v1.0.0-beta.2", true)]
    public void A_release_is_a_pre_release_when_its_tag_has_a_pre_release_suffix(string tag, bool preRelease)
    {
        var release = Assert.Single(ReleaseFeed.Parse(Feed(Entry(tag))));

        Assert.Equal(preRelease, release.Prerelease);
    }

    [Fact]
    public void The_release_page_is_built_from_the_repository_and_the_tag_not_taken_from_the_feed()
    {
        const string Elsewhere = "<entry><updated>2026-10-05T08:30:00Z</updated><link rel=\"alternate\" href=\"https://elsewhere.example/releases/tag/v3.4.0\"/><title>Weir</title></entry>";

        var release = Assert.Single(ReleaseFeed.Parse(Feed(Elsewhere)));

        Assert.Equal($"{ProjectLocation.RepositoryUrl}/releases/tag/v3.4.0", release.HtmlUrl);
    }

    [Fact]
    public void A_feed_larger_than_any_release_feed_is_refused()
    {
        var huge = Feed(Entry("v3.4.0", title: new string('x', 2_100_000)));

        Assert.ThrowsAny<System.Xml.XmlException>(() => ReleaseFeed.Parse(huge));
    }

    [Fact]
    public void An_entry_whose_tag_is_not_a_version_is_left_out()
    {
        var releases = ReleaseFeed.Parse(Feed(Entry("untagged-1b2c3d4e"), Entry("nightly"), Entry("v3.4"), Entry("v3.4.0")));

        Assert.Equal(["3.4.0"], releases.Select(release => release.Version));
    }

    [Fact]
    public void An_entry_without_a_release_link_is_left_out()
    {
        const string NoLink = "<entry><id>x</id><updated>2026-10-05T08:30:00Z</updated><title>No link</title></entry>";

        Assert.Empty(ReleaseFeed.Parse(Feed(NoLink)));
    }

    [Fact]
    public void An_unreadable_updated_time_leaves_the_release_without_a_published_time()
    {
        var release = Assert.Single(ReleaseFeed.Parse(Feed(Entry("v3.4.0", "yesterday"))));

        Assert.Null(release.PublishedAt);
    }

    [Fact]
    public void A_stable_install_is_offered_only_stable_releases_and_a_release_candidate_the_newest_of_either()
    {
        var releases = ReleaseFeed.Parse(Feed(Entry("v3.3.0-rc.1"), Entry("v3.2.17"), Entry("untagged-abc")));

        Assert.Equal("3.2.17", ReleaseSelection.NewestFor(releases, "3.2.16")?.Version);
        Assert.Equal("3.3.0-rc.1", ReleaseSelection.NewestFor(releases, "3.2.17-rc.1")?.Version);
    }

    [Fact]
    public void Text_that_is_not_xml_is_refused()
    {
        Assert.ThrowsAny<System.Xml.XmlException>(() => ReleaseFeed.Parse("not xml"));
    }

    [Fact]
    public void A_feed_that_declares_a_doctype_is_refused()
    {
        Assert.ThrowsAny<System.Xml.XmlException>(() => ReleaseFeed.Parse("<!DOCTYPE feed [<!ENTITY x \"y\">]><feed xmlns=\"http://www.w3.org/2005/Atom\"/>"));
    }
}
