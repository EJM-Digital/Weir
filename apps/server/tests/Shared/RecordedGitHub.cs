using System.Net;

namespace Weir.Tests.Shared;

/// <summary>
/// GitHub, answering as it did when it was recorded (<c>fixtures/recorded/github-releases</c>): the public release feed with the
/// feed's bytes, and the releases API with the API's. What Weir asks for is kept, so a test can say which of the two it used.
/// </summary>
internal sealed class RecordedGitHub : HttpMessageHandler
{
    private static readonly string Folder = FindFolder();

    private readonly byte[] _feed = File.ReadAllBytes(Path.Join(Folder, "releases.atom"));
    private readonly byte[] _api = File.ReadAllBytes(Path.Join(Folder, "releases-api.json"));
    private readonly List<string> _asked = [];

    /// <summary>The release feed answers 500 (GitHub is slow to answer a feed it has not got ready), and the API is asked instead.</summary>
    public bool FeedIsDown { get; init; }

    /// <summary>The API refuses, as GitHub does when its allowance is spent.</summary>
    public bool ApiIsRefusing { get; init; }

    public IReadOnlyList<string> Asked
    {
        get
        {
            lock (_asked)
            {
                return [.. _asked];
            }
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        lock (_asked)
        {
            _asked.Add(url);
        }

        var reply = request.RequestUri.Host switch
        {
            "github.com" when !FeedIsDown && request.RequestUri.AbsolutePath.EndsWith("/releases.atom", StringComparison.Ordinal) => Ok(_feed, "application/atom+xml; charset=utf-8"),
            "api.github.com" when !ApiIsRefusing && request.RequestUri.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal) => Ok(_api, "application/json; charset=utf-8"),
            _ => new HttpResponseMessage(request.RequestUri.Host == "api.github.com" && ApiIsRefusing ? HttpStatusCode.Forbidden : HttpStatusCode.InternalServerError),
        };
        return Task.FromResult(reply);
    }

    private static HttpResponseMessage Ok(byte[] body, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        return response;
    }

    private static string FindFolder()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Join(folder.FullName, "apps", "server", "tests", "fixtures", "recorded", "github-releases");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The recordings of GitHub (apps/server/tests/fixtures/recorded/github-releases) were not found above " + AppContext.BaseDirectory);
    }
}
