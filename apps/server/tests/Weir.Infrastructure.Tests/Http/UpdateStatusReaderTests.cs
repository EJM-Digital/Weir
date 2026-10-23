using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.Updates;
using Weir.Infrastructure.Http;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Http;

/// <summary>What the update status says when GitHub limits the network or a check fails, and what the log records.</summary>
public sealed class UpdateStatusReaderTests : IDisposable
{
    private static readonly DateTimeOffset LimitLifts = new(2026, 1, 15, 23, 35, 0, TimeSpan.Zero);

    private readonly StoreFixture _fixture = new(("WEIR_VERSION", "3.2.16"));
    private readonly RecordingLogger<UpdateStatusReader> _log = new();
    private readonly UpdateStatusReader _reader;
    private Exception _failure = new ReleaseFetchException("offline");

    public UpdateStatusReaderTests()
    {
        _reader = new UpdateStatusReader(new FailingCatalog(this), _fixture.Options, _fixture.Database, new SuiteSettingsStore(_fixture.Users), new UpdateFiles(_fixture.Options), _log);
    }

    public void Dispose() => _fixture.Dispose();

    private static string TextOf(WireObject status, string key) => ((WireString)status[key]).Value;

    private async Task UseTimezone(string name)
    {
        await _fixture.WithUnitOfWork(async uow => await new SuiteSettingsStore(_fixture.Users).EnsureAsync(uow));
        await _fixture.Execute($"UPDATE suite_settings SET app_timezone = '{name}'");
    }

    [Fact]
    public async Task A_limit_says_when_the_next_check_is_and_keeps_the_last_known_release()
    {
        var lastKnown = new GitHubReleaseRecord("v3.3.0", "3.3.0", "Weir 3.3.0", null, null, false, false, []);
        _failure = new ReleaseFetchException(403, new ReleaseRateLimit(LimitLifts, lastKnown));

        var status = await _reader.ReadAsync(CancellationToken.None);

        Assert.Equal("rate_limited", TextOf(status, "status"));
        Assert.Equal("GitHub is limiting update checks from your network right now. Weir will check again at 11:35 pm.", TextOf(status, "summary"));
        Assert.Equal("2026-01-15T23:35:00Z", TextOf(status, "retry_at"));
        Assert.Equal("3.3.0", TextOf(status, "latest_version"));
        Assert.Equal(WireBool.True, status["known_update_available"]);
    }

    [Fact]
    public async Task A_limit_with_nothing_newer_known_offers_no_update()
    {
        var lastKnown = new GitHubReleaseRecord("v3.2.16", "3.2.16", null, null, null, false, false, []);
        _failure = new ReleaseFetchException(403, new ReleaseRateLimit(LimitLifts, lastKnown));

        var status = await _reader.ReadAsync(CancellationToken.None);

        Assert.Equal(WireBool.False, status["known_update_available"]);
    }

    [Fact]
    public async Task A_limit_names_the_time_on_the_clock_of_the_chosen_time_zone()
    {
        await UseTimezone("Australia/Sydney");
        _failure = new ReleaseFetchException(429, new ReleaseRateLimit(LimitLifts, null));

        var status = await _reader.ReadAsync(CancellationToken.None);

        Assert.EndsWith("Weir will check again at 10:35 am.", TextOf(status, "summary"), StringComparison.Ordinal);
        Assert.Equal(WireNull.Instance, status["latest_version"]);
    }

    [Fact]
    public async Task A_limit_is_logged_at_information_with_when_it_lifts_and_a_repeat_only_at_debug()
    {
        _failure = new ReleaseFetchException(403, new ReleaseRateLimit(LimitLifts, null));

        await _reader.ReadAsync(CancellationToken.None);
        await _reader.ReadAsync(CancellationToken.None);

        var logged = Assert.Single(_log.At(LogLevel.Information));
        Assert.Contains("HTTP 403", logged, StringComparison.Ordinal);
        Assert.Contains("11:35 pm", logged, StringComparison.Ordinal);
        Assert.Single(_log.At(LogLevel.Debug));
        Assert.Equal(2, _log.Count);
    }

    [Fact]
    public async Task A_failed_check_gives_its_reason_in_plain_words_and_keeps_the_code_for_the_log()
    {
        _failure = new ReleaseFetchException(500);

        var status = await _reader.ReadAsync(CancellationToken.None);

        Assert.Equal("unavailable", TextOf(status, "status"));
        Assert.Equal("Could not check for updates right now. GitHub could not answer the check.", TextOf(status, "summary"));
        var logged = Assert.Single(_log.At(LogLevel.Information));
        Assert.Contains("HTTP 500", logged, StringComparison.Ordinal);
        Assert.Equal(1, _log.Count);
    }

    [Fact]
    public async Task A_refused_check_says_so_without_the_status_code()
    {
        _failure = new ReleaseFetchException(403);

        var status = await _reader.ReadAsync(CancellationToken.None);

        Assert.Equal("Could not check for updates right now. GitHub refused the check.", TextOf(status, "summary"));
    }

    [Fact]
    public async Task The_same_failure_again_is_logged_at_debug_and_a_different_one_at_information()
    {
        _failure = new HttpRequestException("No such host is known.");
        await _reader.ReadAsync(CancellationToken.None);
        await _reader.ReadAsync(CancellationToken.None);

        _failure = new ReleaseFetchException(403);
        await _reader.ReadAsync(CancellationToken.None);

        Assert.Equal(2, _log.At(LogLevel.Information).Count);
        Assert.Single(_log.At(LogLevel.Debug));
    }

    [Fact]
    public async Task A_check_that_cannot_reach_GitHub_says_so()
    {
        _failure = new HttpRequestException("No such host is known.");

        var status = await _reader.ReadAsync(CancellationToken.None);

        Assert.Equal("Could not check for updates right now. Weir could not reach GitHub.", TextOf(status, "summary"));
        Assert.Contains("No such host is known.", Assert.Single(_log.At(LogLevel.Information)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_Weir_cannot_read_says_so()
    {
        _failure = new FormatException("not a release");

        var status = await _reader.ReadAsync(CancellationToken.None);

        Assert.Equal("Could not check for updates right now. GitHub sent an answer Weir could not read.", TextOf(status, "summary"));
    }

    [Fact]
    public async Task A_catalog_with_no_release_yet_reads_as_nothing_published()
    {
        _failure = new ReleaseFetchException(404);

        var status = await _reader.ReadAsync(CancellationToken.None);

        Assert.Equal("not_published", TextOf(status, "status"));
    }

    [Fact]
    public async Task A_limit_is_still_reported_in_UTC_when_the_time_zone_setting_cannot_be_read()
    {
        using var temp = new TempDirectory();
        var unreadable = new SqliteDatabase(temp.Join("missing-folder", "weir.sqlite3"));
        var reader = new UpdateStatusReader(
            new FailingCatalog(this), _fixture.Options, unreadable, new SuiteSettingsStore(_fixture.Users), new UpdateFiles(_fixture.Options), _log);
        _failure = new ReleaseFetchException(403, new ReleaseRateLimit(LimitLifts, null));

        var status = await reader.ReadAsync(CancellationToken.None);

        Assert.EndsWith("Weir will check again at 11:35 pm.", TextOf(status, "summary"), StringComparison.Ordinal);
    }

    private sealed class FailingCatalog(UpdateStatusReaderTests owner) : IReleaseCatalogClient
    {
        public Task<GitHubReleaseRecord?> FetchLatestAsync(string currentVersion, CancellationToken cancellationToken) => throw owner._failure;
    }
}
