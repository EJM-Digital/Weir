using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Validation;
using Weir.Infrastructure.Logging;
using Weir.Infrastructure.Settings;

namespace Weir.Api.Endpoints;

/// <summary>
/// Files Weir writes when asked: a configuration snapshot right now (System › Backups' "Back up now"), and the
/// whole server log as a download for a support request (System › Logs).
/// </summary>
public static class SuiteFileEndpoints
{
    public static IEndpointRouteBuilder MapSuiteFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<SuiteFileEndpointHandlers>();
        endpoints.MapV1("POST", "/suite/configuration-backups", handlers.PostBackupAsync);
        endpoints.MapV1("GET", "/suite/logs/download", handlers.DownloadLogAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="SuiteFileEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class SuiteFileEndpointHandlers
{
    private const string LogMediaType = "application/x-ndjson; charset=utf-8";

    private readonly ConfigurationBackups _backups;
    private readonly WeirLogFile _logFile;

    public SuiteFileEndpointHandlers(ConfigurationBackups backups, WeirLogFile logFile)
    {
        _backups = backups;
        _logFile = logFile;
    }

    public async Task<ApiResult> PostBackupAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);
        var uow = await request.DbAsync().ConfigureAwait(false);
        ConfigurationBackupRecord row;
        try
        {
            row = await _backups.CreateAsync(uow, BackupSource.Manual).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            request.LoggerFactory.CreateLogger("weir.platform.suite_settings.backups").LogWarning(exception, "A backup asked for by hand could not be written.");
            throw new ApiException(
                StatusCodes.Status500InternalServerError,
                "Weir could not write the backup file. Check that Weir can write to its backup folder, then try again.");
        }

        await request.CommitAsync().ConfigureAwait(false);
        return new JsonApiResult(StatusCodes.Status201Created, ConfigurationBackups.ItemOut(row));
    }

    public async Task<ApiResult> DownloadLogAsync(ApiRequest request)
    {
        // Install-level operational data (the whole server log, not just this account's activity): admins only,
        // like the configuration backup above.
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);

        // A snapshot copied aside under the write lock, so the slow part - handing potentially many megabytes to a
        // browser - runs with the lock already released and logging never waits on a download (#741 review).
        var snapshotPath = System.IO.Path.Join(
            System.IO.Path.GetTempPath(), $"weir-log-download-{Guid.NewGuid():N}.tmp");
        if (!_logFile.SnapshotTo(snapshotPath))
        {
            request.LoggerFactory.CreateLogger("weir.platform.suite_settings.logs_service").LogWarning("Log download skipped because the active log could not be opened.");
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "Weir could not open its log file just now. Try again in a moment.");
        }

        var fileName = string.Create(CultureInfo.InvariantCulture, $"weir-log-{request.Time.GetLocalNow():yyyyMMdd-HHmmss}.log");
        return new CustomApiResult(async context =>
        {
            var logger = request.LoggerFactory.CreateLogger("weir.platform.suite_settings.logs_service");
            try
            {
                await using var snapshot = new FileStream(
                    snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81_920,
                    options: FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = LogMediaType;
                context.Response.ContentLength = snapshot.Length;
                context.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
                // Operational data (paths, request ids): never cached, as auth responses already are not.
                context.Response.Headers.CacheControl = "no-store, private";
                await snapshot.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not stream the log download snapshot.");
                try
                {
                    File.Delete(snapshotPath);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // Best effort: FileOptions.DeleteOnClose already handles the common case.
                }
            }
        });
    }
}
