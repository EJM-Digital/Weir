using System.Text;
using System.Text.Json.Nodes;
using Weir.Core.Configuration;
using Weir.Core.Notifications;
using Weir.Infrastructure.Http;
using Weir.Infrastructure.Notifications;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Notifications;

/// <summary>A security alert reaches every alert that is switched on, whichever job events it listens for, and a failure is only logged.</summary>
public sealed class SecurityAlertTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly StoreFixture _store = new();
    private readonly NotificationChannelStore _channels = new();
    private readonly List<string> _warnings = [];
    private readonly List<(string Url, JsonObject Body)> _posts = [];

    public void Dispose() => _store.Dispose();

    private NotificationDispatcher Dispatcher(Func<string, int>? answer = null) =>
        new(new RecordingPoster(_posts, answer ?? (_ => 204)), _store.Clock, _channels, MachineIdentity.From("RIG"));

    private async Task AddChannelAsync(string label, string provider, string url, string eventName, bool enabled) =>
        await _store.WithUnitOfWork(uow => _channels.CreateAsync(uow, label, provider, url, [eventName], enabled));

    private async Task SendAsync(NotificationDispatcher dispatcher)
    {
        dispatcher.DispatchSecurityAlert(_store.Database, "Something happened", "Plain words.", (message, _) => _warnings.Add(message));
        await dispatcher.WhenIdleAsync().WaitAsync(Ceiling);
    }

    [Fact]
    public async Task Every_enabled_alert_gets_it_whatever_events_it_listens_for_and_a_disabled_one_does_not()
    {
        await AddChannelAsync("Failures", "webhook", "https://alerts.example.com/failures", "job_failed", enabled: true);
        await AddChannelAsync("Completions", "discord", "https://alerts.example.com/completions", "job_completed", enabled: true);
        await AddChannelAsync("Off", "webhook", "https://alerts.example.com/off", "job_failed", enabled: false);

        await SendAsync(Dispatcher());

        Assert.Equal(["https://alerts.example.com/completions", "https://alerts.example.com/failures"], _posts.Select(post => post.Url).Order().ToList());
        var webhook = _posts.Single(post => post.Url.EndsWith("failures", StringComparison.Ordinal)).Body;
        Assert.Equal("auth_sign_in_recovered", webhook["event"]!.GetValue<string>());
        Assert.Equal("auth", webhook["module"]!.GetValue<string>());
        Assert.Equal("Something happened", webhook["title"]!.GetValue<string>());
        Assert.Equal("Plain words.", webhook["detail"]!.GetValue<string>());
        Assert.Empty(_warnings);
    }

    [Fact]
    public async Task With_no_alert_set_up_nothing_is_sent()
    {
        await SendAsync(Dispatcher());

        Assert.Empty(_posts);
        Assert.Empty(_warnings);
    }

    [Fact]
    public async Task An_alert_that_cannot_be_delivered_is_named_in_the_log()
    {
        await AddChannelAsync("Chat", "webhook", "https://alerts.example.com/chat", "job_failed", enabled: true);

        await SendAsync(Dispatcher(_ => 503));

        Assert.Equal(
            ["The alert \"Chat\" was not delivered, and Weir will not send it again. " + ExternalUrlPolicy.DestinationRefused(503)],
            _warnings);
    }

    [Fact]
    public async Task Alerts_that_cannot_be_read_are_logged_and_nothing_is_thrown()
    {
        await _store.Execute("DROP TABLE notification_channels");

        await SendAsync(Dispatcher());

        var warning = Assert.Single(_warnings);
        Assert.StartsWith("Notification dispatch: failed to read channels for event=auth_sign_in_recovered", warning, StringComparison.Ordinal);
    }

    private sealed class RecordingPoster(List<(string Url, JsonObject Body)> posts, Func<string, int> answer) : IExternalJsonPoster
    {
        public Task<int> PostJsonAsync(string url, byte[] body, IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (posts)
            {
                posts.Add((url, JsonNode.Parse(Encoding.UTF8.GetString(body))!.AsObject()));
            }

            return Task.FromResult(answer(url));
        }
    }
}
