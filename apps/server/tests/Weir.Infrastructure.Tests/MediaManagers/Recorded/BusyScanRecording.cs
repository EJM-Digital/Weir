using Weir.Core.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>
/// The recorded replies of a Sonarr taking a scan, and the answer it gives while busy with another. The app keeps one Downloaded Scan at a
/// time; asked for a second it can answer with the scan it already has (Sonarr 4.0.20 did once, in a recording run: the answer carried the
/// other scan's id and path, and the file Weir asked about was never scanned). No run produces that answer on demand, so the busy answer is
/// the recorded answer to the scan with the two members that make it another scan's changed: its path, and its id, which becomes the id of
/// the command the recording <c>command-status-finished</c> shows finished.
/// </summary>
internal static class BusyScanRecording
{
    public const string App = "sonarr";

    private const string BusyWith = "/downloads/Another.Release/Another.Release.mkv";

    /// <summary>The recording of the app taking a scan: the mappings it lists, then the command it accepts.</summary>
    public static ArrRecording NextFile => ArrRecording.Load(App, "downloaded-scan-next-file");

    public static ArrExchange Accepted => NextFile.To("POST", DownloadedScanRules.CommandPath);

    /// <summary>The file the recorded scan was asked for, as Weir wrote it.</summary>
    public static string OutputPath => (string)Accepted.Request.Body!["path"]!;

    /// <summary>The recorded answer to the scan, made another scan's.</summary>
    public static ArrExchange Busy()
    {
        var status = ArrRecording.Load(App, "command-status-finished").Only;
        var body = Accepted.Reply.Body!.DeepClone().AsObject();
        body["id"] = long.Parse(status.Request.Path[(status.Request.Path.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        body["body"]!["path"] = BusyWith;
        return Accepted with { Reply = Accepted.Reply with { Body = body } };
    }

    /// <summary>The app's mappings, then each scan answer in turn; the last answer repeats.</summary>
    public static ArrRecording Answering(params ArrExchange[] scans) =>
        NextFile.WithExchanges([NextFile.Exchanges[0], .. scans]);
}
