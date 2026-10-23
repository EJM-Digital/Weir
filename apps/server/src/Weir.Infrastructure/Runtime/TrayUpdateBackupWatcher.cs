using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Core;
using Weir.Core.Configuration;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Runtime;

/// <summary>
/// <para>
/// Saves the copy of Weir's data that the tray asks for before it applies an update, while this server is still running: the
/// tray writes <see cref="RequestFileName"/> in Weir's data folder, holding
/// <c>{ "id": "…", "requested_at": "2026-10-10T08:30:00Z", "target_version": "1.0.0-rc.13" }</c>, and this answers in
/// <see cref="ResultFileName"/>, first <c>{ "id": "…", "state": "started" }</c> as soon as it has heard (a server too old to have
/// this watcher never writes it, which is how the tray knows to leave the copy to the server that starts after the update), then
/// <c>"saved"</c> with the copy's <c>path</c>, or <c>"failed"</c> with a plain <c>reason</c>. A file, like the other hand-offs, so
/// it needs no session and only a process that can write the data folder can ask. A request made more than <see cref="MaxAge"/>
/// ago, or already answered, is deleted unanswered.
/// </para>
/// <para>
/// While the watcher is listening it keeps <see cref="ReadyFileName"/> in the data folder, <c>{ "pid": …, "started_at": … }</c> for
/// this process, and removes it when the server stops. It is how the tray tells a server that can take the request from one built
/// before it existed, without waiting to find out: a marker that names the running server means the request will be heard.
/// </para>
/// </summary>
public sealed class TrayUpdateBackupWatcher : BackgroundService
{
    public const string RequestFileName = "update-backup-request.json";
    public const string ResultFileName = "update-backup-result.json";
    public const string ReadyFileName = "update-backup-ready";

    public const string StartedState = "started";
    public const string SavedState = "saved";
    public const string FailedState = "failed";

    /// <summary>How old a request may be and still be answered.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    /// <summary>How long a request that cannot be read yet is given to be finished before it is read once more.</summary>
    public static readonly TimeSpan HalfWrittenWait = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a request that was refused for a moment waits before it is looked at again; the tray asks again at 15 s, 45 s and 105 s, and gives up at 225 s.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly WeirOptions _options;
    private readonly SqliteDatabase _database;
    private readonly TimeProvider _time;
    private readonly ILogger<TrayUpdateBackupWatcher> _logger;
    private readonly Channel<bool> _written = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private FileSystemWatcher? _watcher;
    private string? _lastAnswered;

    public TrayUpdateBackupWatcher(WeirOptions options, SqliteDatabase database, TimeProvider time, ILogger<TrayUpdateBackupWatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _database = database;
        _time = time;
        _logger = logger;
    }

    /// <summary>Starts listening before the server starts answering, so a request made from then on is never missed.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _watcher = TryWatch();
        if (_watcher is not null)
        {
            WriteReadyMarker();
        }

        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        RemoveReadyMarker();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        RemoveReadyMarker();
        _watcher?.Dispose();
        base.Dispose();
    }

    private void WriteReadyMarker()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var text = JsonSerializer.Serialize(new { Pid = self.Id, StartedAt = self.StartTime.ToUniversalTime() }, Json);
            AtomicFileWriter.Replace(_options.WeirHome, ReadyFileName, Encoding.UTF8.GetBytes(text));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(exception, "Weir could not say that it can save a copy of its data for the tray; the tray will leave the copy to the start after its update.");
        }
    }

    private void RemoveReadyMarker()
    {
        try
        {
            File.Delete(Path.Join(_options.WeirHome, ReadyFileName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The tray tells a marker that names another process from one that names this one.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_watcher is null)
        {
            return;
        }

        try
        {
            _written.Writer.TryWrite(true);
            while (true)
            {
                await _written.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(TrayHandOffWatcher.Settle, stoppingToken).ConfigureAwait(false);
                try
                {
                    if (!await AnswerAsync(stoppingToken).ConfigureAwait(false))
                    {
                        // The request was refused for a moment (the tray, or a virus scanner, had it): look again rather than leave the tray unanswered.
                        await Task.Delay(RetryAfter, stoppingToken).ConfigureAwait(false);
                        _written.Writer.TryWrite(true);
                    }
                }
#pragma warning disable CA1031 // Nothing a request does may stop the server; it is logged, and the tray's wait ends without an answer.
                catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    _logger.LogWarning(exception, "Weir could not answer the update backup request from the tray.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server is stopping.
        }
    }

    private FileSystemWatcher? TryWatch()
    {
        try
        {
            var watcher = new FileSystemWatcher(_options.WeirHome, RequestFileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += (_, _) => _written.Writer.TryWrite(true);
            watcher.Changed += (_, _) => _written.Writer.TryWrite(true);
            watcher.Renamed += (_, _) => _written.Writer.TryWrite(true);
            watcher.Error += (_, _) => _written.Writer.TryWrite(true);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Weir could not watch {Folder} for the tray's update backup requests; the tray will not be able to have a copy saved before it updates, and the update's own start saves one instead.", _options.WeirHome);
            return null;
        }
    }

    private sealed record Request(string Id, DateTimeOffset RequestedAt, string TargetVersion);

    /// <summary>Answers the request in the file. False when the file was refused and is to be looked at again.</summary>
    private async Task<bool> AnswerAsync(CancellationToken stoppingToken)
    {
        var path = Path.Join(_options.WeirHome, RequestFileName);
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
            await Task.Delay(HalfWrittenWait, stoppingToken).ConfigureAwait(false);
            var again = ReplacedFile.ReadText(path);
            if (again.State != ReplacedFileState.Read)
            {
                return Settled(again.State);
            }

            text = again.Text;
            request = Parse(text);
        }

        if (request is null)
        {
            _logger.LogInformation("Weir ignored an update backup request from the tray that it could not read.");
        }
        else if (request.Id == _lastAnswered)
        {
            _logger.LogDebug("Weir already answered the tray's update backup request {Id}.", request.Id);
        }
        else if (_time.GetUtcNow() - request.RequestedAt > MaxAge)
        {
            _logger.LogInformation("Weir ignored an update backup request from the tray that was made more than {Minutes} minutes ago.", MaxAge.TotalMinutes);
        }
        else
        {
            _lastAnswered = request.Id;
            await SaveAndReportAsync(request, stoppingToken).ConfigureAwait(false);
        }

        DeleteIfUnchanged(path, text);
        return true;
    }

    private async Task SaveAndReportAsync(Request request, CancellationToken stoppingToken)
    {
        await WriteResultAsync(new Result(request.Id, StartedState, null, null), stoppingToken).ConfigureAwait(false);
        Result result;
        try
        {
            var saved = await BlockingWork.RunAsync(() => Save(request.TargetVersion)).WaitAsync(stoppingToken).ConfigureAwait(false);
            result = new Result(request.Id, SavedState, saved.DatabasePath, null);
        }
        catch (PreUpdateBackupException exception)
        {
            result = new Result(request.Id, FailedState, null, exception.Reason);
        }
#pragma warning disable CA1031 // Whatever goes wrong is a failed copy, and the tray must be told.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogError(exception, "Weir could not save a copy of its data for the tray's update.");
            result = new Result(request.Id, FailedState, null, new PreUpdateBackupException().Reason);
        }

        await WriteResultAsync(result, stoppingToken).ConfigureAwait(false);
    }

    private PreUpdateBackupFile Save(string targetVersion)
    {
        using var connection = _database.Open();
        var revision = SchemaMigrator.ReadRecordedRevision(connection)
            ?? throw new InvalidOperationException("The database records no schema revision.");
        var backup = new PreUpdateBackup(
            _options.BackupDir, _options.WeirHome, targetVersion, _time, _logger, fromVersion: WeirVersion.Resolve(_options.VersionOverride));
        return backup.Save(connection, revision);
    }

    private sealed record Result(string Id, string State, string? Path, string? Reason);

    /// <summary>How many times an answer is written before giving up, and the pause between tries.</summary>
    private const int WriteAttempts = 10;

    private static readonly TimeSpan WriteRetryPause = TimeSpan.FromMilliseconds(50);

    // The tray reads the file while it waits, and renaming a new one over a file that is being opened can fail for a moment; an
    // answer that never lands leaves the tray waiting for a copy that is already saved, so it is tried again.
    private async Task WriteResultAsync(Result result, CancellationToken cancellationToken)
    {
        var text = JsonSerializer.Serialize(new { result.Id, result.State, result.Path, result.Reason, WrittenAt = _time.GetUtcNow() }, Json);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                AtomicFileWriter.Replace(_options.WeirHome, ResultFileName, Encoding.UTF8.GetBytes(text));
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == WriteAttempts)
                {
                    _logger.LogWarning(exception, "Weir could not write its answer to the tray's update backup request.");
                    return;
                }
            }

            await Task.Delay(WriteRetryPause, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Request? Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && Text(root, "id") is { Length: > 0 } id
                && Text(root, "requested_at") is { } requestedAt
                && DateTimeOffset.TryParse(requestedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                && Text(root, "target_version") is { Length: > 0 } target
                    ? new Request(id, at, target)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Removes the answered file, unless the tray has written another request into it since it was read: that one is heard on its own.</summary>
    // No file is nothing to answer; a file that is refused for a moment is looked at again.
    private bool Settled(ReplacedFileState state)
    {
        if (state == ReplacedFileState.Busy)
        {
            _logger.LogDebug("The tray's update backup request is refused for a moment; looking again shortly.");
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
            _logger.LogWarning(exception, "Weir could not remove the tray's update backup request {Path}.", path);
        }
    }
}
