using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Validation;
using Weir.Infrastructure.Processing;

namespace Weir.Api.Endpoints;

/// <summary>
/// First-run library setup: the libraries Weir can offer once a media manager or download client is connected, and a
/// dry-run check of the folders a person has confirmed, so problems show before anything is created. Both only read.
/// Creating the libraries is the ordinary <c>POST /processing/libraries</c>, so every rule that applies there applies here.
/// </summary>
public static class ProcessingLibrarySetupEndpoints
{
    public static IEndpointRouteBuilder MapProcessingLibrarySetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingLibrarySetupEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/library-suggestions", handlers.GetLibrarySuggestionsAsync);
        endpoints.MapV1("POST", "/processing/library-check", handlers.PostLibraryCheckAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingLibrarySetupEndpoints"/>, constructor-injected with the services they need.</summary>
internal sealed class ProcessingLibrarySetupEndpointHandlers
{
    private const int MaxFolderLength = 4000;

    private readonly LibrarySuggestions _suggestions;
    private readonly ProposedLibraryCheck _check;

    public ProcessingLibrarySetupEndpointHandlers(LibrarySuggestions suggestions, ProposedLibraryCheck check)
    {
        _suggestions = suggestions;
        _check = check;
    }

    private static string FolderField(string mediaType, string kind) => $"{mediaType}_{kind}_folder";

    private static WireObject SuggestedLibraryOut(SuggestedLibrary library) => new WireObject()
        .Set("library_id", library.LibraryId)
        .Set("name", library.Name)
        .Set("media_type", library.MediaType)
        .Set("watched_folder", library.WatchedFolder)
        .Set("output_folder", library.OutputFolder)
        .Set("source_label", library.SourceLabel)
        .Set("manager_connection_ids", new WireArray(library.ManagerConnectionIds.Select(id => (WireValue)WireValue.Of(id))));

    /// <summary><c>GET /processing/library-suggestions</c>: one library per media type the connected managers and download
    /// clients know a folder for, and a plain sentence for each connection that could not say.</summary>
    public async Task<ApiResult> GetLibrarySuggestionsAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var suggested = await _suggestions.SuggestAsync(uow, request.Context.RequestAborted).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject()
            .Set("libraries", new WireArray(suggested.Libraries.Select(library => (WireValue)SuggestedLibraryOut(library))))
            .Set("notes", new WireArray(suggested.Notes.Select(note => (WireValue)WireValue.Of(note)))));
    }

    /// <summary><c>POST /processing/library-check</c>: for the Movies and TV folders given (<c>movie_watched_folder</c>,
    /// <c>movie_output_folder</c>, <c>tv_watched_folder</c>, <c>tv_output_folder</c>; a media type with neither is left out),
    /// what creating those libraries would be refused for and, where it would not, the folder chain. Saves nothing.</summary>
    public async Task<ApiResult> PostLibraryCheckAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var proposals = new List<ProposedLibrary>();
        foreach (var mediaType in ProcessingMediaScopes.All)
        {
            var watched = model.OptionalStr(FolderField(mediaType, "watched"), string.Empty, maxLength: MaxFolderLength)!.Trim();
            var output = model.OptionalStr(FolderField(mediaType, "output"), string.Empty, maxLength: MaxFolderLength)!.Trim();
            if (watched.Length > 0 || output.Length > 0)
            {
                proposals.Add(new ProposedLibrary(mediaType, watched, output));
            }
        }

        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var checks = await _check.CheckAsync(uow, proposals, request.Context.RequestAborted).ConfigureAwait(false);
        var result = new WireObject();
        foreach (var mediaType in ProcessingMediaScopes.All)
        {
            var index = proposals.FindIndex(proposal => proposal.MediaType == mediaType);
            result.Set(mediaType, index < 0 ? WireNull.Instance : (WireValue)checks[index]);
        }

        return ApiRoutes.Ok(result);
    }
}
