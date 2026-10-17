using System.Text.Json.Nodes;

namespace Weir.Contract.Tests.Harness;

/// <summary>What a request needs from the world it is sent into: the real library, file, path, size and time that stand in for the web tests' stand-ins.</summary>
internal sealed record WebSample(
    long? LibraryId = null,
    long? FileId = null,
    string? Path = null,
    string? LibraryFolder = null,
    long? SizeBytes = null,
    JsonNode? ModifiedAt = null);

/// <summary>One request the web app sends, ready to send: its method, its address and its body without the token.</summary>
internal sealed record WebRequest(string Name, HttpMethod Method, string Path, JsonObject Body)
{
    /// <summary>Sends it as the web app does: the token of <paramref name="client"/>'s session goes into the body.</summary>
    public async Task<WeirResponse> SendAsync(WeirClient client)
    {
        var body = (JsonObject)Body.DeepClone();
        body["csrf_token"] = await client.CsrfTokenAsync();
        return await client.RequestAsync(Method, Path, body);
    }

    /// <summary>The same request with one field changed, for the refusal of a body the web app never builds (a confirmation turned off).</summary>
    public WebRequest With(string field, JsonNode? value)
    {
        var body = (JsonObject)Body.DeepClone();
        body[field] = value;
        return this with { Body = body };
    }
}

/// <summary>
/// The requests the web app sends that can delete a file or switch on a clean, as the web tests built them
/// (<c>fixtures/web-bodies/destructive-requests.json</c>, written by <c>npm run web-bodies:write</c> in <c>apps/web</c> and checked against the
/// web app's own request code on every run). A test never writes one of these bodies by hand: it names the request and says which real
/// library, file, path, size and time stand in for the web tests' stand-ins.
/// </summary>
internal static class WebBodies
{
    private const string Fixture = "fixtures/web-bodies/destructive-requests.json";

    // The stand-ins apps/web/src/test/wire/destructive-requests.ts builds the requests with.
    private const long LibraryStandIn = 7001;
    private const long FileStandIn = 7002;
    private const long SizeStandIn = 7003;
    private const string PathStandIn = "{path}";
    private const string LibraryFolderStandIn = "{library_folder}";
    private const string ModifiedAtStandIn = "{modified_at}";

    private static readonly Lazy<IReadOnlyDictionary<string, JsonObject>> Requests = new(Load);

    /// <summary>Every request the fixture holds.</summary>
    public static IReadOnlyCollection<string> Names => [.. Requests.Value.Keys];

    /// <summary>The request called <paramref name="name"/> in the fixture, sent into the world <paramref name="sample"/> describes.</summary>
    public static WebRequest Get(string name, WebSample sample)
    {
        if (!Requests.Value.TryGetValue(name, out var recorded))
        {
            throw new ArgumentException($"The web app sends no request called '{name}'. It sends: {string.Join(", ", Requests.Value.Keys)}.");
        }

        var path = Swap(Swap((string)recorded["path"]!, "/libraries/", LibraryStandIn, sample.LibraryId), "/files/", FileStandIn, sample.FileId);
        var body = (JsonObject)Replace(recorded["body"]!, sample)!;
        return new WebRequest(name, new HttpMethod((string)recorded["method"]!), path, body);
    }

    private static string Swap(string path, string segment, long standIn, long? real)
    {
        var standInSegment = segment + standIn;
        if (!path.Contains(standInSegment, StringComparison.Ordinal))
        {
            return path;
        }

        return real is { } value
            ? path.Replace(standInSegment, segment + value, StringComparison.Ordinal)
            : throw new ArgumentException($"The request is sent to {path}, which needs the real id of {segment.Trim('/')}.");
    }

    private static JsonNode? Replace(JsonNode? node, WebSample sample)
    {
        switch (node)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (name, value) in obj)
                {
                    copy[name] = Replace(value, sample);
                }

                return copy;
            case JsonArray array:
                return new JsonArray([.. array.Select(item => Replace(item, sample))]);
            case JsonValue value when value.TryGetValue<string>(out var text):
                return text switch
                {
                    PathStandIn => JsonValue.Create(sample.Path ?? throw Missing(text)),
                    LibraryFolderStandIn => JsonValue.Create(sample.LibraryFolder ?? throw Missing(text)),
                    ModifiedAtStandIn => (sample.ModifiedAt ?? throw Missing(text)).DeepClone(),
                    _ when text.StartsWith('{') && text.EndsWith('}') => throw Missing(text),
                    _ => value.DeepClone(),
                };
            case JsonValue value when value.TryGetValue<long>(out var number) && number == SizeStandIn:
                return JsonValue.Create(sample.SizeBytes ?? throw Missing("a size in bytes"));
            default:
                return node?.DeepClone();
        }
    }

    private static ArgumentException Missing(string what) => new($"The request needs {what}, and the sample gives none.");

    private static Dictionary<string, JsonObject> Load()
    {
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, Fixture);
        Assert.True(File.Exists(file), $"The web tests' requests are not beside the tests: {file}");
        var all = JsonNode.Parse(File.ReadAllText(file))!.AsArray();
        return all.Select(entry => entry!.AsObject()).ToDictionary(entry => (string)entry["name"]!, StringComparer.Ordinal);
    }
}
