using System.Text;
using System.Text.Json.Nodes;

namespace Weir.Contract.Tests.Harness.Fakes;

/// <summary>
/// A scripted answer: a status, an optional JSON body or plain text, optional extra headers. No body and no text means no payload at all.
/// </summary>
public sealed record Reply(int Status = 200, JsonNode? Body = null, IReadOnlyDictionary<string, string>? Headers = null, string? Text = null)
{
    /// <summary>200 with this JSON body.</summary>
    public static Reply Ok(JsonNode? body) => new(200, body);

    internal HttpAnswer ToAnswer() => Body is not null
        ? new HttpAnswer(Status, Encoding.UTF8.GetBytes(Body.ToJsonString()), "application/json", Headers)
        : Text is { Length: > 0 }
            ? new HttpAnswer(Status, Encoding.UTF8.GetBytes(Text), "text/plain", Headers)
            : new HttpAnswer(Status, [], Headers: Headers);
}
