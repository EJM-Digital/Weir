using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Validation;
using Weir.Infrastructure.Processing;

namespace Weir.Api.Endpoints;

/// <summary>
/// The per-file processing log — <c>/api/v1/processing/files/{file_id}/log</c> (view and plain-text download). See
/// <see cref="ProcessingFilesEndpoints"/> for the rest of the Files screen.
/// </summary>
public static class ProcessingFileLogEndpoints
{
    public static IEndpointRouteBuilder MapProcessingFileLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingFileLogEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/files/{file_id}/log", handlers.GetFileLogAsync);
        endpoints.MapV1("GET", "/processing/files/{file_id}/log/download", handlers.DownloadFileLogAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingFileLogEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ProcessingFileLogEndpointHandlers
{
    private readonly OperatorSettingsStore _operatorSettings;
    private readonly FileLogStore _fileLogs;
    private readonly FileStateStore _files;
    private readonly LibraryStore _libraries;

    public ProcessingFileLogEndpointHandlers(OperatorSettingsStore operatorSettings, FileLogStore fileLogs, FileStateStore files, LibraryStore libraries)
    {
        _operatorSettings = operatorSettings ?? throw new ArgumentNullException(nameof(operatorSettings));
        _fileLogs = fileLogs ?? throw new ArgumentNullException(nameof(fileLogs));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
    }

    public async Task<ApiResult> GetFileLogAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("file_id", issues);
        var limit = request.Query("limit") is { } rawLimit && FieldRules.TryInt(new WireString(rawLimit), ["query", "limit"], 1, 500, issues, out var parsedLimit) ? (int)parsedLimit : 50;
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var row = await ProcessingFilesEndpoints.RequireFileAsync(uow, _files, id).ConfigureAwait(false);
        var operatorRow = await _operatorSettings.EnsureAsync(uow).ConfigureAwait(false);
        var rows = await _fileLogs.LogsForFileAsync(uow, row.RelativePath, limit).ConfigureAwait(false);
        var target = (await _libraries.ManagerConnectionIdsAsync(uow, row.LibraryId).ConfigureAwait(false)).Count == 0
            ? HandBackTarget.OutputFolder
            : HandBackTarget.MediaManager;
        await request.CommitAsync().ConfigureAwait(false);

        var entries = new List<WireValue>();
        foreach (var entry in rows)
        {
            var detail = _fileLogs.ParseDetail(entry.DetailJson);
            var story = FileStory.NarratePass(detail, entry.LibraryName, target);
            entries.Add(new WireObject()
                .Set("id", entry.Id)
                .Set("recorded_at", entry.RecordedAt.ToWireText())
                .Set("outcome", entry.Outcome)
                .Set("title", entry.Title)
                .Set("library_name", entry.LibraryName)
                .Set("detail", detail)
                .Set("story", new WireArray(story.Select(step => (WireValue)new WireObject()
                    .Set("heading", step.Heading)
                    .Set("sentence", step.Sentence)
                    .Set("tone", step.Tone.ToString().ToLowerInvariant())))));
        }

        return ApiRoutes.Ok(new WireObject()
            .Set("file_id", id)
            .Set("relative_path", row.RelativePath)
            .Set("retention_days", operatorRow.FileLogRetentionDays)
            .Set("entries", new WireArray(entries)));
    }

    public async Task<ApiResult> DownloadFileLogAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("file_id", issues);
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var row = await ProcessingFilesEndpoints.RequireFileAsync(uow, _files, id).ConfigureAwait(false);
        var rows = await _fileLogs.LogsForFileAsync(uow, row.RelativePath, 500).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);

        var safe = new string([.. row.RelativePath.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')]);
        safe = safe.Length > 80 ? safe[^80..] : safe;
        safe = safe.Trim('-');
        if (safe.Length == 0)
        {
            safe = "file";
        }

        var fileName = $"weir-{safe}.log.txt";
        var quoted = Uri.EscapeDataString(fileName);
        var text = _fileLogs.RenderLogText(rows);
        return new CustomApiResult(context =>
        {
            context.Response.Headers.ContentDisposition = quoted != fileName ? $"attachment; filename*=utf-8''{quoted}" : $"attachment; filename=\"{fileName}\"";
            return ApiResponses.WritePlainTextAsync(context, StatusCodes.Status200OK, text);
        });
    }
}
