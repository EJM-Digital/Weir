using System.Globalization;
using Weir.Core.Json;
using Weir.Core.Updates;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Http;

/// <summary>Whether a newer Weir exists, as far as Weir last found out.</summary>
/// <param name="Status">
/// <c>checking</c> (nothing learned yet), <c>up_to_date</c>, <c>update_available</c>, <c>downloaded</c> (waiting for the tray to
/// install it), <c>not_published</c>, <c>rate_limited</c> (GitHub is limiting update checks; the latest version is the last one
/// known), <c>off</c> (the Windows app's owner switched checking off) or <c>unavailable</c>.
/// </param>
/// <param name="LatestVersion">The newest version known, or the one waiting to install; null when none is known.</param>
public sealed record UpdateOutlookSnapshot(string Status, string? LatestVersion);

/// <summary>
/// What Weir last learned about newer releases, kept so System can show it on every read without asking GitHub each time. A
/// read that finds the answer stale starts a fresh check in the background and still answers at once.
/// </summary>
public sealed class UpdateOutlook
{
    public const string Checking = "checking";
    public const string Downloaded = "downloaded";

    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly UpdateStatusReader _status;
    private readonly UpdateFiles _files;
    private readonly TimeProvider _time;
    private readonly DataChangePublisher _changes;
    private readonly Lock _gate = new();
    private UpdateOutlookSnapshot _known = new(Checking, null);
    private DateTimeOffset? _nextCheckAt;
    private bool _checking;

    public UpdateOutlook(UpdateStatusReader status, UpdateFiles files, TimeProvider time, DataChangePublisher changes)
    {
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
    }

    /// <summary>The outlook now: an update the tray already downloaded wins over what the last check found.</summary>
    public UpdateOutlookSnapshot Current()
    {
        CheckWhenStale();
        var state = _files.ReadState();
        if (state.Get("downloaded")?.IsTruthy == true)
        {
            return new UpdateOutlookSnapshot(Downloaded, TextOf(state.Get("pending_version")));
        }

        lock (_gate)
        {
            return _known;
        }
    }

    /// <summary>Asks for the latest release and keeps the answer; open screens hear of it when the answer is a different one.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var status = await _status.ReadAsync(cancellationToken).ConfigureAwait(false);
        var outlook = new UpdateOutlookSnapshot(TextOf(status.Get("status")) ?? Checking, TextOf(status.Get("latest_version")));
        bool changed;
        lock (_gate)
        {
            changed = outlook != _known;
            _known = outlook;
            _nextCheckAt = NextCheckAt(outlook.Status, TextOf(status.Get("retry_at")));
        }

        if (changed)
        {
            _changes.Publish(DataTopics.Update);
        }
    }

    /// <summary>Starts a fresh check in the background when the last answer has gone stale, and returns at once.</summary>
    public void CheckWhenStale()
    {
        lock (_gate)
        {
            if (_checking || (_nextCheckAt is { } at && _time.GetUtcNow() < at))
            {
                return;
            }

            _checking = true;
        }

        _ = Task.Run(RunCheckAsync, CancellationToken.None);
    }

    /// <summary>When the answer goes stale: once GitHub says its limit lifts, five minutes after a failure, otherwise half an hour on.</summary>
    private DateTimeOffset NextCheckAt(string status, string? retryAt)
    {
        var now = _time.GetUtcNow();

        // An off answer cost nothing to learn, and switching checking on must show at once, so it is never kept.
        if (status == UpdateStatus.OffStatus)
        {
            return now;
        }

        if (status == UpdateStatus.RateLimitedStatus && retryAt is not null && DateTimeOffset.TryParse(retryAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lifts))
        {
            return lifts;
        }

        return now + (status is "unavailable" or UpdateStatus.RateLimitedStatus ? RetryAfterFailure : FreshFor);
    }

    private async Task RunCheckAsync()
    {
        try
        {
            await CheckAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _checking = false;
            }
        }
    }

    private static string? TextOf(WireValue? value) => value is WireString text && text.Value.Length > 0 ? text.Value : null;
}
