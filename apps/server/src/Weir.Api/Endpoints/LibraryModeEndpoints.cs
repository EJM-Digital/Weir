using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.LibraryMode;
using Weir.Core.Processing;
using Weir.Core.Validation;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using static Weir.Api.Endpoints.EndpointLookups;

namespace Weir.Api.Endpoints;

/// <summary>
/// Library mode (#505): a library's folder settings and manual scan trigger. See
/// <c>docs/archive/server-port-notes.md</c>, "Library mode", for the storage decision behind it. The dashboard
/// reads (overview/problems) live in <see cref="LibraryModeOverviewEndpoints"/>; the files table, Clean, leave-alone
/// and the schedule toggle live in <see cref="LibraryModeFilesEndpoints"/>; the #509 redownload flow lives in
/// <see cref="LibraryModeRedownloadsEndpoints"/>. Shared shaping is in <see cref="LibraryModeMapping"/>.
/// </summary>
public static class LibraryModeEndpoints
{
    public static IEndpointRouteBuilder MapLibraryModeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<LibraryModeEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/libraries/{library_id}/library-settings", handlers.GetSettingsAsync);
        endpoints.MapV1("PUT", "/processing/libraries/{library_id}/library-settings", handlers.PutSettingsAsync);
        endpoints.MapV1("POST", "/processing/libraries/{library_id}/library-scan", handlers.PostScanAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="LibraryModeEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class LibraryModeEndpointHandlers
{
    private readonly LibrarySettingsStore _librarySettings;
    private readonly LibraryScanStore _scans;
    private readonly ProcessingJobStore _jobs;
    private readonly LibraryStore _libraries;

    public LibraryModeEndpointHandlers(LibrarySettingsStore librarySettings, LibraryScanStore scans, ProcessingJobStore jobs, LibraryStore libraries)
    {
        _librarySettings = librarySettings ?? throw new ArgumentNullException(nameof(librarySettings));
        _scans = scans ?? throw new ArgumentNullException(nameof(scans));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
    }

    public async Task<ApiResult> GetSettingsAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var libraryId = request.PathInt("library_id", issues);
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        await RequireLibraryAsync(uow, _libraries, libraryId, LibraryModeMapping.NoLibraryWithThatId).ConfigureAwait(false);
        var settings = await _librarySettings.GetAsync(uow, libraryId).ConfigureAwait(false);
        return ApiRoutes.Ok(LibraryModeMapping.SettingsOut(settings));
    }

    public async Task<ApiResult> PutSettingsAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var libraryId = request.PathInt("library_id", issues);
        var model = new BodyModel(payload, issues);
        var folders = model.StrList("library_folders", []);
        var cleanHardlinkedFiles = model.OptionalBool("clean_hardlinked_files");
        var skipIfManagerWouldRedownload = model.OptionalBool("skip_if_manager_would_redownload");
        var keepOriginalAfterClean = model.OptionalBool("keep_original_after_clean");
        var originalsFolder = model.OptionalStr("originals_folder", maxLength: 4000);
        var ruleSetGiven = model.Has("library_rule_set_id");
        var ruleSetId = model.OptionalInt("library_rule_set_id", ge: 1);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var library = await RequireLibraryAsync(uow, _libraries, libraryId, LibraryModeMapping.NoLibraryWithThatId).ConfigureAwait(false);
        IReadOnlyList<string> validated;
        string? validatedOriginalsFolder = null;
        try
        {
            validated = LibraryFolderRules.Validate(folders, library, request.Options.WeirHome);
            if (originalsFolder is not null)
            {
                validatedOriginalsFolder = LibraryFolderRules.ValidateOriginalsFolder(originalsFolder, validated, library, request.Options.WeirHome);
            }
        }
        catch (LibraryModeException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        if (ruleSetGiven && ruleSetId is { } wanted)
        {
            await RequireRuleSetAsync(uow, wanted).ConfigureAwait(false);
        }

        var existing = await _librarySettings.GetAsync(uow, libraryId).ConfigureAwait(false);
        var updated = existing with
        {
            Folders = validated,
            CleanHardlinkedFiles = cleanHardlinkedFiles ?? existing.CleanHardlinkedFiles,
            SkipIfManagerWouldRedownload = skipIfManagerWouldRedownload ?? existing.SkipIfManagerWouldRedownload,
            KeepOriginalAfterClean = keepOriginalAfterClean ?? existing.KeepOriginalAfterClean,
            OriginalsFolder = validatedOriginalsFolder ?? existing.OriginalsFolder,
            RuleSetId = ruleSetGiven ? ruleSetId : existing.RuleSetId,
        };
        await _librarySettings.SetAsync(uow, libraryId, updated).ConfigureAwait(false);
        await ReplanIfRulesChangedAsync(uow, library, existing, updated).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(LibraryModeMapping.SettingsOut(updated));
    }

    private async Task RequireRuleSetAsync(UnitOfWork uow, long ruleSetId)
    {
        if (await _libraries.GetRuleSetAsync(uow, ruleSetId).ConfigureAwait(false) is null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "That rules profile no longer exists. Choose another one.");
        }
    }

    /// <summary>
    /// A different profile changes what every file in the index would do, so the library is checked again in the background
    /// (a normal scan, trigger "rule_change", like saving the profile's own rules). Nothing is cleaned by it.
    /// </summary>
    private async Task ReplanIfRulesChangedAsync(UnitOfWork uow, ProcessingLibraryRecord library, LibrarySettings before, LibrarySettings after)
    {
        if (after.Folders.Count == 0
            || LibraryModeRules.EffectiveRuleSetId(library, before) == LibraryModeRules.EffectiveRuleSetId(library, after))
        {
            return;
        }

        await _scans.RequestScanAsync(uow, _jobs, library.Id, "rule_change").ConfigureAwait(false);
    }

    public async Task<ApiResult> PostScanAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var libraryId = request.PathInt("library_id", issues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var library = await RequireLibraryAsync(uow, _libraries, libraryId, LibraryModeMapping.NoLibraryWithThatId).ConfigureAwait(false);
        var settings = await _librarySettings.GetAsync(uow, libraryId).ConfigureAwait(false);
        if (settings.Folders.Count == 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "Add at least one library folder before scanning.");
        }

        var active = await _scans.ActiveScanAsync(uow, library.Id).ConfigureAwait(false);
        if (active is not null)
        {
            await request.CommitAsync().ConfigureAwait(false);
            return ApiRoutes.Ok(new WireObject().Set("job_id", active.JobId).Set("status", active.Status).Set("already_running", true));
        }

        var job = await _scans.RequestScanAsync(uow, _jobs, library.Id, "manual").ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("job_id", job.Id).Set("status", job.Status).Set("already_running", false));
    }
}
