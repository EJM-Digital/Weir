using Weir.Core.MediaManagers;

namespace Weir.ClientRecorder;

/// <summary>What the recorder was told, read once from the environment.</summary>
internal sealed class ClientSettings
{
    private ClientSettings(string kind, string url, string? authUrl, string outputFolder, string? configFolder, string? setupPassword, string? version)
    {
        Kind = kind;
        Url = url;
        AuthUrl = authUrl;
        OutputFolder = outputFolder;
        ConfigFolder = configFolder;
        SetupPassword = setupPassword;
        Version = version;
    }

    /// <summary>The kind of client, as Weir names it (<see cref="DownloadClientKinds"/>).</summary>
    public string Kind { get; }

    /// <summary>Where the client listens, as the recorder reaches it.</summary>
    public string Url { get; }

    /// <summary>Where a second copy of the client that asks for a user name and password listens, for the client that has to be started that way.</summary>
    public string? AuthUrl { get; }

    /// <summary>The folder the recordings are written under, one folder per client and version.</summary>
    public string OutputFolder { get; }

    /// <summary>The client's own config folder on this machine, for a client that writes its API key there.</summary>
    public string? ConfigFolder { get; }

    /// <summary>The password a client starts with: qBittorrent prints a temporary one in its log, the others have a published one.</summary>
    public string? SetupPassword { get; }

    /// <summary>The version to file the recordings under when the client does not say it.</summary>
    public string? Version { get; }

    public static ClientSettings FromEnvironment()
    {
        var kind = Required("RECORDER_CLIENT").ToLowerInvariant();
        if (!DownloadClientKinds.All.Contains(kind))
        {
            throw new InvalidOperationException($"RECORDER_CLIENT must be one of {string.Join(", ", DownloadClientKinds.All)}.");
        }

        return new ClientSettings(
            kind,
            Optional("RECORDER_URL") ?? DefaultUrl(kind),
            Optional("RECORDER_AUTH_URL"),
            Required("RECORDER_OUT_DIR"),
            Optional("RECORDER_CONFIG_DIR"),
            Optional("RECORDER_SETUP_PASSWORD"),
            Optional("RECORDER_VERSION"));
    }

    private static string DefaultUrl(string kind) => kind switch
    {
        DownloadClientKinds.QBittorrent => "http://localhost:8080",
        DownloadClientKinds.Transmission => "http://localhost:9091",
        DownloadClientKinds.Deluge => "http://localhost:8112",
        DownloadClientKinds.Sabnzbd => "http://localhost:8085",
        _ => "http://localhost:6789",
    };

    private static string Required(string name) => Optional(name) ?? throw new InvalidOperationException($"{name} is not set.");

    private static string? Optional(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
