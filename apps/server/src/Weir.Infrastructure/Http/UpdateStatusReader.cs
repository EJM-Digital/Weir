using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weir.Core;
using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Core.Updates;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Http;

/// <summary>
/// The update status of this install: what the latest published release is and whether this one is behind it. A check that
/// fails for any reason reads as unavailable, never as an error, and says why. While GitHub is limiting the network the status
/// says when Weir will check again and keeps the last release it knew of.
/// </summary>
public sealed class UpdateStatusReader
{
    private readonly IReleaseCatalogClient _releases;
    private readonly WeirOptions _options;
    private readonly SqliteDatabase _database;
    private readonly SuiteSettingsStore _suiteSettings;
    private readonly UpdateFiles _files;
    private readonly ILogger<UpdateStatusReader> _logger;
    private string? _lastIssue;

    public UpdateStatusReader(
        IReleaseCatalogClient releases,
        WeirOptions options,
        SqliteDatabase database,
        SuiteSettingsStore suiteSettings,
        UpdateFiles files,
        ILogger<UpdateStatusReader> logger)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(suiteSettings);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(logger);
        _releases = releases;
        _options = options;
        _database = database;
        _suiteSettings = suiteSettings;
        _files = files;
        _logger = logger;
    }

    /// <summary>The <c>GET /suite/update-status</c> body.</summary>
    public async Task<WireObject> ReadAsync(CancellationToken cancellationToken)
    {
        var installType = UpdateFiles.DetectInstallType(_options.RuntimeKind);
        var currentVersion = WeirVersion.Resolve(_options.VersionOverride);
        // The Windows app lets its owner switch checking off, and then nothing is asked of GitHub. A Docker or source install has no
        // tray to hold that choice and keeps looking, since this page is how it learns of a release.
        if (installType == "windows" && _files.ReadSettings().Get("check_for_updates")?.IsTruthy != true)
        {
            return UpdateStatus.Off(currentVersion, installType);
        }

        try
        {
            var release = await _releases.FetchLatestAsync(currentVersion, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastIssue, null);
            return release is null
                ? NotPublished(currentVersion, installType)
                : UpdateStatus.FromRelease(currentVersion, installType, release);
        }
        catch (ReleaseFetchException exception) when (exception.RateLimit is { } limit)
        {
            var summary = UpdateStatus.RateLimitedSummary(limit.ResetsAt, await TimezoneAsync(cancellationToken).ConfigureAwait(false));
            _logger.Log(
                LevelFor($"limit until {limit.ResetsAt:O}"),
                "Update check: GitHub answered HTTP {Status} because it is limiting this network. {Summary}",
                exception.StatusCode,
                summary);

            return UpdateStatus.RateLimited(currentVersion, installType, limit.LastKnown, limit.ResetsAt, summary);
        }
        catch (ReleaseFetchException exception) when (exception.StatusCode == 404)
        {
            return NotPublished(currentVersion, installType);
        }
#pragma warning disable CA1031 // An update check that fails for any reason reads as unavailable, never an error page.
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            var reason = ReasonFor(exception);
            _logger.Log(LevelFor(reason), "Update check failed: {Reason} ({Detail})", reason, exception.Message);
            return UpdateStatus.Unavailable(currentVersion, installType, "unavailable", $"Could not check for updates right now. {reason}");
        }
    }

    /// <summary>
    /// Information when the problem is a new one, Debug while it goes on: screens ask again every few minutes, and the same
    /// line over and over would bury the log.
    /// </summary>
    private LogLevel LevelFor(string issue) => Interlocked.Exchange(ref _lastIssue, issue) == issue ? LogLevel.Debug : LogLevel.Information;

    private static string ReasonFor(Exception exception) => exception switch
    {
        ReleaseFetchException { StatusCode: 403 or 429 } => "GitHub refused the check.",
        ReleaseFetchException => "GitHub could not answer the check.",
        HttpRequestException or TaskCanceledException => "Weir could not reach GitHub.",
        _ => "GitHub sent an answer Weir could not read.",
    };

    /// <summary>The time zone chosen in System, for the clock time a limit lifts at; UTC when the setting cannot be read.</summary>
    private async Task<string?> TimezoneAsync(CancellationToken cancellationToken)
    {
        try
        {
            var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
            await using (uow.ConfigureAwait(false))
            {
                return (await _suiteSettings.GetAsync(uow).ConfigureAwait(false))?.AppTimezone;
            }
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            return null;
        }
    }

    private static WireObject NotPublished(string currentVersion, string installType) =>
        UpdateStatus.Unavailable(currentVersion, installType, "not_published", "No public Weir release is published yet.");
}
