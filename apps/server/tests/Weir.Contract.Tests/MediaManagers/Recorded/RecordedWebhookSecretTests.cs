using System.Net;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// The secret Deluno must present, replayed with Deluno's recorded hand-offs and polls. With the instance secret set (the world's
/// usual start) a hand-off with no secret or a wrong one is refused as Deluno recorded the refusal. Once a connection has a secret of
/// its own, shown once when Weir generates it, only the secrets of Deluno connections open the intake: the instance secret no longer
/// does, a replaced secret does not, and one connection's secret does not open the hand-offs of another. With no instance secret
/// and no connection secret Deluno's hand-off is taken unsigned, and the routes that reveal paths stay shut.
/// </summary>
[ContractArea("media_managers")]
public sealed class RecordedWebhookSecretTests
{
    private const string RefusedHandoffId = "5f6060a3-79fa-46cc-887a-1995972de4de";

    private static Replay ReplayWith(RecordedWorld world, string secret) =>
        new(() => world.Scenario.Server.CreateClient(), secret, world.Translation);

    private static RecordedCall StatusOf(string handoffId) =>
        DelunoSide.Request("status-ask-queued").Exchanges[0].Request with { Path = $"/api/v1/intake/handoffs/deluno/{handoffId}" };

    private static RecordedCall CancelOf(string handoffId) =>
        DelunoSide.Request("cancel-queued").Exchanges[0].Request with { Path = $"/api/v1/intake/handoffs/deluno/{handoffId}" };

    [Fact]
    public async Task With_the_instance_secret_set_a_hand_off_with_no_secret_or_a_wrong_one_is_refused_as_recorded_and_leaves_no_trace()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var refused = Recording.Load("handoff-refused-wrong-secret");

        await world.Replay.SendAsync(refused, Credential.None);
        await world.Replay.SendAsync(refused, Credential.Wrong);

        Assert.Empty(await RemuxJobs.ForHandoffAsync(world.Scenario.Admin, world.Translation.Id(RefusedHandoffId)));
    }

    [Fact]
    public async Task A_generated_secret_is_shown_once_opens_the_intake_alone_and_a_replaced_one_stops_working()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var admin = world.Scenario.Admin;
        var connectionId = await world.ConnectionIdAsync();
        var refused = Recording.Load("handoff-refused-wrong-secret");

        var generated = await ManagerConnections.GenerateSecretAsync(admin, connectionId);
        var secret = (string)generated["webhook_secret"]!;

        Assert.False(string.IsNullOrWhiteSpace(secret));
        Assert.Equal("/api/v1/intake/webhook/deluno", (string)generated["webhook_url_path"]!);
        Assert.Equal("X-Webhook-Secret", (string)generated["header_name"]!);
        var read = await admin.GetAsync($"{ManagerConnections.Route}/{connectionId}");
        Assert.True((bool)read.Fields["webhook_secret_is_set"]!);
        Assert.DoesNotContain(secret, read.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, (await admin.GetAsync(ManagerConnections.Route)).Text, StringComparison.Ordinal);

        // The instance secret no longer opens the intake, and neither does no secret or a wrong one, as Deluno recorded the refusal.
        await world.Replay.SendAsync(refused, Credential.Right);
        var own = ReplayWith(world, secret);
        await own.SendAsync(refused, Credential.None);
        await own.SendAsync(refused, Credential.Wrong);

        // The connection's own secret does, and Deluno's recorded hand-off is answered as recorded.
        var recorded = DelunoSide.Request("handoff-single-file");
        world.PutFilm(recorded.Only.Request.SourcePath(), needsWork: true);
        await own.SendAsync(recorded);

        // A new secret replaces it: the old one is refused and the new one is taken.
        var replaced = (string)(await ManagerConnections.GenerateSecretAsync(admin, connectionId))["webhook_secret"]!;
        Assert.NotEqual(secret, replaced);
        await own.SendAsync(refused, Credential.Right);
        var next = DelunoSide.HandoffLike("a-second-release-for-the-new-secret", "Gprecnewsecret.2020.1080p.WEB-DL.x264-GOLDEN.mkv");
        world.PutFilm(next.SourcePath(), needsWork: true);
        var accepted = await ReplayWith(world, replaced).SendCallAsync(next);
        Assert.True(accepted.Status == HttpStatusCode.OK, accepted.ToString());
    }

    [Fact]
    public async Task One_connections_secret_does_not_open_the_hand_offs_of_another()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var admin = world.Scenario.Admin;
        var firstId = await world.ConnectionIdAsync();
        var firstSecret = (string)(await ManagerConnections.GenerateSecretAsync(admin, firstId))["webhook_secret"]!;
        var second = await ManagerConnections.CreateAsync(admin, new System.Text.Json.Nodes.JsonObject { ["base_url"] = "http://192.0.2.77:5099", ["api_key"] = "k" });
        Assert.True(second.Status == HttpStatusCode.Created, second.ToString());
        var secondSecret = (string)(await ManagerConnections.GenerateSecretAsync(admin, JsonFields.Id(second.Fields)))["webhook_secret"]!;
        var release = world.HoldEveryPass();
        var handoff = DelunoSide.HandoffLike("a-hand-off-of-the-first-connection", "Gprecfirstconn.2020.1080p.WEB-DL.x264-GOLDEN.mkv");
        world.PutFilm(handoff.SourcePath(), needsWork: true);
        var first = ReplayWith(world, firstSecret);
        var other = ReplayWith(world, secondSecret);
        Assert.True((await first.SendCallAsync(handoff)).Status == HttpStatusCode.OK);
        var id = handoff.HandoffId();
        var refusal = Recording.Load("handoff-refused-wrong-secret").Only.Reply;

        // The second connection's secret is a good one for the intake, but this hand-off is the first connection's.
        var polled = await other.SendCallAsync(StatusOf(id));
        var cancelled = await other.SendCallAsync(CancelOf(id));

        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized), (polled.Status, cancelled.Status));
        Assert.Equal((string)refusal.Body!["detail"]!, (string)polled.Fields["detail"]!);
        Assert.Equal((string)refusal.Body["detail"]!, (string)cancelled.Fields["detail"]!);
        Assert.NotEqual("cancelled", await RemuxJobs.StatusForHandoffAsync(admin, id));
        Assert.Equal(HttpStatusCode.OK, (await first.SendCallAsync(StatusOf(id))).Status);
        await File.WriteAllTextAsync(release, string.Empty);
    }

    [Fact]
    public async Task With_no_instance_secret_and_none_on_the_connection_Deluno_is_taken_unsigned_and_the_routes_that_show_paths_stay_shut()
    {
        await using var world = await DelunoSide.StartWorldAsync((ManagerEnvironment.WebhookSecretVariable, string.Empty));
        var recorded = DelunoSide.Request("handoff-single-file");
        var hand = recorded.Only.Request;
        world.PutFilm(hand.SourcePath(), needsWork: true);

        await world.Replay.SendAsync(recorded, Credential.None);

        var connection = await world.Scenario.Admin.GetAsync($"{ManagerConnections.Route}/{await world.ConnectionIdAsync()}");
        Assert.False((bool)connection.Fields["webhook_secret_is_set"]!);
        Assert.False(string.IsNullOrEmpty((string?)connection.Fields["unsigned_webhook_warning"]));
        var polled = await world.Replay.SendCallAsync(StatusOf(hand.HandoffId()), Credential.None);
        Assert.Equal(HttpStatusCode.Forbidden, polled.Status);
        Assert.Equal("Set a webhook secret in Weir so a media manager can ask about hand-offs.", (string)polled.Fields["detail"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await world.Replay.SendCallAsync(CancelOf(hand.HandoffId()), Credential.None)).Status);
        await Poll.UntilAsync(
            async () => await RemuxJobs.StatusForHandoffAsync(world.Scenario.Admin, hand.HandoffId()) == "completed" ? "done" : null,
            "Weir to finish the unsigned hand-off");
    }
}
