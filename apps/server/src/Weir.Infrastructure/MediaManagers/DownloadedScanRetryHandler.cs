using Microsoft.Extensions.Logging;
using Weir.Core.Jobs;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// Handles <c>processing.downloaded_scan_retry.v1</c>: asks a Sonarr or Radarr once more to scan a file Weir wrote, after the app said
/// it was busy with another scan. The job is held back until its time, so no worker waits for the other app; when the app is busy
/// again the next ask is queued the same way, until <see cref="Weir.Core.MediaManagers.DownloadedScanRetrySchedule"/> runs out.
/// </summary>
public sealed partial class DownloadedScanRetryHandler : IJobHandler
{
    public const string Kind = "processing.downloaded_scan_retry.v1";

    private readonly ProcessingJobStore _jobs;
    private readonly DownloadedScanNotifier _notifier;
    private readonly ILogger<DownloadedScanRetryHandler> _logger;

    public DownloadedScanRetryHandler(ProcessingJobStore jobs, DownloadedScanNotifier notifier, ILogger<DownloadedScanRetryHandler> logger)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string JobKind => Kind;

    public async Task HandleAsync(JobWorkContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (DownloadedScanRequest.Parse(context.PayloadJson) is not { } request)
        {
            LogUnreadable(_logger, context.Id);
            return;
        }

        var uow = await UnitOfWork.OpenAsync(_jobs.Database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            await _notifier.AskAgainAsync(uow, request, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} to ask a media manager to scan again carries no usable request, so it was dropped.")]
    private static partial void LogUnreadable(ILogger logger, long jobId);
}
