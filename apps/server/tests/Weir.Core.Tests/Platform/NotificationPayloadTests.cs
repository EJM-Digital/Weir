using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Weir.Core.Notifications;
using Weir.Core.Time;

namespace Weir.Core.Tests.Platform;

/// <summary>
/// What an alert looks like on the wire. A Discord channel gets one embed in Discord's documented webhook shape (an embed's colour is a
/// whole number, its fields are name, value and inline); any other channel gets the flat JSON body Weir documents.
/// </summary>
public sealed class NotificationPayloadTests
{
    private const int Green = 0x2ECC71;
    private const int Red = 0xE74C3C;

    private static readonly Timestamp Now = Timestamp.UtcNow(new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 8, 30, 15, TimeSpan.Zero)));

    private static JsonObject Parse(byte[] body) => JsonNode.Parse(Encoding.UTF8.GetString(body))!.AsObject();

    [Fact]
    public void A_discord_alert_is_one_embed_and_nothing_else()
    {
        var body = Parse(NotificationRules.DiscordPayload(
            "Weir on RIG job completed", "Job 41 (processing.file.remux_pass.v1) finished successfully.", "processing_job_completed", "processing", 41, Now, "Weir on RIG"));

        Assert.Equal(["embeds"], body.Select(member => member.Key));
        var embed = Assert.Single(body["embeds"]!.AsArray())!.AsObject();
        Assert.Equal(["title", "description", "color", "fields", "footer", "timestamp"], embed.Select(member => member.Key));
        Assert.Equal("Weir on RIG job completed", (string?)embed["title"]);
        Assert.Equal("Job 41 (processing.file.remux_pass.v1) finished successfully.", (string?)embed["description"]);
        Assert.Equal("Weir on RIG", (string?)embed["footer"]!["text"]);
        Assert.Equal(["text"], embed["footer"]!.AsObject().Select(member => member.Key));
        Assert.Equal(Now.IsoFormat(), (string?)embed["timestamp"]);
    }

    [Fact]
    public void A_discord_alert_names_the_module_and_the_job_in_two_inline_fields()
    {
        var embed = Parse(NotificationRules.DiscordPayload("t", "d", "processing_job_completed", "processing", 41, Now, "Weir"))["embeds"]![0]!.AsObject();

        var fields = embed["fields"]!.AsArray();
        Assert.Equal(2, fields.Count);
        Assert.Equal(["name", "value", "inline"], fields[0]!.AsObject().Select(member => member.Key));
        Assert.Equal(("Module", "processing", true), ((string?)fields[0]!["name"], (string?)fields[0]!["value"], (bool?)fields[0]!["inline"]));
        Assert.Equal(("Job ID", "41", true), ((string?)fields[1]!["name"], (string?)fields[1]!["value"], (bool?)fields[1]!["inline"]));
    }

    [Theory]
    [InlineData("processing_job_completed", Green)]
    [InlineData("job_completed", Green)]
    [InlineData("processing_job_failed", Red)]
    [InlineData("system_job_failed", Red)]
    public void A_discord_alert_is_green_when_a_job_completed_and_red_otherwise(string jobEvent, int colour)
    {
        var embed = Parse(NotificationRules.DiscordPayload("t", "d", jobEvent, "processing", 1, Now, "Weir"))["embeds"]![0]!;

        Assert.Equal(colour, (int?)embed["color"]);
    }

    [Fact]
    public void A_webhook_alert_is_one_flat_object_with_the_documented_members()
    {
        var body = Parse(NotificationRules.WebhookPayload(
            "processing_job_failed", "processing", 41, "processing.file.remux_pass.v1", "Weir on RIG job failed", "Job 41 failed.", Now, "RIG"));

        Assert.Equal(
            ["event", "module", "job_id", "job_kind", "title", "detail", "timestamp", "app", "machine_name"],
            body.Select(member => member.Key));
        Assert.Equal("processing_job_failed", (string?)body["event"]);
        Assert.Equal(41, (long?)body["job_id"]);
        Assert.Equal("processing.file.remux_pass.v1", (string?)body["job_kind"]);
        Assert.Equal("Weir", (string?)body["app"]);
        Assert.Equal("RIG", (string?)body["machine_name"]);
        Assert.Equal(Now.IsoFormat(), (string?)body["timestamp"]);
    }

    [Fact]
    public void The_two_shapes_share_no_top_level_member()
    {
        var discord = Parse(NotificationRules.DiscordPayload("t", "d", "job_failed", "processing", 1, Now, "Weir"));
        var webhook = Parse(NotificationRules.WebhookPayload("job_failed", "processing", 1, "k", "t", "d", Now, "RIG"));

        Assert.Empty(discord.Select(member => member.Key).Intersect(webhook.Select(member => member.Key)));
    }
}
