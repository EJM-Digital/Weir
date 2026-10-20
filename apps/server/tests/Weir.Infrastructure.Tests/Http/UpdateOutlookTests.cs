using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Updates;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Http;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Http;

/// <summary>Whether a newer Weir exists, kept between reads so System never asks GitHub more than it must.</summary>
public sealed class UpdateOutlookTests : IDisposable
{
    private readonly StoreFixture _fixture = new(("WEIR_VERSION", "3.2.16"));
    private readonly ScriptedCatalog _catalog = new();
    private readonly DataChangePublisher _changes = new();
    private readonly UpdateOutlook _outlook;

    public UpdateOutlookTests()
    {
        var files = new UpdateFiles(_fixture.Options);
        _outlook = new UpdateOutlook(new UpdateStatusReader(_catalog, _fixture.Options, _fixture.Database, new SuiteSettingsStore(_fixture.Users), files, NullLogger<UpdateStatusReader>.Instance), files, _fixture.Clock, _changes);
    }

    public void Dispose() => _fixture.Dispose();

    private static GitHubReleaseRecord Release(string version) => new($"v{version}", version, null, null, null, false, false, []);

    private void TrayHasDownloaded(string version) =>
        File.WriteAllText(Path.Join(_fixture.Home.Path, UpdateFiles.StateFileName), $"{{\"downloaded\": true, \"version\": \"{version}\"}}");

    [Fact]
    public async Task A_newer_release_reads_as_an_update_available()
    {
        _catalog.Answers(Release("3.3.0"));

        await _outlook.CheckAsync(CancellationToken.None);

        Assert.Equal(new UpdateOutlookSnapshot("update_available", "3.3.0"), _outlook.Current());
    }

    [Fact]
    public async Task The_same_release_reads_as_up_to_date()
    {
        _catalog.Answers(Release("3.2.16"));

        await _outlook.CheckAsync(CancellationToken.None);

        Assert.Equal(new UpdateOutlookSnapshot("up_to_date", "3.2.16"), _outlook.Current());
    }

    [Fact]
    public async Task A_check_that_cannot_reach_the_release_list_reads_as_unavailable()
    {
        _catalog.Fails();

        await _outlook.CheckAsync(CancellationToken.None);

        Assert.Equal(new UpdateOutlookSnapshot("unavailable", null), _outlook.Current());
    }

    [Fact]
    public async Task A_limit_keeps_the_last_known_release_and_is_not_asked_about_again_until_it_lifts()
    {
        var lifts = _fixture.Clock.GetUtcNow().AddMinutes(20);
        _catalog.LimitedUntil(lifts, Release("3.3.0"));
        await _outlook.CheckAsync(CancellationToken.None);

        Assert.Equal(new UpdateOutlookSnapshot("rate_limited", "3.3.0"), _outlook.Current());

        _fixture.Clock.Advance(TimeSpan.FromMinutes(19));
        _outlook.Current();
        Assert.Equal(1, _catalog.Calls);

        _catalog.Answers(Release("3.3.0"));
        _fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        _outlook.Current();
        await Eventually.ThatAsync(() => _catalog.Calls == 2);
        await Eventually.ThatAsync(() => _outlook.Current().Status == "update_available");
    }

    [Fact]
    public async Task An_update_the_tray_already_downloaded_is_reported_whatever_the_last_check_found()
    {
        _catalog.Answers(Release("3.3.0"));
        await _outlook.CheckAsync(CancellationToken.None);
        TrayHasDownloaded("3.4.0");

        Assert.Equal(new UpdateOutlookSnapshot(UpdateOutlook.Downloaded, "3.4.0"), _outlook.Current());
    }

    [Fact]
    public async Task A_fresh_answer_is_not_asked_for_again_until_half_an_hour_has_passed()
    {
        _catalog.Answers(Release("3.3.0"));
        await _outlook.CheckAsync(CancellationToken.None);

        _fixture.Clock.Advance(TimeSpan.FromMinutes(29));
        _outlook.Current();
        _outlook.Current();

        Assert.Equal(1, _catalog.Calls);

        _fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        _outlook.Current();
        await Eventually.ThatAsync(() => _catalog.Calls == 2);
    }

    [Fact]
    public async Task A_failed_answer_is_asked_for_again_after_five_minutes()
    {
        _catalog.Fails();
        await _outlook.CheckAsync(CancellationToken.None);

        _fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        _outlook.Current();

        await Eventually.ThatAsync(() => _catalog.Calls == 2);
    }

    [Fact]
    public async Task Until_the_first_check_answers_the_outlook_says_it_is_checking()
    {
        _catalog.HoldsTheAnswer();

        Assert.Equal(new UpdateOutlookSnapshot(UpdateOutlook.Checking, null), _outlook.Current());

        _catalog.Answers(Release("3.3.0"));
        await Eventually.ThatAsync(() => _outlook.Current().Status == "update_available");
    }

    private static async Task<string[]> HeardAsync(BroadcastSubscription<string> heard)
    {
        heard.Dispose();
        var topics = new List<string>();
        await foreach (var topic in heard.ReadAllAsync(CancellationToken.None))
        {
            topics.Add(topic);
        }

        return [.. topics];
    }

    [Fact]
    public async Task A_check_that_finds_something_new_is_announced_and_one_that_finds_the_same_is_not()
    {
        _catalog.Answers(Release("3.3.0"));
        using var heard = _changes.Subscribe();

        await _outlook.CheckAsync(CancellationToken.None);
        await _outlook.CheckAsync(CancellationToken.None);
        _catalog.Answers(Release("3.4.0"));
        await _outlook.CheckAsync(CancellationToken.None);

        Assert.Equal([DataTopics.Update, DataTopics.Update], await HeardAsync(heard));
    }

    [Fact]
    public async Task The_update_check_task_asks_only_while_a_stream_is_open()
    {
        _catalog.Answers(Release("3.3.0"));
        var task = new UpdateCheckTask(_outlook, _changes);

        await task.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, _catalog.Calls);

        using var heard = _changes.Subscribe();
        await task.RunOnceAsync(CancellationToken.None);

        await Eventually.ThatAsync(() => _catalog.Calls == 1);
        await Eventually.ThatAsync(() => _outlook.Current().Status == "update_available");
    }

    private sealed class ScriptedCatalog : IReleaseCatalogClient
    {
        private readonly object _gate = new();
        private TaskCompletionSource<GitHubReleaseRecord?>? _held;
        private GitHubReleaseRecord? _release;
        private ReleaseRateLimit? _limit;
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void HoldsTheAnswer()
        {
            lock (_gate)
            {
                _held = new TaskCompletionSource<GitHubReleaseRecord?>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void LimitedUntil(DateTimeOffset resetsAt, GitHubReleaseRecord? lastKnown)
        {
            lock (_gate)
            {
                _limit = new ReleaseRateLimit(resetsAt, lastKnown);
            }
        }

        public void Answers(GitHubReleaseRecord release)
        {
            lock (_gate)
            {
                _limit = null;
                _release = release;
                _held?.TrySetResult(release);
            }
        }

        public void Fails() => _release = null;

        public Task<GitHubReleaseRecord?> FetchLatestAsync(string currentVersion, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            lock (_gate)
            {
                if (_held is { } held)
                {
                    return held.Task;
                }

                if (_limit is { } limit)
                {
                    throw new ReleaseFetchException(403, limit);
                }

                return _release is { } release ? Task.FromResult<GitHubReleaseRecord?>(release) : throw new ReleaseFetchException("offline");
            }
        }
    }
}
