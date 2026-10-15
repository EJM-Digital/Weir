using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Rules;
using Weir.Core.Validation;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Endpoints;

/// <summary>Processing rule sets — <c>/api/v1/processing/rule-sets</c>: how a remux rewrites a file's audio,
/// subtitle and metadata tracks.</summary>
public static class ProcessingRuleSetsEndpoints
{
    public static IEndpointRouteBuilder MapProcessingRuleSetsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingRuleSetsEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/rule-sets", handlers.GetRuleSetsAsync);
        endpoints.MapV1("POST", "/processing/rule-sets", handlers.PostRuleSetAsync);
        endpoints.MapV1("PUT", "/processing/rule-sets/{rule_set_id}", handlers.PutRuleSetAsync);
        endpoints.MapV1("DELETE", "/processing/rule-sets/{rule_set_id}", handlers.DeleteRuleSetAsync);
        return endpoints;
    }

    /// <summary>Shared with <see cref="ProcessingRulesPreviewEndpoints"/>, which validates an unsaved rules
    /// payload the same way a save does, without touching the database. <paramref name="issues"/> must be
    /// the same collector <paramref name="model"/> itself reports to, so a bad nested
    /// <c>track_name_overrides</c> field surfaces as one of this request's own validation errors.</summary>
    internal static LibraryRules.RuleSetInput ReadRuleSetBody(BodyModel model, ValidationIssues issues)
    {
        var overridesDict = model.OptionalDict("track_name_overrides");
        var overrides = new TrackNameOverrides();
        if (overridesDict is not null)
        {
            var overridesModel = new BodyModel(overridesDict, issues);
            overrides = new TrackNameOverrides
            {
                Forced = overridesModel.OptionalStr("forced", defaultValue: overrides.Forced, maxLength: 200) ?? overrides.Forced,
                HearingImpaired = overridesModel.OptionalStr("hearing_impaired", defaultValue: overrides.HearingImpaired, maxLength: 200) ?? overrides.HearingImpaired,
                Commentary = overridesModel.OptionalStr("commentary", defaultValue: overrides.Commentary, maxLength: 200) ?? overrides.Commentary,
                AudioDescription = overridesModel.OptionalStr("audio_description", defaultValue: overrides.AudioDescription, maxLength: 200) ?? overrides.AudioDescription,
            };
            overridesModel.Finish(ExtraFields.Forbid);
        }

        return new LibraryRules.RuleSetInput
        {
            Name = model.Str("name", minLength: 1, maxLength: 120),
            PrimaryAudioLang = model.OptionalStr("primary_audio_lang", defaultValue: "", maxLength: 24) ?? string.Empty,
            SecondaryAudioLang = model.OptionalStr("secondary_audio_lang", defaultValue: "", maxLength: 24) ?? string.Empty,
            TertiaryAudioLang = model.OptionalStr("tertiary_audio_lang", defaultValue: "", maxLength: 24) ?? string.Empty,
            DefaultAudioSlot = model.Literal("default_audio_slot", ["primary", "secondary", "tertiary"], defaultValue: "primary"),
            RemoveCommentary = model.Bool("remove_commentary", defaultValue: false),
            SubtitleMode = model.Literal("subtitle_mode", ["keep_all", "keep_listed", "remove_all"], defaultValue: "keep_all"),
            SubtitleLangsCsv = model.OptionalStr("subtitle_langs_csv", defaultValue: "", maxLength: 500) ?? string.Empty,
            PreserveForcedSubs = model.Bool("preserve_forced_subs", defaultValue: true),
            PreserveDefaultSubs = model.Bool("preserve_default_subs", defaultValue: true),
            AudioSortersJson = model.OptionalStr("audio_sorters_json", defaultValue: "") ?? string.Empty,
            SubtitleSortersJson = model.OptionalStr("subtitle_sorters_json", defaultValue: "") ?? string.Empty,
            KeepOriginalLanguage = model.Bool("keep_original_language", defaultValue: false),
            OriginalLanguageAdditionalCsv = model.OptionalStr("original_language_additional_csv", defaultValue: "", maxLength: 200) ?? string.Empty,
            OriginalLanguageKeepOnlyFirst = model.Bool("original_language_keep_only_first", defaultValue: true),
            OriginalLanguageFirstIfNone = model.Bool("original_language_first_if_none", defaultValue: true),
            OriginalLanguageTreatEmptyAsOriginal = model.Bool("original_language_treat_empty_as_original", defaultValue: false),
            RemoveImages = model.Bool("remove_images", defaultValue: false),
            RemoveAttachments = model.Bool("remove_attachments", defaultValue: false),
            RemoveTitle = model.Bool("remove_title", defaultValue: false),
            RemoveLanguageTags = model.Bool("remove_language_tags", defaultValue: false),
            RemoveOtherMetadata = model.Bool("remove_other_metadata", defaultValue: false),
            AudioPreferenceMode = model.Literal("audio_preference_mode", ["preferred_langs_quality", "preferred_langs_strict", "quality_all_languages"], defaultValue: "preferred_langs_quality"),

            // #495
            RemoveHearingImpairedSubs = model.Bool("remove_hearing_impaired_subs", defaultValue: false),

            // #497
            AudioKeepMode = model.Literal("audio_keep_mode", [RemuxRuleValues.AudioKeepModeSingle, RemuxRuleValues.AudioKeepModePerLanguage], defaultValue: RemuxRuleValues.AudioKeepModeSingle),
            SubtitleMaxPerLanguage = (int)model.Number("subtitle_max_per_language", defaultValue: 0, required: false, ge: 0),
            SubtitleQualityStrategy = model.Literal(
                "subtitle_quality_strategy",
                [RemuxRuleValues.SubtitleStrategyTextFirst, RemuxRuleValues.SubtitleStrategyImageFirst, RemuxRuleValues.SubtitleStrategyAccessibility],
                defaultValue: RemuxRuleValues.SubtitleStrategyTextFirst),

            // #498
            StandardizeTrackNames = model.Bool("standardize_track_names", defaultValue: false),
            TrackNameTemplate = model.OptionalStr("track_name_template", defaultValue: TrackNaming.DefaultTemplate, maxLength: 200) ?? TrackNaming.DefaultTemplate,
            TrackNameOverrides = overrides,
            ClearVideoTrackNames = model.Bool("clear_video_track_names", defaultValue: false),
            RemoveChapters = model.Bool("remove_chapters", defaultValue: false),
        };
    }
}

/// <summary>Handlers for <see cref="ProcessingRuleSetsEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ProcessingRuleSetsEndpointHandlers
{
    private readonly LibrarySettingsStore _librarySettings;
    private readonly LibraryScanStore _scans;
    private readonly ProcessingJobStore _jobs;
    private readonly LibraryStore _libraries;

    public ProcessingRuleSetsEndpointHandlers(LibrarySettingsStore librarySettings, LibraryScanStore scans, ProcessingJobStore jobs, LibraryStore libraries)
    {
        _librarySettings = librarySettings ?? throw new ArgumentNullException(nameof(librarySettings));
        _scans = scans ?? throw new ArgumentNullException(nameof(scans));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
    }

    private async Task<ProcessingRuleSetRecord> RequireRuleSetAsync(UnitOfWork uow, long id) =>
        await _libraries.GetRuleSetAsync(uow, id).ConfigureAwait(false)
        ?? throw new ApiException(StatusCodes.Status404NotFound, "That rule set does not exist.");

    private static WireObject RuleSetOut(ProcessingRuleSetRecord row, int usedByLibraryCount) => new WireObject()
        .Set("id", row.Id)
        .Set("name", row.Name)
        .Set("primary_audio_lang", row.PrimaryAudioLang)
        .Set("secondary_audio_lang", row.SecondaryAudioLang)
        .Set("tertiary_audio_lang", row.TertiaryAudioLang)
        .Set("default_audio_slot", row.DefaultAudioSlot)
        .Set("remove_commentary", row.RemoveCommentary)
        .Set("subtitle_mode", row.SubtitleMode)
        .Set("subtitle_langs_csv", row.SubtitleLangsCsv)
        .Set("preserve_forced_subs", row.PreserveForcedSubs)
        .Set("preserve_default_subs", row.PreserveDefaultSubs)
        .Set("audio_preference_mode", row.AudioPreferenceMode)
        .Set("audio_sorters_json", row.AudioSortersJson)
        .Set("subtitle_sorters_json", row.SubtitleSortersJson)
        .Set("keep_original_language", row.KeepOriginalLanguage)
        .Set("original_language_additional_csv", row.OriginalLanguageAdditionalCsv)
        .Set("original_language_keep_only_first", row.OriginalLanguageKeepOnlyFirst)
        .Set("original_language_first_if_none", row.OriginalLanguageFirstIfNone)
        .Set("original_language_treat_empty_as_original", row.OriginalLanguageTreatEmptyAsOriginal)
        .Set("remove_images", row.RemoveImages)
        .Set("remove_attachments", row.RemoveAttachments)
        .Set("remove_title", row.RemoveTitle)
        .Set("remove_language_tags", row.RemoveLanguageTags)
        .Set("remove_other_metadata", row.RemoveOtherMetadata)
        .Set("remove_hearing_impaired_subs", row.RemoveHearingImpairedSubs)
        .Set("audio_keep_mode", row.AudioKeepMode)
        .Set("subtitle_max_per_language", row.SubtitleMaxPerLanguage)
        .Set("subtitle_quality_strategy", row.SubtitleQualityStrategy)
        .Set("standardize_track_names", row.StandardizeTrackNames)
        .Set("track_name_template", row.TrackNameTemplate)
        .Set("track_name_overrides", new WireObject()
            .Set("forced", row.TrackNameOverrides.Forced)
            .Set("hearing_impaired", row.TrackNameOverrides.HearingImpaired)
            .Set("commentary", row.TrackNameOverrides.Commentary)
            .Set("audio_description", row.TrackNameOverrides.AudioDescription))
        .Set("clear_video_track_names", row.ClearVideoTrackNames)
        .Set("remove_chapters", row.RemoveChapters)
        .Set("used_by_library_count", usedByLibraryCount)
        .Set("updated_at", row.UpdatedAt.ToWireText());

    public async Task<ApiResult> GetRuleSetsAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var rows = await _libraries.ListRuleSetsAsync(uow).ConfigureAwait(false);
        var items = new List<WireValue>();
        foreach (var row in rows)
        {
            items.Add(RuleSetOut(row, await _libraries.RuleSetUsageCountAsync(uow, row.Id).ConfigureAwait(false)));
        }

        return ApiRoutes.Ok(new WireArray(items));
    }

    public async Task<ApiResult> PostRuleSetAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var body = ProcessingRuleSetsEndpoints.ReadRuleSetBody(model, issues);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        ProcessingRuleSetRecord row;
        try
        {
            row = await _libraries.CreateRuleSetAsync(uow, body).ConfigureAwait(false);
        }
        catch (ProcessingLibraryException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        await request.CommitAsync().ConfigureAwait(false);
        return new JsonApiResult(StatusCodes.Status201Created, RuleSetOut(row, await _libraries.RuleSetUsageCountAsync(uow, row.Id).ConfigureAwait(false)));
    }

    public async Task<ApiResult> PutRuleSetAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var pathIssues = new ValidationIssues();
        var id = request.PathInt("rule_set_id", pathIssues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var body = ProcessingRuleSetsEndpoints.ReadRuleSetBody(model, issues);
        model.Finish(ExtraFields.Forbid);
        pathIssues.ThrowIfAny();
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var existing = await RequireRuleSetAsync(uow, id).ConfigureAwait(false);
        ProcessingRuleSetRecord updated;
        try
        {
            updated = await _libraries.UpdateRuleSetAsync(uow, existing, body).ConfigureAwait(false);
        }
        catch (ProcessingLibraryException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        // #505 point 6: saving rules on a library with library folders runs a background re-plan (a normal scan, trigger
        // "rule_change"); the web shows "Apply to library? N files would change" once it finishes. Nothing runs on its own.
        var rescanJobIds = new List<long>();
        foreach (var library in await _libraries.ListAsync(uow).ConfigureAwait(false))
        {
            var settingsForLibrary = await _librarySettings.GetAsync(uow, library.Id).ConfigureAwait(false);
            if (LibraryModeRules.EffectiveRuleSetId(library, settingsForLibrary) != updated.Id || settingsForLibrary.Folders.Count == 0)
            {
                continue;
            }

            var job = await _scans.RequestScanAsync(uow, _jobs, library.Id, "rule_change").ConfigureAwait(false);
            rescanJobIds.Add(job.Id);
        }

        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(RuleSetOut(updated, await _libraries.RuleSetUsageCountAsync(uow, updated.Id).ConfigureAwait(false))
            .Set("library_rescan_job_ids", new WireArray(rescanJobIds.Select(id => (WireValue)new WireInteger(id)))));
    }

    public async Task<ApiResult> DeleteRuleSetAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("rule_set_id", issues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var row = await RequireRuleSetAsync(uow, id).ConfigureAwait(false);
        try
        {
            await _libraries.DeleteRuleSetAsync(uow, row).ConfigureAwait(false);
        }
        catch (ProcessingLibraryException exception)
        {
            throw new ApiException(StatusCodes.Status409Conflict, exception.Message);
        }

        await request.CommitAsync().ConfigureAwait(false);
        return new CustomApiResult(context =>
        {
            ApiResponses.NoContentJson(context);
            return Task.CompletedTask;
        });
    }
}
