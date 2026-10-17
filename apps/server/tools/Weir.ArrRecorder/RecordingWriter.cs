using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weir.ArrRecorder;

/// <summary>
/// Writes a session's exchanges as one recording file:
/// <c>{app, version, state, recordedAt, note, exchanges[{request{method, path, headers, body}, reply{status, headers, body | bodyText}}]}</c>,
/// the format of Deluno's recordings. Secrets are replaced with <c>&lt;scrubbed&gt;</c>, and so is a title's overview, which is free
/// text from a metadata site and plays no part in anything Weir reads.
/// </summary>
internal sealed class RecordingWriter
{
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
        var text = Scrub(document.ToJsonString(Options)).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
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
            request["body"] = WithoutOverviews(exchange.RequestBody.DeepClone());
        }

        var reply = new JsonObject { ["status"] = exchange.Status };
        if (exchange.ReplyHeaders.Count > 0)
        {
            reply["headers"] = HeadersOf(exchange.ReplyHeaders);
        }

        if (exchange.ReplyBody is not null)
        {
            reply["body"] = WithoutOverviews(exchange.ReplyBody.DeepClone());
        }
        else if (exchange.ReplyText is not null)
        {
            reply["bodyText"] = exchange.ReplyText;
        }

        return new JsonObject { ["request"] = request, ["reply"] = reply };
    }

    private static JsonObject HeadersOf(SortedDictionary<string, string> headers) =>
        new([.. headers.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))]);

    private string Scrub(string text)
    {
        foreach (var secret in _secrets)
        {
            text = text.Replace(secret, Scrubbed, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>The node with every string member named <c>overview</c> replaced.</summary>
    internal static JsonNode? WithoutOverviews(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject members:
                foreach (var name in members.Select(member => member.Key).ToList())
                {
                    if (name == "overview" && members[name] is JsonValue)
                    {
                        members[name] = Scrubbed;
                    }
                    else
                    {
                        members[name] = WithoutOverviews(members[name]);
                    }
                }

                break;
            case JsonArray items:
                for (var index = 0; index < items.Count; index++)
                {
                    items[index] = WithoutOverviews(items[index]);
                }

                break;
        }

        return node;
    }
}
