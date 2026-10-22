using System.Text.Json;
using Weir.Core.Json;

namespace Weir.Core.Rules;

/// <summary>
/// ffprobe data the rules engine could not read. <see cref="ErrorKind"/> names the error class the
/// golden files record (<c>KeyError</c>, <c>ValueError</c>, <c>TypeError</c>, <c>AttributeError</c>,
/// <c>OverflowError</c>) so callers and the golden-file tests can tell the cases apart.
/// </summary>
public sealed class RulesInputException : Exception
{
    public RulesInputException()
    {
        ErrorKind = "ValueError";
    }

    public RulesInputException(string message)
        : base(message)
    {
        ErrorKind = "ValueError";
    }

    public RulesInputException(string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorKind = "ValueError";
    }

    public RulesInputException(string errorKind, string message)
        : base(message)
    {
        ErrorKind = errorKind;
    }

    public string ErrorKind { get; }
}

/// <summary>
/// One stream from <c>ffprobe -show_streams -of json</c>.
/// </summary>
/// <remarks>
/// ffprobe's values are loosely typed (bit rates and frame counts arrive as strings, a
/// disposition flag can be a string), and the rules read them with the exact conversions in
/// <see cref="RulesJson"/>. The raw object stays available as <see cref="Json"/>; the typed properties
/// below are lenient readings for callers.
/// </remarks>
public sealed record ProbeStreamInfo
{
    public ProbeStreamInfo(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("An ffprobe stream is a JSON object.", nameof(json));
        }

        Json = json.Clone();
    }

    /// <summary>The stream exactly as ffprobe reported it.</summary>
    public JsonElement Json { get; }

    public static ProbeStreamInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new ProbeStreamInfo(document.RootElement);
    }

    /// <summary>The value under <paramref name="name"/>: null when absent; a JSON <c>null</c> is returned as an element.</summary>
    public JsonElement? Get(string name) => RulesJson.Get(Json, name);

    /// <summary><c>index</c> when it reads as an integer.</summary>
    public long? Index => RulesJson.TryInt(Get("index"), out var index) ? index : null;

    /// <summary><c>channels</c> when it reads as an integer.</summary>
    public long? Channels => RulesJson.TryInt(Get("channels"), out var channels) ? channels : null;

    /// <summary><c>codec_type</c>, stripped and lower-cased; empty when absent or not a string.</summary>
    public string CodecType => RulesJson.IsStr(Get("codec_type")) ? RulesJson.Lower(WireStrings.Strip(Get("codec_type")!.Value.GetString()!)) : string.Empty;

    /// <summary><c>codec_name</c> as written; empty when absent.</summary>
    public string CodecName => RulesJson.StrOr(Get("codec_name"), string.Empty);

    /// <summary>
    /// The tags as the rules read them, string-valued entries only; empty when tags are not an object.
    /// ffprobe never legitimately puts a non-string value in a tag, so one is treated as an absent key
    /// (#537 item 5): stringifying it would turn a JSON <c>null</c> language into a bogus code
    /// <c>"none"</c>, and let a list- or dict-valued title match a "commentary" substring test.
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags
    {
        get
        {
            var tags = Get("tags");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!RulesJson.Truthy(tags) || !RulesJson.IsDict(tags))
            {
                return result;
            }

            foreach (var (key, value) in RulesJson.Items(tags!.Value))
            {
                if (RulesJson.IsStr(value))
                {
                    result[key] = value.GetString()!;
                }
            }

            return result;
        }
    }

    /// <summary>
    /// The disposition flags: each value read as an integer (<see cref="RulesJson.TryInt"/>), skipping values
    /// that do not convert.
    /// </summary>
    public IReadOnlyDictionary<string, long> Disposition
    {
        get
        {
            var disposition = Get("disposition");
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            if (!RulesJson.Truthy(disposition) || !RulesJson.IsDict(disposition))
            {
                return result;
            }

            foreach (var (key, value) in RulesJson.Items(disposition!.Value))
            {
                if (RulesJson.TryInt(value, out var flag))
                {
                    result[key] = flag;
                }
            }

            return result;
        }
    }

    /// <summary>One entry of <see cref="Tags"/>, or null.</summary>
    public string? Tag(string name) => Tags.TryGetValue(name, out var value) ? value : null;
}

/// <summary>The whole <c>ffprobe</c> JSON document for one file.</summary>
public sealed record ProbeResult
{
    public ProbeResult(JsonElement json)
    {
        Json = json.Clone();
    }

    public JsonElement Json { get; }

    public static ProbeResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new ProbeResult(document.RootElement);
    }

    /// <summary>
    /// The entries of <c>streams</c> that are objects, in file order; empty when the document has
    /// no stream list. Entries that are not objects are skipped.
    /// </summary>
    public IReadOnlyList<ProbeStreamInfo> Streams
    {
        get
        {
            if (Json.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var streams = RulesJson.Get(Json, "streams");
            if (!RulesJson.IsList(streams))
            {
                return [];
            }

            return streams!.Value.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.Object)
                .Select(s => new ProbeStreamInfo(s))
                .ToList();
        }
    }

    /// <summary>
    /// The entries of <c>chapters</c> that are objects (#498: present when the probe ran with
    /// <c>-show_chapters</c>, see <see cref="Weir.Core.Media.FfmpegCommands.BuildFfprobeArgv"/>); empty when the
    /// document has no chapter list, including a probe made before that flag existed.
    /// </summary>
    public IReadOnlyList<JsonElement> Chapters
    {
        get
        {
            if (Json.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var chapters = RulesJson.Get(Json, "chapters");
            if (!RulesJson.IsList(chapters))
            {
                return [];
            }

            return chapters!.Value.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object).ToList();
        }
    }
}
