using System.Net;
using Microsoft.Extensions.Logging;
using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>Asks Deluno where its libraries' downloads land: one read for one library, or for all of them.</summary>
internal static partial class DelunoDestinationsReader
{
    /// <summary>
    /// <c>GET /api/integrations/processors/download-destinations</c>, for <paramref name="libraryKey"/> or, when null, every
    /// library. A 404 that says <c>library_not_found</c> is a Deluno that no longer has the library, any other 404 is a Deluno that
    /// predates the route, and a 403 is a key without the Imports scope; none of them throws.
    /// </summary>
    public static async Task<DelunoDestinationsAnswer> ReadAsync(
        ManagerConnection connection, string? libraryKey, IManagerHttpHandlerFactory handlers, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var client = new MediaManagerHttpClient(connection.BaseUrl, connection.ApiKey, handlers, ManagerDialectRules.DownloadDestinationsTimeout, connection.Reference);
            KeyValuePair<string, object>[] query = libraryKey is null ? [] : [new("libraryId", libraryKey)];
            var payload = await client.GetJsonAsync(DelunoDestinationRules.DownloadDestinationsPath, query, cancellationToken).ConfigureAwait(false);
            return DelunoDestinationsAnswer.Read(DelunoDestinationRules.ParseLibraries(payload));
        }
        catch (MediaManagerHttpException exception) when (exception.StatusCode == (int)HttpStatusCode.NotFound && exception.ErrorCode == DelunoDestinationRules.LibraryNotFoundCode)
        {
            LogLibraryNotFound(logger, connection.Label, libraryKey);
            return DelunoDestinationsAnswer.Failed(DelunoDestinationsStatus.LibraryNotFound);
        }
        catch (MediaManagerHttpException exception) when (exception.StatusCode == (int)HttpStatusCode.NotFound)
        {
            LogDestinationsNotOffered(logger, connection.Label);
            return DelunoDestinationsAnswer.Failed(DelunoDestinationsStatus.NotOffered);
        }
        catch (MediaManagerHttpException exception) when (exception.StatusCode == (int)HttpStatusCode.Forbidden)
        {
            LogDestinationsNeedScope(logger, connection.Label);
            return DelunoDestinationsAnswer.Failed(DelunoDestinationsStatus.NeedsImportsScope);
        }
        catch (Exception exception) when (exception is MediaManagerHttpException or MediaManagerUnreachableException)
        {
            LogDestinationsUnreachable(logger, exception, connection.Label);
            return DelunoDestinationsAnswer.Failed(
                DelunoDestinationsStatus.Unreachable, ManagerDialectRules.Unreachable(connection, exception, "where its downloads are saved"));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Label} says it has no library with the id {LibraryKey}, so the workflow linked to that library cannot be checked.")]
    private static partial void LogLibraryNotFound(ILogger logger, string label, string? libraryKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Label} has no download-destinations route, so it is older than the release that added it.")]
    private static partial void LogDestinationsNotOffered(ILogger logger, string label);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Label} refused the download-destinations read: its API key lacks the Imports scope.")]
    private static partial void LogDestinationsNeedScope(ILogger logger, string label);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Label} did not answer when Weir asked where its downloads are saved.")]
    private static partial void LogDestinationsUnreachable(ILogger logger, Exception error, string label);
}
