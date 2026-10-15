using System.Net;
using System.Text.Json.Nodes;
using FsCheck.Fluent;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Processing;
using Weir.TestSupport;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// The two public inputs that take text from outside, fed generated and mutated copies of what Deluno recorded sending: the intake
/// webhook's hand-off and outcome bodies, and the library-folders call. Whatever is sent, the answer is one of the statuses the contract
/// documents, and the server is still answering afterwards.
/// </summary>
[ContractArea("media_managers")]
public sealed class IntakeFuzzTests
{
    private const int Sent = 60;

    private const string Webhook = $"{WeirClient.Api}/intake/webhook/deluno";

    /// <summary>
    /// The statuses the intake routes answer with (docs/deluno-weir-contract.md), a 409 for an outcome Weir cannot take yet, and the web server's own
    /// refusals of a request line or a header that is too long: never a 500, and never a 503 from a busy database.
    /// </summary>
    private static readonly HttpStatusCode[] Documented =
    [
        HttpStatusCode.OK,
        HttpStatusCode.BadRequest,
        HttpStatusCode.Unauthorized,
        HttpStatusCode.Forbidden,
        HttpStatusCode.NotFound,
        HttpStatusCode.Conflict,
        HttpStatusCode.UnprocessableEntity,
        HttpStatusCode.RequestUriTooLong,
        HttpStatusCode.RequestHeaderFieldsTooLarge,
    ];

    private static readonly string[] HandoffStates = ["handoff-single-file", "handoff-release-folder", "handoff-usenet-folder", "handoff-resend"];

    private static readonly string[] OutcomeStates = ["outcome-imported", "outcome-not-imported-removed", "outcome-imported-for-replaced-send"];

    [Fact]
    public async Task Hand_off_bodies_made_from_the_recordings_are_answered_with_a_documented_status()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        foreach (var recorded in HandoffStates.SelectMany(state => DelunoSide.Request(state).Exchanges).Select(exchange => exchange.Request))
        {
            var bodies = PropertyChecks.Samples(BodyMutations.Of((JsonObject)recorded.Body!), Sent, out var seed);
            foreach (var body in bodies)
            {
                var response = await SendAsync(world, recorded, body);
                AssertDocumented(response, seed, body.ToJsonString());
            }
        }

        await AssertServerAnswersAsync(world);
    }

    [Fact]
    public async Task Hand_off_text_that_is_not_json_or_not_an_object_is_answered_with_a_documented_status()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        using var client = world.Scenario.Server.CreateClient();
        var headers = Secret();
        foreach (var recorded in HandoffStates.SelectMany(state => DelunoSide.Request(state).Exchanges).Select(exchange => exchange.Request))
        {
            var texts = PropertyChecks.Samples(BodyMutations.AsBrokenText((JsonObject)recorded.Body!), Sent, out var seed);
            foreach (var text in texts)
            {
                AssertDocumented(await client.PostTextAsync(Webhook, text, headers), seed, text);
            }
        }

        foreach (var text in new[] { string.Empty, "null", "[]", "1", "\"text\"", "true", "{}", "[{}]", "{\"a\":", new string('[', 5000) })
        {
            AssertDocumented(await client.PostTextAsync(Webhook, text, headers), seed: 0, text.Length > 80 ? text[..80] : text);
        }

        await AssertServerAnswersAsync(world);
    }

    [Fact]
    public async Task Outcome_bodies_made_from_the_recordings_are_answered_with_a_documented_status()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var handoff = DelunoSide.Request("handoff-single-file").Only.Request;
        Assert.Equal(HttpStatusCode.OK, (await world.Replay.SendCallAsync(handoff)).Status);
        foreach (var recorded in OutcomeStates.SelectMany(state => DelunoSide.Request(state).Exchanges).Select(exchange => exchange.Request).Where(call => call.Body is JsonObject))
        {
            var bodies = PropertyChecks.Samples(BodyMutations.Of((JsonObject)recorded.Body!), Sent, out var seed);
            foreach (var body in bodies)
            {
                var response = await SendAsync(world, recorded, body);
                AssertDocumented(response, seed, body.ToJsonString());
            }
        }

        await AssertServerAnswersAsync(world);
    }

    [Fact]
    public async Task The_library_folders_call_is_answered_with_a_documented_status_whatever_its_secret_and_query_are()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        using var client = world.Scenario.Server.CreateClient();
        var secrets = Gen.OneOf(
            Gen.Elements(Scenario.WebhookSecret, Scenario.WebhookSecret + " ", " " + Scenario.WebhookSecret, string.Empty, " ", new string('s', 9000)),
            PathGenerators.Name.Where(name => name.All(char.IsAscii)));
        var queries = Gen.OneOf(
            Gen.Elements(string.Empty, "?", "?x", "?x=1&x=2", "?%00", "?a=%ZZ", "?libraryId=../../x", "?" + new string('q', 9000)),
            PathGenerators.Name.Select(name => $"?{Uri.EscapeDataString(name)}={Uri.EscapeDataString(name)}"));
        var calls = PropertyChecks.Samples(secrets.Zip(queries), Sent, out var seed);
        foreach (var (secret, query) in calls)
        {
            var response = await client.GetAsync(
                $"{WeirClient.Api}/intake/library-folders{query}", new Dictionary<string, string> { ["X-Webhook-Secret"] = secret });
            AssertDocumented(response, seed, $"secret of {secret.Length} characters, query {(query.Length > 80 ? query[..80] : query)}");
        }

        await AssertServerAnswersAsync(world);
    }

    /// <summary>
    /// The recorded call with <paramref name="body"/> in place of its own: the recorded headers (an idempotency key, say) and the secret, and
    /// the recorded ids and folders turned into this test's. The replayer is not used because it reads members of the body it expects.
    /// </summary>
    private static async Task<WeirResponse> SendAsync(RecordedWorld world, RecordedCall call, JsonObject body)
    {
        using var deluno = world.Scenario.Server.CreateClient();
        var headers = call.Headers
            .Where(header => header.Key is not ("x-webhook-secret" or "content-type"))
            .ToDictionary(header => header.Key, header => header.Value);
        headers["X-Webhook-Secret"] = Scenario.WebhookSecret;
        return await deluno.PostAsync(world.Translation.Apply(call.Path), world.Translation.Apply(body), headers);
    }

    private static Dictionary<string, string> Secret() => new() { ["X-Webhook-Secret"] = Scenario.WebhookSecret };

    private static void AssertDocumented(WeirResponse response, ulong seed, string sent) =>
        Assert.True(
            Documented.Contains(response.Status),
            $"Weir answered {(int)response.Status} to a body that none of the documented statuses covers (seed {seed}).{Environment.NewLine}" +
            $"Sent: {sent}{Environment.NewLine}Answer: {response.Text}");

    private static async Task AssertServerAnswersAsync(RecordedWorld world)
    {
        using var client = world.Scenario.Server.CreateClient();
        var ready = await client.GetAsync("/ready");
        Assert.True(ready.Status == HttpStatusCode.OK, ready.ToString());
    }
}
