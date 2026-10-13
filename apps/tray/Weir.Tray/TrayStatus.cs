using System.Text.Json;

namespace Weir.Tray;

/// <summary>What the server says about itself for the tray: whether processing is paused, and what it relies on that does not answer.</summary>
/// <param name="Paused">Processing is paused right now.</param>
/// <param name="PausedUntil">When a timed pause ends, or null.</param>
/// <param name="ManagersUnreachable">The media managers (Deluno, Sonarr, Radarr) that do not answer, by name.</param>
/// <param name="FoldersUnreachable">The watched, work and output folders Weir cannot reach, each in the words the tray shows.</param>
/// <param name="ServerOk">False once the server has said it is stopping.</param>
sealed record TrayStatus(
    bool Paused,
    DateTimeOffset? PausedUntil,
    IReadOnlyList<string> ManagersUnreachable,
    IReadOnlyList<string> FoldersUnreachable,
    bool ServerOk)
{
    /// <summary>Whether the server is running but something it relies on does not answer.</summary>
    internal bool SomethingNotAnswering => ManagersUnreachable.Count > 0 || FoldersUnreachable.Count > 0;
}

/// <summary>One look at tray-status.json: what it held and when the server wrote it.</summary>
/// <param name="Status">What the file says, or null when there is no file or it holds nothing usable.</param>
/// <param name="WrittenAtUtc">When the file was last written.</param>
/// <param name="Failed">Whether the file could not be read this time.</param>
readonly record struct TrayStatusReading(TrayStatus? Status, DateTime? WrittenAtUtc, bool Failed)
{
    internal static TrayStatusReading Missing { get; } = new(null, null, false);

    internal static TrayStatusReading Unreadable { get; } = new(null, null, true);
}

/// <summary>
/// tray-status.json in the runtime home, written by the server (atomically, whenever its content changes) for the tray
/// to show: the paused state and what does not answer. A file, like work-state.json, because the tray has no signed-in
/// session and the server should have no route that answers without one.
/// </summary>
static class TrayStatusFile
{
    internal const string FileName = "tray-status.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>
    /// What the file holds now: <see cref="TrayStatusReading.Missing"/> when there is none, or
    /// <see cref="TrayStatusReading.Unreadable"/> when it cannot be read this time (the server is replacing it, or it is
    /// damaged), which says nothing about the status and is never a reason to change what is shown. The file is opened so
    /// that the server can still rename a new one over it while it is read.
    /// </summary>
    internal static TrayStatusReading Read(string runtimeHome, Action<string> log)
    {
        // A name that the server is renaming a new file over can be missing for an instant: a file that is not there is looked for once
        // more after a moment before it is called missing, because missing changes what is shown.
        var reading = ReadOnce(runtimeHome, log);
        if (reading != TrayStatusReading.Missing)
        {
            return reading;
        }

        Thread.Sleep(LookAgainAfter);
        return ReadOnce(runtimeHome, log);
    }

    private static readonly TimeSpan LookAgainAfter = TimeSpan.FromMilliseconds(20);

    private static TrayStatusReading ReadOnce(string runtimeHome, Action<string> log)
    {
        var path = Path.Combine(runtimeHome, FileName);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var wrote = File.GetLastWriteTimeUtc(path);
            return new TrayStatusReading(Parse(JsonSerializer.Deserialize<Wire>(stream, Json)), wrote, Failed: false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return TrayStatusReading.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log($"{FileName} could not be read ({ex.Message}).");
            return TrayStatusReading.Unreadable;
        }
    }

    private static TrayStatus? Parse(Wire? wire) =>
        wire is null
            ? null
            : new TrayStatus(
                wire.Paused ?? false,
                wire.PausedUntil,
                Names(wire.Unreachable?.Managers),
                Names(wire.Unreachable?.Folders),
                wire.ServerOk ?? true);

    private static List<string> Names(List<string>? names) =>
        [.. (names ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim())];

    private sealed record Wire(bool? Paused, DateTimeOffset? PausedUntil, WireUnreachable? Unreachable, bool? ServerOk);

    private sealed record WireUnreachable(List<string>? Managers, List<string>? Folders);
}
