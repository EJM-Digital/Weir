using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Time;
using Weir.Core.Validation;
using Weir.Infrastructure.Artwork;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Processing;

namespace Weir.Api.Endpoints;

/// <summary>
/// Activity's library cleans, <c>/api/v1/processing/library-cleans</c> (#695): what the newest clean did to each library
/// file, filtered the way <c>/processing/files</c> filters downloads, so Activity lists both kinds side by side.
/// </summary>
public static class ProcessingLibraryCleansEndpoints
{
    public static IEndpointRouteBuilder MapProcessingLibraryCleansEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingLibraryCleansEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/library-cleans", handlers.GetLibraryCleansAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingLibraryCleansEndpoints"/>, constructor-injected with the store it needs.</summary>
internal sealed class ProcessingLibraryCleansEndpointHandlers
{
    private const int PathContainsMaxLength = 400;
    private const int WithinDaysMax = 3650;

    private readonly LibraryCleanHistoryStore _cleanHistory;
    private readonly FileStateStore _files;
    private readonly ArtworkPosterUrls _posters;

    public ProcessingLibraryCleansEndpointHandlers(LibraryCleanHistoryStore cleanHistory, FileStateStore files, ArtworkPosterUrls posters)
    {
        _cleanHistory = cleanHistory;
        _files = files;
        _posters = posters;
    }

    public async Task<ApiResult> GetLibraryCleansAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        long? libraryId = request.Query("library_id") is { } rawLibrary && FieldRules.TryInt(new WireString(rawLibrary), ["query", "library_id"], 1, null, issues, out var parsedLibrary)
            ? (long)parsedLibrary
            : null;
        string? pathContains = request.Query("path_contains") is { } rawPath && FieldRules.TryStr(new WireString(rawPath), ["query", "path_contains"], null, PathContainsMaxLength, issues, out var parsedPath)
            ? parsedPath
            : null;
        long? withinDays = request.Query("within_days") is { } rawWithin && FieldRules.TryInt(new WireString(rawWithin), ["query", "within_days"], 1, WithinDaysMax, issues, out var parsedWithin)
            ? (long)parsedWithin
            : null;
        var limit = request.Query("limit") is { } rawLimit && FieldRules.TryInt(new WireString(rawLimit), ["query", "limit"], 1, LibraryCleanHistoryFilter.MaxLimit, issues, out var parsedLimit)
            ? (int)parsedLimit
            : LibraryCleanHistoryFilter.DefaultLimit;
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var rows = await _cleanHistory.ListAsync(uow, new LibraryCleanHistoryFilter
        {
            LibraryId = libraryId,
            PathContains = pathContains,
            Since = withinDays is { } days ? Timestamp.FromUtc(request.Time.GetUtcNow().AddDays(-days).UtcDateTime) : null,
            Limit = limit,
        }).ConfigureAwait(false);
        var libraryNames = await _files.LibraryNamesAsync(uow).ConfigureAwait(false);
        var posters = await _posters.ForFilesAsync(uow, rows.Where(row => row.LibraryId is not null).Select(row => (row.LibraryId!.Value, row.RelativePath))).ConfigureAwait(false);

        var cleans = rows.Select(row => (WireValue)new WireObject()
            .Set("kind", HistoryEntryKinds.LibraryClean)
            .Set("id", row.Id)
            .Set("library_id", row.LibraryId is { } id ? WireValue.Of(id) : WireValue.Null)
            .Set("library_name", row.LibraryId is { } known ? libraryNames.GetValueOrDefault(known, "Unknown workflow") : "Unknown workflow")
            .Set("relative_path", row.RelativePath)
            .Set("poster_url", row.LibraryId is { } posterLibrary && posters.TryGetValue((posterLibrary, row.RelativePath), out var posterUrl) ? posterUrl : null)
            .Set("outcome", row.Outcome)
            .Set("detail", row.Detail)
            .Set("trigger", row.Trigger)
            .Set("recorded_at", row.RecordedAt.ToWireText()));
        return ApiRoutes.Ok(new WireObject()
            .Set("cleans", new WireArray(cleans))
            .Set("returned", rows.Count)
            .Set("limit", limit));
    }
}
