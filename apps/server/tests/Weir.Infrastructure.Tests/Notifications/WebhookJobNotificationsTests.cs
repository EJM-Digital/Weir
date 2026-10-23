using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Configuration;
using Weir.Infrastructure.Http;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Notifications;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Notifications;

/// <summary>
/// Setup › Connections › Alerts channels receive real job events, not only "Send test": the registered notifier turns job
/// outcomes into alerts on every saved channel.
/// </summary>
public sealed class WebhookJobNotificationsTests : IDisposable
{
    private const string RemuxPass = "processing.file.remux_pass.v1";
    private const string LibraryClean = "processing.library.clean.v1";
    private const string FolderScan = "processing.watched_folder.remux_scan_dispatch.v1";

    private readonly StoreFixture _store = new();
    private readonly RecordingPoster _poster = new();
    private readonly NotificationChannelStore _channels = new();
    private readonly NotificationDispatcher _dispatcher;
    private readonly WebhookJobNotifications _notifications;

    public WebhookJobNotificationsTests()
    {
        _dispatcher = new NotificationDispatcher(_poster, _store.Clock, _channels, MachineIdentity.From("RIG"));
        _notifications = new WebhookJobNotifications(_store.Database, _dispatcher, NullLogger<WebhookJobNotifications>.Instance);
    }

    /// <summary>
    /// Everything posted once the background deliveries have finished. Waiting on the deliveries themselves, not on a
    /// clock, is what keeps these tests steady on a slow runner (#669).
    /// </summary>
    private async Task<List<(string Url, string Body)>> PostsAsync()
    {
        await _dispatcher.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return _poster.Posts;
    }

    public void Dispose() => _store.Dispose();

    private Task<Weir.Core.Notifications.NotificationChannelRecord> ChannelAsync(string label, params string[] events) =>
        _store.WithUnitOfWork(uow => _channels.CreateAsync(uow, label, "webhook", $"https://alerts.example.com/{label}", events, enabled: true));

    private Task<Weir.Core.Notifications.NotificationChannelRecord> SwitchAsync(long id, bool enabled) =>
        _store.WithUnitOfWork(async uow =>
        {
            var row = (await _channels.GetAsync(uow, id))!;
            return await _channels.UpdateAsync(uow, row, row.Label, row.Provider, row.Url, Weir.Core.Notifications.NotificationRules.ParseEvents(row.EventsJson), enabled);
        });

    private async Task<long> JobAsync(string kind, string status)
    {
        var job = await new ProcessingJobStore(_store.Database, _store.Clock).EnqueueOrGetAsync($"{kind}:{Guid.NewGuid():N}", kind, "{}");
        await _store.Execute($"UPDATE jobs SET status = '{status}' WHERE id = {job.Id}");
        return job.Id;
    }

    [Fact]
    public async Task A_file_that_finished_reaches_file_processing_and_any_job_channels()
    {
        await ChannelAsync("files", "processing_job_completed");
        await ChannelAsync("any", "job_completed");
        await ChannelAsync("failures", "job_failed");
        var job = await JobAsync(RemuxPass, "completed");

        _notifications.Dispatch("processing", "completed", job, RemuxPass);

        var posts = await PostsAsync();
        Assert.Equal(["https://alerts.example.com/any", "https://alerts.example.com/files"], posts.Select(p => p.Url).Order());
        Assert.All(posts, p => Assert.Contains("\"processing_job_completed\"", p.Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_alert_says_which_machine_it_came_from()
    {
        await ChannelAsync("files", "processing_job_completed");
        var job = await JobAsync(RemuxPass, "completed");

        _notifications.Dispatch("processing", "completed", job, RemuxPass);

        var post = Assert.Single(await PostsAsync());
        Assert.Contains("\"title\": \"Weir on RIG job completed\"", post.Body, StringComparison.Ordinal);
        Assert.Contains("\"machine_name\": \"RIG\"", post.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_library_clean_is_file_processing_too()
    {
        await ChannelAsync("files", "processing_job_failed");
        var job = await JobAsync(LibraryClean, "failed");

        _notifications.Dispatch("processing", "failed", job, LibraryClean);

        var post = Assert.Single(await PostsAsync());
        Assert.Equal("https://alerts.example.com/files", post.Url);
    }

    [Fact]
    public async Task A_scan_finishing_sends_nothing_even_to_any_job_channels()
    {
        await ChannelAsync("any", "job_completed", "processing_job_completed");
        var job = await JobAsync(FolderScan, "completed");

        _notifications.Dispatch("processing", "completed", job, FolderScan);

        Assert.Empty(await PostsAsync());
    }

    [Fact]
    public async Task A_scan_that_failed_for_good_reaches_any_job_failed_but_not_file_processing()
    {
        await ChannelAsync("files", "processing_job_failed");
        await ChannelAsync("failures", "job_failed");
        var job = await JobAsync(FolderScan, "failed");

        _notifications.Dispatch("processing", "failed", job, FolderScan);

        var post = Assert.Single(await PostsAsync());
        Assert.Equal("https://alerts.example.com/failures", post.Url);
    }

    [Fact]
    public async Task A_file_that_failed_for_good_in_a_job_that_ended_normally_reaches_the_failure_channels_only()
    {
        await ChannelAsync("failures", "processing_job_failed");
        await ChannelAsync("any-failure", "job_failed");
        await ChannelAsync("finished", "processing_job_completed");
        var job = await JobAsync(RemuxPass, "completed");

        _notifications.DispatchFailedForGood("processing", job, RemuxPass);

        var posts = await PostsAsync();
        Assert.Equal(["https://alerts.example.com/any-failure", "https://alerts.example.com/failures"], posts.Select(p => p.Url).Order());
        Assert.All(posts, post =>
        {
            var body = JsonNode.Parse(post.Body)!;
            Assert.Equal("processing_job_failed", (string?)body["event"]);
            Assert.Equal("Weir on RIG job failed", (string?)body["title"]);
            Assert.Equal($"Job {job} ({RemuxPass}) failed, and Weir has stopped trying.", (string?)body["detail"]);
        });
    }

    [Fact]
    public async Task A_channel_switched_off_is_sent_nothing_until_it_is_switched_on()
    {
        var channel = await ChannelAsync("files", "processing_job_failed");
        await SwitchAsync(channel.Id, enabled: false);
        var job = await JobAsync(RemuxPass, "failed");

        _notifications.Dispatch("processing", "failed", job, RemuxPass);
        Assert.Empty(await PostsAsync());

        await SwitchAsync(channel.Id, enabled: true);
        _notifications.Dispatch("processing", "failed", job, RemuxPass);

        var post = Assert.Single(await PostsAsync());
        Assert.Equal("https://alerts.example.com/files", post.Url);
    }

    [Fact]
    public async Task A_delivery_the_channel_refuses_is_logged_as_a_warning()
    {
        var logger = new RecordingLogger<WebhookJobNotifications>();
        var notifications = new WebhookJobNotifications(_store.Database, _dispatcher, logger);
        _poster.Status = 500;
        await ChannelAsync("files", "processing_job_failed");
        var job = await JobAsync(RemuxPass, "failed");

        notifications.Dispatch("processing", "failed", job, RemuxPass);
        await PostsAsync();

        Assert.Contains(logger.At(LogLevel.Warning), message => message.Contains("was not delivered", StringComparison.Ordinal));
    }

    [Fact]
    public void A_notifier_that_cannot_start_a_delivery_logs_it_instead_of_throwing()
    {
        var logger = new RecordingLogger<WebhookJobNotifications>();
        var broken = new WebhookJobNotifications(null!, _dispatcher, logger);

        broken.Dispatch("processing", "completed", 1, RemuxPass);
        broken.Dispatch("processing", "failed", 2, FolderScan);
        broken.DispatchFailedForGood("processing", 3, RemuxPass);

        Assert.Equal(3, logger.At(LogLevel.Warning).Count);
    }

    [Fact]
    public async Task A_channel_switched_off_does_not_stop_the_others_hearing()
    {
        await ChannelAsync("on", "processing_job_failed");
        var off = await ChannelAsync("off", "processing_job_failed");
        await SwitchAsync(off.Id, enabled: false);
        var job = await JobAsync(RemuxPass, "failed");

        _notifications.Dispatch("processing", "failed", job, RemuxPass);

        var post = Assert.Single(await PostsAsync());
        Assert.Equal("https://alerts.example.com/on", post.Url);
    }

    [Fact]
    public async Task A_discord_channel_is_sent_an_embed_and_a_webhook_channel_the_flat_body()
    {
        await _store.WithUnitOfWork(uow => _channels.CreateAsync(uow, "chat", "discord", "https://discord.example.com/hook", ["processing_job_failed"], enabled: true));
        await ChannelAsync("flat", "processing_job_failed");
        var job = await JobAsync(RemuxPass, "failed");

        _notifications.Dispatch("processing", "failed", job, RemuxPass);

        var posts = (await PostsAsync()).ToDictionary(post => post.Url, post => JsonNode.Parse(post.Body)!.AsObject());
        var embed = Assert.Single(posts["https://discord.example.com/hook"]["embeds"]!.AsArray())!;
        Assert.Equal("Weir on RIG job failed", (string?)embed["title"]);
        Assert.Equal(0xE74C3C, (int?)embed["color"]);
        Assert.Equal("Weir on RIG", (string?)embed["footer"]!["text"]);
        Assert.False(posts["https://discord.example.com/hook"].ContainsKey("event"));
        Assert.Equal("processing_job_failed", (string?)posts["https://alerts.example.com/flat"]["event"]);
        Assert.False(posts["https://alerts.example.com/flat"].ContainsKey("embeds"));
    }

    [Fact]
    public async Task A_failure_with_a_retry_still_coming_sends_nothing()
    {
        await ChannelAsync("failures", "job_failed", "processing_job_failed");
        var job = await JobAsync(RemuxPass, "pending");

        _notifications.Dispatch("processing", "failed", job, RemuxPass, willRetry: true);

        Assert.Empty(await PostsAsync());
    }

    /// <summary>Records every post instead of sending it.</summary>
    private sealed class RecordingPoster : IExternalJsonPoster
    {
        private readonly ConcurrentQueue<(string Url, string Body)> _posts = new();

        public List<(string Url, string Body)> Posts => [.. _posts];

        public int Status { get; set; } = 204;

        public Task<int> PostJsonAsync(string url, byte[] body, IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken cancellationToken)
        {
            _posts.Enqueue((url, Encoding.UTF8.GetString(body)));
            return Task.FromResult(Status);
        }
    }
}
