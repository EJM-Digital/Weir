using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Xunit.Sdk;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>Which secret Deluno had in the header when it made the call.</summary>
internal enum Credential
{
    /// <summary>The shared secret Weir was given.</summary>
    Right,

    /// <summary>A secret Weir does not know.</summary>
    Wrong,

    /// <summary>No secret header at all.</summary>
    None,
}

/// <summary>
/// Sends the request of a recorded exchange to a running Weir and holds Weir's reply to the one Deluno recorded: the status, the
/// kind of body, and every field of it. A reply that differs fails with the recording's file and the path of each field that changed.
/// </summary>
internal sealed class Replay(Func<WeirClient> createClient, string secret, Translation translation)
{
    public Translation Translation => translation;

    /// <summary>Replays the recording's only exchange.</summary>
    public Task<WeirResponse> SendAsync(Recording recording, params ExpectedDifference[] pins) =>
        SendAsync(recording, recording.Only, Credential.Right, pins);

    /// <summary>Replays one exchange of the recording.</summary>
    public Task<WeirResponse> SendAsync(Recording recording, int exchange, params ExpectedDifference[] pins) =>
        SendAsync(recording, recording.Exchanges[exchange], Credential.Right, pins);

    /// <summary>Replays the recording's only exchange with the secret header the recording's state says Deluno sent.</summary>
    public Task<WeirResponse> SendAsync(Recording recording, Credential credential) =>
        SendAsync(recording, recording.Only, credential, []);

    /// <summary>
    /// Sends a call Deluno makes the same way as a recorded one but for a hand-off the recording does not cover (<see cref="DelunoSide.HandoffLike"/>),
    /// and returns Weir's answer without holding it to a recording.
    /// </summary>
    public async Task<WeirResponse> SendCallAsync(RecordedCall call, Credential credential = Credential.Right)
    {
        using var deluno = createClient();
        return await deluno.RequestAsync(new HttpMethod(call.Method), translation.Apply(call.Path), Request(call.Body), Headers(credential, call));
    }

    private async Task<WeirResponse> SendAsync(
        Recording recording, RecordedExchange exchange, Credential credential, IReadOnlyList<ExpectedDifference> pins)
    {
        var call = exchange.Request;
        var response = await SendCallAsync(call, credential);
        var differences = Differences(exchange.Reply, response, pins);
        if (differences.Count > 0)
        {
            var index = recording.Exchanges.ToList().IndexOf(exchange);
            throw new XunitException(
                $"Weir no longer answers as Deluno recorded.{Environment.NewLine}" +
                $"  recording: {recording.File}, exchange {index}: {call.Method} {call.Path}{Environment.NewLine}" +
                string.Join(Environment.NewLine, differences.Select(difference => "  " + difference)));
        }

        return response;
    }

    private JsonNode? Request(JsonNode? recorded)
    {
        var body = translation.Apply(recorded);
        if (body is JsonObject fields && fields.ContainsKey("occurredUtc"))
        {
            // When Deluno made its report is the time it made it; a replay reports now, written the way the recording wrote it
            // (rc.13 wrote a Z, Deluno rc.30.30 writes +00:00).
            var written = (string?)fields["occurredUtc"] ?? string.Empty;
            fields["occurredUtc"] = DateTimeOffset.UtcNow.ToString(
                written.EndsWith('Z') ? "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'" : "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);
        }

        return body;
    }

    /// <summary>
    /// The secret as <paramref name="credential"/> says, and the headers the recording kept besides the scrubbed secret and the body's
    /// own content type (an Idempotency-Key, say), exactly as recorded.
    /// </summary>
    private Dictionary<string, string> Headers(Credential credential, RecordedCall call)
    {
        var headers = call.Headers
            .Where(header => header.Key is not ("x-webhook-secret" or "content-type"))
            .ToDictionary(header => header.Key, header => header.Value);
        switch (credential)
        {
            case Credential.Right:
                headers["X-Webhook-Secret"] = secret;
                break;
            case Credential.Wrong:
                headers["X-Webhook-Secret"] = secret + "-not";
                break;
        }

        return headers;
    }

    private List<string> Differences(RecordedAnswer recorded, WeirResponse actual, IReadOnlyList<ExpectedDifference> pins)
    {
        var differences = new List<string>();
        if ((int)actual.Status != recorded.Status)
        {
            differences.Add($"reply.status: recorded {recorded.Status}, Weir answered {(int)actual.Status}: {actual.Text}");
            return differences;
        }

        if (recorded.Body is null)
        {
            if (!string.IsNullOrEmpty(recorded.BodyText ?? string.Empty) && actual.Text != recorded.BodyText)
            {
                differences.Add($"reply.bodyText: recorded \"{recorded.BodyText}\", Weir answered \"{actual.Text}\"");
            }

            if (recorded.BodyText is "" && actual.Text.Length > 0)
            {
                differences.Add($"reply.bodyText: recorded no body, Weir answered \"{actual.Text}\"");
            }

            return differences;
        }

        var contentType = actual.Header("Content-Type") is { } header ? MediaTypeHeaderValue.Parse(header).MediaType : null;
        if (contentType != recorded.ContentType)
        {
            differences.Add($"reply.headers.content-type: recorded {recorded.ContentType}, Weir answered {contentType}");
        }

        differences.AddRange(Comparison.Differences(
            translation.Apply(recorded.Body), actual.Json, Comparison.Usual, pins)
            .Select(difference => "reply.body" + difference[1..]));
        return differences;
    }
}
