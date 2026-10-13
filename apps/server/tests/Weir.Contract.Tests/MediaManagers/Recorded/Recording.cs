using System.Text.Json.Nodes;
using Xunit.Sdk;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>What Deluno sent Weir in one recorded exchange. A request with no body has a null <see cref="Body"/>.</summary>
internal sealed record RecordedCall(string Method, string Path, JsonNode? Body);

/// <summary>What Weir answered. A JSON reply is in <see cref="Body"/>, any other in <see cref="BodyText"/>.</summary>
internal sealed record RecordedAnswer(int Status, string? ContentType, JsonNode? Body, string? BodyText);

internal sealed record RecordedExchange(RecordedCall Request, RecordedAnswer Reply);

/// <summary>
/// One recording Deluno made of Weir's real replies (<c>fixtures/recorded/deluno/&lt;version&gt;/&lt;state&gt;.json</c>), loaded as it
/// is. A test never writes a reply by hand: a state nobody recorded fails with where to record it.
/// </summary>
internal sealed class Recording
{
    /// <summary>The Weir version Deluno recorded the replies from.</summary>
    public const string Version = "1.0.0-rc.13";

    private const string Folder = "fixtures/recorded/deluno/" + Version;

    private Recording(string state, string note, IReadOnlyList<RecordedExchange> exchanges)
    {
        State = state;
        Note = note;
        Exchanges = exchanges;
    }

    public string State { get; }

    /// <summary>What Deluno did to Weir to reach this state, in its own words.</summary>
    public string Note { get; }

    public IReadOnlyList<RecordedExchange> Exchanges { get; }

    /// <summary>The file as the repository holds it, which is what a failure names.</summary>
    public string File => $"apps/server/tests/{Folder}/{State}.json";

    public RecordedExchange Only => Exchanges.Count == 1
        ? Exchanges[0]
        : throw new InvalidOperationException($"{File} holds {Exchanges.Count} exchanges; name the one to replay.");

    public static Recording Load(string state)
    {
        var path = Path.Combine(AppContext.BaseDirectory, Folder, $"{state}.json");
        if (!System.IO.File.Exists(path))
        {
            throw new XunitException(
                $"Nobody recorded '{state}' (looked for {Folder}/{state}.json). Do not write the reply by hand: " +
                "say it is missing in the README beside the recordings, and ask Deluno to record it.");
        }

        var document = JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject();
        Assert.Equal("weir", (string)document["app"]!);
        Assert.Equal(Version, (string)document["version"]!);
        Assert.Equal(state, (string)document["state"]!);
        var exchanges = document["exchanges"]!.AsArray().Select(exchange => ReadExchange(exchange!.AsObject())).ToList();
        return new Recording(state, (string)document["note"]!, exchanges);
    }

    private static RecordedExchange ReadExchange(JsonObject exchange)
    {
        var request = exchange["request"]!.AsObject();
        var reply = exchange["reply"]!.AsObject();
        var contentType = (string?)reply["headers"]?["content-type"];
        return new RecordedExchange(
            new RecordedCall((string)request["method"]!, (string)request["path"]!, request["body"]?.DeepClone()),
            new RecordedAnswer((int)reply["status"]!, contentType, reply["body"]?.DeepClone(), (string?)reply["bodyText"]));
    }
}
