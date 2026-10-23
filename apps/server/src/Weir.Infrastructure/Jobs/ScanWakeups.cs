using System.Collections.Concurrent;
using Weir.Infrastructure.Activity;

namespace Weir.Infrastructure.Jobs;

/// <summary>
/// When Weir next looks at each library's watched folder: its next periodic scan, and any look booked for the moment a
/// file there stops being held or a failed file's retry falls due. Processing counts an arriving file down to the real
/// next look instead of guessing.
/// </summary>
/// <remarks>
/// <para>
/// A scan that holds a file until a known time — it is still being written, it changed too recently, the library's
/// window reopens later, a failed file waits out its retry delay — books the library's next look for then, and the scan
/// timer honours the booking on its next tick. Without a booking a held file would wait for the library's next periodic
/// scan, five minutes by default, after its hold had already ended: Processing showed it "due now" in Arriving while a
/// lane stood free. Only the earliest booking per library is kept, because the scan that runs then books the next one itself.
/// </para>
/// <para>
/// This is the only place the next look is known, and it is not stored, so a change to it is announced here as a change to
/// the workflows (<see cref="DataTopics.Libraries"/>) for the screens that count down to it.
/// </para>
/// </remarks>
public sealed class ScanWakeups(DataChangePublisher? changes = null)
{
    /// <summary>How long after a retry falls due the look is booked, so the look finds it due rather than a moment short.</summary>
    private static readonly TimeSpan RetryLookDelay = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<long, DateTimeOffset> _at = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> _periodic = new();

    /// <summary>Book a look at <paramref name="libraryId"/> for when a failed file's retry falls due; nothing when none is owed.</summary>
    public void RequestForRetry(long libraryId, DateTimeOffset? retryAt)
    {
        if (retryAt is { } due)
        {
            Request(libraryId, due + RetryLookDelay);
        }
    }

    /// <summary>The scan timer's next periodic look at <paramref name="libraryId"/>.</summary>
    public void RecordNextPeriodic(long libraryId, DateTimeOffset at) => Announcing(libraryId, () => _periodic[libraryId] = at);

    /// <summary>The scan timer stops looking at <paramref name="libraryId"/> on a schedule (switched off or gone).</summary>
    public void ForgetPeriodic(long libraryId) => Announcing(libraryId, () => _periodic.TryRemove(libraryId, out _));

    /// <summary>The next look at <paramref name="libraryId"/>: the earlier of a booked look and the next periodic one.</summary>
    public DateTimeOffset? NextLookFor(long libraryId)
    {
        DateTimeOffset? booked = _at.TryGetValue(libraryId, out var at) ? at : null;
        DateTimeOffset? periodic = _periodic.TryGetValue(libraryId, out var next) ? next : null;
        return booked is null ? periodic : periodic is null ? booked : booked.Value < periodic.Value ? booked : periodic;
    }

    /// <summary>The scan timer's next periodic look at <paramref name="libraryId"/> alone, ignoring any booked look
    /// for a held file (#747) — the moment the periodic scan-dispatch job is next created, not the earliest look.</summary>
    public DateTimeOffset? NextPeriodicFor(long libraryId) => _periodic.TryGetValue(libraryId, out var next) ? next : null;

    /// <summary>Book a look at <paramref name="libraryId"/> at <paramref name="at"/>, unless an earlier one is booked.</summary>
    public void Request(long libraryId, DateTimeOffset at) =>
        Announcing(libraryId, () => _at.AddOrUpdate(libraryId, at, (_, booked) => at < booked ? at : booked));

    /// <summary>Whether a look is due for <paramref name="libraryId"/> now; a due booking is used up.</summary>
    public bool TakeDue(long libraryId, DateTimeOffset now)
    {
        if (_at.TryGetValue(libraryId, out var at) && at <= now)
        {
            Announcing(libraryId, () => _at.TryRemove(KeyValuePair.Create(libraryId, at)));
            return true;
        }

        return false;
    }

    /// <summary>The booked look for <paramref name="libraryId"/>, if any.</summary>
    public DateTimeOffset? BookedFor(long libraryId) => _at.TryGetValue(libraryId, out var at) ? at : null;

    /// <summary>Runs <paramref name="change"/>, and says the workflows changed if it moved the next look at <paramref name="libraryId"/>.</summary>
    private void Announcing(long libraryId, Action change)
    {
        var before = NextLookFor(libraryId);
        change();
        if (NextLookFor(libraryId) != before)
        {
            changes?.Publish(DataTopics.Libraries);
        }
    }
}
