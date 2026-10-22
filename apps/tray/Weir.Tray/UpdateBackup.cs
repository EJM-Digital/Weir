using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Weir.Tray;

/// <summary>
/// The tray's way to have a copy of Weir's data saved before it applies an update. Every path that applies one asks first
/// (<see cref="TrayShutdown"/>, and the install at start-up), and none applies it when the copy could not be saved.
/// </summary>
interface IUpdateBackup
{
    /// <summary>
    /// Has the running server save a copy of Weir's data before the update to <paramref name="targetVersion"/> is applied. True
    /// when the update may go ahead: the copy is saved, or the server is too old to make one and the server that starts after the
    /// update will. False when the copy could not be saved: the person has been told why, and the update must not be applied.
    /// Never throws.
    /// </summary>
    Task<bool> SaveBeforeApplyAsync(string? targetVersion);
}

/// <summary>How the running server answered a request for a copy of Weir's data.</summary>
enum UpdateBackupOutcome
{
    /// <summary>The copy is saved.</summary>
    Saved,

    /// <summary>The copy could not be saved, or the server that can save one did not; <see cref="UpdateBackupAnswer.Reason"/> says why.</summary>
    Failed,

    /// <summary>No server that can save a copy is running: none, or a build from before it could. The server that starts after the update saves the copy.</summary>
    NotAnswered,
}

/// <param name="Outcome">How the request ended.</param>
/// <param name="Reason">Why the copy could not be saved, in plain words, when <paramref name="Outcome"/> is Failed.</param>
readonly record struct UpdateBackupAnswer(UpdateBackupOutcome Outcome, string? Reason);

/// <summary>A server process: its id and when it began, which together tell it from any other that reuses the id.</summary>
readonly record struct RunningServer(int ProcessId, DateTime StartedUtc)
{
    /// <summary>The two clocks that read a start time (the server's, the tray's) can differ by a little.</summary>
    private static readonly TimeSpan StartTimeSlack = TimeSpan.FromSeconds(2);

    internal bool IsSameProcessAs(RunningServer other) =>
        ProcessId == other.ProcessId && (StartedUtc - other.StartedUtc).Duration() <= StartTimeSlack;
}

/// <summary>
/// <c>update-backup-ready</c> in the runtime home: <c>{ "pid": …, "started_at": … }</c>, which a server whose backup watcher is
/// listening writes (<c>TrayUpdateBackupWatcher</c> in the server) and removes when it stops. A marker that names the running
/// server is how the tray knows a request will be heard; no marker means a build from before the request existed.
/// </summary>
static class UpdateBackupReady
{
    internal const string FileName = "update-backup-ready";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The server the marker names, or null when there is no marker or it names none. See <see cref="Look"/> for a marker that could not be read.</summary>
    internal static RunningServer? Read(string runtimeHome) => Look(runtimeHome).Server;

    /// <summary>
    /// What the marker says. The server writes it by renaming a new file over the last one, so a refused open or a missing name is
    /// looked at again (<see cref="ReplacedFile"/>) before the marker is called absent. <c>Busy</c> is a marker that stayed unreadable:
    /// it says nothing, and is not the same as a build with no marker.
    /// </summary>
    internal static (bool Busy, RunningServer? Server) Look(string runtimeHome)
    {
        var reading = ReplacedFile.ReadText(Path.Combine(runtimeHome, FileName));
        switch (reading.State)
        {
            case ReplacedFileState.Absent:
                return (false, null);
            case ReplacedFileState.Busy:
                return (true, null);
        }

        try
        {
            var wire = JsonSerializer.Deserialize<Wire>(reading.Text, Json);
            return (false, wire is { Pid: { } pid, StartedAt: { } startedAt } ? new RunningServer(pid, startedAt.UtcDateTime) : null);
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    /// <summary>
    /// The server named by the marker, if that process is still running (a server left behind by a tray that was killed, say),
    /// or null: no marker, a process that has gone, or one that only reuses its id.
    /// </summary>
    internal static RunningServer? FindLiveServer(string runtimeHome)
    {
        if (Read(runtimeHome) is not { } marked)
        {
            return null;
        }
        try
        {
            using var process = Process.GetProcessById(marked.ProcessId);
            var live = new RunningServer(process.Id, process.StartTime.ToUniversalTime());
            return !process.HasExited && live.IsSameProcessAs(marked) ? live : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private sealed record Wire(int? Pid, DateTimeOffset? StartedAt);
}

/// <summary>
/// Asks the running server for a copy of Weir's data through two files in the runtime home, as Pause does
/// (<c>PauseRequestFile</c>): <c>update-backup-request.json</c> is the tray's request and <c>update-backup-result.json</c> the
/// server's answer, first <c>started</c> (it heard, and is saving), then <c>saved</c> or <c>failed</c> with a plain reason. Files,
/// not a route, because the tray has no signed-in session, nothing is added to the server's public surface, and only a process
/// that can write the runtime home (readable by the account running Weir alone) can ask. It asks only a server that has said it can
/// answer (<see cref="UpdateBackupReady"/>). A server too busy to hear is asked again on a doubling schedule
/// (<see cref="HearingWindow"/>) up to <see cref="HearingCeiling"/>; one that has said so and then does not answer by then has failed.
/// </summary>
/// <param name="runtimeHome">The data folder.</param>
/// <param name="clock">Times the wait.</param>
/// <param name="server">The server process to ask, or null when none runs.</param>
/// <param name="onBusy">Told, in plain words, when the server has not heard by the end of the first wait.</param>
sealed class UpdateBackupRequest(string runtimeHome, TimeProvider clock, Func<RunningServer?> server, Action<string>? onBusy = null)
{
    internal const string RequestFileName = "update-backup-request.json";
    internal const string ResultFileName = "update-backup-result.json";

    private const string StartedState = "started";
    private const string SavedState = "saved";
    private const string FailedState = "failed";

    /// <summary>
    /// How long a server that has said it can answer has to say it heard the request the first time it is asked. An idle server
    /// answers within a second. A machine at full load stalls Weir's server for up to 17 s at a time (measured on a busy Windows
    /// VM, where the first request went unanswered for about two minutes), so each later ask waits twice as long as the one before.
    /// </summary>
    internal static readonly TimeSpan HearingTime = TimeSpan.FromSeconds(15);

    /// <summary>How many times the request is made before a server that has not said it heard is given up on.</summary>
    internal const int Asks = 4;

    /// <summary>The longest a server that has said it can answer is given to say it heard: all the asks, 15 s then 30 s, 60 s and 120 s.</summary>
    internal static readonly TimeSpan HearingCeiling = HearingTime * ((1 << Asks) - 1);

    /// <summary>The longest a copy may take once the server has said it is saving.</summary>
    internal static readonly TimeSpan LongestSave = TimeSpan.FromMinutes(30);

    /// <summary>How often the answer is read.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>How long the server has to say it heard the request made for the <paramref name="ask"/>th time (from 0): <see cref="HearingTime"/>, doubled each time.</summary>
    internal static TimeSpan HearingWindow(int ask) => HearingTime * (1 << ask);

    internal async Task<UpdateBackupAnswer> AskAsync(string targetVersion, CancellationToken cancellationToken)
    {
        if (server() is not { } running)
        {
            return new UpdateBackupAnswer(UpdateBackupOutcome.NotAnswered, null);
        }

        // A marker that cannot be read is not a server without one: that would let the update go ahead with no copy of Weir's data.
        var (markerBusy, marked) = UpdateBackupReady.Look(runtimeHome);
        if (markerBusy)
        {
            return Failed("Weir's record of whether it can save a copy of its data could not be read just now. Try again in a moment.");
        }
        if (marked is not { } ready || !ready.IsSameProcessAs(running))
        {
            return new UpdateBackupAnswer(UpdateBackupOutcome.NotAnswered, null);
        }

        var id = Guid.NewGuid().ToString("n");
        var asked = clock.GetTimestamp();
        Remove(ResultFileName);
        WriteRequest(id, targetVersion);

        var heard = false;
        var ask = 0;
        var hearBy = HearingWindow(0);
        while (true)
        {
            var result = Read(id);
            switch (result?.State)
            {
                case SavedState:
                    return new UpdateBackupAnswer(UpdateBackupOutcome.Saved, null);
                case FailedState:
                    return new UpdateBackupAnswer(UpdateBackupOutcome.Failed, result.Reason);
                case StartedState:
                    heard = true;
                    break;
                default:
                    break;
            }

            var waited = clock.GetElapsedTime(asked);
            if (server() is not { } now || !now.IsSameProcessAs(running))
            {
                return Failed("Weir stopped while it was saving the copy. Start Weir again, then try again.");
            }
            if (!heard && waited >= hearBy)
            {
                if (ask == Asks - 1)
                {
                    // Taken back, so a server that wakes up later does not save a copy for an update the tray did not go ahead with.
                    Remove(RequestFileName);
                    TrayLog.Write($"Weir did not answer the request to save a copy of its data in {HearingCeiling.TotalSeconds:0} seconds, so the update is not applied.");
                    return Failed("Weir was too busy to answer the request to save a copy of its data. Try again when the computer is less busy; if it keeps happening, restart Weir.");
                }

                ask++;
                hearBy += HearingWindow(ask);
                TrayLog.Write($"Weir has not answered the request to save a copy of its data yet; the computer may be busy. Asking again (request {ask + 1} of {Asks}).");
                if (ask == 1)
                {
                    onBusy?.Invoke("Weir is busy, so saving a copy of its data before the update is taking longer than usual. The update goes ahead once the copy is saved.");
                }

                // A new request over the old one, so a server that missed the first is told again, and one that reads it late never
                // finds it older than the two minutes it answers within.
                WriteRequest(id, targetVersion);
            }
            if (heard && waited >= LongestSave)
            {
                return Failed("Saving the copy is taking too long. Check that the disk Weir keeps its data on is working, then try again.");
            }
            await Task.Delay(PollInterval, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private void WriteRequest(string id, string targetVersion) =>
        AtomicFile.WriteAllText(
            runtimeHome,
            RequestFileName,
            JsonSerializer.Serialize(new { id, requested_at = clock.GetUtcNow(), target_version = targetVersion }, Json));

    private static UpdateBackupAnswer Failed(string reason) => new(UpdateBackupOutcome.Failed, reason);

    private sealed record Wire(string? Id, string? State, string? Reason);

    private Wire? Read(string id)
    {
        try
        {
            using var stream = new FileStream(
                Path.Combine(runtimeHome, ResultFileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var wire = JsonSerializer.Deserialize<Wire>(stream, Json);
            return wire?.Id == id ? wire : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Being replaced, or not finished: read again on the next look.
            return null;
        }
    }

    private void Remove(string fileName)
    {
        try
        {
            File.Delete(Path.Combine(runtimeHome, fileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TrayLog.Write($"Could not remove {fileName}: {ex.Message}");
        }
    }
}

/// <summary>
/// The hook the apply paths call: asks the running server for the copy, and when it cannot be saved says so (the log, a message
/// for the person, and a record that keeps the next start from applying the update by itself). It decides nothing else, and
/// nothing it does can throw, so a path that stops the server afterwards always reaches that step.
/// </summary>
/// <param name="ask">Asks the running server (<see cref="UpdateBackupRequest.AskAsync"/>).</param>
/// <param name="runtimeHome">Where the record of a failed copy is kept.</param>
/// <param name="serverIsRunning">Whether there is a server to ask. With none, the server that starts after the update saves the copy.</param>
/// <param name="onNotUpdated">Told why the update will not be applied, so the person is told.</param>
sealed class UpdateBackupHook(
    Func<string, CancellationToken, Task<UpdateBackupAnswer>> ask,
    string runtimeHome,
    Func<bool> serverIsRunning,
    Action<string> onNotUpdated) : IUpdateBackup
{
    private const string FailedMarkerFileName = "update-backup-failed";

    /// <summary>
    /// <c>update-not-applied.json</c> in the runtime home: <c>{ "version": …, "reason": … }</c> while an update is held back for
    /// want of a copy. The server reads it into System › About (<c>UpdateFiles.NotAppliedFileName</c>); a file of its own so it
    /// does not depend on how update-state.json is written.
    /// </summary>
    private const string NotAppliedFileName = "update-not-applied.json";

    private static readonly JsonSerializerOptions NotAppliedJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<bool> SaveBeforeApplyAsync(string? targetVersion)
    {
        try
        {
            return await SaveAsync(targetVersion).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Whatever goes wrong here is a copy that was not saved; the caller must still be able to stop the server.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            TrayLog.Write($"Could not make sure a copy of Weir's data was saved first, so the update is not applied:\n{ex}");
            return false;
        }
    }

    private async Task<bool> SaveAsync(string? targetVersion)
    {
        if (!serverIsRunning())
        {
            TrayLog.Write("No server is running to save a copy of Weir's data first; the server that starts after the update will.");
            return true;
        }

        var version = string.IsNullOrWhiteSpace(targetVersion) ? "unknown" : targetVersion;
        TrayLog.Write($"Asking the server to save a copy of Weir's data before updating to v{version}.");
        UpdateBackupAnswer answer;
        try
        {
            answer = await ask(version, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A request that could not be made is a copy that was not saved.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            TrayLog.Write($"Could not ask the server to save a copy of Weir's data:\n{ex}");
            answer = new UpdateBackupAnswer(
                UpdateBackupOutcome.Failed,
                "Weir couldn't ask itself to save the copy. Check that the account Weir runs as can write to its data folder, then try again.");
        }

        switch (answer.Outcome)
        {
            case UpdateBackupOutcome.Saved:
                TrayLog.Write("The server saved a copy of Weir's data.");
                Forget();
                return true;
            case UpdateBackupOutcome.NotAnswered:
                TrayLog.Write("No server that can save a copy of Weir's data is running (an older build); the server that starts after the update will save it.");
                return true;
            default:
                var reason = answer.Reason ?? "Something unexpected went wrong. The details are in Weir's log files.";
                TrayLog.Write($"The update to v{version} was not applied: the server could not save a copy of Weir's data. {reason}");
                Remember(version, reason);
                onNotUpdated(reason);
                return false;
        }
    }

    /// <summary>
    /// Whether an earlier run could not save a copy before applying <paramref name="version"/>. The start-up install, which has no
    /// running server to ask, leaves such an update alone: the server it started would only fail to save the copy again.
    /// </summary>
    internal static bool FailedFor(string runtimeHome, string version)
    {
        var reading = ReplacedFile.ReadText(Path.Combine(runtimeHome, FailedMarkerFileName));
        return reading.State switch
        {
            ReplacedFileState.Read => reading.Text.Trim() == version,

            // A marker that cannot be read just now is not a marker that is not there: this decides whether an install goes ahead
            // with no server to save the copy, so unknown leaves the update waiting, and the next start looks again.
            ReplacedFileState.Busy => true,
            _ => false,
        };
    }

    /// <summary>
    /// Why an update was held back and not applied since, or null. It stays until a copy is saved, so a tray that starts again
    /// (a quit that could not apply the update) still says so. A reason for the version that is now running is no longer the case,
    /// and is removed.
    /// </summary>
    internal static string? PendingReason(string runtimeHome, string runningVersion)
    {
        // A record that cannot be read just now says nothing: it is neither shown as missing nor removed.
        var reading = ReplacedFile.ReadText(Path.Combine(runtimeHome, NotAppliedFileName));
        if (reading.State != ReplacedFileState.Read)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(reading.Text);
            var root = document.RootElement;
            var reason = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
            var version = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(reason))
            {
                return null;
            }
            if (AppVersion.Without(version) == runningVersion)
            {
                Delete(runtimeHome, NotAppliedFileName);
                Delete(runtimeHome, FailedMarkerFileName);
                return null;
            }
            return reason;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Remember(string version, string reason)
    {
        try
        {
            AtomicFile.WriteAllText(runtimeHome, FailedMarkerFileName, version);
            AtomicFile.WriteAllText(
                runtimeHome, NotAppliedFileName, JsonSerializer.Serialize(new { version, reason }, NotAppliedJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TrayLog.Write($"Could not record that the copy for v{version} failed: {ex.Message}");
        }
    }

    private void Forget()
    {
        Delete(runtimeHome, FailedMarkerFileName);
        Delete(runtimeHome, NotAppliedFileName);
    }

    private static void Delete(string runtimeHome, string fileName)
    {
        try
        {
            File.Delete(Path.Combine(runtimeHome, fileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TrayLog.Write($"Could not remove {fileName}: {ex.Message}");
        }
    }
}
