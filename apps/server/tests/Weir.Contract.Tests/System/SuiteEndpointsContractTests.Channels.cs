using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.SystemArea.SystemPartBJson;

namespace Weir.Contract.Tests.SystemArea;

public sealed partial class SuiteEndpointsContractTests
{
    private const string Channels = SystemPartBHelpers.Api + "/suite/notification-channels";

    // RFC 6761: .invalid never resolves, so a test notification can never leave the machine.
    private const string UnreachableHook = "https://weir-contract-test.invalid/hook";
    private const string PublicLookingHook = "https://hooks.weir-contract-test.invalid/other";

    // What the API shows of an address: its token is never returned.
    private const string MaskedUnreachableHook = "https://weir-contract-test.invalid/••••";
    private const string MaskedPublicLookingHook = "https://hooks.weir-contract-test.invalid/••••";

    // --- /suite/notification-channels ----------------------------------------------------------

    [Fact]
    public async Task Notification_channels_require_auth()
    {
        using var client = Server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Channels)).Status);
    }

    [Fact]
    public async Task Notification_channels_forbidden_for_viewer()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(Server);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(Channels)).Status);
        var created = await viewer.PostWithCsrfAsync(
            Channels, new JsonObject { ["label"] = "x", ["provider"] = "webhook", ["url"] = UnreachableHook });
        Assert.Equal(HttpStatusCode.Forbidden, created.Status);
    }

    [Fact]
    public async Task Notification_channels_list_shape()
    {
        using var admin = await Server.CreateAdminClientAsync();

        var response = await admin.GetAsync(Channels);

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        var body = response.Fields;
        AssertKeys(["items", "supported_events", "supported_providers"], body);
        Assert.IsType<JsonArray>(body["items"]);
        Assert.Contains("job_failed", Strings(body["supported_events"]));
        Assert.True(Strings(body["supported_providers"]).ToHashSet().IsSupersetOf(["webhook", "discord"]));
    }

    [Fact]
    public async Task Notification_channel_create_update_test_delete()
    {
        using var admin = await Server.CreateAdminClientAsync();
        var created = await admin.PostWithCsrfAsync(
            Channels,
            new JsonObject
            {
                ["label"] = "  Contract hook  ",
                ["provider"] = "webhook",
                ["url"] = UnreachableHook,
                ["events"] = ArrayOf("job_failed"),
            });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        var channel = created.Fields;
        AssertKeys(["id", "label", "provider", "url", "has_token", "events", "enabled", "created_at", "updated_at"], channel);
        Assert.Equal("Contract hook", (string?)channel["label"]);
        Assert.Equal("webhook", (string?)channel["provider"]);
        Assert.Equal(MaskedUnreachableHook, (string?)channel["url"]);
        Assert.True((bool)channel["has_token"]!);
        Assert.Equal(["job_failed"], Strings(channel["events"]));
        Assert.True((bool)channel["enabled"]!);
        var id = (long)channel["id"]!;
        var path = $"{Channels}/{id}";

        var listed = (await admin.GetAsync(Channels)).Fields["items"]!.AsArray();
        Assert.Contains(id, listed.Select(item => (long)item!["id"]!));

        var updated = await admin.PutWithCsrfAsync(
            path,
            new JsonObject
            {
                ["label"] = "Renamed hook",
                ["provider"] = "discord",
                ["url"] = PublicLookingHook,
                ["events"] = ArrayOf("job_completed", "job_failed"),
                ["enabled"] = false,
            });
        Assert.True(updated.Status == HttpStatusCode.OK, updated.ToString());
        Assert.Equal("Renamed hook", (string?)updated.Fields["label"]);
        Assert.Equal("discord", (string?)updated.Fields["provider"]);
        Assert.Equal(MaskedPublicLookingHook, (string?)updated.Fields["url"]);
        Assert.Equal(["job_completed", "job_failed"], Strings(updated.Fields["events"]));
        Assert.False((bool)updated.Fields["enabled"]!);

        var tested = await admin.PostWithCsrfAsync($"{path}/test", new JsonObject());
        Assert.True(tested.Status == HttpStatusCode.OK, tested.ToString());
        AssertKeys(["ok", "error"], tested.Fields);
        Assert.False((bool)tested.Fields["ok"]!);
        Assert.True(IsString(tested.Fields["error"]) && IsTruthy(tested.Fields["error"]));

        var deleted = await admin.DeleteWithCsrfAsync(path);
        Assert.True(deleted.Status == HttpStatusCode.NoContent, deleted.ToString());
        var remaining = (await admin.GetAsync(Channels)).Fields["items"]!.AsArray();
        Assert.DoesNotContain(id, remaining.Select(item => (long)item!["id"]!));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteWithCsrfAsync(path)).Status);
    }

    [Fact]
    public async Task Notification_channel_update_without_an_address_keeps_the_saved_one()
    {
        using var admin = await Server.CreateAdminClientAsync();
        var created = await admin.PostWithCsrfAsync(
            Channels,
            new JsonObject { ["label"] = "Keeps its address", ["provider"] = "webhook", ["url"] = "https://weir-contract-test.invalid/keeps/secret-token", ["events"] = ArrayOf("job_failed") });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        var path = $"{Channels}/{(long)created.Fields["id"]!}";
        try
        {
            var updated = await admin.PutWithCsrfAsync(
                path, new JsonObject { ["label"] = "Renamed", ["provider"] = "webhook", ["events"] = ArrayOf("job_failed") });

            Assert.True(updated.Status == HttpStatusCode.OK, updated.ToString());
            Assert.Equal("https://weir-contract-test.invalid/keeps/••••", (string?)updated.Fields["url"]);
            Assert.DoesNotContain("secret-token", updated.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-token", (await admin.GetAsync(Channels)).Text, StringComparison.Ordinal);
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteWithCsrfAsync(path)).Status);
        }
    }

    [Fact]
    public async Task Notification_channel_create_refuses_an_address_another_alert_already_uses()
    {
        using var admin = await Server.CreateAdminClientAsync();
        var body = new JsonObject { ["label"] = "First", ["provider"] = "webhook", ["url"] = "https://weir-contract-test.invalid/twice", ["events"] = ArrayOf("job_failed") };
        var first = await admin.PostWithCsrfAsync(Channels, body);
        Assert.True(first.Status == HttpStatusCode.Created, first.ToString());
        var path = $"{Channels}/{(long)first.Fields["id"]!}";
        try
        {
            var second = await admin.PostWithCsrfAsync(Channels, With(body, "url", "HTTPS://Weir-Contract-Test.INVALID/twice/"));

            Assert.Equal(HttpStatusCode.Conflict, second.Status);
            Assert.Equal("An alert already sends to that address.", (string?)second.Fields["detail"]);
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteWithCsrfAsync(path)).Status);
        }
    }

    [Fact]
    public async Task Notification_channel_create_rejects_bad_input()
    {
        using var admin = await Server.CreateAdminClientAsync();
        var good = new JsonObject
        {
            ["label"] = "x",
            ["provider"] = "webhook",
            ["url"] = UnreachableHook,
            ["events"] = ArrayOf("job_failed"),
        };

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsync(Channels, good)).Status); // no csrf_token
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostWithCsrfAsync(Channels, With(good, "provider", "carrier-pigeon"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostWithCsrfAsync(Channels, With(good, "url", "http://127.0.0.1:8080/hook"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostWithCsrfAsync(Channels, With(good, "url", "ftp://example.invalid/hook"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostWithCsrfAsync(Channels, With(good, "events", ArrayOf("not_an_event")))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostWithCsrfAsync(Channels, With(good, "events", ArrayOf()))).Status);
    }

    [Fact]
    public async Task Notification_channel_missing_ids_are_404()
    {
        using var admin = await Server.CreateAdminClientAsync();
        var body = new JsonObject
        {
            ["label"] = "x",
            ["provider"] = "webhook",
            ["url"] = UnreachableHook,
            ["events"] = ArrayOf("job_failed"),
        };

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutWithCsrfAsync($"{Channels}/999999", body)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostWithCsrfAsync($"{Channels}/999999/test", new JsonObject())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteWithCsrfAsync($"{Channels}/999999")).Status);
    }

    [Fact]
    public async Task Notification_channel_delete_without_csrf_header_is_refused()
    {
        using var admin = await Server.CreateAdminClientAsync();
        var created = await admin.PostWithCsrfAsync(
            Channels,
            new JsonObject
            {
                ["label"] = "Keep me",
                ["provider"] = "webhook",
                ["url"] = UnreachableHook,
                ["events"] = ArrayOf("job_failed"),
            });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        var path = $"{Channels}/{(long)created.Fields["id"]!}";
        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync(path)).Status);
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteWithCsrfAsync(path)).Status);
        }
    }
}
