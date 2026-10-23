using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Endpoints;

/// <summary>
/// The <c>system.overview</c> side of the Activity stream: the facts System shows about this copy of Weir (its jobs and requests
/// today, its checks, the update, the browsers watching), sent when a stream opens and again each time one of them has changed.
/// The overview is read once for every stream together: <see cref="RefreshAsync"/> reads it and, when a fact differs from the last
/// reading, hands the new frame to every open stream. <see cref="SystemOverviewChangeTask"/> refreshes it every few seconds while
/// a browser is watching, and a stream that opens refreshes it too, so it starts from the facts of the moment. Uptime counts every
/// second and so never makes a frame by itself: the page counts it from <c>started_at</c>.
/// </summary>
internal sealed class SystemOverviewFrames : IDisposable
{
    /// <summary>How many frames one slow stream may hold before its oldest are dropped.</summary>
    private const int StreamBacklog = 8;

    private readonly SystemOverviewReader _overview;
    private readonly SqliteDatabase _database;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly Broadcast<string> _feed = new(StreamBacklog);
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private long _refreshes;
    private string? _facts;
    private string? _frame;

    public SystemOverviewFrames(SystemOverviewReader overview, SqliteDatabase database, IServiceProvider services, ILoggerFactory loggers)
    {
        _overview = overview;
        _database = database;
        _services = services;
        ArgumentNullException.ThrowIfNull(loggers);
        _logger = loggers.CreateLogger("weir.platform.system.overview");
    }

    /// <summary>The frames for one open stream, from when it opens until it ends: the overview as it is now, then each change.</summary>
    public async IAsyncEnumerable<string> ForAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var changes = _feed.Subscribe();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        string? sent = null;
        if (Volatile.Read(ref _frame) is { } current)
        {
            sent = current;
            yield return current;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await changes.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (frame != sent)
            {
                sent = frame;
                yield return frame;
            }
        }
    }

    /// <summary>
    /// Reads the overview and sends it to every open stream if a fact about Weir differs from the last reading. Callers that arrive
    /// before a reading starts share it instead of making another. A reading that fails changes nothing: the next one tries again.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var seen = Volatile.Read(ref _refreshes);
        await _refreshing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _refreshes) != seen)
            {
                return;
            }

            Interlocked.Increment(ref _refreshes);
            if (await TryReadAsync(cancellationToken).ConfigureAwait(false) is not { } overview)
            {
                return;
            }

            var facts = WireJsonWriter.Dumps(WithoutUptime(overview), WireJsonFormat.Compact);
            if (facts == _facts)
            {
                return;
            }

            _facts = facts;
            var frame = Frame(overview);
            Volatile.Write(ref _frame, frame);
            _feed.Publish(frame);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>Held for as long as a reading is under way; whoever holds it queues every other reading behind them.</summary>
    internal SemaphoreSlim ReadingInProgress => _refreshing;

    public void Dispose() => _refreshing.Dispose();

    /// <summary>The <c>system.overview</c> SSE frame: the overview as <c>GET /system/overview</c> answers it.</summary>
    public static string Frame(WireObject overview)
    {
        ArgumentNullException.ThrowIfNull(overview);
        return $"event: system.overview\ndata: {WireJsonWriter.Dumps(overview, WireJsonFormat.Compact)}\n\n";
    }

    private static WireObject WithoutUptime(WireObject overview)
    {
        var facts = overview.Copy();
        facts.Remove("uptime_seconds");
        return facts;
    }

    /// <summary>The overview now, or null when it could not be read.</summary>
    private async Task<WireObject?> TryReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            WireObject overview;
            var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
            await using (uow.ConfigureAwait(false))
            {
                overview = await _overview.ReadAsync(uow, _services, cancellationToken).ConfigureAwait(false);
            }

            return overview;
        }
#pragma warning disable CA1031 // A read that fails must not end the live stream; the next look tries again.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogDebug(exception, "The live System overview could not be read; trying again shortly.");
            return null;
        }
    }
}
