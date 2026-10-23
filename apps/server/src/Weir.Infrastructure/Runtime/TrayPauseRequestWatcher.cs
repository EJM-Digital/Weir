using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Runtime;

/// <summary>
/// Hears the tray ask to pause or resume processing. The tray writes <see cref="FileName"/> in Weir's data folder, holding
/// <c>{ "paused": true, "requested_at": "2026-10-09T08:30:00Z" }</c> (<c>false</c> to resume); the request is applied through
/// <see cref="SuitePauseService"/> as "The tray", which records it in Activity, and the file is deleted once that has
/// succeeded; until then it is tried again. A pause asked for from the tray lasts until it is resumed and leaves the choice to
/// keep looking for new files as it stands. A request is answered once: one that was already applied, one asked for more than
/// <see cref="MaxAge"/> ago (a file left behind while the server was down) and a file that is not that shape are ignored and
/// deleted, and a file the tray has rewritten since it was read is left for the next look. Only a process that can write to the
/// data folder can make a request; no route takes one.
/// </summary>
public sealed class TrayPauseRequestWatcher : BackgroundService
{
    public const string FileName = "pause-request.json";

    /// <summary>Who Activity says paused or resumed processing.</summary>
    public const string RequestedBy = "The tray";

    /// <summary>How old a request may be and still be answered, so a pause asked for long ago is never applied now.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    /// <summary>How long a file that cannot be read yet is given to be finished before it is read once more.</summary>
    public static readonly TimeSpan HalfWrittenWait = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to wait before a request that could not be applied is tried again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(2);

    private readonly WeirOptions _options;
    private readonly SqliteDatabase _database;
    private readonly SuitePauseService _pause;
    private readonly TimeProvider _time;
    private readonly ILogger<TrayPauseRequestWatcher> _logger;
    private readonly Channel<bool> _written = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private FileSystemWatcher? _watcher;
    private DateTimeOffset _lastApplied = DateTimeOffset.MinValue;
    private long _requestsRead;

    public TrayPauseRequestWatcher(WeirOptions options, SqliteDatabase database, SuitePauseService pause, TimeProvider time, ILogger<TrayPauseRequestWatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _database = database;
        _pause = pause;
        _time = time;
        _logger = logger;
    }

    /// <summary>Starts listening before the server starts answering, so a request made from then on is never missed.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _watcher = TryWatch();
        return base.StartAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_watcher is null)
        {
            return;
        }

        try
        {
            // A request left by a tray that asked while the server was down is looked at first.
            _written.Writer.TryWrite(true);
            await foreach (var _ in _written.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(TrayHandOffWatcher.Settle, _time, stoppingToken).ConfigureAwait(false);
                if (!await ApplyAsync(stoppingToken).ConfigureAwait(false))
                {
                    await Task.Delay(RetryAfter, _time, stoppingToken).ConfigureAwait(false);
                    _written.Writer.TryWrite(true);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server is stopping.
        }
    }

    /// <summary>Test seam (<c>InternalsVisibleTo</c> to Weir.Infrastructure.Tests): how many requests have been read from the file, whether or not they were answered yet.</summary>
    internal long RequestsRead => Interlocked.Read(ref _requestsRead);

    private FileSystemWatcher? TryWatch()
    {
        try
        {
            var watcher = new FileSystemWatcher(_options.WeirHome, FileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += (_, _) => _written.Writer.TryWrite(true);
            watcher.Changed += (_, _) => _written.Writer.TryWrite(true);
            watcher.Renamed += (_, _) => _written.Writer.TryWrite(true);

            // Events lost to a full buffer could have been the request.
            watcher.Error += (_, _) => _written.Writer.TryWrite(true);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Weir could not watch {Folder} for the tray's pause requests; Pause and Resume in the tray will not work until Weir is restarted.", _options.WeirHome);
            return null;
        }
    }

    /// <summary>Answers the request in the file, if there is one. False when it is to be looked at again.</summary>
    private async Task<bool> ApplyAsync(CancellationToken stoppingToken)
    {
        try
        {
            return await AnswerAsync(stoppingToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Nothing a request does may stop the server; it is logged and tried again.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Weir could not answer the pause request from the tray; it will try again shortly.");
            return false;
        }
    }

    /// <summary>Answers the request in the file. False when the file was refused (the tray is renaming one into place) and is to be looked at again.</summary>
    private async Task<bool> AnswerAsync(CancellationToken stoppingToken)
    {
        var path = Path.Join(_options.WeirHome, FileName);
        var reading = ReplacedFile.ReadText(path);
        if (reading.State != ReplacedFileState.Read)
        {
            return Settled(reading.State);
        }

        var text = reading.Text;
        var request = Parse(text);
        if (request is null)
        {
            // The tray may still be writing it.
            _logger.LogDebug("The tray's pause request cannot be read yet; looking again shortly.");
            await Task.Delay(HalfWrittenWait, _time, stoppingToken).ConfigureAwait(false);
            var again = ReplacedFile.ReadText(path);
            if (again.State != ReplacedFileState.Read)
            {
                return Settled(again.State);
            }

            text = again.Text;
            request = Parse(text);
        }

        if (request is not null)
        {
            Interlocked.Increment(ref _requestsRead);
        }

        if (request is null)
        {
            _logger.LogInformation("Weir ignored a pause request from the tray that it could not read.");
        }
        else if (request.RequestedAt <= _lastApplied)
        {
            _logger.LogDebug("Weir already answered the tray's pause request made at {RequestedAt}.", request.RequestedAt);
        }
        else if (_time.GetUtcNow() - request.RequestedAt > MaxAge)
        {
            _logger.LogInformation("Weir ignored a pause request from the tray that was made more than {Minutes} minutes ago.", MaxAge.TotalMinutes);
        }
        else
        {
            await ChangeAsync(request.Paused, stoppingToken).ConfigureAwait(false);
            _lastApplied = request.RequestedAt;
        }

        DeleteIfUnchanged(path, text);
        return true;
    }

    private sealed record PauseRequest(bool Paused, DateTimeOffset RequestedAt);

    /// <summary>The request the text holds; null for text that is not one.</summary>
    private static PauseRequest? Parse(string text)
    {
        try
        {
            return WireJsonParser.Parse(text) is WireObject request
                && request.Get("paused") is WireBool paused
                && request.Get("requested_at") is WireString requestedAt
                && DateTimeOffset.TryParse(requestedAt.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                    ? new PauseRequest(paused.Value, at)
                    : null;
        }
        catch (WireJsonDecodeException)
        {
            return null;
        }
    }

    private async Task ChangeAsync(bool paused, CancellationToken stoppingToken)
    {
        var now = Timestamp.UtcNow(_time);
        var uow = await UnitOfWork.OpenAsync(_database, stoppingToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            var current = await _pause.CurrentAsync(uow, now).ConfigureAwait(false);
            await _pause.ChangeAsync(uow, paused, minutes: null, keepEnd: false, current.ScanWhilePaused, now, RequestedBy).ConfigureAwait(false);
            await uow.CommitAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Removes the answered file, unless the tray has written another request into it since it was read: that one is heard on its own.</summary>
    // No file is nothing to answer; a file that is refused for a moment is looked at again.
    private bool Settled(ReplacedFileState state)
    {
        if (state == ReplacedFileState.Busy)
        {
            _logger.LogDebug("The tray's pause request is refused for a moment; looking again shortly.");
        }

        return state != ReplacedFileState.Busy;
    }

    private void DeleteIfUnchanged(string path, string answered)
    {
        try
        {
            // A file that is refused just now is not known to be unchanged, so it is left; it was answered, and finding it again changes nothing.
            var now = ReplacedFile.ReadText(path);
            if (now.State == ReplacedFileState.Read && now.Text == answered)
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The request stays answered: it is remembered, so finding it again changes nothing.
            _logger.LogWarning(exception, "Weir could not remove the tray's pause request {Path}.", path);
        }
    }
}
