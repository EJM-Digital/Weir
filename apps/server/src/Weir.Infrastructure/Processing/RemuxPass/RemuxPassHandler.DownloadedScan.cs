using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class RemuxPassHandler
{
    /// <summary>
    /// The optional downloaded-scan hand-back: after a pass writes a file to a library's output folder, ask
    /// every enabled, opted-in Sonarr/Radarr connection linked to that library to run its Downloaded Scan over it.
    /// Best effort; never throws, and does nothing for a pass that did not itself write or confirm an output file.
    /// </summary>
    private async Task DownloadedScanAsync(WireObject result, string mediaScope, WireObject? origin)
    {
        if (!PassWroteOutput(result, out var outputPath) || result.Get("library_id") is not WireInteger libraryValue)
        {
            return;
        }

        var relativePath = TextIn(result, "relative_media_path", string.Empty);
        try
        {
            var uow = await UnitOfWork.OpenAsync(_database).ConfigureAwait(false);
            await using (uow.ConfigureAwait(false))
            {
                await _downloadedScan.NotifyLibraryAsync(
                    uow, (long)libraryValue.Value, mediaScope, outputPath, relativePath, DownloadId(origin)).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Best effort: a manager that cannot be reached must never fail a pass that already succeeded.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogError(exception, "Downloaded-scan hand-back raised unexpectedly.");
        }
    }

    /// <summary>Whether this pass actually wrote, or confirmed, an output file, and where.</summary>
    internal static bool PassWroteOutput(WireObject result, out string outputPath)
    {
        outputPath = string.Empty;
        if (result.Get("ok") is not WireBool { Value: true })
        {
            return false;
        }

        if (result.Get("outcome") is not WireString { Value: RemuxPassOutcomes.LiveOutputWritten or RemuxPassOutcomes.LiveSkippedNotRequired })
        {
            return false;
        }

        if (result.Get("output_file") is not WireString { Value.Length: > 0 } output)
        {
            return false;
        }

        outputPath = output.Value;
        return true;
    }

    /// <summary>
    /// The download client's own id for this download (a torrent's hash, a usenet client's job id), when the job's origin
    /// carries one. Today that is never the case for these connections (they run the remote-path-mapping flow, not a
    /// hand-off), so this is usually null; the command is sent without <c>downloadClientId</c> either way.
    /// </summary>
    internal static string? DownloadId(WireObject? origin) =>
        origin?.Get("download_id") is { IsTruthy: true } value ? WireConvert.Str(value) : null;
}
