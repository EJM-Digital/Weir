using System.Globalization;
using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>What Weir asked a Sonarr or Radarr to scan, and whether it took the request.</summary>
/// <param name="Command">The <c>POST /api/v3/command</c> body Weir sent.</param>
/// <param name="Accepted">True when the manager is scanning what Weir asked it to.</param>
/// <param name="FailureReason">Why it is not, in plain words; null when it is.</param>
public sealed record ArrDownloadedScanResult(WireObject Command, bool Accepted, string? FailureReason);

/// <summary>
/// The manager calls of the downloaded-scan hand-back: read the manager's remote path mappings, translate Weir's output folder
/// through them, and post the Downloaded Scan command. The mappings are only read; a manager that cannot be asked for them still
/// gets the command with Weir's own path. A manager keeps one scan at a time and answers a second request with the scan already
/// there, so when the answer is another scan's Weir waits for that scan to finish and asks again. It never throws for a manager that
/// is unreachable or refuses.
/// </summary>
public sealed class ArrDownloadedScan
{
    private readonly IManagerHttpHandlerFactory _handlers;
    private readonly TimeProvider _time;

    public ArrDownloadedScan(IManagerHttpHandlerFactory handlers, TimeProvider? time = null)
    {
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Asks <paramref name="connection"/> to scan <paramref name="outputPath"/>, as the manager sees it through its mappings.</summary>
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
            for (var attempt = 1; ; attempt++)
            {
                var reply = await client.PostJsonAsync(DownloadedScanRules.CommandPath, body, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!DownloadedScanRules.IsAnotherScan(reply, managerPath, out var inTheWay))
                {
                    return new ArrDownloadedScanResult(body, Accepted: true, FailureReason: null);
                }

                if (attempt == DownloadedScanRules.MaxAttempts)
                {
                    return new ArrDownloadedScanResult(
                        body,
                        Accepted: false,
                        $"{connection.Label} was still busy with another scan and did not take this one. Weir asked {DownloadedScanRules.MaxAttempts.ToString(CultureInfo.InvariantCulture)} times.");
                }

                await WaitForScanAsync(client, inTheWay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is MediaManagerHttpException or MediaManagerUnreachableException)
        {
            return new ArrDownloadedScanResult(body, Accepted: false, exception.Message);
        }
    }

    /// <summary>Waits until the manager says the scan in the way is over, for at most <see cref="DownloadedScanRules.WaitLimit"/>.</summary>
    private async Task WaitForScanAsync(MediaManagerHttpClient client, long? commandId, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        while (waited < DownloadedScanRules.WaitLimit)
        {
            await Task.Delay(DownloadedScanRules.PollInterval, _time, cancellationToken).ConfigureAwait(false);
            waited += DownloadedScanRules.PollInterval;
            if (commandId is not { } id)
            {
                return;
            }

            try
            {
                var status = await client.GetJsonAsync($"{DownloadedScanRules.CommandPath}/{id.ToString(CultureInfo.InvariantCulture)}", cancellationToken: cancellationToken).ConfigureAwait(false);
                if (DownloadedScanRules.IsFinished(status))
                {
                    return;
                }
            }
            catch (MediaManagerHttpException exception) when (exception.StatusCode == 404)
            {
                // A command the manager no longer has is over.
                return;
            }
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
