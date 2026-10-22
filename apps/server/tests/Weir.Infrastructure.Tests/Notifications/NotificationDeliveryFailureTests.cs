using Weir.Core.Configuration;
using Weir.Core.Notifications;
using Weir.Infrastructure.Http;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Notifications;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Notifications;

/// <summary>What an alert does when its destination cannot be reached or refuses it: the failure is told to the log, never thrown at the job.</summary>
public sealed class NotificationDeliveryFailureTests : IDisposable
{
    private const string Kind = "processing.file.remux_pass.v1";

    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly StoreFixture _store = new();
    private readonly List<string> _warnings = [];
    private readonly NotificationChannelStore _channels = new();

    public void Dispose() => _store.Dispose();

    private NotificationDispatcher DispatcherWhose(Func<string, int> post) => new(new ScriptedPoster(post), _store.Clock, _channels, MachineIdentity.From("RIG"));

    private async Task AddChannelAsync(string label, string url) =>
        await _store.WithUnitOfWork(uow => _channels.CreateAsync(uow, label, "webhook", url, ["processing_job_completed"], enabled: true));

    private async Task<long> CompletedJobAsync()
    {
        var job = await new ProcessingJobStore(_store.Database, _store.Clock).EnqueueOrGetAsync($"{Kind}:{Guid.NewGuid():N}", Kind, "{}");
        await _store.Execute($"UPDATE jobs SET status = 'completed' WHERE id = {job.Id}");
        return job.Id;
    }

    private async Task DispatchAsync(NotificationDispatcher dispatcher)
    {
        dispatcher.DispatchJobNotification(_store.Database, "processing", "completed", await CompletedJobAsync(), Kind, (message, _) => _warnings.Add(message));
        await dispatcher.WhenIdleAsync().WaitAsync(Ceiling);
    }

    [Fact]
    public async Task A_destination_that_refuses_the_alert_is_named_in_the_log_with_its_answer()
    {
        await AddChannelAsync("Chat", "https://alerts.example.com/chat");

        await DispatchAsync(DispatcherWhose(_ => 503));

        var warning = Assert.Single(_warnings);
        Assert.Equal(
            "The alert \"Chat\" was not delivered, and Weir will not send it again. " + ExternalUrlPolicy.DestinationRefused(503),
            warning);
    }

    [Fact]
    public async Task A_destination_Weir_could_not_reach_is_named_in_the_log_with_the_reason()
    {
        await AddChannelAsync("Chat", "https://alerts.example.com/chat");

        await DispatchAsync(DispatcherWhose(_ => throw new ExternalEndpointException(ExternalUrlPolicy.CouldNotConnect)));

        Assert.Equal(
            ["The alert \"Chat\" was not delivered, and Weir will not send it again. " + ExternalUrlPolicy.CouldNotConnect],
            _warnings);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(OperationCanceledException))]
    public async Task A_failure_that_is_not_Weirs_own_is_logged_in_the_generic_words(Type failure)
    {
        await AddChannelAsync("Chat", "https://alerts.example.com/chat");

        await DispatchAsync(DispatcherWhose(_ => throw (Exception)Activator.CreateInstance(failure)!));

        Assert.Equal(
            ["The alert \"Chat\" was not delivered, and Weir will not send it again. " + ExternalUrlPolicy.GenericDeliveryError],
            _warnings);
    }

    [Fact]
    public async Task One_channel_failing_does_not_stop_the_next_one_being_sent()
    {
        await AddChannelAsync("Broken", "https://broken.example.com/hook");
        await AddChannelAsync("Working", "https://working.example.com/hook");
        var delivered = new List<string>();

        await DispatchAsync(DispatcherWhose(url =>
        {
            if (url.Contains("broken", StringComparison.Ordinal))
            {
                throw new ExternalEndpointException(ExternalUrlPolicy.CouldNotDeliver);
            }

            delivered.Add(url);
            return 204;
        }));

        Assert.Equal(["https://working.example.com/hook"], delivered);
        Assert.Single(_warnings);
    }

    [Fact]
    public async Task Channels_that_cannot_be_read_are_logged_and_the_job_is_not_disturbed()
    {
        await _store.Execute("DROP TABLE notification_channels");

        await DispatchAsync(DispatcherWhose(_ => 204));

        var warning = Assert.Single(_warnings);
        Assert.StartsWith("Notification dispatch: failed to read channels for event=processing_job_completed", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_test_message_that_cannot_be_delivered_gives_the_reason_without_throwing()
    {
        var channel = new NotificationChannelRecord(
            1, "Chat", "webhook", "https://alerts.example.com/chat", "[]", true, Weir.Core.Time.Timestamp.UtcNow(_store.Clock), Weir.Core.Time.Timestamp.UtcNow(_store.Clock));

        Assert.Equal(
            ExternalUrlPolicy.CouldNotResolve,
            await DispatcherWhose(_ => throw new ExternalEndpointException(ExternalUrlPolicy.CouldNotResolve)).TestAsync(channel, CancellationToken.None));
        Assert.Equal(
            ExternalUrlPolicy.GenericDeliveryError,
            await DispatcherWhose(_ => throw new InvalidOperationException("unexpected")).TestAsync(channel, CancellationToken.None));
    }

    [Fact]
    public async Task A_test_message_stopped_by_the_caller_is_not_reported_as_a_delivery_failure()
    {
        var channel = new NotificationChannelRecord(
            1, "Chat", "webhook", "https://alerts.example.com/chat", "[]", true, Weir.Core.Time.Timestamp.UtcNow(_store.Clock), Weir.Core.Time.Timestamp.UtcNow(_store.Clock));
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => DispatcherWhose(_ => throw new OperationCanceledException(stopped.Token)).TestAsync(channel, stopped.Token));
    }

    private sealed class ScriptedPoster(Func<string, int> post) : IExternalJsonPoster
    {
        public Task<int> PostJsonAsync(string url, byte[] body, IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(post(url));
    }
}
