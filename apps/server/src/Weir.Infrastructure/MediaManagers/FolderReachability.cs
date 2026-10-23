using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Scheduling;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// Knows which of the switched-on workflows' own folders (watched, work and output) Weir cannot reach, for the tray's dot. It
/// looks every <see cref="Every"/> and at once when the workflows change, whether or not a browser is open, and so asks only
/// about the folders themselves (<see cref="LibraryFolderChainRules.IsUnreachable"/>): the media managers and download clients
/// stay with <see cref="FolderChecksTask"/>, which looks only while a screen is watching and checks far more.
/// <para>
/// Each folder is asked about on a thread of its own and for no longer than <see cref="ProbeTimeout"/>: a share that hangs is
/// unreachable, and is not asked about again until the first question ends, so a dead share holds one thread, not one every
/// look. A folder is reported only once two looks in a row have missed it, so one slow answer is not a problem, and is dropped at
/// the first look that finds it. When the answer is not the one it gave last time it says so on
/// <see cref="DataTopics.FolderChecks"/>, because it is the one that knows.
/// </para>
/// </summary>
public sealed class FolderReachability : BackgroundService
{
    /// <summary>How often the folders are looked at when nothing else prompts it.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(15);

    /// <summary>How soon a folder that was missed is looked at again, to confirm it.</summary>
    public static readonly TimeSpan ConfirmAfter = TimeSpan.FromSeconds(3);

    /// <summary>The longest one folder is given to answer.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private const int MissesToReport = 2;

    private readonly SqliteDatabase _database;
    private readonly LibraryStore _libraries;
    private readonly WeirOptions _options;
    private readonly IFolderProbe _probe;
    private readonly DataChangePublisher _changes;
    private readonly TimeProvider _time;
    private readonly ILogger<FolderReachability> _logger;
    private readonly Channel<bool> _stale = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly TaskCompletionSource _firstLook = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<FolderToReach, Task<bool>> _asking = [];
    private BroadcastSubscription<string>? _heard;
    private IReadOnlyList<string> _unreachable = [];
    private Dictionary<string, int> _misses = [];
    private bool _confirming;

    public FolderReachability(
        SqliteDatabase database,
        LibraryStore libraries,
        WeirOptions options,
        IFolderProbe probe,
        DataChangePublisher changes,
        TimeProvider time,
        ILogger<FolderReachability> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _libraries = libraries;
        _options = options;
        _probe = probe;
        _changes = changes;
        _time = time;
        _logger = logger;
    }

    /// <summary>The folders Weir cannot reach, each in plain words ("The watched folder for Movies"), in workflow order.</summary>
    public IReadOnlyList<string> Unreachable => Volatile.Read(ref _unreachable);

    /// <summary>Completes once the folders have been looked at and nothing is left to confirm, so nothing says "all well" before they have been.</summary>
    public Task FirstLook => _firstLook.Task;

    /// <summary>Listens before the server starts answering, so a change made from then on is never missed.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _heard = _changes.Subscribe();
        return base.StartAsync(cancellationToken);
    }

    public override void Dispose()
    {
        base.Dispose();
        _heard?.Dispose();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        StoppingWork.RunAsync(() => Task.WhenAll(ListenAsync(stoppingToken), KeepLookingAsync(stoppingToken)), stoppingToken);

    /// <summary>Looks at the folders now, and says so if the answer is not the one it gave last time. A look that fails keeps the last answer.</summary>
    internal async Task LookAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reported = Confirm(await ReadAsync(cancellationToken).ConfigureAwait(false));
            if (!reported.SequenceEqual(Unreachable, StringComparer.Ordinal))
            {
                Volatile.Write(ref _unreachable, reported);
                _changes.Publish(DataTopics.FolderChecks);
            }
        }
#pragma warning disable CA1031 // Nothing a look does may stop the server; it is logged and tried again.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _confirming = false;
            _logger.LogWarning(exception, "Weir could not check whether it can reach its workflows' folders; the tray keeps the last answer until the next look.");
        }
        finally
        {
            if (!_confirming)
            {
                _firstLook.TrySetResult();
            }
        }
    }

    /// <summary>Counts the folders missed in a row, forgets those that answered, and returns the ones missed often enough to report.</summary>
    private List<string> Confirm(List<string> missed)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in missed)
        {
            counts[name] = _misses.GetValueOrDefault(name) + 1;
        }

        _misses = counts;
        _confirming = counts.Values.Any(count => count < MissesToReport);
        return [.. missed.Distinct(StringComparer.Ordinal).Where(name => counts[name] >= MissesToReport)];
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            var topic = await _heard!.ReadAsync(stoppingToken).ConfigureAwait(false);
            if (topic == DataTopics.Libraries)
            {
                _stale.Writer.TryWrite(true);
            }
        }
    }

    private async Task KeepLookingAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await LookAsync(stoppingToken).ConfigureAwait(false);
            await UntilNextLookAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits for the interval to pass or the workflows to change, whichever comes first; a folder to confirm shortens the interval.</summary>
    private async Task UntilNextLookAsync(CancellationToken stoppingToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await Task.WhenAny(_stale.Reader.WaitToReadAsync(wait.Token).AsTask(), Task.Delay(_confirming ? ConfirmAfter : Every, _time, wait.Token)).ConfigureAwait(false);
        await wait.CancelAsync().ConfigureAwait(false);
        _stale.Reader.TryRead(out _);
    }

    /// <summary>The folders missed this look, named, in workflow order.</summary>
    private async Task<List<string>> ReadAsync(CancellationToken cancellationToken)
    {
        List<ProcessingLibraryRecord> workflows;
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            workflows = await _libraries.ListAsync(uow, enabledOnly: true).ConfigureAwait(false);
        }

        var folders = workflows
            .SelectMany(workflow => LibraryFolderChainCheck.FoldersToReach(workflow, _options.WeirHome)
                .Select(folder => (Name: $"The {Role(folder.Folder)} folder for {workflow.Name}", Folder: folder)))
            .ToList();
        var missed = await Task.WhenAll(folders.Select(item => IsUnreachableAsync(item.Folder, cancellationToken))).ConfigureAwait(false);
        return [.. folders.Where((_, index) => missed[index]).Select(item => item.Name)];
    }

    /// <summary>Whether the folder did not answer in time, or answered that it cannot be reached. A hung share is unreachable.</summary>
    private async Task<bool> IsUnreachableAsync(FolderToReach folder, CancellationToken cancellationToken)
    {
        try
        {
            return await Ask(folder).WaitAsync(ProbeTimeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return true;
        }
    }

    /// <summary>
    /// The question about one folder, on a thread of its own because a share that has gone quiet can hold it for as long as the
    /// system waits on it. A question still waiting is the answer to the next ask, so a dead share never holds more than one thread.
    /// </summary>
    private Task<bool> Ask(FolderToReach folder)
    {
        lock (_asking)
        {
            foreach (var finished in _asking.Where(entry => entry.Value.IsCompleted && entry.Key != folder).Select(entry => entry.Key).ToList())
            {
                _asking.Remove(finished);
            }

            if (_asking.TryGetValue(folder, out var running) && !running.IsCompleted)
            {
                return running;
            }

            var asked = BlockingWork.RunAsync(() => LibraryFolderChainRules.IsUnreachable(folder, _probe));
            _asking[folder] = asked;
            return asked;
        }
    }

    internal static string Role(LocalFolder folder) => folder switch
    {
        LocalFolder.Watched => "watched",
        LocalFolder.Work => "work",
        LocalFolder.Output => "output",
        _ => throw new ArgumentOutOfRangeException(nameof(folder), folder, null),
    };
}
