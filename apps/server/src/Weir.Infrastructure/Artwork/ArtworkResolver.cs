using Microsoft.Extensions.Logging;
using Weir.Core.Artwork;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Artwork;

/// <summary>
/// Finds the poster and original language of each queued title, one at a time and within <see cref="ArtworkRateLimiter"/>'s limits:
/// asks the metadata service, stores the image, and remembers what came of it. A title is asked about once and the one answer serves
/// both; one the service does not know is asked about again after a week, and one that could not be asked is retried with growing
/// waits.
/// </summary>
public sealed class ArtworkResolver : IDisposable
{
    private readonly SqliteDatabase _database;
    private readonly ArtworkLookupStore _lookups;
    private readonly ArtworkGatewayClient _gateway;
    private readonly ArtworkPosterFiles _posterFiles;
    private readonly ArtworkRateLimiter _limiter;
    private readonly TimeProvider _time;
    private readonly ILogger<ArtworkResolver> _logger;
    private readonly SemaphoreSlim _searchGate = new(1, 1);

    public ArtworkResolver(
        SqliteDatabase database,
        ArtworkLookupStore lookups,
        ArtworkGatewayClient gateway,
        ArtworkPosterFiles posterFiles,
        ArtworkRateLimiter limiter,
        TimeProvider time,
        ILogger<ArtworkResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(lookups);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(posterFiles);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _lookups = lookups;
        _gateway = gateway;
        _posterFiles = posterFiles;
        _limiter = limiter;
        _time = time;
        _logger = logger;
    }

    public void Dispose() => _searchGate.Dispose();

    /// <summary>Settle up to <paramref name="maxLookups"/> due titles, stopping early when the service is busy or cannot be reached. Returns how many were settled.</summary>
    public async Task<int> ResolveDueAsync(int maxLookups, CancellationToken cancellationToken)
    {
        var settled = 0;
        while (settled < maxLookups && !_limiter.IsPaused && await NextDueAsync(cancellationToken).ConfigureAwait(false) is { } lookup)
        {
            if (!await ResolveAsync(lookup, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            settled++;
        }

        return settled;
    }

    private async Task<ArtworkLookup?> NextDueAsync(CancellationToken cancellationToken)
    {
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            return await _lookups.NextDueAsync(uow, _time.GetUtcNow()).ConfigureAwait(false);
        }
    }

    private async Task<ArtworkLookup?> FindAsync(string key, CancellationToken cancellationToken)
    {
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            return await _lookups.FindAsync(uow, key, _time.GetUtcNow()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ask the service about a title now rather than at its turn in the queue, for a caller that needs its original language. The answer
    /// is remembered like any other, so the poster that comes with it is not searched for again; the caller reads what it needs from the
    /// lookup afterwards.
    /// </summary>
    public async Task SearchNowAsync(ArtworkLookup lookup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        await SearchAsync(lookup, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What a search left: whether the service can take more work, and the poster it named, if any.</summary>
    private readonly record struct Searched(bool ServiceAvailable, string? PosterRef);

    /// <summary>
    /// One search at a time. A caller asking now and the background pass can reach the same title together; whichever goes second
    /// reads the title again, finds that the other search settled it, and does not ask the service a second time.
    /// </summary>
    private async Task<Searched> SearchAsync(ArtworkLookup lookup, CancellationToken cancellationToken)
    {
        await _searchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await FindAsync(lookup.Key, cancellationToken).ConfigureAwait(false) ?? lookup;
            if (SettledSince(lookup, current))
            {
                return new Searched(true, current.PosterRef);
            }

            return await SearchUnguardedAsync(current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _searchGate.Release();
        }
    }

    /// <summary>Whether a search recorded something about the title after <paramref name="seen"/> was read: every search changes one of these.</summary>
    private static bool SettledSince(ArtworkLookup seen, ArtworkLookup current) =>
        (seen.OriginalLanguage, seen.Outcome, seen.Attempts, seen.IsDue) != (current.OriginalLanguage, current.Outcome, current.Attempts, current.IsDue);

    /// <summary>One search and the settling of its answer: the language and the poster are remembered, and a title with no poster to fetch is settled as missing.</summary>
    private async Task<Searched> SearchUnguardedAsync(ArtworkLookup lookup, CancellationToken cancellationToken)
    {
        await _limiter.WaitForSearchAsync(cancellationToken).ConfigureAwait(false);
        var answer = await _gateway.FindAsync(lookup, cancellationToken).ConfigureAwait(false);
        if (answer.Status != GatewayStatus.Ok)
        {
            return new Searched(await SettleWithoutPosterAsync(lookup, answer.Status, answer.RetryAfter, cancellationToken).ConfigureAwait(false), null);
        }

        var match = answer.Value!;
        await WriteAsync(uow => _lookups.RecordMatchAsync(uow, lookup.Key, match.OriginalLanguage, match.PosterRef), cancellationToken).ConfigureAwait(false);
        var posterRef = lookup.PosterRef ?? match.PosterRef;
        return posterRef is null
            ? new Searched(await SettleWithoutPosterAsync(lookup, GatewayStatus.NotFound, retryAfter: null, cancellationToken).ConfigureAwait(false), null)
            : new Searched(true, posterRef);
    }

    /// <summary>Settle one title. False when the service cannot take more work for now, so the caller stops.</summary>
    private async Task<bool> ResolveAsync(ArtworkLookup lookup, CancellationToken cancellationToken)
    {
        var posterRef = lookup.PosterRef;
        if (posterRef is null)
        {
            var searched = await SearchAsync(lookup, cancellationToken).ConfigureAwait(false);
            if (searched.PosterRef is null)
            {
                return searched.ServiceAvailable;
            }

            posterRef = searched.PosterRef;
        }

        var posterId = ArtworkKeys.PosterId(posterRef);
        if (!await HasPosterAsync(posterId, cancellationToken).ConfigureAwait(false))
        {
            var image = await _gateway.FetchPosterAsync(posterRef, cancellationToken).ConfigureAwait(false);
            if (image.Status != GatewayStatus.Ok)
            {
                return await SettleWithoutPosterAsync(lookup, image.Status, image.RetryAfter, cancellationToken).ConfigureAwait(false);
            }

            if (!await StorePosterAsync(posterId, posterRef, image.Value!, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Weir could not replace a poster that is being sent just now, so it kept the old one and will try again.");
                return false;
            }
        }

        await WriteAsync(uow => _lookups.RecordFoundAsync(uow, lookup.Key, posterId), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HasPosterAsync(string posterId, CancellationToken cancellationToken)
    {
        if (!_posterFiles.Exists(posterId))
        {
            return false;
        }

        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            return await _lookups.FindPosterAsync(uow, posterId).ConfigureAwait(false) is not null;
        }
    }

    private async Task<bool> StorePosterAsync(string posterId, string posterRef, GatewayBody image, CancellationToken cancellationToken)
    {
        if (!await _posterFiles.WriteAsync(posterId, image.Bytes, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await WriteAsync(uow => _lookups.AddPosterAsync(uow, posterId, posterRef, image.ContentType, image.Bytes.Length), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> SettleWithoutPosterAsync(ArtworkLookup lookup, GatewayStatus status, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        switch (status)
        {
            case GatewayStatus.NotFound:
                await WriteAsync(uow => _lookups.RecordMissingAsync(uow, lookup.Key, now + ArtworkSchedule.MissingRetryAfter), cancellationToken).ConfigureAwait(false);
                return true;
            case GatewayStatus.Busy:
                _limiter.HonourBusy(retryAfter);
                _logger.LogInformation("The metadata service asked Weir to wait before looking up more posters.");
                return false;
            default:
                var attempts = lookup.Attempts + 1;
                await WriteAsync(uow => _lookups.RecordFailureAsync(uow, lookup.Key, attempts, now + ArtworkSchedule.FailureBackoff(attempts)), cancellationToken).ConfigureAwait(false);
                _limiter.HonourUnreachable();
                return false;
        }
    }

    private async Task WriteAsync(Func<UnitOfWork, Task> write, CancellationToken cancellationToken)
    {
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            await write(uow).ConfigureAwait(false);
            await uow.CommitAsync().ConfigureAwait(false);
        }
    }
}
