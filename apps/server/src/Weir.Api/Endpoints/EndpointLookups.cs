using Microsoft.AspNetCore.Http;
using Weir.Api.Http;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Validation;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Endpoints;

/// <summary>Route lookups that several endpoint groups share: a row by id, or a 404 with a fixed detail.</summary>
internal static class EndpointLookups
{
    public const string NoSuchLibrary = "That workflow does not exist.";

    public const string NoSuchConnection = "That media manager connection does not exist.";

    /// <summary>The library with <paramref name="id"/>, or a 404 carrying <paramref name="notFoundDetail"/>.</summary>
    public static async Task<ProcessingLibraryRecord> RequireLibraryAsync(UnitOfWork uow, LibraryStore libraries, long id, string notFoundDetail = NoSuchLibrary) =>
        await libraries.GetAsync(uow, id).ConfigureAwait(false)
        ?? throw new ApiException(StatusCodes.Status404NotFound, notFoundDetail);

    /// <summary>The <c>connection_id</c> route value as an integer of at least 1; 0 with a validation issue otherwise.</summary>
    public static long ConnectionId(ApiRequest request, ValidationIssues issues)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = request.RouteValue("connection_id");
        return FieldRules.TryInt(new WireString(raw), ["path", "connection_id"], 1, null, issues, out var value)
            ? value > long.MaxValue ? long.MaxValue : (long)value
            : 0;
    }

    /// <summary>The media manager connection with <paramref name="connectionId"/>, or a 404.</summary>
    public static async Task<MediaManagerConnectionRecord> RequireConnectionAsync(UnitOfWork uow, MediaManagerConnectionStore connections, long connectionId) =>
        await connections.GetAsync(uow, connectionId).ConfigureAwait(false)
        ?? throw new ApiException(StatusCodes.Status404NotFound, NoSuchConnection);
}
