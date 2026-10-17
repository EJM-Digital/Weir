using System.Net;
using System.Text.Json.Nodes;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// An alert's address carries its secret token, so no route gives it back: the list, create and update answer with the address
/// masked, an update that sends no address keeps the saved one, and two alerts never send to the same address.
/// </summary>
public sealed class NotificationChannelAddressApiTests
{
    private const string Channels = "/api/v1/suite/notification-channels";
    private const string Token = "s3cr3t-T0ken_abc";
    private const string DiscordAddress = "https://discord.com/api/webhooks/1034598712/" + Token;
    private const string DiscordMasked = "https://discord.com/api/webhooks/1034598712/••••";

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    private static async Task<HttpResponseMessage> CreateAsync(ApiTestClient client, string label, string url) =>
        await client.PostAsync(
            Channels, new { csrf_token = await client.CsrfAsync(), label, provider = "discord", url, events = new[] { "job_failed" } });

    private static async Task<long> CreatedIdAsync(ApiTestClient client, string label, string url)
    {
        using var created = await CreateAsync(client, label, url);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await Json(created))["id"]!.GetValue<long>();
    }

    private static async Task<HttpResponseMessage> UpdateAsync(ApiTestClient client, long id, object? url, string label = "Renamed") =>
        await client.PutAsync(
            $"{Channels}/{id}",
            url is null
                ? new JsonObject { ["csrf_token"] = await client.CsrfAsync(), ["label"] = label, ["provider"] = "discord" }
                : new JsonObject { ["csrf_token"] = await client.CsrfAsync(), ["label"] = label, ["provider"] = "discord", ["url"] = url.ToString() });

    private static Task<string?> SavedAddressAsync(WeirTestServer server, long id) =>
        TestDatabase.ScalarStringAsync(server, "SELECT url FROM notification_channels WHERE id = $id", ("$id", id));

    [Fact]
    public async Task No_route_gives_back_the_token_in_an_alerts_address()
    {
        var (server, client) = await StartAsync();
        await using var stop = server;

        using var created = await CreateAsync(client, "Discord", DiscordAddress);
        var id = (await Json(created))["id"]!.GetValue<long>();
        using var updated = await UpdateAsync(client, id, DiscordAddress);
        using var listed = await client.GetAsync(Channels);

        foreach (var response in new[] { created, updated, listed })
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        }

        Assert.Equal(DiscordMasked, (await Json(created))["url"]!.GetValue<string>());
        Assert.Equal(DiscordMasked, (await Json(updated))["url"]!.GetValue<string>());
        Assert.Equal(DiscordMasked, (await Json(listed))["items"]![0]!["url"]!.GetValue<string>());
        Assert.Equal(DiscordAddress, await SavedAddressAsync(server, id));
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1034598712/" + Token, "https://discord.com/api/webhooks/1034598712/••••", true)]
    [InlineData("https://hooks.example.com/weir", "https://hooks.example.com/••••", true)]
    [InlineData("https://hooks.example.com:8443/a/b/c?key=" + Token, "https://hooks.example.com:8443/a/b/••••?••••", true)]
    [InlineData("https://hooks.example.com/?key=" + Token, "https://hooks.example.com/?••••", true)]
    [InlineData("https://hooks.example.com", "https://hooks.example.com", false)]
    [InlineData("https://hooks.example.com/", "https://hooks.example.com/", false)]
    public async Task The_address_is_shown_with_its_token_hidden_and_says_whether_it_had_one(string address, string shown, bool hadToken)
    {
        var (server, client) = await StartAsync();
        await using var stop = server;
        await CreatedIdAsync(client, "Hook", address);

        var item = (await Json(await client.GetAsync(Channels)))["items"]![0]!;

        Assert.Equal(shown, item["url"]!.GetValue<string>());
        Assert.Equal(hadToken, item["has_token"]!.GetValue<bool>());
        Assert.DoesNotContain(Token, item.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_update_with_no_address_keeps_the_saved_one()
    {
        var (server, client) = await StartAsync();
        await using var stop = server;
        var id = await CreatedIdAsync(client, "Discord", DiscordAddress);

        using var updated = await UpdateAsync(client, id, url: null);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Renamed", (await Json(updated))["label"]!.GetValue<string>());
        Assert.Equal(DiscordAddress, await SavedAddressAsync(server, id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_update_with_a_blank_address_keeps_the_saved_one(string blank)
    {
        var (server, client) = await StartAsync();
        await using var stop = server;
        var id = await CreatedIdAsync(client, "Discord", DiscordAddress);

        using var updated = await UpdateAsync(client, id, blank);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(DiscordAddress, await SavedAddressAsync(server, id));
    }

    [Fact]
    public async Task An_update_that_sends_the_masked_address_back_keeps_the_saved_one()
    {
        var (server, client) = await StartAsync();
        await using var stop = server;
        var id = await CreatedIdAsync(client, "Discord", DiscordAddress);

        using var updated = await UpdateAsync(client, id, DiscordMasked);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(DiscordAddress, await SavedAddressAsync(server, id));
    }

    [Fact]
    public async Task An_update_with_a_typed_address_replaces_the_saved_one()
    {
        var (server, client) = await StartAsync();
        await using var stop = server;
        var id = await CreatedIdAsync(client, "Discord", DiscordAddress);
        const string Replacement = "https://discord.com/api/webhooks/777/replacement-token";

        using var updated = await UpdateAsync(client, id, Replacement);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(Replacement, await SavedAddressAsync(server, id));
        Assert.Equal("https://discord.com/api/webhooks/777/••••", (await Json(updated))["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_typed_address_that_is_refused_leaves_the_saved_one_alone()
    {
        var (server, client) = await StartAsync();
        await using var stop = server;
        var id = await CreatedIdAsync(client, "Discord", DiscordAddress);

        using var refused = await UpdateAsync(client, id, "http://127.0.0.1:8080/hook");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(DiscordAddress, await SavedAddressAsync(server, id));
    }

    [Fact]
    public async Task A_new_alert_still_needs_an_address()
    {
        var (server, client) = await StartAsync();
        await using var stop = server;

        using var refused = await client.PostAsync(
            Channels, new { csrf_token = await client.CsrfAsync(), label = "None", provider = "discord", events = new[] { "job_failed" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM notification_channels"));
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1034598712/" + Token)]
    [InlineData("HTTPS://Discord.COM/api/webhooks/1034598712/" + Token)]
    [InlineData("https://discord.com/api/webhooks/1034598712/" + Token + "/")]
    [InlineData("  https://discord.com/api/webhooks/1034598712/" + Token + "  ")]
    public async Task A_second_alert_to_the_same_address_is_refused_in_plain_words(string again)
    {
        var (server, client) = await StartAsync();
        await using var stop = server;
        await CreatedIdAsync(client, "Discord", DiscordAddress);

        using var refused = await CreateAsync(client, "Again", again);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("An alert already sends to that address.", await Detail(refused));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM notification_channels"));
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1034598712/" + Token + "x")]
    [InlineData("https://discord.com/api/webhooks/1034598712/" + "S3CR3T-t0KEN_ABC")]
    [InlineData("http://discord.com/api/webhooks/1034598712/" + Token)]
    [InlineData("https://discord.com/api/webhooks/1034598712/" + Token + "?x=1")]
    public async Task An_address_that_differs_in_its_path_scheme_or_query_is_a_different_address(string other)
    {
        var (server, client) = await StartAsync();
        await using var stop = server;
        await CreatedIdAsync(client, "Discord", DiscordAddress);

        using var created = await CreateAsync(client, "Other", other);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task An_update_to_another_alerts_address_is_refused_and_to_its_own_is_not()
    {
        var (server, client) = await StartAsync();
        await using var stop = server;
        var first = await CreatedIdAsync(client, "First", DiscordAddress);
        var second = await CreatedIdAsync(client, "Second", "https://discord.com/api/webhooks/777/other-token");

        using var taken = await UpdateAsync(client, second, "https://Discord.com/api/webhooks/1034598712/" + Token + "/");
        using var own = await UpdateAsync(client, first, DiscordAddress);

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("An alert already sends to that address.", await Detail(taken));
        Assert.Equal("https://discord.com/api/webhooks/777/other-token", await SavedAddressAsync(server, second));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }
}
