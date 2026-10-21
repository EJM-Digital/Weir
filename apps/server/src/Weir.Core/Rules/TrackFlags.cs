using System.Text.RegularExpressions;

namespace Weir.Core.Rules;

/// <summary>Where a <see cref="TrackFlag"/> came from, for the plan notes and file's track list.</summary>
public enum TrackFlagSource
{
    /// <summary>Neither the disposition nor the name said so.</summary>
    None,

    /// <summary>ffprobe's own disposition flag said so. Checked first; a name never overrides it.</summary>
    Disposition,

    /// <summary>No disposition flag was set, but the track's name said so.</summary>
    Name,
}

/// <summary>One yes/no fact about a track, and where it came from.</summary>
public readonly record struct TrackFlag(bool Value, TrackFlagSource Source)
{
    public static readonly TrackFlag Absent = new(false, TrackFlagSource.None);

    public bool FromName => Value && Source == TrackFlagSource.Name;

    public bool FromDisposition => Value && Source == TrackFlagSource.Disposition;
}

/// <summary>
/// What a track is, beyond its codec and language (issue #495):
/// hearing-impaired (SDH/CC), forced/signs, dub, audio description and commentary.
/// </summary>
public sealed record TrackFlags
{
    public TrackFlag HearingImpaired { get; init; } = TrackFlag.Absent;
    public TrackFlag Forced { get; init; } = TrackFlag.Absent;
    public TrackFlag Dub { get; init; } = TrackFlag.Absent;
    public TrackFlag AudioDescription { get; init; } = TrackFlag.Absent;
    public TrackFlag Commentary { get; init; } = TrackFlag.Absent;
}

/// <summary>
/// Reads <see cref="TrackFlags"/> from one ffprobe stream: the disposition first, then the track's
/// name (<c>tags.title</c>, plus <c>tags.comment</c> for commentary).
/// A name only adds a flag; it never clears one the disposition set, and a disposition flag is
/// never itself derived from the name. Short abbreviations match only as a whole word, so "CC"
/// inside "Accessibility" and "HI" inside "This" do not count; the longer keywords match as a
/// case-insensitive substring anywhere in the name.
/// </summary>
public static partial class TrackFlagsReader
{
    // Whole-word abbreviations (a short one would otherwise match inside an unrelated word) are the
    // generated regexes below.

    // Substrings: multi-word or long enough that an accidental match inside another word is not a risk.
    private static readonly string[] HearingImpairedKeywords =
    [
        "closed caption", "hearing impaired", "for deaf",
        "doven", "slechthorend", // Dutch
        "hörgeschädigte", "gehörlose", "schwerhörige", // German
        "sourds", "malentendant", // French
        "sordos", "sordi", // Spanish, Italian
        "surdos", // Portuguese
        "döva", "hörselskad", // Swedish
        "døve", "hørselshemm", "hørehæmm", // Norwegian, Danish
    ];

    private static readonly string[] ForcedKeywords = ["forced", "foreign"];
    private static readonly string[] AudioDescriptionKeywords = ["descriptive", "audio descri"];
    private static readonly string[] CommentaryKeywords = ["commentary"];

    [GeneratedRegex(@"\b(?:cc|hi|hoh|sdh|shd)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HearingImpairedWordRegex();

    [GeneratedRegex(@"\bsigns\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForcedWordRegex();

    [GeneratedRegex(@"\b(?:dub|dubbed|dubbing|dubtitle)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DubWordRegex();

    private static bool ContainsAny(string lowerText, IReadOnlyList<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (lowerText.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NameMatch(string name, Regex? wholeWord, IReadOnlyList<string> substrings)
    {
        if (name.Length == 0)
        {
            return false;
        }

        if (wholeWord is not null && wholeWord.IsMatch(name))
        {
            return true;
        }

        return substrings.Count > 0 && ContainsAny(name.ToLowerInvariant(), substrings);
    }

    private static TrackFlag Compute(bool fromDisposition, string name, Regex? wholeWord, IReadOnlyList<string> substrings)
    {
        if (fromDisposition)
        {
            return new TrackFlag(true, TrackFlagSource.Disposition);
        }

        return NameMatch(name, wholeWord, substrings) ? new TrackFlag(true, TrackFlagSource.Name) : TrackFlag.Absent;
    }

    /// <summary>The flags for one ffprobe stream (audio or subtitle).</summary>
    public static TrackFlags Detect(ProbeStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var disposition = stream.Disposition;
        var tags = stream.Tags;
        var title = tags.GetValueOrDefault("title") ?? string.Empty;
        var comment = tags.GetValueOrDefault("comment") ?? string.Empty;

        var commentaryFromDisposition = disposition.GetValueOrDefault("comment") != 0;
        var commentary = commentaryFromDisposition
            ? new TrackFlag(true, TrackFlagSource.Disposition)
            : NameMatch(title, null, CommentaryKeywords) || NameMatch(comment, null, CommentaryKeywords)
                ? new TrackFlag(true, TrackFlagSource.Name)
                : TrackFlag.Absent;

        return new TrackFlags
        {
            HearingImpaired = Compute(disposition.GetValueOrDefault("hearing_impaired") != 0, title, HearingImpairedWordRegex(), HearingImpairedKeywords),
            Forced = Compute(disposition.GetValueOrDefault("forced") != 0, title, ForcedWordRegex(), ForcedKeywords),
            Dub = Compute(disposition.GetValueOrDefault("dub") != 0, title, DubWordRegex(), []),
            AudioDescription = Compute(disposition.GetValueOrDefault("visual_impaired") != 0, title, null, AudioDescriptionKeywords),
            Commentary = commentary,
        };
    }
}
