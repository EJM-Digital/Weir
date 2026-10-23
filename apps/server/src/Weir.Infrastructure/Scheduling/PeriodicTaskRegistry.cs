using Weir.Infrastructure.Activity;

namespace Weir.Infrastructure.Scheduling;

/// <summary>One task Weir runs on its own, as the System screen lists it.</summary>
/// <param name="Key">A stable id: a task's name, or the kind of work and the workflow it is for.</param>
/// <param name="Label">What the task does, in words, with its workflow's name when it belongs to one.</param>
/// <param name="Running">A run is under way.</param>
/// <param name="LastRunAt">When the last run finished.</param>
/// <param name="LastOk">Whether the last run worked; null until one has finished.</param>
/// <param name="LastError">Why the last run failed, in a sentence; null when it worked.</param>
/// <param name="NextRunAt">When the next run is due, as far as Weir knows.</param>
/// <param name="Interval">How often it runs, when that is a fixed time.</param>
public sealed record PeriodicTaskStatus(
    string Key,
    string Label,
    bool Running,
    DateTimeOffset? LastRunAt,
    bool? LastOk,
    string? LastError,
    DateTimeOffset? NextRunAt,
    TimeSpan? Interval);

/// <summary>
/// What every listed periodic task has done and will do next: the timers announce when each is next due, and whatever
/// runs the work reports when a run starts and how it ended. The System screen reads the list, and each start or end is
/// pushed to the open streams.
/// </summary>
/// <remarks>
/// A run is reported only for a task that has been planned, so work started by hand for something with no timer (a scan
/// of a workflow whose schedule is off) never adds a row. Starts and ends are counted, so a task that overlaps itself
/// stays running until the last run ends.
/// </remarks>
public sealed class PeriodicTaskRegistry
{
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Broadcast<long> _changes = new(backlog: 1);
    private long _version;

    public PeriodicTaskRegistry(TimeProvider time) => _time = time ?? throw new ArgumentNullException(nameof(time));

    /// <summary>
    /// Says when a task is next due. A task seen for the first time is added; a task already listed keeps its history. A
    /// change to its label, its next time or its interval is announced, so a countdown never counts to a time that has passed;
    /// the timers say it again on every tick, and saying the same thing again announces nothing.
    /// </summary>
    public void Plan(string key, string label, DateTimeOffset nextRunAt, TimeSpan? interval)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(label);
        bool announce;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                announce = entry.Label != label || entry.NextRunAt != nextRunAt || entry.Interval != interval;
                entry.Label = label;
                entry.NextRunAt = nextRunAt;
                entry.Interval = interval;
            }
            else
            {
                _entries.Add(key, new Entry(label) { NextRunAt = nextRunAt, Interval = interval });
                announce = true;
            }
        }

        if (announce)
        {
            Announce();
        }
    }

    /// <summary>A run of a planned task has started. Nothing happens for a task that is not planned.</summary>
    public void Begin(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return;
            }

            entry.RunsUnderWay++;
        }

        Announce();
    }

    /// <summary>
    /// A run of a planned task has ended, worked or not. <paramref name="nextRunAt"/> replaces the next time when the runner
    /// knows it; null leaves what the timer last said.
    /// </summary>
    public void End(string key, bool ok, string? error, DateTimeOffset? nextRunAt = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return;
            }

            entry.RunsUnderWay = Math.Max(0, entry.RunsUnderWay - 1);
            entry.LastRunAt = _time.GetUtcNow();
            entry.LastOk = ok;
            entry.LastError = ok ? null : error;
            entry.NextRunAt = nextRunAt ?? entry.NextRunAt;
        }

        Announce();
    }

    /// <summary>The task has no timer any more (switched off, or its workflow is gone).</summary>
    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        bool removed;
        lock (_gate)
        {
            removed = _entries.Remove(key);
        }

        if (removed)
        {
            Announce();
        }
    }

    /// <summary>Every planned task, by label.</summary>
    public IReadOnlyList<PeriodicTaskStatus> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries
                .Select(pair => pair.Value.ToStatus(pair.Key))
                .OrderBy(task => task.Label, StringComparer.Ordinal)
                .ThenBy(task => task.Key, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Listens for runs starting and ending, and for tasks coming and going. An item says only that something changed:
    /// read <see cref="Snapshot"/> for what, so a burst of changes collapses into one read.
    /// </summary>
    public BroadcastSubscription<long> SubscribeToChanges() => _changes.Subscribe();

    private void Announce() => _changes.Publish(Interlocked.Increment(ref _version));

    private sealed class Entry(string label)
    {
        public string Label { get; set; } = label;

        public int RunsUnderWay { get; set; }

        public DateTimeOffset? LastRunAt { get; set; }

        public bool? LastOk { get; set; }

        public string? LastError { get; set; }

        public DateTimeOffset NextRunAt { get; set; }

        public TimeSpan? Interval { get; set; }

        public PeriodicTaskStatus ToStatus(string key) =>
            new(key, Label, RunsUnderWay > 0, LastRunAt, LastOk, LastError, NextRunAt, Interval);
    }
}
