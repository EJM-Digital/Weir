using Weir.Core.MediaManagers;
using Weir.Core.Time;

namespace Weir.Infrastructure.ConnectionTraffic;

/// <summary>What Weir last learned about how a connection is used.</summary>
/// <param name="AnswerMilliseconds">How long the last call to it took.</param>
/// <param name="UsedAt">When Weir last talked to it, or it last called Weir.</param>
public readonly record struct ConnectionUsage(long? AnswerMilliseconds, Timestamp? UsedAt)
{
    /// <summary>
    /// A stored timestamp as the API writes it, always with its UTC offset: the database keeps the wall clock alone, and a
    /// value held in memory still carries its offset, so both read the same.
    /// </summary>
    public static string? WireText(Timestamp? at) => at is { } value ? Timestamp.FromUtc(value.AsUtc).ToWireText() : null;
}

/// <summary>
/// The newest <see cref="ConnectionUsage"/> of every connection, kept in memory so a busy queue poll never writes to
/// SQLite. <see cref="ConnectionUsageFlushTask"/> saves what changed on a timer; a list of connections reads through
/// <see cref="Overlay"/>, so it never shows older values than the ones waiting to be saved.
/// </summary>
public sealed class ConnectionUsageLedger
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ConnectionRef, ConnectionUsage> _latest = [];
    private readonly HashSet<ConnectionRef> _unsaved = [];

    /// <summary>Notes a finished call, or an inbound one. A call still being asked changes nothing.</summary>
    public void Record(ConnectionActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (activity.Phase == ConnectionPhase.Asked)
        {
            return;
        }

        lock (_gate)
        {
            var before = _latest.GetValueOrDefault(activity.Connection);
            _latest[activity.Connection] = new ConnectionUsage(activity.Milliseconds ?? before.AnswerMilliseconds, activity.At);
            _unsaved.Add(activity.Connection);
        }
    }

    /// <summary>The stored values, replaced by anything newer held here.</summary>
    public ConnectionUsage Overlay(ConnectionRef connection, long? storedAnswerMilliseconds, Timestamp? storedUsedAt)
    {
        lock (_gate)
        {
            return _latest.TryGetValue(connection, out var latest)
                ? new ConnectionUsage(latest.AnswerMilliseconds ?? storedAnswerMilliseconds, latest.UsedAt)
                : new ConnectionUsage(storedAnswerMilliseconds, storedUsedAt);
        }
    }

    /// <summary>The connections whose usage changed since it was last taken, with their newest values.</summary>
    public IReadOnlyList<(ConnectionRef Connection, ConnectionUsage Usage)> TakeUnsaved()
    {
        lock (_gate)
        {
            var taken = _unsaved.Select(connection => (connection, _latest[connection])).ToList();
            _unsaved.Clear();
            return taken;
        }
    }

    /// <summary>Puts connections back after a save failed, so the next one tries them again.</summary>
    public void ReturnUnsaved(IEnumerable<ConnectionRef> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        lock (_gate)
        {
            _unsaved.UnionWith(connections);
        }
    }

    /// <summary>Drops a removed connection, so a new one that takes its id starts clean.</summary>
    public void Forget(ConnectionRef connection)
    {
        lock (_gate)
        {
            _latest.Remove(connection);
            _unsaved.Remove(connection);
        }
    }
}
