using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Infrastructure.Activity;

namespace Weir.Infrastructure.Runtime;

/// <summary>
/// Hears the Windows tray's side of the hand-off. The tray and the server talk through small files in Weir's data folder:
/// the tray writes <see cref="UpdateFiles.StateFileName"/> at each step of an update (checking, downloading, downloaded, failed), and rewrites
/// <see cref="LanAccessFile.FileName"/> after it has asked Windows for the firewall rule. When one of them is written, by
/// the tray or by the server itself, the stream says the data it carries changed
/// (<see cref="DataTopics.Update"/>, <see cref="DataTopics.NetworkAccess"/>), so System › About shows the tray's answer the
/// moment it is given. A burst of writes (a file is written whole to a scratch file and renamed into place) is announced once, after
/// the folder has been quiet for <see cref="Settle"/>; a burst that never goes quiet is still announced every
/// <see cref="MostWaited"/>, so the screens are never left a long way behind.
/// </summary>
public sealed class TrayHandOffWatcher : BackgroundService
{
    /// <summary>How long the folder must stay quiet before a burst of writes is announced.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    /// <summary>The longest a burst is held back, however steadily it keeps writing.</summary>
    public static readonly TimeSpan MostWaited = TimeSpan.FromSeconds(2);

    private static readonly IReadOnlyDictionary<string, string> TopicByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [UpdateFiles.StateFileName] = DataTopics.Update,
        [UpdateFiles.SettingsFileName] = DataTopics.Update,
        [UpdateFiles.NotAppliedFileName] = DataTopics.Update,
        [LanAccessFile.FileName] = DataTopics.NetworkAccess,
    };

    private readonly WeirOptions _options;
    private readonly DataChangePublisher _changes;
    private readonly TimeProvider _time;
    private readonly ILogger<TrayHandOffWatcher> _logger;
    private readonly TimeSpan _settle;
    private readonly Channel<string> _written = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private FileSystemWatcher? _watcher;

    public TrayHandOffWatcher(WeirOptions options, DataChangePublisher changes, TimeProvider time, ILogger<TrayHandOffWatcher> logger, TimeSpan? settle = null)
    {
        _settle = settle ?? Settle;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Starts listening before the server starts answering, so a write made from then on is never missed.</summary>
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
            await AnnounceAsync(stoppingToken).ConfigureAwait(false);
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
            var watcher = new FileSystemWatcher(_options.WeirHome)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 16 * 1024,
            };
            watcher.Created += (_, change) => Hear(change.Name);
            watcher.Changed += (_, change) => Hear(change.Name);
            watcher.Deleted += (_, change) => Hear(change.Name);
            watcher.Renamed += (_, change) => Hear(change.Name);

            // Events lost to a full buffer could have been any of the files, so every topic is announced.
            watcher.Error += (_, _) => _written.Writer.TryWrite(string.Empty);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Weir could not watch {Folder} for the tray's answers; System › About shows them when it is next opened.", _options.WeirHome);
            return null;
        }
    }

    /// <summary>Takes note that <paramref name="fileName"/> was written; the watcher's own events arrive here, and a test may call it directly.</summary>
    internal void Hear(string? fileName)
    {
        if (fileName is not null && TopicByFile.ContainsKey(fileName))
        {
            _written.Writer.TryWrite(fileName);
        }
    }

    private async Task AnnounceAsync(CancellationToken stoppingToken)
    {
        var heard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var first in _written.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            heard.Add(first);
            await SettleAsync(heard, stoppingToken).ConfigureAwait(false);

            foreach (var topic in TopicsFor(heard))
            {
                _changes.Publish(topic);
            }

            heard.Clear();
        }
    }

    /// <summary>Gathers what is written until the folder has been quiet for the settle time, or the most-waited time has passed.</summary>
    private async Task SettleAsync(HashSet<string> heard, CancellationToken stoppingToken)
    {
        var started = _time.GetTimestamp();
        bool written;
        do
        {
            await Task.Delay(_settle, _time, stoppingToken).ConfigureAwait(false);
            written = false;
            while (_written.Reader.TryRead(out var next))
            {
                heard.Add(next);
                written = true;
            }
        }
        while (written && _time.GetElapsedTime(started) < MostWaited);
    }

    private static IEnumerable<string> TopicsFor(HashSet<string> files) =>
        (files.Contains(string.Empty) ? TopicByFile.Values : files.Select(file => TopicByFile[file])).Distinct(StringComparer.Ordinal);
}
