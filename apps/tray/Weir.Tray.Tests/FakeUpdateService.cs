namespace Weir.Tray.Tests;

/// <summary>
/// Stands in for Velopack. It finds and downloads a fixed update, counts the checks and downloads, and records which apply
/// calls were made, in order; <see cref="OnApply"/> lets a test look at the world at the moment an install would start.
/// A check or download can be made to fail, and a download can be held open until a test lets it finish.
/// </summary>
internal sealed class FakeUpdateService : IUpdateService
{
    internal const string AppliedAndExited = "apply-and-exit";
    internal const string AppliedAndRestarted = "apply-and-restart";
    internal const string AppliedLeftWaiting = "apply-left-waiting";

    private readonly List<string> _applied = [];

    /// <summary>What an earlier run left waiting on disk, or null for nothing.</summary>
    public string? LeftWaitingVersion { get; init; }

    /// <summary>Whether a check finds <see cref="FoundVersion"/>.</summary>
    public bool FindsUpdate { get; init; } = true;

    public string FoundVersion { get; init; } = "9.9.9";

    /// <summary>When set, every check ends with this failure.</summary>
    public string? CheckFailure { get; init; }

    /// <summary>When set, every download ends with this failure.</summary>
    public string? DownloadFailure { get; init; }

    /// <summary>When set, a check does not finish until this task does.</summary>
    public Task? HoldCheck { get; init; }

    /// <summary>When set, a download does not finish until this task does.</summary>
    public Task? HoldDownload { get; init; }

    public int Checks { get; private set; }

    public int Downloads { get; private set; }

    public Action<string>? OnApply { get; set; }

    /// <summary>The apply calls made so far, by the constants above.</summary>
    public IReadOnlyList<string> Applied => _applied;

    public bool IsInstalled { get; init; } = true;

    public bool HasPendingUpdate { get; private set; }

    public bool IsDownloaded { get; private set; }

    public string? PendingVersion => HasPendingUpdate ? FoundVersion : null;

    /// <summary>Marks an update as downloaded, as if a download during this run had finished.</summary>
    public FakeUpdateService AlreadyDownloaded()
    {
        HasPendingUpdate = true;
        IsDownloaded = true;
        return this;
    }

    public async Task<UpdateOutcome> CheckForUpdateAsync()
    {
        Checks++;
        if (HoldCheck is not null)
        {
            await HoldCheck;
        }
        if (CheckFailure is not null)
        {
            return UpdateOutcome.Failed(CheckFailure);
        }
        HasPendingUpdate = FindsUpdate;
        return FindsUpdate ? UpdateOutcome.Succeeded : UpdateOutcome.NothingToDo;
    }

    public async Task<UpdateOutcome> DownloadUpdateAsync()
    {
        Downloads++;
        if (HoldDownload is not null)
        {
            await HoldDownload;
        }
        if (DownloadFailure is not null)
        {
            return UpdateOutcome.Failed(DownloadFailure);
        }
        IsDownloaded = HasPendingUpdate;
        return HasPendingUpdate ? UpdateOutcome.Succeeded : UpdateOutcome.NothingToDo;
    }

    public string? FindUpdateLeftWaiting() => LeftWaitingVersion;

    public void ApplyAndExit() => Record(AppliedAndExited);

    public void ApplyAndRestart() => Record(AppliedAndRestarted);

    public void ApplyLeftWaitingUpdateAndRestart() => Record(AppliedLeftWaiting);

    private void Record(string call)
    {
        _applied.Add(call);
        OnApply?.Invoke(call);
    }
}
