using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Activity;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Rules;
using Weir.Core.Text;
using Weir.Core.Validation;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Api.Endpoints;

/// <summary>
/// Choose tracks by hand (#501) — <c>/api/v1/processing/files/{file_id}/tracks</c> and <c>/manual-plan</c>: what a
/// file's video/audio/subtitle/attachment streams are, what the library's rules would do with each, and queuing a
/// manual keep/order choice for a file on hold. See <see cref="ProcessingFilesEndpoints"/> for the rest of the
/// Files screen.
/// </summary>
public static class ProcessingFileTracksEndpoints
{
    public static IEndpointRouteBuilder MapProcessingFileTracksEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingFileTracksEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/files/{file_id}/tracks", handlers.GetFileTracksAsync);
        endpoints.MapV1("POST", "/processing/files/{file_id}/manual-plan", handlers.PostManualPlanAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingFileTracksEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ProcessingFileTracksEndpointHandlers
{
    private readonly FileStateStore _files;
    private readonly LibraryStore _libraries;
    private readonly MediaTools _mediaTools;
    private readonly ProcessingJobStore _jobs;
    private readonly ActivityStore _activity;

    public ProcessingFileTracksEndpointHandlers(
        FileStateStore files, LibraryStore libraries, MediaTools mediaTools, ProcessingJobStore jobs, ActivityStore activity)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _mediaTools = mediaTools ?? throw new ArgumentNullException(nameof(mediaTools));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
    }

    private static WireObject StreamOut(int index, string kind, ProbeStreamInfo stream, TrackDecision? decision)
    {
        var tags = stream.Tags;
        var disposition = stream.Disposition;
        long? channels = null;
        if (kind == "audio" && stream.Get("channels") is { } raw && raw.ValueKind == JsonValueKind.Number && raw.TryGetInt64(out var n))
        {
            channels = n;
        }

        return new WireObject()
            .Set("index", index)
            .Set("type", kind)
            .Set("codec", stream.CodecName.Length > 0 ? stream.CodecName : null)
            .Set("language", tags.GetValueOrDefault("language"))
            .Set("title", tags.GetValueOrDefault("title"))
            .Set("channels", channels)
            .Set("default", disposition.GetValueOrDefault("default") != 0)
            .Set("forced", disposition.GetValueOrDefault("forced") != 0)
            .Set("rule_would_keep", decision?.WouldKeep ?? false)
            .Set("rule_reason", decision?.Reason ?? "This pass does not act on this stream type.");
    }

    public async Task<ApiResult> GetFileTracksAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("file_id", issues);
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        ManualPlanFileContext context;
        try
        {
            context = await ManualPlanSupport.LoadAsync(uow, _files, _libraries, _mediaTools, request.Options, id, request.Context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (ManualPlanEnqueueException exception)
        {
            throw new ApiException(exception.StatusCode, exception.Message);
        }

        SourceFingerprint fingerprint;
        try
        {
            fingerprint = SourceFiles.Fingerprint(context.SourcePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, $"Weir could not read this file safely: {exception.Message}");
        }

        await request.CommitAsync().ConfigureAwait(false);

        var (video, audio, subtitles) = RemuxRules.SplitStreams(context.Probe);
        var attachments = RemuxRules.AttachmentStreams(context.Probe);
        var decisionsByKey = RemuxRules.ExplainTracks(video, audio, subtitles, context.Rules, attachments)
            .ToDictionary(d => (d.InputIndex, d.Kind));

        TrackDecision? DecisionFor(int index, params string[] kinds)
        {
            foreach (var kind in kinds)
            {
                if (decisionsByKey.TryGetValue((index, kind), out var found))
                {
                    return found;
                }
            }

            return null;
        }

        var rows = new List<(int Index, WireObject Row)>();
        var covered = new HashSet<int>();
        foreach (var stream in video)
        {
            if (stream.Index is not { } idx)
            {
                continue;
            }

            var decision = DecisionFor((int)idx, "video", "image");
            rows.Add(((int)idx, StreamOut((int)idx, decision?.Kind ?? "video", stream, decision)));
            covered.Add((int)idx);
        }

        foreach (var stream in audio)
        {
            if (stream.Index is not { } idx)
            {
                continue;
            }

            rows.Add(((int)idx, StreamOut((int)idx, "audio", stream, DecisionFor((int)idx, "audio"))));
            covered.Add((int)idx);
        }

        foreach (var stream in subtitles)
        {
            if (stream.Index is not { } idx)
            {
                continue;
            }

            rows.Add(((int)idx, StreamOut((int)idx, "subtitle", stream, DecisionFor((int)idx, "subtitle"))));
            covered.Add((int)idx);
        }

        foreach (var stream in attachments)
        {
            if (stream.Index is not { } idx)
            {
                continue;
            }

            rows.Add(((int)idx, StreamOut((int)idx, "attachment", stream, DecisionFor((int)idx, "attachment"))));
            covered.Add((int)idx);
        }

        foreach (var stream in context.Probe.Streams)
        {
            if (stream.Index is not { } idx || covered.Contains((int)idx))
            {
                continue;
            }

            rows.Add(((int)idx, StreamOut((int)idx, stream.CodecType.Length > 0 ? stream.CodecType : "other", stream, null)));
        }

        rows.Sort((a, b) => a.Index.CompareTo(b.Index));
        return ApiRoutes.Ok(new WireObject()
            .Set("file_id", context.File.Id)
            .Set("relative_path", context.File.RelativePath)
            .Set("media_scope", context.Library.MediaType)
            .Set("source_fingerprint", ManualPlanJson.ToPyDict(fingerprint))
            .Set("streams", new WireArray(rows.Select(r => (WireValue)r.Row))));
    }

    public async Task<ApiResult> PostManualPlanAsync(ApiRequest request)
    {
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var session = await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("file_id", issues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Ignore);
        issues.ThrowIfAny();

        if (payload is not WireObject body || body.Get("keep") is not WireArray keepList)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "'keep' is required and must be a list of {index, default, forced}.");
        }

        var keep = new List<ManualKeepEntry>();
        foreach (var item in keepList.Items)
        {
            if (item is not WireObject entry || entry.Get("index") is not WireInteger indexValue)
            {
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, "Each item in 'keep' must be an object with an integer 'index'.");
            }

            var isDefault = entry.Get("default") is WireBool { Value: true };
            var forced = entry.Get("forced") is WireBool { Value: true };
            keep.Add(new ManualKeepEntry((int)indexValue.Value, isDefault, forced));
        }

        if (body.Get("order") is not WireArray orderList)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "'order' is required and must be a list of integers.");
        }

        var order = new List<int>();
        foreach (var item in orderList.Items)
        {
            if (item is not WireInteger orderIndex)
            {
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, "'order' must be a list of integers.");
            }

            order.Add((int)orderIndex.Value);
        }

        var choice = new ManualPlanChoice(keep, order);

        request.RequireConfirmationToken(csrfToken, "Invalid or expired CSRF token.");

        var uow = await request.DbAsync().ConfigureAwait(false);
        ManualPlanFileContext context;
        try
        {
            context = await ManualPlanSupport.LoadAsync(uow, _files, _libraries, _mediaTools, request.Options, id, request.Context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (ManualPlanEnqueueException exception)
        {
            throw new ApiException(exception.StatusCode, exception.Message);
        }

        if (context.File.Status != ProcessingFileStatuses.OnHold)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "This file is not on hold, so a manual track choice does not apply. Refresh Activity and try again.");
        }

        var streams = RemuxRules.SplitStreams(context.Probe);
        var kinds = ManualTrackPlan.ClassifyIndices(streams);
        if (!ManualTrackPlan.TryValidate(choice, kinds, out var problem))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, problem);
        }

        SourceFingerprint fingerprint;
        try
        {
            fingerprint = SourceFiles.Fingerprint(context.SourcePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, $"Weir could not read this file safely: {exception.Message}");
        }

        var job = await ManualPlanSupport.EnqueueManualPlanAsync(uow, _jobs, context, choice, fingerprint).ConfigureAwait(false);
        var name = System.IO.Path.GetFileName(context.File.RelativePath);
        await _activity.RecordAsync(
            uow,
            ActivityEventTypes.ProcessingFileManualPlanQueued,
            "processing",
            $"Manual track choice queued for {name}",
            $"{session.User.Username} chose {Plural.Of(choice.Keep.Count, "track")} to keep for {context.File.RelativePath} (job {job.Id}).")
            .ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);

        return ApiRoutes.Ok(new WireObject()
            .Set("ok", true)
            .Set("job_id", job.Id)
            .Set("dedupe_key", job.DedupeKey)
            .Set("job_kind", job.JobKind));
    }
}
