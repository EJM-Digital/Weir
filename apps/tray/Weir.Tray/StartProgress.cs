using System.Globalization;
using System.Text.Json;

namespace Weir.Tray;

/// <summary>
/// How far a starting server says it has got: one reading of startup-progress.json
/// (<c>StartupProgress</c> in the server's <c>Weir.Infrastructure/Runtime</c> writes it; keep the two in step).
/// </summary>
/// <param name="ProcessId">The server process that wrote it. A file of another process is not this start's.</param>
/// <param name="Phase">The server's name for the phase: starting, opening_database, saving_copy, migrating, starting_listener, ready.</param>
/// <param name="Step">The migration it is on, from 1; 0 outside migrating.</param>
/// <param name="Steps">How many migrations there are; 0 outside migrating.</param>
/// <param name="ExpectWithin">How long the server says it may be silent from this write; zero when it asks for nothing beyond the tray's own limit.</param>
/// <param name="At">When the server wrote it, by its own clock.</param>
sealed record StartProgress(int ProcessId, string Phase, int Step, int Steps, TimeSpan ExpectWithin, DateTimeOffset At)
{
    /// <summary>The phase in the words the log uses.</summary>
    internal string Words => Phase switch
    {
        "starting" => "starting",
        "opening_database" => "opening the database",
        "saving_copy" => "saving a copy of the data",
        "migrating" when Steps > 0 => string.Create(CultureInfo.InvariantCulture, $"updating the database (step {Step} of {Steps})"),
        "migrating" => "updating the database",
        "starting_listener" => "starting to listen",
        "ready" => "ready",
        _ => Phase,
    };
}

/// <summary>startup-progress.json in the runtime home: read, never written, by the tray.</summary>
static class StartProgressFile
{
    internal const string FileName = "startup-progress.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>How many times a busy file is opened, and how far apart, before this look gives up on it.</summary>
    internal const int Attempts = 3;

    internal static readonly TimeSpan RetryAfter = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// What the file says now, or null when there is no file or it holds nothing usable. The server replaces the file whole (written
    /// aside, then renamed over it), so a reading is of one complete record or of none; a torn or partial file that gets here anyway
    /// is no reading. The file is opened so that the server can still rename a new one over it.
    /// </summary>
    /// <remarks>
    /// While the server renames a new file over this one, or a virus scanner has it, Windows can refuse the open as access denied
    /// (<see cref="UnauthorizedAccessException"/>, from a delete that is pending) or as a sharing violation (<see cref="IOException"/>).
    /// That is the file being busy, not missing and not damaged: it is opened again after a moment, and when it stays busy the look
    /// has no news (null), which the supervisor does not count as silence on its own: it counts the time since the record last
    /// changed, on either channel.
    /// </remarks>
    internal static StartProgress? Read(string runtimeHome) => Read(runtimeHome, Open, Thread.Sleep);

    internal static StartProgress? Read(string runtimeHome, Func<string, Stream> open, Action<TimeSpan> pause)
    {
        var path = Path.Combine(runtimeHome, FileName);
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                using var stream = open(path);
                return Parse(JsonSerializer.Deserialize<Wire>(stream, Json));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < Attempts)
                {
                    pause(RetryAfter);
                }
            }
        }

        return null;
    }

    /// <summary>The record on a progress line the server wrote to its standard output (after <see cref="Prefix"/>), or null if it is not one.</summary>
    internal static StartProgress? ParseLine(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Parse(JsonSerializer.Deserialize<Wire>(line.AsSpan(Prefix.Length), Json));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The newer of two readings of one server (the pipe's, the file's): the later <see cref="StartProgress.At"/>, the pipe's when
    /// they are equal. It depends on the two readings alone, so it changes only when one of them advances; alternating between two
    /// readings that are each unchanged would otherwise look like progress for ever.
    /// </summary>
    internal static StartProgress? Newer(StartProgress? fromPipe, StartProgress? fromFile) =>
        fromPipe is null ? fromFile : fromFile is null ? fromPipe : fromFile.At > fromPipe.At ? fromFile : fromPipe;

    /// <summary>What a progress line on standard output starts with (<c>StartupProgress.PipePrefix</c> in the server).</summary>
    internal const string Prefix = "weir-start-progress ";

    /// <summary>The environment variable that asks the server for the progress lines (<c>StartupProgress.PipeVariable</c> in the server).</summary>
    internal const string PipeVariable = "WEIR_PROGRESS_STDOUT";

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static StartProgress? Parse(Wire? wire) =>
        wire is { Pid: { } pid, Phase: { Length: > 0 } phase, At: { } at }
            ? new StartProgress(
                pid,
                phase,
                Math.Max(0, wire.Step ?? 0),
                Math.Max(0, wire.Steps ?? 0),
                TimeSpan.FromSeconds(Math.Max(0, wire.ExpectWithinS ?? 0)),
                at)
            : null;

    private sealed record Wire(int? Pid, string? Phase, int? Step, int? Steps, long? ExpectWithinS, DateTimeOffset? At);
}
