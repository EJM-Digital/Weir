using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// What differs between the machine that made a recording and this test, and nothing else: the hand-off ids a test makes its own,
/// and the folders the recorded Windows paths stand for. It is applied to the request before it is sent and to the reply before
/// it is compared, so the recorded text is never edited and a reply that differs in any other way still fails.
/// </summary>
internal sealed partial class Translation
{
    private readonly Dictionary<string, string> _ids = [];
    private readonly List<(string Recorded, string Live)> _roots = [];

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex RecordedId();

    /// <summary>The id this test uses where the recording has <paramref name="recorded"/>; made up once, then the same.</summary>
    public string Id(string recorded)
    {
        if (!_ids.TryGetValue(recorded, out var live))
        {
            live = $"rec-{Guid.NewGuid().ToString("N")[..12]}";
            _ids[recorded] = live;
        }

        return live;
    }

    /// <summary>The recorded text with only its ids made this test's own; a path in it stays the recorded one.</summary>
    public JsonNode? Ids(JsonNode? node) => node switch
    {
        null => null,
        JsonObject fields => new JsonObject(fields.Select(field => KeyValuePair.Create(field.Key, Ids(field.Value)))),
        JsonArray items => new JsonArray([.. items.Select(Ids)]),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(RecordedId().Replace(text, match => Id(match.Value))),
        _ => node.DeepClone(),
    };

    /// <summary>Folder <paramref name="live"/> on this machine is what the recording's <paramref name="recorded"/> folder is.</summary>
    public Translation Folder(string recorded, string live)
    {
        _roots.Add((recorded, live));
        _roots.Sort((left, right) => right.Recorded.Length.CompareTo(left.Recorded.Length));
        return this;
    }

    public JsonNode? Apply(JsonNode? node) => node switch
    {
        null => null,
        JsonObject fields => new JsonObject(fields.Select(field => KeyValuePair.Create(field.Key, Apply(field.Value)))),
        JsonArray items => new JsonArray([.. items.Select(Apply)]),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(Apply(text)),
        _ => node.DeepClone(),
    };

    public string Apply(string text)
    {
        // Every id in the recording is the one the VM made up; this test makes up its own, the same wherever the recording repeats it.
        text = RecordedId().Replace(text, match => Id(match.Value));

        foreach (var (recorded, live) in _roots)
        {
            if (text.StartsWith(recorded + "\\", StringComparison.Ordinal))
            {
                // A path: what follows the folder is written with this machine's separators.
                return live + Path.DirectorySeparatorChar + text[(recorded.Length + 1)..].Replace('\\', Path.DirectorySeparatorChar);
            }

            // A sentence that names the folder: the folder changes, the words around it do not.
            text = text.Replace(recorded, live, StringComparison.Ordinal);
        }

        return text;
    }
}
