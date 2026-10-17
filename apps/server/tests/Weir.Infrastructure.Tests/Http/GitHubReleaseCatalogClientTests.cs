using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weir.Core.Updates;
using Weir.Infrastructure.Http;

namespace Weir.Infrastructure.Tests.Http;

/// <summary>
/// Looking up the newest release from the public release feed without touching GitHub's API allowance, and carrying on from
/// the API when the feed cannot answer.
/// </summary>
public sealed class GitHubReleaseCatalogClientTests : IDisposable
{
    private const string Running = "3.2.16";
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeGitHub _github = new();
    private readonly GitHubReleaseCatalogClient _client;

    public GitHubReleaseCatalogClientTests()
    {
        _client = new GitHubReleaseCatalogClient(_clock, NullLogger<GitHubReleaseCatalogClient>.Instance, _github);
    }

    public void Dispose() => _github.Dispose();

    private static string ReleaseList(string version, bool preRelease = false) =>
        $$"""
        [{"tag_name":"v{{version}}","name":"Weir {{version}}","html_url":"{{ProjectLocation.RepositoryUrl}}/releases/tag/v{{version}}",
          "published_at":"2026-10-01T10:00:00Z","draft":false,"prerelease":{{(preRelease ? "true" : "false")}},
          "assets":[{"name":"Weir-win-Setup.exe","url":"{{ProjectLocation.ApiRepositoryUrl}}/releases/assets/1",
                     "browser_download_url":"{{ProjectLocation.RepositoryUrl}}/releases/download/v{{version}}/Weir-win-Setup.exe",
                     "size":1000,"content_type":"application/octet-stream"}]}]
        """;

    private static string Feed(params string[] tags) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><feed xmlns=\"http://www.w3.org/2005/Atom\">"
        + string.Concat(tags.Select(tag =>
            $"<entry><id>tag:github.com,2008:Repository/1/{tag}</id><updated>2026-10-05T08:30:00Z</updated>"
            + $"<link rel=\"alternate\" type=\"text/html\" href=\"{ProjectLocation.RepositoryUrl}/releases/tag/{tag}\"/><title>Weir {tag}</title></entry>"))
        + "</feed>";

    private static HttpResponseMessage Ok(string body, string mediaType) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    private static HttpResponseMessage Limited(HttpStatusCode status, DateTimeOffset resetsAt, DateTimeOffset? githubNow = null)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Date = githubNow;
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", resetsAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    private void FeedAnswers(params string[] tags) => _github.PublicFeed = _ => Ok(Feed(tags), "application/atom+xml");

    private void FeedFails() => _github.PublicFeed = _ => Status(HttpStatusCode.InternalServerError);

    private void ApiAnswers(string version, bool preRelease = false) => _github.Api = _ => Ok(ReleaseList(version, preRelease), "application/json");

    [Fact]
    public async Task The_newest_release_comes_from_the_feed_with_its_installer_download_and_the_api_is_never_asked()
    {
        FeedAnswers("v3.4.0", "v3.3.0", "untagged-1b2c3d", "nightly");

        var latest = await _client.FetchLatestAsync(Running, CancellationToken.None);

        Assert.Equal("3.4.0", latest?.Version);
        Assert.Equal($"{ProjectLocation.RepositoryUrl}/releases/tag/v3.4.0", latest?.HtmlUrl);
        Assert.Equal($"{ProjectLocation.RepositoryUrl}/releases/download/v3.4.0/Weir-win-Setup.exe", latest?.WindowsInstallerAsset()?.BrowserDownloadUrl);
        Assert.Equal(0, _github.ApiCalls);
    }

    [Fact]
    public async Task A_stable_install_is_offered_the_newest_stable_release_and_a_release_candidate_the_newest_of_either()
    {
        FeedAnswers("v3.3.0-rc.2", "v3.2.17", "v3.2.16");

        var stable = await _client.FetchLatestAsync(Running, CancellationToken.None);
        var preRelease = await _client.FetchLatestAsync("3.3.0-rc.1", CancellationToken.None);

        Assert.Equal("3.2.17", stable?.Version);
        Assert.False(stable?.Prerelease);
        Assert.Equal("3.3.0-rc.2", preRelease?.Version);
        Assert.True(preRelease?.Prerelease);
        Assert.Equal(0, _github.ApiCalls);
    }

    [Fact]
    public async Task A_feed_with_no_release_for_the_channel_is_an_answer_of_none_not_a_reason_to_ask_the_api()
    {
        FeedAnswers("v3.3.0-rc.1");

        var latest = await _client.FetchLatestAsync(Running, CancellationToken.None);

        Assert.Null(latest);
        Assert.Equal(0, _github.ApiCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_feed_that_answers_with_an_error_status_leaves_the_api_to_answer(HttpStatusCode refusal)
    {
        _github.PublicFeed = _ => Status(refusal);
        ApiAnswers("3.3.0");

        var latest = await _client.FetchLatestAsync(Running, CancellationToken.None);

        Assert.Equal("3.3.0", latest?.Version);
        Assert.Equal(1, _github.ApiCalls);
    }

    [Fact]
    public async Task A_feed_that_cannot_be_reached_or_read_leaves_the_api_to_answer()
    {
        ApiAnswers("3.3.0");
        _github.PublicFeed = _ => throw new HttpRequestException("offline");
        var unreachable = await _client.FetchLatestAsync(Running, CancellationToken.None);

        _github.PublicFeed = _ => Ok("<html>not a feed", "text/html");
        var unreadable = await _client.FetchLatestAsync(Running, CancellationToken.None);

        Assert.Equal("3.3.0", unreachable?.Version);
        Assert.Equal("3.3.0", unreadable?.Version);
        Assert.Equal(2, _github.ApiCalls);
    }

    [Fact]
    public async Task A_release_the_api_marks_as_a_pre_release_is_one_whatever_its_tag_says()
    {
        FeedFails();
        ApiAnswers("3.3.0", preRelease: true);

        var latest = await _client.FetchLatestAsync(Running, CancellationToken.None);

        Assert.Null(latest);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_limit_leaves_the_api_alone_until_it_lifts_and_the_feed_is_still_asked_each_time(HttpStatusCode refusal)
    {
        var resetsAt = _clock.GetUtcNow().AddMinutes(20);
        FeedFails();
        _github.Api = _ => Limited(refusal, resetsAt);

        var during = await Assert.ThrowsAsync<ReleaseFetchException>(() => _client.FetchLatestAsync(Running, CancellationToken.None));
        _clock.Advance(TimeSpan.FromMinutes(10));
        var later = await Assert.ThrowsAsync<ReleaseFetchException>(() => _client.FetchLatestAsync(Running, CancellationToken.None));

        Assert.Equal((int)refusal, during.StatusCode);
        Assert.Equal(resetsAt, during.RateLimit?.ResetsAt);
        Assert.Equal(during.RateLimit, later.RateLimit);
        Assert.Equal(1, _github.ApiCalls);
        Assert.Equal(2, _github.FeedCalls);

        ApiAnswers("3.5.0");
        _clock.Advance(TimeSpan.FromMinutes(11));
        var after = await _client.FetchLatestAsync(Running, CancellationToken.None);

        Assert.Equal("3.5.0", after?.Version);
        Assert.Equal(2, _github.ApiCalls);
    }

    [Fact]
    public async Task A_feed_that_recovers_during_a_limit_answers_without_the_api()
    {
        FeedFails();
        _github.Api = _ => Limited(HttpStatusCode.Forbidden, _clock.GetUtcNow().AddMinutes(20));
        await Assert.ThrowsAsync<ReleaseFetchException>(() => _client.FetchLatestAsync(Running, CancellationToken.None));

        FeedAnswers("v3.4.0");
        var latest = await _client.FetchLatestAsync(Running, CancellationToken.None);

        Assert.Equal("3.4.0", latest?.Version);
        Assert.Equal(1, _github.ApiCalls);
    }

    [Fact]
    public async Task A_limit_carries_the_newest_release_the_feed_last_gave()
    {
        FeedAnswers("v3.3.0");
        await _client.FetchLatestAsync(Running, CancellationToken.None);
        FeedFails();
        _github.Api = _ => Limited(HttpStatusCode.Forbidden, _clock.GetUtcNow().AddMinutes(2));

        var failure = await Assert.ThrowsAsync<ReleaseFetchException>(() => _client.FetchLatestAsync(Running, CancellationToken.None));

        Assert.Equal(new DateTimeOffset(2026, 1, 15, 10, 2, 0, TimeSpan.Zero), failure.RateLimit?.ResetsAt);
        Assert.Equal("3.3.0", failure.RateLimit?.LastKnown?.Version);
    }

    [Theory]
    [InlineData(-1000, 1)]
    [InlineData(86400, 60)]
    public async Task A_reset_that_makes_no_sense_is_kept_between_a_minute_and_an_hour(long resetAfterSeconds, int expectedMinutes)
    {
        var now = _clock.GetUtcNow();
        FeedFails();
        _github.Api = _ => Limited(HttpStatusCode.Forbidden, now.AddSeconds(resetAfterSeconds));

        var failure = await Assert.ThrowsAsync<ReleaseFetchException>(() => _client.FetchLatestAsync(Running, CancellationToken.None));

        Assert.Equal(now.AddMinutes(expectedMinutes), failure.RateLimit?.ResetsAt);
    }

    [Fact]
    public async Task A_reset_is_a_wait_measured_on_githubs_clock_so_a_fast_local_clock_does_not_shorten_it()
    {
        var now = _clock.GetUtcNow();
        var githubNow = now.AddMinutes(-10);
        FeedFails();
        _github.Api = _ => Limited(HttpStatusCode.Forbidden, githubNow.AddMinutes(20), githubNow);

        var failure = await Assert.ThrowsAsync<ReleaseFetchException>(() => _client.FetchLatestAsync(Running, CancellationToken.None));

        Assert.Equal(now.AddMinutes(20), failure.RateLimit?.ResetsAt);
    }

    [Fact]
    public async Task A_retry_after_date_is_measured_on_githubs_clock_too()
    {
        var now = _clock.GetUtcNow();
        var githubNow = now.AddMinutes(10);
        FeedFails();
        _github.Api = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Date = githubNow;
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(githubNow.AddMinutes(15));
            return response;
        };

        var failure = await Assert.ThrowsAsync<ReleaseFetchException>(() => _client.FetchLatestAsync(Running, CancellationToken.None));

        Assert.Equal(now.AddMinutes(15), failure.RateLimit?.ResetsAt);
    }

    [Fact]
    public async Task A_reset_too_large_to_be_a_time_is_an_error_status_not_a_limit()
    {
        FeedFails();
        _github.Api = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", "99999999999999999");
            return response;
        };

        var failure = await Assert.ThrowsAsync<ReleaseFetchException>(() => _client.FetchLatestAsync(Running, CancellationToken.None));

        Assert.Equal(403, failure.StatusCode);
        Assert.Null(failure.RateLimit);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task An_api_refusal_that_is_not_a_limit_is_an_error_status_and_is_asked_again_next_time(HttpStatusCode refusal)
    {
        FeedFails();
        _github.Api = _ => Status(refusal);

        var failure = await Assert.ThrowsAsync<ReleaseFetchException>(() => _client.FetchLatestAsync(Running, CancellationToken.None));
        await Assert.ThrowsAsync<ReleaseFetchException>(() => _client.FetchLatestAsync(Running, CancellationToken.None));

        Assert.Equal((int)refusal, failure.StatusCode);
        Assert.Null(failure.RateLimit);
        Assert.Equal(2, _github.ApiCalls);
    }

    /// <summary>GitHub's API and public release feed, answering as the test says and counting what was asked.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private int _apiCalls;
        private int _feedCalls;

        public Func<HttpRequestMessage, HttpResponseMessage> Api { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public Func<HttpRequestMessage, HttpResponseMessage> PublicFeed { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public int ApiCalls => Volatile.Read(ref _apiCalls);

        public int FeedCalls => Volatile.Read(ref _feedCalls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "api.github.com")
            {
                Interlocked.Increment(ref _apiCalls);
                return Task.FromResult(Api(request));
            }

            Assert.Equal(ReleaseCatalog.ReleasesFeedUrl, request.RequestUri?.ToString());
            Interlocked.Increment(ref _feedCalls);
            return Task.FromResult(PublicFeed(request));
        }
    }
}
