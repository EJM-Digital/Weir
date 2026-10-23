using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>
/// <c>ProcessingActivityProgressReporter</c>: one live <c>processing.file_processing_progress</c> row per pass, inserted on the
/// first save and rewritten after. A failed write never interrupts ffprobe or ffmpeg.
/// </summary>
/// <remarks>
/// Reports arrive on the tool's output reader, twice a second from ffmpeg. <see cref="Report"/> hands every one to
/// <see cref="LiveProgressStore"/> at once (in memory, so this is cheap), and only saves to the database on the first
/// report, when the reported status changes (started, finishing, finished, failed), and at
/// <see cref="CompleteAsync"/>. Live progress stays in memory and streams to clients; the database is written only
/// at start, stage changes and the end, so a long pass never competes for the write lock (#710, #750).
/// </remarks>
public sealed class ActivityProgressReporter
{
    private readonly SqliteDatabase? _database;
    private readonly Func<WireObject, Task> _save;
    private readonly long _jobId;
    private readonly WireObject _extra;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly LiveProgressStore _liveProgress;
    private readonly Lock _lock = new();
    private WireObject? _pending;
    private WireObject? _latest;
    private string? _relativeMediaPath;
    private string? _savedStatus;
    private bool _unsaved;
    private bool _writing;
    private bool _completed;
    private Task _writer = Task.CompletedTask;

    public ActivityProgressReporter(SqliteDatabase database, long jobId, WireObject extra, ILogger logger, TimeProvider time, LiveProgressStore liveProgress)
        : this(
            database ?? throw new ArgumentNullException(nameof(database)),
            logger ?? throw new ArgumentNullException(nameof(logger)),
            save: null,
            jobId,
            extra,
            time,
            liveProgress)
    {
    }

    /// <summary>For tests: every save goes to <paramref name="save"/> instead of the database.</summary>
    internal ActivityProgressReporter(Func<WireObject, Task> save, long jobId, WireObject extra, TimeProvider time, LiveProgressStore liveProgress)
        : this(database: null, logger: null, save ?? throw new ArgumentNullException(nameof(save)), jobId, extra, time, liveProgress)
    {
    }

    private ActivityProgressReporter(SqliteDatabase? database, ILogger? logger, Func<WireObject, Task>? save, long jobId, WireObject extra, TimeProvider time, LiveProgressStore liveProgress)
    {
        _database = database;
        _logger = logger;
        _save = save ?? SaveToActivityAsync;
        _jobId = jobId;
        _extra = extra ?? new WireObject();
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(liveProgress);
        _time = time;
        _liveProgress = liveProgress;
    }

    /// <summary>The progress row, once written; the handler turns it into the completed row.</summary>
    public long? ActivityId { get; private set; }

    /// <summary>
    /// Takes a report without waiting for it to be saved: updates <see cref="LiveProgressStore"/> at once, and queues a
    /// database save only for the first report and a change of stage. Reports after <see cref="CompleteAsync"/> are
    /// ignored.
    /// </summary>
    public void Report(WireObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var body = new WireObject().Set("job_id", _jobId);
        foreach (var (key, value) in _extra.Items.Concat(payload.Items))
        {
            body.Set(key, value);
        }

        // The row is rewritten in place, so its created_at stays at the pass's start. This is how a reader
        // tells a long pass that is still reporting from one that died (LiveProgressStore).
        body.Set("reported_at", Timestamp.UtcNow(_time).ToWireText());

        var relativeMediaPath = LiveProgress.Text(body, "relative_media_path");
        var status = LiveProgress.StatusOf(body);
        if (relativeMediaPath is not null)
        {
            if (LiveProgressStore.IsLiveStatus(status))
            {
                _liveProgress.Update(relativeMediaPath, LiveProgress.FromReport(body));
            }
            else
            {
                _liveProgress.Remove(relativeMediaPath);
            }
        }

        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            _relativeMediaPath ??= relativeMediaPath;
            if (PassStages.IsPreparation(LiveProgress.Text(body, "stage")))
            {
                // Checking and planning are only worth showing live; the Activity row starts when the write does.
                return;
            }

            _latest = body;
            var isFirstReport = _savedStatus is null;
            if (!isFirstReport && status == _savedStatus)
            {
                // Percent-only movement inside the same stage stays in memory only; the database is not
                // touched per progress line (#750). CompleteAsync saves it anyway if the pass ends without
                // another stage change to carry it out.
                _unsaved = true;
                return;
            }

            _savedStatus = status;
            _unsaved = false;
            Enqueue(body);
        }
    }

    /// <summary>Saves whatever is currently queued, and returns once it is saved.</summary>
    public Task FlushAsync()
    {
        lock (_lock)
        {
            return _writer;
        }
    }

    /// <summary>
    /// Saves the newest report — even one that did not change stage, so the pass's final percent and message are not
    /// lost — and stops taking more. The handler calls this before it turns the row into the completed row, so no
    /// late progress save can overwrite that. The file also leaves <see cref="LiveProgressStore"/>: the pass is no
    /// longer live, whatever it ends as.
    /// </summary>
    public Task CompleteAsync()
    {
        lock (_lock)
        {
            if (!_completed)
            {
                _completed = true;
                if (_unsaved && _latest is { } body)
                {
                    _unsaved = false;
                    Enqueue(body);
                }
            }

            if (_relativeMediaPath is { } path)
            {
                _liveProgress.Remove(path);
            }

            return _writer;
        }
    }

    /// <summary>Queues <paramref name="body"/> for the single background writer, starting it if it is idle. Caller holds <see cref="_lock"/>.</summary>
    private void Enqueue(WireObject body)
    {
        _pending = body;
        if (!_writing)
        {
            _writing = true;
            _writer = Task.Run(WriteAsync);
        }
    }

    /// <summary>One writer at a time: saves the pending report until none is pending, so writes never overlap or reorder.</summary>
    private async Task WriteAsync()
    {
        while (true)
        {
            WireObject body;
            lock (_lock)
            {
                if (_pending is null)
                {
                    _writing = false;
                    return;
                }

                body = _pending;
                _pending = null;
            }

            await _save(body).ConfigureAwait(false);
        }
    }

    private async Task SaveToActivityAsync(WireObject body)
    {
        try
        {
            var uow = await UnitOfWork.OpenAsync(_database!).ConfigureAwait(false);
            await using (uow.ConfigureAwait(false))
            {
                var name = FileName(body.Get("relative_media_path"));
                var detail = WireStrings.Slice(WireJsonWriter.Dumps(body, WireJsonFormat.Compact), 6000);
                if (ActivityId is { } id)
                {
                    await SqliteActivityWriter.UpdateAsync(uow, id, title: Title(LiveProgress.StatusOf(body), name), detail: detail).ConfigureAwait(false);
                    await uow.CommitAsync().ConfigureAwait(false);
                    return;
                }

                var inserted = await SqliteActivityWriter.RecordAsync(
                    uow,
                    new ActivityEventDraft(ActivityEventTypes.ProcessingFileProcessingProgress, "processing", $"Processing {name}", detail)).ConfigureAwait(false);
                await uow.CommitAsync().ConfigureAwait(false);
                ActivityId = inserted;
            }
        }
#pragma warning disable CA1031 // A progress update must never interrupt the media pass; the next report is saved instead.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger!.LogWarning(exception, "Weir could not save a progress update; continuing the media pass.");
        }
    }

    private static string Title(string status, string name) => status switch
    {
        "waiting" => $"Waiting to process {name}",
        "finishing" => $"Finishing {name}",
        "finished" => $"{name} finished processing",
        "failed" => $"{name} could not be processed",
        _ => $"Processing {name}",
    };

    /// <summary><c>Path(str(relative_media_path or "")).name or "this file"</c>.</summary>
    private static string FileName(WireValue? value)
    {
        var text = value is { IsTruthy: true } ? WireConvert.Str(value) : string.Empty;
        var name = MediaPathNames.Name(text, OperatingSystem.IsWindows());
        return name.Length > 0 ? name : "this file";
    }
}
