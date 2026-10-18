using System.Text.Json.Nodes;
using Xunit.Sdk;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>What was sent in one recorded exchange. A request with no body has a null <see cref="Body"/>; <see cref="Headers"/> are lower-case names, empty when the recorder kept none.</summary>
internal sealed record RecordedCall(string Method, string Path, JsonNode? Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>What was answered. A JSON reply is in <see cref="Body"/>, any other in <see cref="BodyText"/>; <see cref="Headers"/> are lower-case names.</summary>
internal sealed record RecordedAnswer(int Status, string? ContentType, JsonNode? Body, string? BodyText, IReadOnlyDictionary<string, string> Headers);

internal sealed record RecordedExchange(RecordedCall Request, RecordedAnswer Reply);

/// <summary>
/// One recording made of the talk between Deluno and Weir, loaded as it is. A test never writes a request or a reply by hand: a state
/// nobody recorded fails with where to record it. Two sets exist: Deluno's recordings of Weir's real replies
/// (<c>fixtures/recorded/deluno/&lt;version&gt;/&lt;state&gt;.json</c>, <see cref="Load"/>) and Deluno's recordings of its own side
/// (<c>fixtures/recorded/deluno-side/&lt;version&gt;/&lt;state&gt;.json</c>, <see cref="LoadDelunoSide"/>).
/// </summary>
internal sealed class Recording
{
    /// <summary>The Weir version Deluno recorded the replies from.</summary>
    public const string Version = "1.0.0-rc.13";

    /// <summary>The Deluno version whose requests to Weir, and whose reports from Weir, were recorded.</summary>
    public const string DelunoSideRequestsVersion = "1.0.0-rc.30.30";

    /// <summary>The Deluno version whose own replies to Weir were recorded, in the server code.</summary>
    public const string DelunoSideRepliesVersion = "1.0.0-rc.30.32";

    /// <summary>
    /// The Deluno version whose talk with Weir was recorded in Compose by the scenario shard (<c>scripts/scenarios-ci/with-deluno</c>), with a
    /// recording proxy between the real Deluno and the real Weir: every state in it is named <c>real-*</c>.
    /// </summary>
    public const string DelunoSideRealStackVersion = "1.0.0-rc.30.33";

    private const string WeirRecordedByDeluno = "fixtures/recorded/deluno/" + Version;

    private Recording(string folder, string state, string note, IReadOnlyList<RecordedExchange> exchanges)
    {
        Folder = folder;
        State = state;
        Note = note;
        Exchanges = exchanges;
    }

    public string State { get; }

    /// <summary>What was done to reach this state, in the recorder's own words.</summary>
    public string Note { get; }

    public IReadOnlyList<RecordedExchange> Exchanges { get; }

    private string Folder { get; }

    /// <summary>The file as the repository holds it, which is what a failure names.</summary>
    public string File => $"apps/server/tests/{Folder}/{State}.json";

    public RecordedExchange Only => Exchanges.Count == 1
        ? Exchanges[0]
        : throw new InvalidOperationException($"{File} holds {Exchanges.Count} exchanges; name the one to replay.");

    /// <summary>Deluno's recording of what Weir answered (<c>app</c> is weir).</summary>
    public static Recording Load(string state) => Read(WeirRecordedByDeluno, "weir", Version, state);

    /// <summary>Deluno's recording of its own side (<c>app</c> is deluno): the rc.30.30 requests and reports, or the rc.30.32 replies.</summary>
    public static Recording LoadDelunoSide(string version, string state) =>
        Read($"fixtures/recorded/deluno-side/{version}", "deluno", version, state);

    private static Recording Read(string folder, string app, string version, string state)
    {
        var path = Path.Combine(AppContext.BaseDirectory, folder, $"{state}.json");
        if (!System.IO.File.Exists(path))
        {
            throw new XunitException(
                $"Nobody recorded '{state}' (looked for {folder}/{state}.json). Do not write the request or the reply by hand: " +
                "say it is missing in the README beside the recordings, and ask Deluno to record it.");
        }

        var document = JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject();
        Assert.Equal(app, (string)document["app"]!);
        Assert.Equal(version, (string)document["version"]!);
        Assert.Equal(state, (string)document["state"]!);
        var exchanges = document["exchanges"]!.AsArray().Select(exchange => ReadExchange(exchange!.AsObject())).ToList();
        return new Recording(folder, state, (string)document["note"]!, exchanges);
    }

    private static RecordedExchange ReadExchange(JsonObject exchange)
    {
        var request = exchange["request"]!.AsObject();
        var reply = exchange["reply"]!.AsObject();
        var replyHeaders = Headers(reply);
        return new RecordedExchange(
            new RecordedCall((string)request["method"]!, (string)request["path"]!, request["body"]?.DeepClone(), Headers(request)),
            new RecordedAnswer(
                (int)reply["status"]!, replyHeaders.GetValueOrDefault("content-type"), reply["body"]?.DeepClone(), (string?)reply["bodyText"], replyHeaders));
    }

    private static Dictionary<string, string> Headers(JsonObject message) =>
        message["headers"] is JsonObject headers
            ? headers.ToDictionary(header => header.Key.ToLowerInvariant(), header => (string)header.Value!)
            : [];
}
