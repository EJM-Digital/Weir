using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Settings;
using Weir.Core.Time;
using Weir.Core.Validation;
using Weir.Infrastructure.Logging;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Settings;

namespace Weir.Api.Endpoints;

/// <summary>Suite settings, the configuration bundle (export/import), configuration-backup listing and download, and the security overview.</summary>
public static class SuiteConfigurationEndpoints
{
    public static IEndpointRouteBuilder MapSuiteConfigurationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<SuiteConfigurationEndpointHandlers>();

        // Suite settings. One URL per handler, with no alias spellings: the shipped web app is built from
        // this repo, and a second address for the same handler is a second thing to secure, document and test.
        endpoints.MapV1("GET", "/suite/settings", handlers.GetSettingsAsync);
        endpoints.MapV1("PUT", "/suite/settings", handlers.PutSettingsAsync);
        endpoints.MapV1("GET", "/suite/configuration-bundle", handlers.GetBundleAsync);
        endpoints.MapV1("PUT", "/suite/configuration-bundle", handlers.PutBundleAsync);
        endpoints.MapV1("GET", "/suite/configuration-backups", handlers.GetBackupsAsync);
        endpoints.MapV1("GET", "/suite/configuration-backups/{backup_id}/download", handlers.DownloadBackupAsync);
        endpoints.MapV1("PUT", "/suite/backup-password", handlers.PutBackupPasswordAsync);
        endpoints.MapV1("GET", "/suite/security-overview", handlers.GetSecurityOverviewAsync);
        return endpoints;
    }

    /// <summary>
    /// A file download with disposition, length, last-modified and an MD5 ETag of mtime and size, the headers
    /// existing clients and the contract suite expect.
    /// </summary>
    internal static async Task WriteFileAsync(HttpContext context, string path, string fileName, string mediaType)
    {
        var info = new FileInfo(path);
        var bytes = await File.ReadAllBytesAsync(path, context.RequestAborted).ConfigureAwait(false);
        var mtimeTicks = info.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks;
        var mtime = (mtimeTicks / TimeSpan.TicksPerSecond) + ((mtimeTicks % TimeSpan.TicksPerSecond) * 100 * 1e-9);
#pragma warning disable CA5351 // The ETag is an MD5 of mtime and size; it is not a security control.
        var etag = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(WireJsonWriter.FloatRepr(mtime) + "-" + info.Length.ToString(CultureInfo.InvariantCulture))));
#pragma warning restore CA5351
        var quoted = Uri.EscapeDataString(fileName);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = mediaType;
        context.Response.Headers.ContentDisposition = quoted != fileName ? $"attachment; filename*=utf-8''{quoted}" : $"attachment; filename=\"{fileName}\"";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.LastModified = info.LastWriteTimeUtc.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);
        context.Response.Headers.ETag = $"\"{etag}\"";
        context.Response.Headers.AcceptRanges = "bytes";
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>Handlers for <see cref="SuiteConfigurationEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class SuiteConfigurationEndpointHandlers
{
    private const int MaxBackupPasswordLength = 256;

    private readonly SuiteSettingsStore _suiteSettings;
    private readonly ConfigurationBundleStore _bundle;
    private readonly ConfigurationBackups _backups;
    private readonly BackupPasswordStore _backupPassword;
    private readonly ITimeZoneResolver _zones;
    private readonly ScanSettingsChanges _scanSettingsChanges;
    private readonly WeirLogFile _logFile;

    public SuiteConfigurationEndpointHandlers(
        SuiteSettingsStore suiteSettings,
        ConfigurationBundleStore bundle,
        ConfigurationBackups backups,
        BackupPasswordStore backupPassword,
        ITimeZoneResolver zones,
        ScanSettingsChanges scanSettingsChanges,
        WeirLogFile logFile)
    {
        _suiteSettings = suiteSettings ?? throw new ArgumentNullException(nameof(suiteSettings));
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _backupPassword = backupPassword ?? throw new ArgumentNullException(nameof(backupPassword));
        _zones = zones ?? throw new ArgumentNullException(nameof(zones));
        _scanSettingsChanges = scanSettingsChanges ?? throw new ArgumentNullException(nameof(scanSettingsChanges));
        _logFile = logFile ?? throw new ArgumentNullException(nameof(logFile));
    }

    public async Task<ApiResult> GetSettingsAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(SuiteSettingsRules.BuildOut(await _suiteSettings.EnsureAsync(uow).ConfigureAwait(false)));
    }

    public async Task<ApiResult> PutSettingsAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var wizard = model.OptionalStr("setup_wizard_state", minLength: 1, maxLength: 32);
        var timezone = model.Str("app_timezone", minLength: 1, maxLength: 120);
        var logRetention = model.Number("log_retention_days", 0, required: true, ge: 1, le: 3650);
        var activityRetention = model.OptionalInt("activity_retention_days", ge: 0, le: 3650);
        var backupEnabled = model.OptionalBool("configuration_backup_enabled");
        var backupHours = model.OptionalInt("configuration_backup_interval_hours", ge: 1, le: 720);
        var backupTime = model.OptionalStr("configuration_backup_preferred_time", minLength: 5, maxLength: 5);
        model.Finish(ExtraFields.Ignore);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);
        var uow = await request.DbAsync().ConfigureAwait(false);
        WireObject output;
        SuiteSettingsRecord updated;
        try
        {
            var normalized = SuiteSettingsRules.Normalize(
                new SuiteSettingsUpdate(timezone, logRetention, wizard, activityRetention, backupEnabled, backupHours, backupTime),
                _zones);
            var before = await _suiteSettings.EnsureAsync(uow).ConfigureAwait(false);
            await _suiteSettings.UpdateAsync(uow, before, SuiteSettingsRules.Apply(before, normalized)).ConfigureAwait(false);
            updated = await _suiteSettings.EnsureAsync(uow).ConfigureAwait(false);
            output = SuiteSettingsRules.BuildOut(updated);
        }
        catch (WireValueException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        await request.CommitAsync().ConfigureAwait(false);
        PruneLog(request, (int)Math.Max(1, Math.Min(3650, updated.LogRetentionDays)));
        return ApiRoutes.Ok(output);
    }

    /// <summary>
    /// The settings are already saved, so a log that cannot be pruned (for example one holding invalid UTF-8)
    /// is logged and never turns the response into a 500 (#536).
    /// </summary>
    private void PruneLog(ApiRequest request, int keepDays)
    {
        var logger = request.LoggerFactory.CreateLogger("weir.core.logging");
        try
        {
            if (!_logFile.Prune(keepDays))
            {
                logger.LogWarning("Suite log prune skipped because the active log could not be rewritten.");
            }
        }
#pragma warning disable CA1031 // Pruning the log is housekeeping; a failure is logged and the request still succeeds.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            logger.LogWarning(exception, "Suite log prune skipped because the active log could not be rewritten.");
        }
    }

    public async Task<ApiResult> GetBundleAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        try
        {
            return ApiRoutes.Ok(await _bundle.BuildAsync(uow).ConfigureAwait(false));
        }
        catch (WireValueException exception)
        {
            throw new ApiException(StatusCodes.Status500InternalServerError, exception.Message);
        }
    }

    public async Task<ApiResult> PutBundleAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var bundle = model.Dict("bundle");
        var backupPassword = model.OptionalStr("password", minLength: 1, maxLength: MaxBackupPasswordLength);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);
        var uow = await request.DbAsync().ConfigureAwait(false);
        try
        {
            await _bundle.ApplyAsync(uow, bundle, _zones, request.Options.WeirHome, backupPassword).ConfigureAwait(false);
        }
        catch (WireValueException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        await request.CommitAsync().ConfigureAwait(false);
        _scanSettingsChanges.Record();
        return ApiRoutes.Ok(await _bundle.BuildAsync(uow).ConfigureAwait(false));
    }

    public async Task<ApiResult> GetBackupsAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var rows = await ConfigurationBackups.ListAsync(uow).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject()
            .Set("directory", _backups.Directory)
            .Set("password_set", await _backupPassword.IsSetAsync(uow).ConfigureAwait(false))
            .Set("items", new WireArray(rows.Select(row => (WireValue)ConfigurationBackups.ItemOut(row)))));
    }

    /// <summary>
    /// Sets or changes the password the secrets in future backups are locked with. Backups already made keep the password they were
    /// made with. The password is checked, used to make a key and dropped: it is never stored, echoed or logged.
    /// </summary>
    public async Task<ApiResult> PutBackupPasswordAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var password = model.Str("password", minLength: 1, maxLength: MaxBackupPasswordLength);
        var confirmation = model.Str("confirm_password", minLength: 1, maxLength: MaxBackupPasswordLength);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "The two backup passwords do not match. Type the same password in both boxes.");
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        try
        {
            await _backupPassword.SetAsync(uow, password).ConfigureAwait(false);
        }
        catch (WireValueException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("password_set", true));
    }

    public async Task<ApiResult> DownloadBackupAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var backupId = request.PathInt("backup_id", issues);
        issues.ThrowIfAny();
        var uow = await request.DbAsync().ConfigureAwait(false);
        string path;
        ConfigurationBackupRecord row;
        try
        {
            (path, row) = await _backups.GetFileAsync(uow, backupId).ConfigureAwait(false);
        }
        catch (WireValueException exception)
        {
            throw new ApiException(StatusCodes.Status404NotFound, exception.Message);
        }

        return new CustomApiResult(context => SuiteConfigurationEndpoints.WriteFileAsync(context, path, row.FileName, "application/json"));
    }

    public async Task<ApiResult> GetSecurityOverviewAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(SecurityOverview.Build(request.Options));
    }
}
