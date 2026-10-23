using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Core.Settings;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Runtime;

/// <summary>
/// Keeps <see cref="TrayStatus.FileName"/> in Weir's data folder true to what the tray shows: written once the server starts,
/// again whenever the pause, the media managers that do not answer or the folders Weir cannot reach change, and a last time
/// when the server stops cleanly, saying it is no longer running. A burst of changes is read once, at most once a
/// <see cref="Settle"/>, and the file is replaced (whole, then renamed into place) only when its contents differ.
/// A reading or a write that fails is tried again after <see cref="RetryAfter"/>, so what is on show is never left as an earlier run
/// wrote it, and nothing it does can stop the server.
/// </summary>
public sealed class TrayStatusWriter : BackgroundService
{
    /// <summary>The least time between one reading and the next, so a busy queue does not rewrite the file for every job.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait before a reading or a write that failed is tried again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(2);

    /// <summary>The longest the first status waits for the first look at the folders.</summary>
    public static readonly TimeSpan FirstLookWait = TimeSpan.FromSeconds(5);

    /// <summary>How many times the file is written, and how far apart, when the rename is refused.</summary>
    private const int WriteAttempts = 5;
    private static readonly TimeSpan WriteRetryAfter = TimeSpan.FromMilliseconds(100);

    /// <summary>The data that makes up the status: the pause, the managers, and the folders (<see cref="FolderReachability"/> says when its answer changes).</summary>
    private static readonly HashSet<string> Topics = [DataTopics.Pause, DataTopics.Connections, DataTopics.FolderChecks];

    private readonly WeirOptions _options;
    private readonly SqliteDatabase _database;
    private readonly SuiteSettingsStore _settings;
    private readonly FolderReachability _folders;
    private readonly MediaManagerConnectionStore _managers;
    private readonly DataChangePublisher _changes;
    private readonly TimeProvider _time;
    private readonly ILogger<TrayStatusWriter> _logger;
    private readonly Channel<bool> _stale = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private BroadcastSubscription<string>? _heard;
    private TrayStatus? _status;
    private string _written = string.Empty;
    private long _readings;

    public TrayStatusWriter(
        WeirOptions options,
        SqliteDatabase database,
        SuiteSettingsStore settings,
        FolderReachability folders,
        MediaManagerConnectionStore managers,
        DataChangePublisher changes,
        TimeProvider time,
        ILogger<TrayStatusWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(managers);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _database = database;
        _settings = settings;
        _folders = folders;
        _managers = managers;
        _changes = changes;
        _time = time;
        _logger = logger;
    }

    /// <summary>Listens before the server starts answering, so a change made from then on is never missed.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _heard = _changes.Subscribe();
        return base.StartAsync(cancellationToken);
    }

    /// <summary>Once the last reading is done, says in the file that the server has stopped.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_status is { } last)
        {
            await WriteAsync(last with { ServerOk = false }, cancellationToken).ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        // Stopping first: the listener reads in a loop that only cancellation ends, so its subscription is not closed under it.
        base.Dispose();
        _heard?.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.WhenAll(ListenAsync(stoppingToken), KeepWrittenAsync(stoppingToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server is stopping.
        }
    }

    /// <summary>Marks the status stale whenever data it is made of changes; the reading itself is left to the loop that throttles it.</summary>
    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            var topic = await _heard!.ReadAsync(stoppingToken).ConfigureAwait(false);
            if (Topics.Contains(topic))
            {
                _stale.Writer.TryWrite(true);
            }
        }
    }

    /// <summary>Writes the status at once, then again after each settled change; a reading or a write that fails is tried again after <see cref="RetryAfter"/>.</summary>
    private async Task KeepWrittenAsync(CancellationToken stoppingToken)
    {
        await FirstFolderLookAsync(stoppingToken).ConfigureAwait(false);
        await ReadWriteAndRetryAsync(stoppingToken).ConfigureAwait(false);
        while (true)
        {
            await _stale.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(Settle, _time, stoppingToken).ConfigureAwait(false);
            await ReadWriteAndRetryAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The first status waits for the folders to have been looked at, so the tray never hears "all well" before they have been. A
    /// share that is slow to answer holds it for no longer than <see cref="FirstLookWait"/>; the tray hears the rest as it comes.
    /// </summary>
    private async Task FirstFolderLookAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _folders.FirstLook.WaitAsync(FirstLookWait, _time, stoppingToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Written without them; FolderReachability tells the writer when it has an answer.
        }
    }

    /// <summary>Test seam (<c>InternalsVisibleTo</c> to Weir.Infrastructure.Tests): how many readings have finished, whether or not they changed the file.</summary>
    internal long Readings => Interlocked.Read(ref _readings);

    private async Task ReadWriteAndRetryAsync(CancellationToken stoppingToken)
    {
        var written = await ReadAndWriteAsync(stoppingToken).ConfigureAwait(false);
        Interlocked.Increment(ref _readings);
        if (!written)
        {
            await Task.Delay(RetryAfter, _time, stoppingToken).ConfigureAwait(false);
            _stale.Writer.TryWrite(true);
        }
    }

    private async Task<bool> ReadAndWriteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var status = await ReadAsync(stoppingToken).ConfigureAwait(false);
            _status = status;
            return await WriteAsync(status, stoppingToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Nothing a reading does may stop the server; it is logged and tried again.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Weir could not read what the tray shows about it; the tray may be a step behind until the next change.");
            return false;
        }
    }

    private async Task<TrayStatus> ReadAsync(CancellationToken stoppingToken)
    {
        var uow = await UnitOfWork.OpenAsync(_database, stoppingToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            var row = await _settings.GetAsync(uow).ConfigureAwait(false);
            var pause = row is null ? null : PauseState.Resolve(row, Timestamp.UtcNow(_time).AsUtc);
            var managers = await _managers.ListEnabledAsync(uow).ConfigureAwait(false);
            return new TrayStatus(
                pause is { Paused: true },
                pause is { Paused: true, PausedUntil: { } until } ? until.AsUtc : null,
                [.. managers.Where(manager => manager.LastTestOk == false).Select(manager => manager.Label)],
                _folders.Unreachable,
                ServerOk: true);
        }
    }

    /// <summary>
    /// Replaces the file when its contents differ. The tray may have it open to read when the new one is renamed into place, so a
    /// refused rename is tried again a few times before it counts as failed.
    /// </summary>
    private async Task<bool> WriteAsync(TrayStatus status, CancellationToken cancellationToken)
    {
        var contents = status.ToJson();
        if (contents == _written)
        {
            return true;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                AtomicFileWriter.Replace(_options.WeirHome, TrayStatus.FileName, Encoding.UTF8.GetBytes(contents));
                _written = contents;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == WriteAttempts)
                {
                    _logger.LogWarning(exception, "Weir could not tell the tray how it is; the tray may be a step behind until the next change.");
                    return false;
                }
            }

            await Task.Delay(WriteRetryAfter, _time, cancellationToken).ConfigureAwait(false);
        }
    }
}
