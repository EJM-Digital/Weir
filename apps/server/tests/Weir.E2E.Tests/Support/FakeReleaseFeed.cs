using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>
/// GitHub's public release feed, played from the recording of it (<c>fixtures/recorded/github-releases</c>) by a local server: a test server is pointed here
/// with <c>WEIR_RELEASES_FEED_URL</c>. <see cref="FakeHttpServer.Requests"/> says what Weir asked for.
/// </summary>
public sealed class FakeReleaseFeed : FakeHttpServer
{
    /// <summary>The newest release in the recording.</summary>
    public const string NewestVersion = "1.0.0-rc.18";

    private const string FeedPath = "/releases.atom";

    private readonly byte[] _recording = File.ReadAllBytes(Path.Combine(RepoPaths.Fixtures, "recorded", "github-releases", "releases.atom"));

    /// <summary>The address to give the server under test.</summary>
    public string Url => BaseUrl + FeedPath;

    /// <summary>How many times Weir has read the feed.</summary>
    public int Reads => Requests.Count(request => request.Path == FeedPath);

    protected override HttpAnswer Answer(RecordedRequest request) =>
        request.Method == "GET" && request.Path == FeedPath
            ? new HttpAnswer(200, _recording, "application/atom+xml; charset=utf-8")
            : new HttpAnswer(404, []);
}
