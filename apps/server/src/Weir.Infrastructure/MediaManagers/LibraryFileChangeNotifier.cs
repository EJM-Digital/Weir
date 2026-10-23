using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Activity;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// Tells every media manager that owns a library-mode file to re-read it after a swap (#507). Library mode
/// (#505) is a separate, not-yet-landed area; this depends on nothing from it beyond <see cref="LibraryFileChange"/>.
///
/// <para>Per connection serving the change's scope: describe it (for its library roots and, for an external
/// manager, its advertised capabilities), translate the changed path into that manager's own view of it,
/// and either match it to a title through <see cref="IMediaManagerPort.ListLibraryFilesAsync"/> (Sonarr/Radarr)
/// or hand the path straight to <see cref="IMediaManagerPort.FileChangedAsync"/> (Deluno, gated on
/// <c>external-file-changed</c>). A transient failure is retried with backoff
/// (<see cref="JobQueueRules.DefaultMaxAttempts"/> attempts in all); exhausting them records an Activity
/// warning rather than failing the caller — the swap already kept the file, so a manager that cannot be told
/// right now will catch up at its next disk scan. A manager that plainly does not own the path (no matching
/// title, or a 404 from Deluno) is left alone, silently.</para>
///
/// <para>Batches are de-duplicated in memory: once a (manager, title) pair is notified, a repeat within
/// <see cref="LibraryFileChangeRules.CoalesceWindow"/> is skipped without a network call, so ten changed
/// episodes of one series produce one <c>RescanSeries</c>.</para>
/// </summary>
public sealed class LibraryFileChangeNotifier : ILibraryFileChangeNotifier
{
    private enum AttemptOutcome
    {
        Notified,
        Coalesced,
        NotOwned,
        CapabilityMissing,
    }

    private readonly MediaManagerConnectionService _connections;
    private readonly SqliteDatabase _database;
    private readonly IActivityWriter _activity;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentlyNotified = new(StringComparer.Ordinal);

    public LibraryFileChangeNotifier(
        MediaManagerConnectionService connections,
        SqliteDatabase database,
        IActivityWriter activity,
        TimeProvider time,
        ILogger<LibraryFileChangeNotifier>? logger = null,
        Func<int, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(time);
        _connections = connections;
        _database = database;
        _activity = activity;
        _time = time;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _delay = delay ?? DefaultDelayAsync;
    }

    public async Task NotifyAsync(LibraryFileChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (string.IsNullOrWhiteSpace(change.FilePath))
        {
            throw new ArgumentException("A library file change needs a file path.", nameof(change));
        }

        var scope = string.Equals(change.MediaScope, MediaManagerKinds.Tv, StringComparison.OrdinalIgnoreCase)
            ? MediaManagerKinds.Tv
            : MediaManagerKinds.Movie;

        List<ManagerConnection> connections;
        await using (var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false))
        {
            connections = await _connections.ConnectionsForScopeAsync(uow, scope).ConfigureAwait(false);
        }

        foreach (var connection in connections)
        {
            // Every kind a connection can be saved with has a port.
            await ProcessConnectionAsync(connection, _connections.Ports.PortForKind(connection.Kind)!, scope, change, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One connection, retried up to <see cref="JobQueueRules.DefaultMaxAttempts"/> times; never throws.</summary>
    private async Task ProcessConnectionAsync(ManagerConnection connection, IMediaManagerPort port, string scope, LibraryFileChange change, CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= JobQueueRules.DefaultMaxAttempts; attempt++)
        {
            try
            {
                var outcome = await AttemptAsync(connection, port, scope, change, cancellationToken).ConfigureAwait(false);
                switch (outcome)
                {
                    case AttemptOutcome.Notified:
                        await RecordNotifiedAsync(connection, change).ConfigureAwait(false);
                        return;
                    case AttemptOutcome.CapabilityMissing:
                        await RecordSkippedAsync(connection, change).ConfigureAwait(false);
                        return;
                    default: // Coalesced or NotOwned: nothing to tell the operator.
                        return;
                }
            }
            catch (Exception exception) when (exception is MediaManagerHttpException or MediaManagerUnreachableException)
            {
                lastFailure = exception;
                if (attempt < JobQueueRules.DefaultMaxAttempts)
                {
                    await _delay(attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        _logger.LogWarning(lastFailure, "Could not tell {Label} that a library file changed.", connection.Label);
        await RecordWarningAsync(connection, change).ConfigureAwait(false);
    }

    /// <summary>Resolve, de-dupe and call once. Throws <see cref="MediaManagerHttpException"/>/<see cref="MediaManagerUnreachableException"/> on a transient failure worth retrying.</summary>
    private async Task<AttemptOutcome> AttemptAsync(ManagerConnection connection, IMediaManagerPort port, string scope, LibraryFileChange change, CancellationToken cancellationToken)
    {
        var description = await port.DescribeAsync(connection, cancellationToken).ConfigureAwait(false);
        if (description.Status != SignalStatus.Reported)
        {
            throw new MediaManagerUnreachableException(description.Detail ?? $"{connection.Label} did not answer.");
        }

        var advertised = description.Advertised;
        var managerPath = ResolveManagerPath(change, description.Libraries);
        // The notifier is only handed a connection whose kind has a port, and every kind with a port has a profile.
        var isArr = ManagerKindProfiles.ForKind(connection.Kind)!.IsArr;
        if (isArr)
        {
            var filesSignal = await port.ListLibraryFilesAsync(connection, scope, cancellationToken).ConfigureAwait(false);
            if (filesSignal.Status == SignalStatus.Unreachable)
            {
                throw new MediaManagerUnreachableException(filesSignal.Detail ?? $"{connection.Label} did not answer.");
            }

            var match = filesSignal.Files.FirstOrDefault(file => LibraryFileChangeRules.PathsEqual(file.FilePath, managerPath));
            if (match is null)
            {
                return AttemptOutcome.NotOwned;
            }

            var dedupeKey = DedupeKey(connection, match.TitleId);
            if (RecentlyNotified(dedupeKey))
            {
                return AttemptOutcome.Coalesced;
            }

            await port.FileChangedAsync(connection, advertised, match.TitleId, managerPath, change.Reason, cancellationToken).ConfigureAwait(false);
            MarkNotified(dedupeKey);
            return AttemptOutcome.Notified;
        }

        if (!advertised.Contains(ManagerDialectRules.ExternalFileChangedCapability))
        {
            return AttemptOutcome.CapabilityMissing;
        }

        var externalDedupeKey = DedupeKey(connection, managerPath);
        if (RecentlyNotified(externalDedupeKey))
        {
            return AttemptOutcome.Coalesced;
        }

        try
        {
            await port.FileChangedAsync(connection, advertised, null, managerPath, change.Reason, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaManagerHttpException exception) when (exception.Message.Contains("HTTP 404", StringComparison.Ordinal))
        {
            // Deluno's own answer for "that path is not a file I track": this manager does not own it.
            return AttemptOutcome.NotOwned;
        }

        MarkNotified(externalDedupeKey);
        return AttemptOutcome.Notified;
    }

    /// <summary>
    /// The path this manager would see for <paramref name="change"/>: unchanged when no local library root
    /// was given (Weir and every manager share the same paths), otherwise the first of the manager's own
    /// library roots the file's local root translates against (<c>HandoffCompletionReporter.TranslateOutputPath</c>
    /// — the same prefix substitution the hand-off path already reuses, applied to a library root here rather
    /// than an output folder).
    /// </summary>
    private static string ResolveManagerPath(LibraryFileChange change, IReadOnlyList<ManagerLibraryDescriptor> libraries)
    {
        if (string.IsNullOrEmpty(change.LocalLibraryRoot))
        {
            return change.FilePath;
        }

        foreach (var library in libraries)
        {
            if (!string.IsNullOrEmpty(library.RootPath) &&
                HandoffCompletionReporter.TranslateOutputPath(change.FilePath, change.LocalLibraryRoot, library.RootPath) is { } translated)
            {
                return translated;
            }
        }

        return change.FilePath;
    }

    private bool RecentlyNotified(string key) =>
        _recentlyNotified.TryGetValue(key, out var last) && _time.GetUtcNow() - last < LibraryFileChangeRules.CoalesceWindow;

    private void MarkNotified(string key)
    {
        var now = _time.GetUtcNow();
        _recentlyNotified[key] = now;
        foreach (var stale in _recentlyNotified.Where(entry => now - entry.Value >= LibraryFileChangeRules.CoalesceWindow).Select(entry => entry.Key).ToArray())
        {
            _recentlyNotified.TryRemove(stale, out _);
        }
    }

    private static string DedupeKey(ManagerConnection connection, string target)
    {
        var connectionKey = Convert.ToString(connection.ConnectionId, CultureInfo.InvariantCulture);
        return $"{connectionKey}::{target}";
    }

    private Task<long> RecordNotifiedAsync(ManagerConnection connection, LibraryFileChange change) =>
        Record(LibraryFileChangeRules.NotifiedEventType, connection, change, "success", $"Told {connection.Label} that {FileName(change)} changed", note: null);

    private Task<long> RecordSkippedAsync(ManagerConnection connection, LibraryFileChange change) =>
        Record(
            LibraryFileChangeRules.NotifySkippedEventType,
            connection,
            change,
            "skipped",
            $"{connection.Label} was not told that {FileName(change)} changed",
            note: $"{connection.Label} does not advertise the {ManagerDialectRules.ExternalFileChangedCapability} capability, so Weir made no call; it will pick up the change on its own schedule.");

    private Task<long> RecordWarningAsync(ManagerConnection connection, LibraryFileChange change) =>
        Record(
            LibraryFileChangeRules.NotifyWarningEventType,
            connection,
            change,
            "warning",
            $"Weir could not tell {connection.Label} that {FileName(change)} changed",
            note: LibraryFileChangeRules.CouldNotTellWarning(connection));

    private Task<long> Record(string eventType, ManagerConnection connection, LibraryFileChange change, string result, string title, string? note)
    {
        var detail = new WireObject()
            .Set("manager", connection.Label)
            .Set("path", change.FilePath)
            .Set("reason", change.Reason)
            .Set("trigger", "worker")
            .Set("result", result);
        if (note is not null)
        {
            detail.Set("note", note);
        }

        return _activity.RecordAsync(new ActivityEventDraft(
            eventType,
            "library",
            title,
            WireStrings.Slice(WireJsonWriter.Dumps(detail, WireJsonFormat.Compact), 10_000)));
    }

    private static string FileName(LibraryFileChange change) => MediaPathNames.Name(change.FilePath, OperatingSystem.IsWindows());

    private static Task DefaultDelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var seconds = Math.Min(Math.Pow(2, attempt - 1), 5);
        return Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
    }
}
