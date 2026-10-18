using Weir.Core.Jobs;
using Weir.Core.Json;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// One scan Weir asks a Sonarr or Radarr for: which connection, which file, and how many times that app has already said it is busy.
/// It is the payload of the job that asks again (<see cref="DownloadedScanRetryHandler"/>).
/// </summary>
/// <param name="ConnectionId">The media manager connection to ask.</param>
/// <param name="ArrScope">Movies or TV, which picks the scan command.</param>
/// <param name="OutputPath">The file Weir wrote, as Weir sees it.</param>
/// <param name="RelativeMediaPath">The file's path inside its library, which Activity names it by.</param>
/// <param name="DownloadId">The download client's own id for the download, when Weir knows it.</param>
/// <param name="BusyAnswers">How many times in a row the app has said it is busy.</param>
public sealed record DownloadedScanRequest(
    long ConnectionId, string ArrScope, string OutputPath, string RelativeMediaPath, string? DownloadId, int BusyAnswers = 0)
{
    public string ToJson() =>
        WireJsonWriter.Dumps(
            new WireObject()
                .Set("connection_id", ConnectionId)
                .Set("arr_scope", ArrScope)
                .Set("output_path", OutputPath)
                .Set("relative_media_path", RelativeMediaPath)
                .Set("download_id", DownloadId)
                .Set("busy_answers", BusyAnswers),
            WireJsonFormat.Compact);

    /// <summary>The request a job carries, or null when its payload is not one.</summary>
    public static DownloadedScanRequest? Parse(string? payloadJson)
    {
        var payload = JobPayload.ParseObject(payloadJson);
        if (JobPayload.StrictInteger(payload, "connection_id") is not { } connectionId
            || JobPayload.StringProperty(payload, "arr_scope") is not { Length: > 0 } scope
            || JobPayload.StringProperty(payload, "output_path") is not { Length: > 0 } outputPath)
        {
            return null;
        }

        return new DownloadedScanRequest(
            connectionId,
            scope,
            outputPath,
            JobPayload.StringProperty(payload, "relative_media_path") ?? string.Empty,
            JobPayload.StringProperty(payload, "download_id"),
            (int)(JobPayload.StrictInteger(payload, "busy_answers") ?? 0));
    }
}
