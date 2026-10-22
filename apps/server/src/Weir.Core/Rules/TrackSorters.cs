using Weir.Core.Json;

namespace Weir.Core.Rules;

/// <summary>
/// The ordered, editable sorter list: key building, storage and the seeded default.
/// </summary>
public static partial class TrackSorters
{
    /// <summary>
    /// The vocabulary, ending with <c>content_tier</c> (issue #497), which the golden files predate.
    /// </summary>
    public static IReadOnlyList<string> Fields { get; } =
        ["bitrate", "channels", "codec", "language", "title", "default", "forced", "commentary", "content_tier"];

    /// <summary>
    /// The default audio ranking. Its leading key is <c>content_tier</c> (main &gt; dub/audio
    /// description &gt; commentary, issue #497), so a dub or audio-description track does not tie with
    /// the main track on this first key. <see cref="Presets"/>' <c>quality_all_languages</c> preset
    /// keeps a plain <c>commentary</c> key — the issue asks for <c>content_tier</c> only on the default.
    /// </summary>
    public static IReadOnlyList<TrackSorter> DefaultAudioSorters { get; } =
    [
        new("content_tier"),
        new("channels"),
        new("codec"),
        new("bitrate"),
        new("default"),
    ];

    public static IReadOnlyList<TrackSorter> DefaultSubtitleSorters { get; } =
    [
        new("forced"),
        new("default"),
        new("language"),
    ];

    /// <summary>The three policies as starting points an operator can edit.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<TrackSorter>> Presets { get; } =
        new Dictionary<string, IReadOnlyList<TrackSorter>>(StringComparer.Ordinal)
        {
            ["preferred_langs_quality"] = DefaultAudioSorters,
            ["preferred_langs_strict"] = DefaultAudioSorters,
            ["quality_all_languages"] = [new("commentary"), new("channels"), new("codec"), new("bitrate")],
        };

    internal static bool LargerIsBetter(string field) => field is "bitrate" or "channels";

    /// <summary>One sorter's contribution to the sort key. Lower sorts first.</summary>
    public static IReadOnlyList<long> SorterKeyComponent(TrackSorter sorter, SortableTrack track)
    {
        ArgumentNullException.ThrowIfNull(sorter);
        ArgumentNullException.ThrowIfNull(track);
        var actual = track.Get(sorter.Field);

        if (sorter.Value is not null)
        {
            var (op, expected) = SplitExpression(sorter.Value);
            var matched = Compare(actual, op, expected, sorter.Field);
            if (sorter.Reversed)
            {
                matched = !matched;
            }

            return [matched ? 0 : 1];
        }

        if (sorter.Field is "default" or "forced" or "commentary")
        {
            long flag = Truthy(actual) ? 1 : 0;
            // commentary naturally sorts commentary last: it is a demotion.
            if (sorter.Field == "commentary")
            {
                return [sorter.Reversed ? 1 - flag : flag];
            }

            return [sorter.Reversed ? flag : 1 - flag];
        }

        if (sorter.Field == "content_tier")
        {
            // Issue #497: main (0) beats a dub or audio-description track (1), which beats
            // commentary (2) — a superset of the plain "commentary" demotion.
            long tier = track.Commentary ? 2 : track.Dub || track.AudioDescription ? 1 : 0;
            return [sorter.Reversed ? 2 - tier : tier];
        }

        if (LargerIsBetter(sorter.Field))
        {
            var number = Number(actual);
            // Unknown sorts after known, in both directions.
            long unknown = number <= 0 ? 1 : 0;
            var score = number > 0 ? -Math.Min(number, 2_000_000_000) : 0;
            if (sorter.Reversed)
            {
                score = -score;
            }

            return [unknown, score];
        }

        if (sorter.Field == "codec")
        {
            var rank = track.CodecRank;
            return [sorter.Reversed ? -rank : rank];
        }

        var text = RulesJson.Lower(WireStrings.Strip(Truthy(actual) ? Str(actual!) : string.Empty));
        var parts = new List<long> { text.Length == 0 ? 1 : 0 };
        foreach (var rune in WireStrings.Slice(text, 32).EnumerateRunes())
        {
            parts.Add(sorter.Reversed ? -rune.Value : rune.Value);
        }

        return parts;
    }

    /// <summary>The whole key, in the operator's order, ending in the track index.</summary>
    public static IReadOnlyList<long> SortKeyForTrack(IEnumerable<TrackSorter> sorters, SortableTrack track)
    {
        ArgumentNullException.ThrowIfNull(sorters);
        ArgumentNullException.ThrowIfNull(track);
        var parts = new List<long>();
        foreach (var sorter in sorters)
        {
            parts.AddRange(SorterKeyComponent(sorter, track));
        }

        parts.Add(track.Index);
        return parts;
    }
}
