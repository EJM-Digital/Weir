using System.Text;
using System.Text.Json.Nodes;
using Weir.Core.Configuration;
using Weir.Core.Notifications;
using Weir.Core.Time;
using Weir.Infrastructure.Http;
using Weir.Infrastructure.Notifications;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Notifications;

/// <summary>What "Send test" says: the destination took the message, or why it did not.</summary>
public sealed class NotificationDispatcherTests : IDisposable
{
    private readonly StoreFixture _store = new();

    public void Dispose() => _store.Dispose();

    private NotificationDispatcher DispatcherAnswering(Func<string, byte[], int> answer, out Func<List<(string Url, JsonObject Body, IReadOnlyDictionary<string, string> Headers)>> posts)
    {
        var poster = new AnsweringPoster(answer);
        posts = () => poster.Posts;
        return new NotificationDispatcher(poster, _store.Clock, new NotificationChannelStore(), MachineIdentity.From("RIG"));
    }

    private static NotificationChannelRecord Channel(string provider) =>
        new(7, "Chat", provider, "https://alerts.example.com/hook", "[\"job_failed\"]", true, Timestamp.UtcNow(TimeProvider.System), Timestamp.UtcNow(TimeProvider.System));

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(299)]
    public async Task A_destination_that_takes_the_test_message_is_a_success(int status)
    {
        var dispatcher = DispatcherAnswering((_, _) => status, out _);

        Assert.Null(await dispatcher.TestAsync(Channel("webhook"), CancellationToken.None));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task A_destination_that_answers_with_an_error_is_not_a_success(int status)
    {
        var dispatcher = DispatcherAnswering((_, _) => status, out _);

        var error = await dispatcher.TestAsync(Channel("discord"), CancellationToken.None);

        Assert.Equal($"The notification destination answered with an error (HTTP {status}), so it did not take the message. Check the address.", error);
    }

    [Theory]
    [InlineData("webhook", "event")]
    [InlineData("discord", "embeds")]
    public async Task The_test_message_is_in_the_shape_of_the_channels_provider(string provider, string member)
    {
        var dispatcher = DispatcherAnswering((_, _) => 204, out var posts);

        await dispatcher.TestAsync(Channel(provider), CancellationToken.None);

        var post = Assert.Single(posts());
        Assert.Equal("https://alerts.example.com/hook", post.Url);
        Assert.True(post.Body.ContainsKey(member));
        Assert.Equal("Weir/1.0", post.Headers["User-Agent"]);
    }

    private sealed class AnsweringPoster(Func<string, byte[], int> answer) : IExternalJsonPoster
    {
        private readonly List<(string Url, JsonObject Body, IReadOnlyDictionary<string, string> Headers)> _posts = [];

        public List<(string Url, JsonObject Body, IReadOnlyDictionary<string, string> Headers)> Posts => _posts;

        public Task<int> PostJsonAsync(string url, byte[] body, IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken cancellationToken)
        {
            _posts.Add((url, JsonNode.Parse(Encoding.UTF8.GetString(body))!.AsObject(), headers));
            return Task.FromResult(answer(url, body));
        }
    }
}
