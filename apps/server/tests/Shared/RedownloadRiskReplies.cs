using System.Globalization;
using System.Text.Json.Nodes;

namespace Weir.Tests.Shared;

/// <summary>
/// What Radarr answers the two reads behind the re-download check (the file and its quality profile) for a title whose English-and-Japanese
/// file earns +50 from a "Multi-Audio" language format. Every member is the one a real Radarr gave (<c>redownload-risk</c> in
/// <c>fixtures/recorded/radarr</c>); no recorded run has a language-based custom format, so the members that make one are set here: the file's
/// languages, custom formats and score, and the profile's upgrade choice, cutoff and format scores.
/// </summary>
internal sealed record RedownloadRiskReplies(string FilePath, string FileJson, string ProfilePath, string ProfileJson, long FileId, long ProfileId)
{
    /// <summary>The line the check words a lost +50 as, for the title it is asked about.</summary>
    public static string MultiAudioWarning(string manager, string title) =>
        $"{manager} may download {title} again: its 'Multi-Audio' format (+50) would no longer match.";

    /// <summary>
    /// The replies for a profile whose cutoff is <paramref name="cutoffFormatScore"/>. The file scores 100 with its format, so losing the
    /// format puts the title at 50: below a cutoff of 60 (the manager would fetch it again), not below 40.
    /// </summary>
    public static RedownloadRiskReplies For(int cutoffFormatScore = 60)
    {
        const int multiAudioFormatId = 9;
        var recording = ArrRecording.Load("radarr", "redownload-risk");
        var file = recording.Exchanges.Single(exchange => exchange.Request.Path.StartsWith("/api/v3/moviefile/", StringComparison.Ordinal));
        var profile = recording.Exchanges.Single(exchange => exchange.Request.Path.StartsWith("/api/v3/qualityprofile/", StringComparison.Ordinal));

        var movieFile = file.Reply.Body!.DeepClone().AsObject();
        movieFile["languages"] = new JsonArray(Language(1, "English"), Language(8, "Japanese"));
        movieFile["customFormats"] = new JsonArray(MultiAudioFormat(multiAudioFormatId));
        movieFile["customFormatScore"] = 100;
        var qualityProfile = profile.Reply.Body!.DeepClone().AsObject();
        qualityProfile["upgradeAllowed"] = true;
        qualityProfile["cutoffFormatScore"] = cutoffFormatScore;
        qualityProfile["formatItems"] = new JsonArray(new JsonObject { ["id"] = 1, ["format"] = multiAudioFormatId, ["name"] = "Multi-Audio", ["score"] = 50 });

        return new RedownloadRiskReplies(
            file.Request.Path, movieFile.ToJsonString(), profile.Request.Path, qualityProfile.ToJsonString(), IdOf(file), IdOf(profile));
    }

    private static long IdOf(ArrExchange exchange) =>
        long.Parse(exchange.Request.Path[(exchange.Request.Path.LastIndexOf('/') + 1)..], CultureInfo.InvariantCulture);

    private static JsonObject Language(int id, string name) => new() { ["id"] = id, ["name"] = name };

    private static JsonObject MultiAudioFormat(int id) => new()
    {
        ["id"] = id,
        ["name"] = "Multi-Audio",
        ["specifications"] = new JsonArray(new JsonObject
        {
            ["implementation"] = "LanguageSpecification",
            ["implementationName"] = "Language",
            ["negate"] = false,
            ["fields"] = new JsonArray(
                new JsonObject { ["name"] = "value", ["value"] = 1 },
                new JsonObject { ["name"] = "exceptLanguage", ["value"] = true }),
        }),
    };
}
