using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weir.ArrRecorder;

/// <summary>
/// Writes a session's exchanges as one recording file:
/// <c>{app, version, state, recordedAt, note, exchanges[{request{method, path, headers, body}, reply{status, headers, body | bodyText}}]}</c>,
/// the format of Deluno's recordings. Secrets are replaced with <c>&lt;scrubbed&gt;</c>, and so is a title's overview, which is free
/// text from a metadata site and plays no part in anything Weir reads.
/// </summary>
internal sealed partial class RecordingWriter
{
    private const int MinimumSecretLength = 8;

    private static readonly string[] CookieAttributes = ["expires", "path", "domain", "max-age", "samesite", "secure", "httponly"];

    public const string Scrubbed = "<scrubbed>";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _app;
    private readonly string _version;
    private readonly string _folder;
    private readonly List<string> _secrets;

    public RecordingWriter(string app, string version, string outputFolder, IEnumerable<string> secrets)
    {
        _app = app;
        _version = version;
        _folder = Path.Combine(outputFolder, app, version);
        _secrets = [.. secrets.Where(secret => secret.Length > 0)];
    }

    public string Folder => _folder;

    public string Write(string state, string note, IReadOnlyList<Exchange> exchanges)
    {
        var document = new JsonObject
        {
            ["app"] = _app,
            ["version"] = _version,
            ["state"] = state,
            ["recordedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["note"] = note,
            ["exchanges"] = new JsonArray([.. exchanges.Select(ExchangeOf)]),
        };
        var text = Scrub(document.ToJsonString(Options), [.. _secrets, .. SessionSecrets(exchanges)]).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, state + ".json");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static JsonObject ExchangeOf(Exchange exchange)
    {
        var request = new JsonObject { ["method"] = exchange.Method, ["path"] = exchange.Path };
        if (exchange.RequestHeaders.Count > 0)
        {
            request["headers"] = HeadersOf(exchange.RequestHeaders);
        }

        if (exchange.RequestBody is not null)
        {
            request["body"] = WithoutOverviews(exchange.RequestBody);
        }
        else if (exchange.RequestText is not null)
        {
            request["bodyText"] = exchange.RequestText;
        }

        var reply = new JsonObject { ["status"] = exchange.Status };
        if (exchange.ReplyHeaders.Count > 0)
        {
            reply["headers"] = HeadersOf(exchange.ReplyHeaders);
        }

        if (exchange.ReplyBody is not null)
        {
            reply["body"] = WithoutOverviews(exchange.ReplyBody);
        }
        else if (exchange.ReplyText is not null)
        {
            reply["bodyText"] = exchange.ReplyText;
        }

        return new JsonObject { ["request"] = request, ["reply"] = reply };
    }

    private static JsonObject HeadersOf(SortedDictionary<string, string> headers) =>
        new([.. headers.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))]);

    /// <summary>
    /// The secrets a session carries in its headers, which only the client knows until it says them: the values of the cookies it sets and
    /// sends back, a session id, the token of a <c>Basic</c> or <c>Bearer</c> sign-in. A value too short to be one is left alone, so it
    /// cannot take every occurrence of an ordinary word with it.
    /// </summary>
    internal static IEnumerable<string> SessionSecrets(IEnumerable<Exchange> exchanges)
    {
        foreach (var exchange in exchanges)
        {
            foreach (var (name, value) in exchange.RequestHeaders.Concat(exchange.ReplyHeaders))
            {
                switch (name)
                {
                    case "set-cookie" or "cookie":
                        foreach (var secret in CookieValues(value))
                        {
                            yield return secret;
                        }

                        break;
                    case "x-transmission-session-id":
                        yield return value;
                        break;
                    case "authorization":
                        yield return value[(value.IndexOf(' ', StringComparison.Ordinal) + 1)..];
                        break;
                }
            }
        }
    }

    [GeneratedRegex(@"(?<=^|[,;]\s*)(?<name>[A-Za-z0-9_.\-]+)=(?<value>[^;,\s]+)")]
    private static partial Regex CookiePair();

    private static IEnumerable<string> CookieValues(string header) =>
        CookiePair().Matches(header)
            .Where(pair => !CookieAttributes.Contains(pair.Groups["name"].Value, StringComparer.OrdinalIgnoreCase))
            .Select(pair => pair.Groups["value"].Value)
            .Where(secret => secret.Length >= MinimumSecretLength);

    private static string Scrub(string text, IEnumerable<string> secrets)
    {
        foreach (var secret in secrets.Where(secret => secret.Length > 0).OrderByDescending(secret => secret.Length))
        {
            text = text.Replace(secret, Scrubbed, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>A copy of the node with every string member named <c>overview</c> replaced.</summary>
    internal static JsonNode? WithoutOverviews(JsonNode? node) => node switch
    {
        JsonObject members => new JsonObject([.. members.Select(member => KeyValuePair.Create(
            member.Key,
            member.Key == "overview" && member.Value is JsonValue ? (JsonNode?)Scrubbed : WithoutOverviews(member.Value)))]),
        JsonArray items => new JsonArray([.. items.Select(WithoutOverviews)]),
        JsonValue value => value.DeepClone(),
        _ => null,
    };
}
