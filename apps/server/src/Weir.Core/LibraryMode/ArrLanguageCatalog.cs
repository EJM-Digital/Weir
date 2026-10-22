using Weir.Core.Rules;

namespace Weir.Core.LibraryMode;

/// <summary>
/// Maps a Sonarr or Radarr custom-format language specification's <c>Value</c> (an integer id private to that
/// product) to Weir's canonical language code (<see cref="OriginalLanguage.CanonicalLanguage"/>), so it can be
/// compared against the codes Weir's own rules engine uses for audio tracks.
/// </summary>
/// <remarks>
/// <para>
/// <b>The id -&gt; English name half is verified from source</b> (<c>NzbDrone.Core.Languages.Language</c>'s
/// named instances): Sonarr <c>src/NzbDrone.Core/Languages/Language.cs:73-126</c> at <c>v5-develop</c>; Radarr
/// same path, <c>:73-132</c>, at <c>develop</c>. The two products agree up to and including id 25 (Czech) but
/// <b>not</b> past it: Sonarr's 26 is Arabic, Radarr's 26 is Hindi, and Radarr adds languages (Bengali, Telugu,
/// ...) Sonarr does not have at all — this is why the two tables below are kept separate rather than merged into
/// one.
/// </para>
/// <para>
/// <b>The English name -&gt; ISO code half is Weir's own assignment</b>, not read from either product (neither
/// exposes one): a standard ISO 639-2 bibliographic code, reusing <see cref="OriginalLanguage"/>'s existing
/// group for every language it already knows, matched to whichever alias that group is keyed on internally. A
/// language with no widely-used three-letter form Weir already recognises (regional variants such as
/// "Portuguese (Brazil)" or "Spanish (Latino)", which ffprobe has no separate tag for either) is folded into its
/// parent language's code. <c>Language.Original</c> (id -2), <c>Language.Unknown</c> (id 0) and Radarr's
/// <c>Language.Any</c> (id -1) resolve to <see langword="null"/>: they name a language dynamically from the
/// title's own metadata (or "no particular language"), which this evaluator does not look up, rather than a
/// fixed one a removed track could be compared against.
/// </para>
/// </remarks>
public static class ArrLanguageCatalog
{
    private static readonly Dictionary<int, string> SonarrNames = new()
    {
        [0] = "Unknown",
        [1] = "English",
        [2] = "French",
        [3] = "Spanish",
        [4] = "German",
        [5] = "Italian",
        [6] = "Danish",
        [7] = "Dutch",
        [8] = "Japanese",
        [9] = "Icelandic",
        [10] = "Chinese",
        [11] = "Russian",
        [12] = "Polish",
        [13] = "Vietnamese",
        [14] = "Swedish",
        [15] = "Norwegian",
        [16] = "Finnish",
        [17] = "Turkish",
        [18] = "Portuguese",
        [19] = "Flemish",
        [20] = "Greek",
        [21] = "Korean",
        [22] = "Hungarian",
        [23] = "Hebrew",
        [24] = "Lithuanian",
        [25] = "Czech",
        [26] = "Arabic",
        [27] = "Hindi",
        [28] = "Bulgarian",
        [29] = "Malayalam",
        [30] = "Ukrainian",
        [31] = "Slovak",
        [32] = "Thai",
        [33] = "Portuguese",
        [34] = "Spanish",
        [35] = "Romanian",
        [36] = "Latvian",
        [37] = "Persian",
        [38] = "Catalan",
        [39] = "Croatian",
        [40] = "Serbian",
        [41] = "Bosnian",
        [42] = "Estonian",
        [43] = "Tamil",
        [44] = "Indonesian",
        [45] = "Macedonian",
        [46] = "Slovenian",
        [47] = "Azerbaijani",
        [48] = "Uzbek",
        [49] = "Malay",
        [50] = "Urdu",
        [51] = "Romansh",
        [52] = "Georgian",
        [-2] = "Original",
    };

    private static readonly Dictionary<int, string> RadarrNames = new()
    {
        [0] = "Unknown",
        [1] = "English",
        [2] = "French",
        [3] = "Spanish",
        [4] = "German",
        [5] = "Italian",
        [6] = "Danish",
        [7] = "Dutch",
        [8] = "Japanese",
        [9] = "Icelandic",
        [10] = "Chinese",
        [11] = "Russian",
        [12] = "Polish",
        [13] = "Vietnamese",
        [14] = "Swedish",
        [15] = "Norwegian",
        [16] = "Finnish",
        [17] = "Turkish",
        [18] = "Portuguese",
        [19] = "Flemish",
        [20] = "Greek",
        [21] = "Korean",
        [22] = "Hungarian",
        [23] = "Hebrew",
        [24] = "Lithuanian",
        [25] = "Czech",
        [26] = "Hindi",
        [27] = "Romanian",
        [28] = "Thai",
        [29] = "Bulgarian",
        [30] = "Portuguese",
        [31] = "Arabic",
        [32] = "Ukrainian",
        [33] = "Persian",
        [34] = "Bengali",
        [35] = "Slovak",
        [36] = "Latvian",
        [37] = "Spanish",
        [38] = "Catalan",
        [39] = "Croatian",
        [40] = "Serbian",
        [41] = "Bosnian",
        [42] = "Estonian",
        [43] = "Tamil",
        [44] = "Indonesian",
        [45] = "Telugu",
        [46] = "Macedonian",
        [47] = "Slovenian",
        [48] = "Malayalam",
        [49] = "Kannada",
        [50] = "Albanian",
        [51] = "Afrikaans",
        [52] = "Marathi",
        [53] = "Tagalog",
        [54] = "Urdu",
        [55] = "Romansh",
        [56] = "Mongolian",
        [57] = "Georgian",
        [-1] = "Any",
        [-2] = "Original",
    };

    /// <summary>Names with no fixed language (they depend on the title, or mean "none in particular").</summary>
    private static readonly HashSet<string> Dynamic = new(StringComparer.Ordinal) { "Unknown", "Original", "Any" };

    private static readonly Dictionary<string, string> NameToCode = new(StringComparer.Ordinal)
    {
        ["English"] = "eng",
        ["French"] = "fre",
        ["Spanish"] = "spa",
        ["German"] = "ger",
        ["Italian"] = "ita",
        ["Danish"] = "dan",
        ["Dutch"] = "dut",
        ["Japanese"] = "jpn",
        ["Icelandic"] = "ice",
        ["Chinese"] = "chi",
        ["Russian"] = "rus",
        ["Polish"] = "pol",
        ["Vietnamese"] = "vie",
        ["Swedish"] = "swe",
        ["Norwegian"] = "nor",
        ["Finnish"] = "fin",
        ["Turkish"] = "tur",
        ["Portuguese"] = "por",
        ["Flemish"] = "dut",
        ["Greek"] = "gre",
        ["Korean"] = "kor",
        ["Hungarian"] = "hun",
        ["Hebrew"] = "heb",
        ["Lithuanian"] = "lit",
        ["Czech"] = "cze",
        ["Arabic"] = "ara",
        ["Hindi"] = "hin",
        ["Bulgarian"] = "bul",
        ["Malayalam"] = "mal",
        ["Ukrainian"] = "ukr",
        ["Slovak"] = "slo",
        ["Thai"] = "tha",
        ["Romanian"] = "rum",
        ["Latvian"] = "lav",
        ["Persian"] = "per",
        ["Catalan"] = "cat",
        ["Croatian"] = "hrv",
        ["Serbian"] = "srp",
        ["Bosnian"] = "bos",
        ["Estonian"] = "est",
        ["Tamil"] = "tam",
        ["Indonesian"] = "ind",
        ["Macedonian"] = "mac",
        ["Slovenian"] = "slv",
        ["Azerbaijani"] = "aze",
        ["Uzbek"] = "uzb",
        ["Malay"] = "may",
        ["Urdu"] = "urd",
        ["Romansh"] = "roh",
        ["Georgian"] = "geo",
        ["Bengali"] = "ben",
        ["Telugu"] = "tel",
        ["Kannada"] = "kan",
        ["Albanian"] = "alb",
        ["Afrikaans"] = "afr",
        ["Marathi"] = "mar",
        ["Tagalog"] = "tgl",
        ["Mongolian"] = "mon",
    };

    /// <summary>Weir's canonical code for a manager's language id, or <see langword="null"/> when Weir cannot place it.</summary>
    public static string? CanonicalCodeFor(string managerKind, int languageId)
    {
        var names = managerKind switch
        {
            "radarr" => RadarrNames,
            "sonarr" => SonarrNames,
            _ => null,
        };
        if (names is null || !names.TryGetValue(languageId, out var name) || Dynamic.Contains(name))
        {
            return null;
        }

        return OriginalLanguage.CanonicalLanguage(NameToCode[name]);
    }
}
