using System.Net;
using System.Xml;
using System.Xml.Linq;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Weir.Tray;

/// <summary>
/// Where the tray finds Weir's updates, without spending GitHub's API allowance: 60 requests an hour for a whole network,
/// which Velopack's <see cref="GithubSource"/> spends on every check and which every Weir, tray and other GitHub client on
/// that network shares. The newest release comes from the public release feed (<c>releases.atom</c>), and its Velopack feed
/// and packages are read from the release's download folder; neither counts against the allowance. Only when the release
/// feed cannot be read is <see cref="GithubSource"/> asked.
/// </summary>
sealed class ReleaseFeedSource : IUpdateSource
{
    private const string TagLinkMarker = "/releases/tag/";
    private const long MostCharacters = 2_000_000;
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    private readonly string _repoUrl;
    private readonly bool _includePreReleases;
    private readonly IFileDownloader _downloader;
    private readonly GithubSource _api;
    private volatile IUpdateSource _serving;

    /// <param name="repoUrl">The repository's address, <see cref="ProjectLocation.RepositoryUrl"/>.</param>
    /// <param name="includePreReleases">Whether pre-releases are offered; <see cref="UpdateChannel.IncludesPreReleases"/> says for an install.</param>
    /// <param name="downloader">Carries every request; the default is Velopack's own.</param>
    internal ReleaseFeedSource(string repoUrl, bool includePreReleases, IFileDownloader? downloader = null)
    {
        _repoUrl = repoUrl.TrimEnd('/');
        _includePreReleases = includePreReleases;
        _downloader = downloader ?? new HttpClientFileDownloader();
        _api = new GithubSource(_repoUrl, accessToken: null, prerelease: includePreReleases, _downloader);
        _serving = _api;
    }

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        IReadOnlyList<string> tags;
        try
        {
            tags = TagsNewestFirst(await _downloader.DownloadString($"{_repoUrl}/releases.atom").ConfigureAwait(false), _includePreReleases);
        }
        catch (Exception exception) when (exception is HttpRequestException or XmlException or OperationCanceledException)
        {
            logger.Warn($"The release feed could not be read ({exception.Message}), so GitHub's API is asked for the release list.");
            _serving = _api;
            return await _api.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease).ConfigureAwait(false);
        }

        foreach (var tag in tags)
        {
            var source = new SimpleWebSource($"{_repoUrl}/releases/download/{Uri.EscapeDataString(tag)}/", _downloader);
            try
            {
                var feed = await source.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease).ConfigureAwait(false);
                _serving = source;
                return feed;
            }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                // The release feed lists a tag before its Windows files are attached, as the Windows and Docker jobs publish separately.
                logger.Info($"{tag} has no Windows update files yet, so the next older release is tried.");
            }
        }

        return new VelopackAssetFeed();
    }

    public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default) =>
        _serving.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);

    /// <summary>
    /// The tags of the releases in the release feed, newest first by SemVer precedence, counting pre-releases only when asked.
    /// The feed has no pre-release flag, so a release is a pre-release when its tag has a semver pre-release suffix
    /// (<c>-rc.1</c>). An entry whose tag is not a version, such as the <c>untagged-…</c> address of a draft, is left out.
    /// Throws <see cref="XmlException"/> when the text is not XML or is larger than a release feed can be.
    /// </summary>
    internal static IReadOnlyList<string> TagsNewestFirst(string feed, bool includePreReleases)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MostCharacters };
        using var reader = XmlReader.Create(new StringReader(feed), settings);
        var found = new List<(string Tag, SemanticVersion Version)>();
        foreach (var entry in XDocument.Load(reader).Descendants(Atom + "entry"))
        {
            var link = entry.Elements(Atom + "link").Select(element => (string?)element.Attribute("href")).FirstOrDefault(href => href?.Contains(TagLinkMarker, StringComparison.Ordinal) == true);
            if (link is null)
            {
                continue;
            }

            var tag = Uri.UnescapeDataString(link[(link.IndexOf(TagLinkMarker, StringComparison.Ordinal) + TagLinkMarker.Length)..]);
            if (!SemanticVersion.TryParse(tag.TrimStart('v'), out var version) || (version.IsPrerelease && !includePreReleases))
            {
                continue;
            }

            found.Add((tag, version));
        }

        return [.. found.OrderByDescending(release => release.Version).Select(release => release.Tag)];
    }
}
