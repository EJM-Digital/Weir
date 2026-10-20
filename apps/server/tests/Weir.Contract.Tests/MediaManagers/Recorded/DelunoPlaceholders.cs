using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// Deluno's own recordings of its replies replace what differs from one run to the next with a placeholder: <c>&lt;id&gt;</c> and
/// <c>&lt;utc&gt;</c> where a generated id or time stood, <c>&lt;data-root&gt;</c> for the folder its test host ran in, and
/// <c>&lt;library:x&gt;</c>, <c>&lt;handoff:x&gt;</c> and <c>&lt;connection:x&gt;</c> for an id with a name. A placeholder stands for
/// any value of its type, so a reply served to Weir gets a real one: the same one wherever a name repeats, a time that is now, and
/// the test's own folder where the data root stood.
/// </summary>
internal sealed partial class DelunoPlaceholders(string dataRoot)
{
    private readonly Dictionary<string, string> _named = [];

    private DelunoPlaceholders(string dataRoot, Dictionary<string, string> named)
        : this(dataRoot)
    {
        _named = named;
    }

    /// <summary>These placeholders with Deluno's data root somewhere else: every id they gave a name stays what it was.</summary>
    public DelunoPlaceholders MovedTo(string newDataRoot) => new(newDataRoot, _named);

    [GeneratedRegex("<(?<kind>data-root|id|utc|library|handoff|connection)(?::(?<name>[^>]+))?>")]
    private static partial Regex Placeholder();

    /// <summary>The value this test uses for the library Deluno's recording calls <paramref name="name"/>.</summary>
    public string Library(string name) => Named("library", name);

    public string Apply(string text) => Placeholder().Replace(text, match => match.Groups["kind"].Value switch
    {
        "data-root" => dataRoot.Replace('\\', '/'),
        "utc" => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
        "id" => Guid.NewGuid().ToString("N"),
        var kind => Named(kind, match.Groups["name"].Value),
    });

    public JsonNode? Apply(JsonNode? node) => node switch
    {
        null => null,
        JsonObject fields => new JsonObject(fields.Select(field => KeyValuePair.Create(field.Key, Apply(field.Value)))),
        JsonArray items => new JsonArray([.. items.Select(Apply)]),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(Apply(text)),
        _ => node.DeepClone(),
    };

    private string Named(string kind, string name)
    {
        var key = $"{kind}:{name}";
        if (!_named.TryGetValue(key, out var value))
        {
            value = $"{kind}-{name}-{Guid.NewGuid().ToString("N")[..8]}";
            _named[key] = value;
        }

        return value;
    }
}
