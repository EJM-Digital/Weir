using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>What Weir asked a Sonarr or Radarr to scan, and whether it took the request.</summary>
/// <param name="Command">The <c>POST /api/v3/command</c> body Weir sent.</param>
/// <param name="Accepted">True when the manager is scanning what Weir asked it to.</param>
/// <param name="FailureReason">Why it is not, in plain words; null when it is.</param>
/// <param name="Busy">True when the manager is busy with another scan, so asking again later can work; false for every other reason it did not take the request.</param>
/// <param name="RetryAfter">How long the manager asked Weir to wait before asking again, when it said.</param>
public sealed record ArrDownloadedScanResult(WireObject Command, bool Accepted, string? FailureReason, bool Busy = false, TimeSpan? RetryAfter = null);

/// <summary>
/// The manager calls of the downloaded-scan hand-back: read the manager's remote path mappings, translate Weir's output folder
/// through them, and post the Downloaded Scan command. The mappings are only read; a manager that cannot be asked for them still
/// gets the command with Weir's own path. A manager keeps one scan at a time and answers a second request with the scan already
/// there, or with a request to slow down; either is reported as busy, and asking again later is the caller's to schedule
/// (<see cref="DownloadedScanRetrySchedule"/>): nothing here waits for another app. It never throws for a manager that is unreachable
/// or refuses.
/// </summary>
public sealed class ArrDownloadedScan
{
    private readonly IManagerHttpHandlerFactory _handlers;

    public ArrDownloadedScan(IManagerHttpHandlerFactory handlers)
    {
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    }

    /// <summary>Asks <paramref name="connection"/> once to scan <paramref name="outputPath"/>, as the manager sees it through its mappings.</summary>
    public async Task<ArrDownloadedScanResult> RequestAsync(
        ManagerConnection connection, string arrScope, string outputPath, string? downloadId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var mappings = await MappingsAsync(connection, cancellationToken).ConfigureAwait(false);
        var managerPath = DownloadedScanRules.TranslateOutputPath(outputPath, mappings);
        var body = DownloadedScanRules.CommandBody(arrScope, managerPath, downloadId);
        var client = new MediaManagerHttpClient(connection.BaseUrl, connection.ApiKey, _handlers, ManagerDialectRules.DescribeTimeout, connection.Reference);
        try
        {
            var reply = await client.PostJsonAsync(DownloadedScanRules.CommandPath, body, cancellationToken: cancellationToken).ConfigureAwait(false);
            return DownloadedScanRules.IsAnotherScan(reply, managerPath, out _)
                ? new ArrDownloadedScanResult(body, Accepted: false, $"{connection.Label} is busy with another scan.", Busy: true)
                : new ArrDownloadedScanResult(body, Accepted: true, FailureReason: null);
        }
        catch (MediaManagerRateLimitedException exception)
        {
            var retryAfter = exception.RetryAfterSeconds is > 0 and var seconds ? TimeSpan.FromSeconds(Math.Min(seconds, DownloadedScanRetrySchedule.LongestWait.TotalSeconds)) : (TimeSpan?)null;
            return new ArrDownloadedScanResult(body, Accepted: false, $"{connection.Label} is busy.", Busy: true, retryAfter);
        }
        catch (Exception exception) when (exception is MediaManagerHttpException or MediaManagerUnreachableException)
        {
            return new ArrDownloadedScanResult(body, Accepted: false, exception.Message);
        }
    }

    /// <summary>The manager's remote path mappings, or none when they cannot be read.</summary>
    private async Task<List<RemotePathMappingEntry>> MappingsAsync(ManagerConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var client = new MediaManagerHttpClient(connection.BaseUrl, connection.ApiKey, _handlers, ManagerDialectRules.DescribeTimeout, connection.Reference);
            var raw = await client.GetJsonAsync(ManagerSetupRules.RemotePathMappingPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ManagerSetupRules.ParseMappings(raw);
        }
        catch (Exception exception) when (exception is MediaManagerHttpException or MediaManagerUnreachableException)
        {
            return [];
        }
    }
}
