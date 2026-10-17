using Microsoft.Extensions.Logging;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Http;

/// <summary>
/// Delivers job notifications to the channels on Setup › Connections › Alerts.
/// </summary>
/// <remarks>
/// Every job reports itself as module <c>processing</c>, which would make "File processing finished" and "Any job
/// completed" the same event, fired by folder scans every few minutes as much as by files. Here a file's own work — cleaning a
/// download, passing it through, rejecting it, cleaning a library file — is file processing, and alerts as such (and
/// under "Any job" too). Background work (scans, sweeps) alerts only when it has failed for good, under "Any job
/// permanently failed"; a routine scan finishing is not news.
/// </remarks>
public sealed class WebhookJobNotifications : IJobNotifications
{
    private const string FileJobPrefix = "processing.file.";
    private const string LibraryCleanJobKind = "processing.library.clean.v1";

    private readonly SqliteDatabase _database;
    private readonly NotificationDispatcher _dispatcher;
    private readonly ILogger<WebhookJobNotifications> _logger;

    public WebhookJobNotifications(SqliteDatabase database, NotificationDispatcher dispatcher, ILogger<WebhookJobNotifications> logger)
    {
        _database = database;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>Whether a job is one file's own work, as opposed to background work such as a scan or a sweep.</summary>
    public static bool IsFileJob(string jobKind) =>
        jobKind.StartsWith(FileJobPrefix, StringComparison.Ordinal) || string.Equals(jobKind, LibraryCleanJobKind, StringComparison.Ordinal);

    public void Dispatch(string moduleName, string eventKind, long jobId, string jobKind, bool willRetry = false)
    {
        try
        {
            if (IsFileJob(jobKind))
            {
                // processing_job_* ("File processing …"), and the job_* channels through the dispatcher's generic match.
                _dispatcher.DispatchJobNotification(_database, "processing", eventKind, jobId, jobKind, Warn, willRetry);
                return;
            }

            if (eventKind == "failed")
            {
                // "system_job_failed" is no subscribable event itself, so only job_failed ("Any job permanently failed")
                // channels receive it.
                _dispatcher.DispatchJobNotification(_database, "system", eventKind, jobId, jobKind, Warn, willRetry);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // The contract is that this never throws into the worker; delivery problems are only ever logged.
            _logger.LogWarning(exception, "Could not start alert delivery for job {JobId} ({JobKind}).", jobId, jobKind);
        }
    }

    public void DispatchFailedForGood(string moduleName, long jobId, string jobKind)
    {
        try
        {
            _dispatcher.DispatchJobNotification(_database, moduleName, "failed", jobId, jobKind, Warn, workFailed: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(exception, "Could not start alert delivery for job {JobId} ({JobKind}).", jobId, jobKind);
        }
    }

    private void Warn(string message, Exception? exception) => _logger.LogWarning(exception, "{Message}", message);
}
