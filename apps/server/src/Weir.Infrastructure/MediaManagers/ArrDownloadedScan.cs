using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>What Weir asked a Sonarr or Radarr to scan, and whether it accepted the request.</summary>
/// <param name="Command">The <c>POST /api/v3/command</c> body Weir sent.</param>
/// <param name="Accepted">True when the manager accepted the command.</param>
/// <param name="FailureReason">Why it did not, in the exception's words; null when it did.</param>
public sealed record ArrDownloadedScanResult(WireObject Command, bool Accepted, string? FailureReason);

/// <summary>
/// The manager calls of the downloaded-scan hand-back: read the manager's remote path mappings, translate Weir's output folder
/// through them, and post the Downloaded Scan command. The mappings are only read; a manager that cannot be asked for them still
/// gets the command with Weir's own path. It never throws for a manager that is unreachable or refuses.
/// </summary>
public sealed class ArrDownloadedScan
{
    private readonly IManagerHttpHandlerFactory _handlers;

    public ArrDownloadedScan(IManagerHttpHandlerFactory handlers)
    {
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    }

    /// <summary>Asks <paramref name="connection"/> to scan <paramref name="outputPath"/>, as the manager sees it through its mappings.</summary>
    public async Task<ArrDownloadedScanResult> RequestAsync(
        ManagerConnection connection, string arrScope, string outputPath, int? downloadClientId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var mappings = await MappingsAsync(connection, cancellationToken).ConfigureAwait(false);
        var managerPath = DownloadedScanRules.TranslateOutputPath(outputPath, mappings);
        var body = DownloadedScanRules.CommandBody(arrScope, managerPath, downloadClientId);
        try
        {
            var client = new MediaManagerHttpClient(connection.BaseUrl, connection.ApiKey, _handlers, ManagerDialectRules.DescribeTimeout, connection.Reference);
            await client.PostJsonAsync(DownloadedScanRules.CommandPath, body, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ArrDownloadedScanResult(body, Accepted: true, FailureReason: null);
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
