using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>
/// When the server is slow to get to work, it saves what every thread is doing, once per run and once more if the trouble comes back after
/// a cool-down, so a stall that happens on a machine nobody is watching can be named afterwards. Saving never costs the server more than a
/// warning in the log when it fails. The writer is a stand-in that makes a file of a size the test chooses.
/// </summary>
public sealed class StallDumpCaptureTests : IDisposable
{
    private readonly TempDirectory _logs = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 3, 31, 46, TimeSpan.Zero));
    private readonly CapturingLogger<ThreadPoolWatch> _log = new();
    private readonly SnapshotWriter _writer = new();

    public void Dispose() => _logs.Dispose();

    private string Folder => _logs.Join("diagnostics");

    private StallDumpCapture Capture(long mostBytes = StallDumpCapture.MostBytes) => new(_logs.Path, _writer, _time, _log, mostBytes);

    private static async Task TakenAsync(StallDumpCapture capture) => await capture.Last;

    [Fact]
    public async Task The_first_stall_saves_one_snapshot_in_the_diagnostics_folder_and_the_log_names_the_file()
    {
        var capture = Capture();

        capture.Request();
        await TakenAsync(capture);

        var file = Assert.Single(Directory.GetFiles(Folder));
        Assert.Equal($"stall-20261028T033146Z-pid{Environment.ProcessId}.dmp", Path.GetFileName(file));
        var (level, message) = Assert.Single(_log.Messages);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains(file, message, StringComparison.Ordinal);
        Assert.Contains("what each of its threads was doing", message, StringComparison.Ordinal);
        Assert.Contains("share it only with someone you trust", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_snapshot_is_taken_on_a_thread_of_its_own_because_the_pool_is_what_is_short()
    {
        var capture = Capture();

        capture.Request();
        await TakenAsync(capture);

        Assert.False(_writer.OnPoolThread);
    }

    [Fact]
    public async Task A_stall_inside_the_cool_down_saves_nothing_more()
    {
        var capture = Capture();
        capture.Request();
        await TakenAsync(capture);

        _time.Advance(StallDumpCapture.CoolDown - TimeSpan.FromSeconds(1));
        capture.Request();
        await TakenAsync(capture);

        Assert.Equal(1, _writer.Calls);
    }

    [Fact]
    public async Task A_stall_after_the_cool_down_saves_a_second_snapshot_and_a_third_is_never_saved()
    {
        var capture = Capture();
        capture.Request();
        await TakenAsync(capture);

        _time.Advance(StallDumpCapture.CoolDown);
        capture.Request();
        await TakenAsync(capture);
        _time.Advance(TimeSpan.FromDays(1));
        capture.Request();
        await TakenAsync(capture);

        Assert.Equal(StallDumpCapture.MostCaptures, _writer.Calls);
        Assert.Equal(2, Directory.GetFiles(Folder).Length);
    }

    [Fact]
    public async Task A_snapshot_that_could_not_be_written_is_a_warning_and_still_counts_as_taken()
    {
        _writer.Fails = true;
        var capture = Capture();

        capture.Request();
        await TakenAsync(capture);
        capture.Request();
        await TakenAsync(capture);

        var (level, message) = Assert.Single(_log.Messages);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("could not save what its threads were doing", message, StringComparison.Ordinal);
        Assert.Equal(1, _writer.Calls);
        Assert.Empty(Directory.GetFiles(Folder));
    }

    [Fact]
    public async Task A_snapshot_over_the_size_limit_is_deleted_and_said()
    {
        _writer.Bytes = 3 * 1024 * 1024;
        var capture = Capture(mostBytes: 2 * 1024 * 1024);

        capture.Request();
        await TakenAsync(capture);

        Assert.Empty(Directory.GetFiles(Folder));
        var (level, message) = Assert.Single(_log.Messages);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("snapshot was 3 MB, over the 2 MB it keeps", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_snapshot_exactly_at_the_size_limit_is_kept()
    {
        _writer.Bytes = 2 * 1024 * 1024;
        var capture = Capture(mostBytes: 2 * 1024 * 1024);

        capture.Request();
        await TakenAsync(capture);

        Assert.Single(Directory.GetFiles(Folder));
    }

    [Fact]
    public async Task Only_the_newest_snapshots_stay_on_disk()
    {
        Directory.CreateDirectory(Folder);
        for (var day = 1; day <= 5; day++)
        {
            var old = Path.Join(Folder, $"stall-2026010{day}T000000Z-pid1.dmp");
            await File.WriteAllBytesAsync(old, [1]);
            File.SetLastWriteTimeUtc(old, new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc));
        }

        var other = Path.Join(Folder, "notes.txt");
        await File.WriteAllTextAsync(other, "not a snapshot");
        var capture = Capture();

        capture.Request();
        await TakenAsync(capture);

        var kept = Directory.GetFiles(Folder).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(
            ["notes.txt", "stall-20260104T000000Z-pid1.dmp", "stall-20260105T000000Z-pid1.dmp", $"stall-20261028T033146Z-pid{Environment.ProcessId}.dmp"],
            kept);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_made_is_a_warning()
    {
        var blocked = _logs.Join("blocked");
        await File.WriteAllTextAsync(blocked, "a file where the logs folder should be");
        var capture = new StallDumpCapture(blocked, _writer, _time, _log);

        capture.Request();
        await TakenAsync(capture);

        Assert.Equal(0, _writer.Calls);
        Assert.Single(_log.Messages, message => message.Level == LogLevel.Warning);
    }

    [Fact]
    public void A_capture_needs_its_parts()
    {
        Assert.Throws<ArgumentException>(() => new StallDumpCapture(string.Empty, _writer, _time, _log));
        Assert.Throws<ArgumentNullException>(() => new StallDumpCapture(_logs.Path, null!, _time, _log));
        Assert.Throws<ArgumentNullException>(() => new StallDumpCapture(_logs.Path, _writer, null!, _log));
        Assert.Throws<ArgumentNullException>(() => new StallDumpCapture(_logs.Path, _writer, _time, null!));
    }

    [Fact]
    public void A_process_that_has_no_diagnostics_channel_cannot_be_written()
    {
        // No process has this id, so the channel to it does not exist: what a server started with diagnostics off looks like to its own writer.
        var writer = new DiagnosticsClientDumpWriter(int.MaxValue);

        Assert.ThrowsAny<Exception>(() => writer.Write(_logs.Join("never.dmp")));
        Assert.False(File.Exists(_logs.Join("never.dmp")));
    }

    /// <summary>
    /// The real thing, for a person to run (<c>WEIR_REAL_DUMP=1</c>): a triage dump of this test process through its own diagnostics channel
    /// exists, is not empty, and is far smaller than the full dump the same process would give.
    /// </summary>
    [RealDumpFact]
    public void The_real_writer_saves_a_triage_dump_of_its_own_process()
    {
        var path = _logs.Join("real.dmp");

        new DiagnosticsClientDumpWriter(Environment.ProcessId).Write(path);

        var size = new FileInfo(path).Length;
        Assert.True(size > 0, "The dump is empty.");
        Console.WriteLine($"Triage dump of {Process.GetCurrentProcess().ProcessName}: {size / (1024 * 1024)} MB");
    }

    private sealed class SnapshotWriter : IStackDumpWriter
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool Fails { get; set; }

        public long Bytes { get; set; } = 1024;

        public bool OnPoolThread { get; private set; } = true;

        public void Write(string path)
        {
            Interlocked.Increment(ref _calls);
            OnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            if (Fails)
            {
                throw new IOException("The diagnostics channel is not there.");
            }

            File.WriteAllBytes(path, new byte[Bytes]);
        }
    }

    public sealed class RealDumpFactAttribute : FactAttribute
    {
        public RealDumpFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("WEIR_REAL_DUMP") != "1")
            {
                Skip = "Set WEIR_REAL_DUMP=1 to write a real triage dump of the test process.";
            }
        }
    }
}
