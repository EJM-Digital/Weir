using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Rules;
using Weir.Core.Validation;
using Weir.Api.Http;
using Weir.Infrastructure.Browse;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Endpoints;

/// <summary>
/// "Try on a file": preview what a library's saved or unsaved rules would do to a real media file,
/// without processing, queueing or writing anything (issue #502). Built on the rules engine
/// (<see cref="RemuxRules.PlanRemux"/>) and the ffprobe layer (<see cref="MediaTools"/>).
/// </summary>
public static class ProcessingRulesPreviewEndpoints
{
    public static IEndpointRouteBuilder MapProcessingRulesPreviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingRulesPreviewEndpointHandlers>();
        endpoints.MapV1("POST", "/processing/libraries/{library_id}/preview", handlers.PostPreviewAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingRulesPreviewEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ProcessingRulesPreviewEndpointHandlers
{
    private readonly RulesPreviewGate _gate;
    private readonly MediaTools _mediaTools;
    private readonly IOriginalLanguageLookup _originalLanguageLookup;
    private readonly LibraryStore _libraries;

    public ProcessingRulesPreviewEndpointHandlers(
        RulesPreviewGate gate, MediaTools mediaTools, IOriginalLanguageLookup originalLanguageLookup, LibraryStore libraries)
    {
        _gate = gate;
        _mediaTools = mediaTools;
        _originalLanguageLookup = originalLanguageLookup;
        _libraries = libraries;
    }

    private static ILogger PreviewLogger(ApiRequest request) => request.LoggerFactory.CreateLogger("weir.processing.rules_preview");

    public async Task<ApiResult> PostPreviewAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var pathIssues = new ValidationIssues();
        var libraryId = request.PathInt("library_id", pathIssues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var relativePath = model.OptionalStr("relative_path", maxLength: 4000);
        var absolutePath = model.OptionalStr("absolute_path", maxLength: 4000);
        var ruleSetId = model.OptionalInt("rule_set_id", ge: 1);

        // "rules" is optional and, when present, validated exactly like a rule-set save (the same field
        // readers as PUT /processing/rule-sets/{id}), just never written to the database. It is read by hand
        // rather than through BodyModel.Dict, which always requires the key.
        LibraryRules.RuleSetInput? ruleSetInput = null;
        if (payload is WireObject topLevel && topLevel.TryGetValue("rules", out var rawRules) && rawRules is not WireNull)
        {
            if (rawRules is not WireObject rulesDict)
            {
                issues.Add(new ValidationIssue("dict_type", ["body", "rules"], "Input should be a valid dictionary", rawRules));
            }
            else
            {
                var rulesModel = new BodyModel(rulesDict, issues);
                ruleSetInput = ProcessingRuleSetsEndpoints.ReadRuleSetBody(rulesModel, issues);
                rulesModel.Finish(ExtraFields.Forbid);
            }
        }

        // Ignore, not Forbid: "rules" is read by hand above (BodyModel.Dict always requires the key, and
        // this one is optional), so BodyModel never marks it declared and Forbid would misreport it as an
        // unknown field. The nested rules object is still validated strictly (see rulesModel.Finish above).
        model.Finish(ExtraFields.Ignore);
        pathIssues.ThrowIfAny();
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var hasRelative = !string.IsNullOrWhiteSpace(relativePath);
        var hasAbsolute = !string.IsNullOrWhiteSpace(absolutePath);
        if (hasRelative == hasAbsolute)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "Give either relative_path or absolute_path, not both or neither.");
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        var library = await EndpointLookups.RequireLibraryAsync(uow, _libraries, libraryId).ConfigureAwait(false);

        var resolvedPath = hasRelative
            ? ResolveWithinLibraryFolders(library, relativePath!)
            : ResolveViaLocalBrowseAllowList(absolutePath!);

        if (!_gate.TryEnter())
        {
            throw new ApiException(StatusCodes.Status409Conflict, "Another rules preview is already running. Try again in a moment.");
        }

        try
        {
            return await RunPreviewAsync(request, uow, library, resolvedPath, new RulesChoice(ruleSetInput, ruleSetId)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary><c>relative_path</c> must resolve under the library's watched folder or its output
    /// folder, and the resulting file must exist. A library with neither folder configured, or a path
    /// that escapes both, is refused with a plain 400 detail (not a 422 field error — this is a
    /// business rule, not a shape check).</summary>
    private static string ResolveWithinLibraryFolders(ProcessingLibraryRecord library, string relativePath)
    {
        foreach (var root in new[] { library.WatchedFolder, library.OutputFolder })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = RemuxPassPaths.ResolveMediaFileUnderRoot(root, relativePath);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new ApiException(
            StatusCodes.Status400BadRequest,
            "That path is not an existing file inside this workflow's watched or output folder.");
    }

    /// <summary><c>absolute_path</c> goes through the same allow-list the local file picker (<c>GET
    /// /system/directories</c>) already enforces: an absolute path under a real drive root (Windows) or
    /// the filesystem root, naming a file that exists.</summary>
    private static string ResolveViaLocalBrowseAllowList(string absolutePath)
    {
        try
        {
            return DirectoryBrowser.ValidateFilePath(absolutePath);
        }
        catch (DirectoryBrowseException exception)
        {
            throw new ApiException(exception.StatusCode, exception.Message);
        }
    }

    private async Task<ApiResult> RunPreviewAsync(
        ApiRequest request,
        UnitOfWork uow,
        ProcessingLibraryRecord library,
        string resolvedPath,
        RulesChoice choice)
    {
        var scope = string.Equals(library.MediaType, ProcessingMediaScopes.Tv, StringComparison.OrdinalIgnoreCase) ? ProcessingMediaScopes.Tv : ProcessingMediaScopes.Movie;

        var config = await BuildConfigAsync(uow, library, choice).ConfigureAwait(false);

        System.Text.Json.JsonElement probeJson;
        try
        {
            probeJson = await _mediaTools.FfprobeJsonAsync(
                resolvedPath,
                probeSizeMb: request.Options.ProcessingProbeSizeMb,
                analyzeDurationSeconds: request.Options.ProcessingAnalyzeDurationSeconds,
                cancellationToken: request.Context.RequestAborted).ConfigureAwait(false);
        }
        catch (MediaUnreadableException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, ToolFailureText.Plain(exception));
        }
#pragma warning disable CA1031 // Any ffprobe failure is the operator's 400, not a server error.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            PreviewLogger(request).LogWarning(exception, "Reading {File} for a rules preview failed.", MediaPathNames.Name(resolvedPath, OperatingSystem.IsWindows()));
            throw new ApiException(StatusCodes.Status400BadRequest, "Weir could not read this file. Check it is a complete media file that Weir can open.");
        }

        var probe = new ProbeResult(probeJson);
        var (video, audio, subtitles) = RemuxRules.SplitStreams(probe);
        if (video.Count == 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "This file contains no video stream, so Weir cannot preview a plan for it.");
        }

        WireObject? originalLanguageOut = null;
        if (config.OriginalLanguage is { Enabled: true } originalRules)
        {
            (config, originalLanguageOut) = await ApplyOriginalLanguageAsync(request, config, originalRules, scope, library.Id, resolvedPath, audio).ConfigureAwait(false);
        }

        var plan = RemuxRules.PlanRemux(video, audio, subtitles, config, RemuxRules.AttachmentStreams(probe));
        if (plan is null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "No audio track would remain with these rules, so Weir cannot preview a plan for this file.");
        }

        var remuxRequired = RemuxRules.IsRemuxRequired(plan, audio, subtitles);
        var duration = RemuxPassMedia.ProbeDurationSeconds(probe);
        var rows = RulesPreview.BuildTrackRows(probe, plan);
        var estimatedReduction = RulesPreview.EstimateDroppedBytes(probe, plan, duration);

        var result = new WireObject()
            .Set("library_id", library.Id)
            .Set("media_scope", scope)
            .Set("inspected_path", resolvedPath)
            .Set("tracks", new WireArray(rows.Select(row => (WireValue)row.ToOut())))
            .Set("notes", new WireArray(plan.AudioSelectionNotes.Select(n => (WireValue)new WireString(n))))
            .Set("metadata_notes", new WireArray(plan.MetadataNotes.Select(n => (WireValue)new WireString(n))))
            .Set("remux_required", remuxRequired)
            .Set("estimated_size_reduction_bytes", estimatedReduction is { } bytes ? WireValue.Of(bytes) : WireValue.Null)
            .Set("estimated_size_reduction_is_estimate", true);
        if (originalLanguageOut is not null)
        {
            result.Set("original_language", originalLanguageOut);
        }

        return ApiRoutes.Ok(result);
    }

    /// <summary>
    /// Unsaved rules (validated exactly like a save) take priority, then a saved profile the request names (the Library
    /// page previews with the profile its library cleans by); otherwise the workflow's saved rule set, otherwise the shipped
    /// defaults — the same fallback order a live pass uses.
    /// </summary>
    private async Task<ProcessingRulesConfig> BuildConfigAsync(UnitOfWork uow, ProcessingLibraryRecord library, RulesChoice choice)
    {
        var ruleSetInput = choice.Unsaved;
        if (ruleSetInput is not null)
        {
            ProcessingRuleSetRecord previewRow;
            try
            {
                var label = ruleSetInput.Name.Trim();
                previewRow = LibraryRules.ApplyRuleSetFields(new ProcessingRuleSetRecord { Name = label.Length > 0 ? label : "Preview" }, ruleSetInput);
            }
            catch (ProcessingLibraryException exception)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
            }

            return RemuxPassPaths.RulesConfigFor(previewRow);
        }

        if (choice.SavedRuleSetId is { } requestedId)
        {
            var requested = await _libraries.GetRuleSetAsync(uow, requestedId).ConfigureAwait(false)
                ?? throw new ApiException(StatusCodes.Status400BadRequest, "That rules profile no longer exists. Choose another one.");
            return RemuxPassPaths.RulesConfigFor(requested);
        }

        if (library.RuleSetId is { } ruleSetId && await _libraries.GetRuleSetAsync(uow, ruleSetId).ConfigureAwait(false) is { } savedRow)
        {
            return RemuxPassPaths.RulesConfigFor(savedRow);
        }

        return RemuxRules.DefaultConfig();
    }

    /// <summary>Which rules a preview runs: edits not yet saved, or a saved profile by id; neither means the workflow's own.</summary>
    private sealed record RulesChoice(LibraryRules.RuleSetInput? Unsaved, long? SavedRuleSetId);

    /// <summary>
    /// #537 item 4's lookup, run the same way a live pass runs it: declining (lookup off, no match,
    /// unreachable) leaves the configured language preferences in charge, with a note saying so, rather
    /// than failing the preview.
    /// </summary>
    private async Task<(ProcessingRulesConfig Config, WireObject Record)> ApplyOriginalLanguageAsync(
        ApiRequest request,
        ProcessingRulesConfig config,
        OriginalLanguageRules rules,
        string scope,
        long libraryId,
        string resolvedPath,
        IReadOnlyList<ProbeStreamInfo> audio)
    {
        LookupResult lookup;
        try
        {
            lookup = await _originalLanguageLookup.LookupAsync(scope, libraryId, resolvedPath, origin: null, request.Context.RequestAborted).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A failed metadata lookup is shown as unreachable; the preview still answers.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            PreviewLogger(request).LogWarning(exception, "The original-language lookup failed for a rules preview.");
            lookup = new LookupResult { Status = LookupResult.StatusUnreachable, Detail = "Weir could not look up the original language just now." };
        }

        var tracks = audio
            .Where(stream => stream.Index is not null)
            .Select(stream => new OriginalLanguageTrack((int)stream.Index!.Value, stream.Tag("language") ?? string.Empty))
            .ToList();
        var outcome = OriginalLanguage.SelectTracks(rules, lookup, tracks);
        var record = new WireObject()
            .Set("lookup_status", lookup.Status)
            .Set("lookup_detail", lookup.Detail)
            .Set("original_language", lookup.Metadata?.OriginalLanguage)
            .Set("note", outcome.Note);
        return (config.WithOriginalLanguage(outcome), record);
    }
}
