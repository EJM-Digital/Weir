namespace Weir.ArrRecorder;

/// <summary>What the recorder was told, read once from the environment.</summary>
internal sealed class RecorderSettings
{
    private RecorderSettings(ArrFlavour flavour, string url, string configFolder, string workFolder, string outputFolder, int webhookPort, string webhookHost, string transmissionUrl)
    {
        Flavour = flavour;
        Url = url;
        ConfigFolder = configFolder;
        WorkFolder = workFolder;
        OutputFolder = outputFolder;
        WebhookPort = webhookPort;
        WebhookHost = webhookHost;
        TransmissionUrl = transmissionUrl;
    }

    public ArrFlavour Flavour { get; }

    /// <summary>Where the app listens, as the recorder reaches it.</summary>
    public string Url { get; }

    /// <summary>The app's own config folder on this machine; its <c>config.xml</c> holds the API key the app generated.</summary>
    public string ConfigFolder { get; }

    /// <summary>The folder on this machine that the app sees as <c>/downloads</c>.</summary>
    public string WorkFolder { get; }

    /// <summary>The folder the recordings are written under, one folder per app and version.</summary>
    public string OutputFolder { get; }

    /// <summary>The port the recorder listens on for the app's webhook.</summary>
    public int WebhookPort { get; }

    /// <summary>The name under which the app's container reaches the recorder.</summary>
    public string WebhookHost { get; }

    /// <summary>The download client's address, as the recorder reaches it.</summary>
    public string TransmissionUrl { get; }

    public static RecorderSettings FromEnvironment()
    {
        var app = Required("RECORDER_APP").ToLowerInvariant();
        var flavour = app switch
        {
            "sonarr" => ArrFlavour.Sonarr,
            "radarr" => ArrFlavour.Radarr,
            _ => throw new InvalidOperationException("RECORDER_APP must be sonarr or radarr."),
        };
        return new RecorderSettings(
            flavour,
            Optional("RECORDER_URL", $"http://localhost:{flavour.Port}"),
            Required("RECORDER_CONFIG_DIR"),
            Required("RECORDER_WORK_DIR"),
            Required("RECORDER_OUT_DIR"),
            int.Parse(Optional("RECORDER_WEBHOOK_PORT", flavour.Port == 8989 ? "18080" : "18081"), System.Globalization.CultureInfo.InvariantCulture),
            Optional("RECORDER_WEBHOOK_HOST", "host.docker.internal"),
            Optional("RECORDER_TRANSMISSION_URL", "http://localhost:9091"));
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} is not set.");

    private static string Optional(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}

/// <summary>What differs between Sonarr and Radarr in the recorded session: names, ports, folders and the title the session uses.</summary>
internal sealed record ArrFlavour(
    string Kind,
    string Scope,
    int Port,
    string RootFolder,
    string LookupPath,
    string TitlePath,
    string ReleaseTitle,
    IReadOnlyList<string> ImportNames,
    string DeleteEvent)
{
    /// <summary>Breaking Bad: TheTVDB 81189. The session grabs its fourth episode and imports its first three.</summary>
    public static readonly ArrFlavour Sonarr = new(
        "sonarr",
        "tv",
        8989,
        "/tv",
        "/api/v3/series/lookup?term=tvdb:81189",
        "/api/v3/series",
        "Breaking.Bad.S01E04.720p.WEB.h264-RECORDER",
        ["Breaking.Bad.S01E01.720p.WEB.h264-RECORDER", "Breaking.Bad.S01E02.720p.WEB.h264-RECORDER", "Breaking.Bad.S01E03.720p.WEB.h264-RECORDER"],
        "EpisodeFileDelete");

    /// <summary>Big Buck Bunny: TMDb 10378.</summary>
    public static readonly ArrFlavour Radarr = new(
        "radarr",
        "movie",
        7878,
        "/movies",
        "/api/v3/movie/lookup?term=tmdb:10378",
        "/api/v3/movie",
        "Big.Buck.Bunny.2008.1080p.BluRay.x264-RECORDER",
        ["Big.Buck.Bunny.2008.720p.BluRay.x264-RECORDER"],
        "MovieFileDelete");

    public bool IsSonarr => Kind == "sonarr";
}
