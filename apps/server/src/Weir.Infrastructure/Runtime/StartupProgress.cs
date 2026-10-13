using System.Text;
using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Runtime;

/// <summary>Where a starting server is, in the order it passes through them.</summary>
public enum StartupPhase
{
    /// <summary>Configuration read; the log, the data folder and the services are being made.</summary>
    Starting,

    /// <summary>Opening the database and reading which schema revision it is at.</summary>
    OpeningDatabase,

    /// <summary>Saving a copy of the data before the schema is changed for an update.</summary>
    SavingCopy,

    /// <summary>Applying schema migrations; the step and the number of steps say how far.</summary>
    Migrating,

    /// <summary>The database is open at this build's schema; the server is noting the start, clearing old sessions and naming connections.</summary>
    Preparing,

    /// <summary>The listener and the background services are starting.</summary>
    StartingListener,

    /// <summary>Listening and answering. <c>/ready</c> answers 200 from here.</summary>
    Ready,
}

/// <summary>
/// What a starting server tells the Windows tray about how far it has got. The same record goes out two ways, in this order:
/// <list type="number">
/// <item>as a line on the server's standard output, <c>weir-start-progress {record}</c>, when the tray asked for it
/// (<see cref="PipeVariable"/>). The tray owns that pipe, so a line is progress the moment it is written and cannot be late, busy
/// or refused the way a file can; it is the channel the tray judges a start by.</item>
/// <item>as <c>startup-progress.json</c> in Weir's data folder, replaced (whole, then renamed into place), for readers outside the
/// tray (the scenario suite, support). A write that is refused is tried again, a few times, because Windows refuses a rename for
/// a moment while a reader or a virus scanner has the file; and a write that still fails is tried again with the next beat.</item>
/// </list>
/// A record is written at every phase and, while SQLite is working inside one, at most once a second. The phases that do no database
/// work (<see cref="StartupPhase.Starting"/>, <see cref="StartupPhase.Preparing"/>, <see cref="StartupPhase.StartingListener"/>) write
/// one record each, and the tray gives them its ordinary window:
/// <code>
/// { "pid": 5764, "phase": "migrating", "step": 3, "steps": 7, "expect_within_s": 468, "at": "2026-10-13T06:22:58.1234567Z" }
/// </code>
/// <c>pid</c> is the server's process id, so the file of an earlier server is never taken for this one's. <c>at</c> is when the
/// record was made: a phase change or a <see cref="Beat"/>. <c>step</c> and <c>steps</c> are 0 outside
/// <see cref="StartupPhase.Migrating"/>. <c>expect_within_s</c> is how long the server says it may be silent from this write
/// (0: no longer than the tray's own limit). It is asked for only once SQLite has been seen working in the phase (a beat), so a phase that
/// has not done any work gets the ordinary window.
/// </summary>
/// <remarks>
/// <para>
/// A beat is not a timer. It comes from the work itself (SQLite calls <see cref="SqliteWorkProgress"/> while a statement runs),
/// so a start that is blocked, on a lock or on a file, stops beating, which a timer on another thread would hide.
/// </para>
/// <para>
/// Work on a large database has steps SQLite does in one go, such as writing and syncing a copy of a gigabyte, in which nothing
/// can be reported. <c>expect_within_s</c> is the idea of <c>sd_notify EXTEND_TIMEOUT_USEC</c>: the service says how long it needs
/// and the supervisor believes it up to a ceiling of its own (<see cref="StartupPatience"/>). It applies to the silence that follows a
/// beat, which is such a step. A start that has not beaten, or that is waiting for a lock (SQLite gives up on a lock after 30 s with an
/// error that ends the start), is held to the ordinary window.
/// </para>
/// <para>Neither channel can stop the start: a line or a file that cannot be written is skipped, and the start goes on.</para>
/// </remarks>
public sealed class StartupProgress
{
    public const string FileName = "startup-progress.json";

    /// <summary>The environment variable with which the tray asks for the progress lines on standard output.</summary>
    public const string PipeVariable = "WEIR_PROGRESS_STDOUT";

    /// <summary>What a progress line on standard output starts with; the rest of the line is the record.</summary>
    public const string PipePrefix = "weir-start-progress ";

    /// <summary>The least time between two records of the same phase.</summary>
    public static readonly TimeSpan BeatEvery = TimeSpan.FromSeconds(1);

    /// <summary>How many times a refused write of the file is tried, and how far apart (the numbers the tray status file uses too).</summary>
    internal const int FileAttempts = 5;

    internal static readonly TimeSpan FileRetryAfter = TimeSpan.FromMilliseconds(100);

    private readonly string _home;
    private readonly int _processId;
    private readonly TimeProvider _time;
    private readonly Action<string>? _pipe;
    private readonly Lock _gate = new();
    private StartupPhase _phase = StartupPhase.Starting;
    private int _step;
    private int _steps;
    private TimeSpan _expectWithin;
    private TimeSpan _allowance;
    private bool _worked;
    private long _lastAt;
    private bool _any;

    /// <param name="home">Weir's data folder, where the file is.</param>
    /// <param name="processId">This server's process id.</param>
    /// <param name="time">The clock a beat is spaced on.</param>
    /// <param name="pipe">Takes each progress line for standard output when the tray asked for them (it must not wait); null when it did not.</param>
    public StartupProgress(string home, int processId, TimeProvider time, Action<string>? pipe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        _home = home;
        _processId = processId;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _pipe = pipe;
    }

    /// <summary>Writes the file. A test replaces it.</summary>
    internal Action<string, string, byte[]> Replace { get; set; } = AtomicFileWriter.Replace;

    /// <summary>Waits between two tries of a refused write. A test replaces it.</summary>
    internal Action<TimeSpan> Pause { get; set; } = Thread.Sleep;

    /// <summary>
    /// The phase, and the step of <paramref name="steps"/> when it has them. Always written. <paramref name="expectWithin"/> is the
    /// longest the server may be silent after SQLite has been seen working in this phase, when that is longer than the tray's own limit;
    /// the record says so only once that has happened, in this phase (a later step of it carries it on), and not on entering a phase.
    /// </summary>
    public void Enter(StartupPhase phase, int step = 0, int steps = 0, TimeSpan expectWithin = default)
    {
        lock (_gate)
        {
            if (phase != _phase)
            {
                _worked = false;
            }

            _phase = phase;
            _step = step;
            _steps = steps;
            _allowance = expectWithin;
            _expectWithin = _worked ? _allowance : TimeSpan.Zero;
            Write();
        }
    }

    /// <summary>
    /// SQLite is working in the current phase. The first beat of a phase is written at once; later ones at most once every
    /// <see cref="BeatEvery"/>.
    /// </summary>
    public void Beat()
    {
        lock (_gate)
        {
            if (!_any || !_worked || _time.GetElapsedTime(_lastAt) >= BeatEvery)
            {
                _worked = true;
                _expectWithin = _allowance;
                Write();
            }
        }
    }

    internal static string WireName(StartupPhase phase) => phase switch
    {
        StartupPhase.Starting => "starting",
        StartupPhase.OpeningDatabase => "opening_database",
        StartupPhase.SavingCopy => "saving_copy",
        StartupPhase.Migrating => "migrating",
        StartupPhase.Preparing => "preparing",
        StartupPhase.StartingListener => "starting_listener",
        StartupPhase.Ready => "ready",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    private void Write()
    {
        var now = _time.GetUtcNow();
        var text = WireJsonWriter.Dumps(
            new WireObject()
                .Set("pid", _processId)
                .Set("phase", WireName(_phase))
                .Set("step", _step)
                .Set("steps", _steps)
                .Set("expect_within_s", (long)_expectWithin.TotalSeconds)
                .Set("at", Timestamp.FromUtc(now.UtcDateTime).ToWireText()),
            WireJsonFormat.Compact);
        _lastAt = _time.GetTimestamp();
        _any = true;

        // The pipe first: it is what the tray judges the start by, so nothing that goes wrong with the file can hold it back.
        WriteToPipe(text);
        WriteFile(text);
    }

    private void WriteToPipe(string record)
    {
        if (_pipe is null)
        {
            return;
        }

        _pipe(PipePrefix + record);
    }

    private void WriteFile(string record)
    {
        var bytes = Encoding.UTF8.GetBytes(record);
        for (var attempt = 1; attempt <= FileAttempts; attempt++)
        {
            try
            {
                Directory.CreateDirectory(_home);
                Replace(_home, FileName, bytes);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Windows refuses a rename over a file that is open or being scanned, for a moment, as access denied or a sharing
                // violation. Try again; when it keeps refusing the next beat tries again, and the pipe has already said it.
                if (attempt < FileAttempts)
                {
                    Pause(FileRetryAfter);
                }
            }
        }
    }
}
