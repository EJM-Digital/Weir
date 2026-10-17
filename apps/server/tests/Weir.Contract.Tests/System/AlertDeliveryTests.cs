using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.SystemArea;

/// <summary>
/// Setup, Connections, Alerts: "Send test" really reaches the address a channel names, as the JSON body Weir documents for a
/// webhook and as the one embed Discord documents for a Discord channel, and says plainly when the other end refuses it.
/// <para>
/// The address is a public-looking name. The server resolves it to a public address and applies the same rules as for a real
/// alert; a test server started with <see cref="FakeAlertReceiver.Env"/> only connects to the receiver instead of the internet.
/// </para>
/// </summary>
[ContractArea("system")]
public sealed class AlertDeliveryTests(AlertDeliveryTests.ReceiverFixture world) : IClassFixture<AlertDeliveryTests.ReceiverFixture>
{
    private const string Channels = SystemPartBHelpers.Api + "/suite/notification-channels";

    private FakeAlertReceiver Receiver => world.Receiver;

    private static async Task<string> CreateChannelAsync(WeirClient admin, string provider, string label, string address)
    {
        var created = await admin.PostWithCsrfAsync(
            Channels,
            new JsonObject
            {
                ["label"] = label,
                ["provider"] = provider,
                ["url"] = $"http://hooks.example.com{address}",
                ["events"] = new JsonArray("job_completed", "job_failed"),
            });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        return $"{Channels}/{(long)created.Fields["id"]!}";
    }

    private static async Task<JsonObject> TestAsync(WeirClient admin, string channelPath)
    {
        var tested = await admin.PostWithCsrfAsync($"{channelPath}/test", new JsonObject());
        Assert.True(tested.Status == HttpStatusCode.OK, tested.ToString());
        return tested.Fields;
    }

    [Fact]
    public async Task A_test_to_a_webhook_channel_arrives_as_the_documented_json_post()
    {
        using var admin = await world.Server.CreateAdminClientAsync();
        var path = await CreateChannelAsync(admin, "webhook", "Webhook test", "/webhook-body");

        var result = await TestAsync(admin, path);

        Assert.True((bool)result["ok"]!);
        Assert.Null(result["error"]);
        var post = Assert.Single(await Receiver.WaitForPostsAsync("/webhook-body", 1));
        Assert.Equal("POST", post.Method);
        Assert.Equal("hooks.example.com", post.Header("Host"));
        Assert.StartsWith("application/json", post.Header("Content-Type"), StringComparison.Ordinal);
        Assert.Equal("Weir/1.0", post.Header("User-Agent"));
        var body = post.Json!.AsObject();
        Assert.Equal(
            ["event", "module", "job_id", "job_kind", "title", "detail", "timestamp", "app", "machine_name"],
            body.Select(member => member.Key));
        Assert.Equal("job_completed", (string?)body["event"]);
        Assert.Equal("processing", (string?)body["module"]);
        Assert.Equal(0, (long?)body["job_id"]);
        Assert.Equal("test", (string?)body["job_kind"]);
        // Weir is named after the machine it runs on.
        var machine = (string?)body["machine_name"];
        Assert.False(string.IsNullOrWhiteSpace(machine));
        var app = $"Weir on {machine}";
        Assert.Equal($"{app} test notification", (string?)body["title"]);
        Assert.Equal($"This is a test notification from {app}.", (string?)body["detail"]);
        Assert.Equal("Weir", (string?)body["app"]);
        Assert.True(DateTimeOffset.TryParse((string?)body["timestamp"], out _), body.ToJsonString());
    }

    [Fact]
    public async Task A_test_to_a_discord_channel_arrives_as_one_green_embed()
    {
        using var admin = await world.Server.CreateAdminClientAsync();
        var path = await CreateChannelAsync(admin, "discord", "Discord test", "/discord-body");

        var result = await TestAsync(admin, path);

        Assert.True((bool)result["ok"]!);
        var post = Assert.Single(await Receiver.WaitForPostsAsync("/discord-body", 1));
        Assert.StartsWith("application/json", post.Header("Content-Type"), StringComparison.Ordinal);
        var body = post.Json!.AsObject();
        Assert.Equal(["embeds"], body.Select(member => member.Key));
        var embed = Assert.Single(body["embeds"]!.AsArray())!.AsObject();
        Assert.Equal(["title", "description", "color", "fields", "footer", "timestamp"], embed.Select(member => member.Key));
        var app = (string?)embed["footer"]!["text"];
        Assert.Matches("^Weir on .+$", app);
        Assert.Equal($"{app} test notification", (string?)embed["title"]);
        Assert.Equal($"This is a test notification from {app}.", (string?)embed["description"]);
        Assert.Equal(0x2ECC71, (int?)embed["color"]);
        var fields = embed["fields"]!.AsArray();
        Assert.Equal(("Module", "processing", true), ((string?)fields[0]!["name"], (string?)fields[0]!["value"], (bool?)fields[0]!["inline"]));
        Assert.Equal(("Job ID", "0", true), ((string?)fields[1]!["name"], (string?)fields[1]!["value"], (bool?)fields[1]!["inline"]));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_destination_that_answers_with_an_error_is_reported_plainly_and_asked_once(HttpStatusCode answer)
    {
        using var admin = await world.Server.CreateAdminClientAsync();
        var address = $"/refused-{(int)answer}";
        var path = await CreateChannelAsync(admin, "webhook", $"Refusing {(int)answer}", address);
        Receiver.Answers(address, answer);

        var result = await TestAsync(admin, path);

        Assert.False((bool)result["ok"]!);
        Assert.Equal(
            $"The notification destination answered with an error (HTTP {(int)answer}), so it did not take the message. Check the address.",
            (string?)result["error"]);
        Assert.Single(await Receiver.WaitForPostsAsync(address, 1));

        // Nothing sends it again on its own: a second post is there only after the person asks for another test.
        Receiver.Answers(address, HttpStatusCode.NoContent);
        Assert.True((bool)(await TestAsync(admin, path))["ok"]!);
        Assert.Equal(2, (await Receiver.WaitForPostsAsync(address, 2)).Count);
    }

    [Fact]
    public async Task A_destination_that_redirects_is_not_followed_and_is_reported()
    {
        using var admin = await world.Server.CreateAdminClientAsync();
        var path = await CreateChannelAsync(admin, "webhook", "Redirecting", "/redirected");
        Receiver.RedirectsTo("/redirected", "http://hooks.example.com/elsewhere");

        var result = await TestAsync(admin, path);

        Assert.False((bool)result["ok"]!);
        Assert.Equal("The notification destination returned a redirect; redirects are disabled.", (string?)result["error"]);
        Assert.Single(await Receiver.WaitForPostsAsync("/redirected", 1));
        Assert.Empty(Receiver.PostsTo("/elsewhere"));
    }

    public sealed class ReceiverFixture : IAsyncLifetime
    {
        public FakeAlertReceiver Receiver { get; } = new();

        public WeirServer Server { get; private set; } = null!;

        public async Task InitializeAsync() => Server = await WeirServer.StartNewAsync(Receiver.Env);

        public async Task DisposeAsync()
        {
            await Server.DisposeAsync();
            Receiver.Dispose();
        }
    }
}
