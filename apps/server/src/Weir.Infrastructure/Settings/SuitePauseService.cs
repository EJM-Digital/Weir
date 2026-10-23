using System.Globalization;
using Weir.Core.Activity;
using Weir.Core.Settings;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Settings;

/// <summary>
/// The suite-wide pause: every way it changes (someone pausing or resuming, a timed pause running out) is written to the
/// <c>suite_settings</c> row and to Activity in the same unit of work, so what Weir did can always be read back, with who
/// and when. Once a change commits, the live stream says the <see cref="DataTopics.Pause"/> data changed, so every open
/// screen shows it without asking.
/// </summary>
public sealed class SuitePauseService
{
    private const string PublishedKey = "weir_pause_change_published";

    private readonly SuiteSettingsStore _settings;
    private readonly ActivityStore _activity;
    private readonly DataChangePublisher _changes;
    private readonly WorkerWakeSignals? _workers;

    public SuitePauseService(SuiteSettingsStore settings, ActivityStore activity, DataChangePublisher changes, WorkerWakeSignals? workers = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
        _workers = workers;
    }

    /// <summary>The pause as it stands at <paramref name="now"/>; a timed pause that has run out is lifted, and said so, first.</summary>
    public async Task<PauseState> CurrentAsync(UnitOfWork uow, Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var row = await _settings.EnsureAsync(uow).ConfigureAwait(false);
        return await LapseAsync(uow, row, now).ConfigureAwait(false);
    }

    /// <summary>
    /// Pauses (for <paramref name="minutes"/>, or until resumed when none) or resumes. <paramref name="actor"/> is who did it, worded as the subject of the Activity sentence: a person's name, or "The tray". A
    /// request that says nothing about the length (<paramref name="keepEnd"/>) leaves a pause that is already running as it is,
    /// whatever else it changes; an explicit "no length" makes the pause last until it is resumed.
    /// </summary>
    public async Task<PauseState> ChangeAsync(UnitOfWork uow, bool paused, long? minutes, bool keepEnd, bool scanWhilePaused, Timestamp now, string actor)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var row = await _settings.EnsureAsync(uow).ConfigureAwait(false);
        var before = await LapseAsync(uow, row, now).ConfigureAwait(false);
        row = row with { ProcessingPaused = before.Paused, ProcessingPausedUntil = before.Paused ? before.PausedUntil : null };

        Timestamp? until = !paused ? null
            : keepEnd && before.Paused ? before.PausedUntil
            : minutes is { } length ? Timestamp.FromUtc(now.AsUtc.AddMinutes((double)length)) : null;
        await _settings.UpdateAsync(uow, row, row with { ProcessingPaused = paused, ScanWhilePaused = scanWhilePaused, ProcessingPausedUntil = until }).ConfigureAwait(false);
        PublishOnCommit(uow);
        var after = PauseState.Resolve(paused, until, scanWhilePaused, now.AsUtc);

        if (after.Paused && (!before.Paused || before.PausedUntil?.AsUtc != after.PausedUntil?.AsUtc || before.ScanWhilePaused != after.ScanWhilePaused))
        {
            await _activity.RecordAsync(uow, ActivityEventTypes.SystemProcessingPaused, "system", "Processing paused", PausedDetail(after, actor)).ConfigureAwait(false);
        }
        else if (!after.Paused && before.Paused)
        {
            await _activity.RecordAsync(uow, ActivityEventTypes.SystemProcessingResumed, "system", "Processing resumed", $"{actor} resumed processing.").ConfigureAwait(false);
            // Someone resumed it, so the idle slots look now rather than at their next poll. After the commit, so the look sees the resumed state.
            uow.OnCommitted(() => _workers?.WakeEveryLane());
        }

        return after;
    }

    private async Task<PauseState> LapseAsync(UnitOfWork uow, SuiteSettingsRecord row, Timestamp now)
    {
        var state = PauseState.Resolve(row, now.AsUtc);
        if (!state.Expired)
        {
            return state;
        }

        await _settings.UpdateAsync(uow, row, row with { ProcessingPaused = false, ProcessingPausedUntil = null }).ConfigureAwait(false);
        PublishOnCommit(uow);
        await _activity.RecordAsync(
            uow,
            ActivityEventTypes.SystemProcessingResumed,
            "system",
            "Processing resumed",
            $"The pause ran out at {UtcText(state.PausedUntil)}, so processing was resumed.").ConfigureAwait(false);
        return state with { Expired = false };
    }

    /// <summary>Announces the change once <paramref name="uow"/> commits; a unit that lifts a lapsed pause and then changes it announces once.</summary>
    private void PublishOnCommit(UnitOfWork uow)
    {
        if (uow.Items.TryAdd(PublishedKey, true))
        {
            uow.OnCommitted(() => _changes.Publish(DataTopics.Pause));
        }
    }

    private static string PausedDetail(PauseState state, string actor)
    {
        var length = state.PausedUntil is { } until ? $"until {UtcText(until)}" : "until you resume it";
        var looking = state.ScanWhilePaused
            ? "Weir keeps looking for new files and starts nothing."
            : "Weir does not look for new files either.";
        return $"{actor} paused processing {length}. {looking}";
    }

    internal static string UtcText(Timestamp? at) =>
        at is { } value ? value.AsUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : "an unknown time";
}
