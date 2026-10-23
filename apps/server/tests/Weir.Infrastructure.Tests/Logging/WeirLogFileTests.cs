using System.Runtime.Versioning;
using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Logging;

namespace Weir.Infrastructure.Tests.Logging;

/// <summary>The log file on a real folder: what it does when the file is gone, unreadable or cannot be replaced.</summary>
public sealed class WeirLogFileTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    private static string Line(DateTimeOffset at, string message) =>
        LogLineFormat.JsonLine(at, Microsoft.Extensions.Logging.LogLevel.Information, "Test", message, null, null, null);

    private static string[] LinesOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private WeirLogFile Open(out string path)
    {
        path = _folder.Join("weir.log");
        return new WeirLogFile(path, TimeProvider.System);
    }

    [Fact]
    public void A_line_written_after_the_log_is_disposed_is_dropped()
    {
        var file = Open(out var path);
        file.WriteLine("before");
        file.Dispose();

        file.WriteLine("after");

        Assert.Equal(["before"], LinesOf(path));
    }

    [Fact]
    public void A_log_that_was_deleted_is_started_again_by_the_next_prune()
    {
        using var file = Open(out var path);
        file.WriteLine(Line(DateTimeOffset.UtcNow.AddDays(-10), "old"));
        File.Delete(path);

        Assert.True(file.Prune(keepDays: 3));
        file.WriteLine("fresh");

        Assert.Equal(["fresh"], LinesOf(path));
    }

    [Fact]
    public void A_deleted_log_reads_as_failed_and_snapshots_as_an_empty_file()
    {
        using var file = Open(out var path);
        file.WriteLine("line");
        File.Delete(path);
        var snapshot = _folder.Join("snapshot.log");

        Assert.False(file.ReadLines(_ => { }));
        Assert.True(file.SnapshotTo(snapshot));
        Assert.Equal(0, new FileInfo(snapshot).Length);
    }

    [Fact]
    public void A_log_nobody_may_read_is_not_pruned_read_or_snapshotted_and_keeps_its_lines()
    {
        using var file = Open(out var path);
        file.WriteLine(Line(DateTimeOffset.UtcNow.AddDays(-10), "old"));
        using (var unreadable = UnreadablePath.Create(path))
        {
            if (unreadable is null)
            {
                return; // running as a privileged user: nothing can be refused
            }

            Assert.False(file.Prune(keepDays: 3));
            Assert.False(file.ReadLines(_ => { }));
            Assert.False(file.SnapshotTo(_folder.Join("snapshot.log")));
            file.WriteLine("still logging");
        }

        Assert.Equal(2, LinesOf(path).Length);
    }

    [Fact]
    public void A_snapshot_that_cannot_be_written_fails_and_leaves_nothing_behind()
    {
        using var file = Open(out _);
        file.WriteLine("line");
        var destination = _folder.Join("missing-folder", "snapshot.log");

        Assert.False(file.SnapshotTo(destination));
        Assert.False(File.Exists(destination));
    }

    [WindowsFact("A file held without delete sharing cannot be replaced only on Windows")]
    public void A_prune_that_cannot_swap_the_cleaned_log_in_fails_and_the_log_goes_on()
    {
        using var file = Open(out var path);
        file.WriteLine(Line(DateTimeOffset.UtcNow.AddDays(-10), "old"));
        file.WriteLine(Line(DateTimeOffset.UtcNow, "recent"));
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.False(file.Prune(keepDays: 3));
        }

        file.WriteLine("still logging");

        Assert.Equal(3, LinesOf(path).Length);
        Assert.Empty(Directory.GetFiles(_folder.Path, "*.prune"));
    }

    [PosixFact("A folder that refuses new files is a POSIX permission")]
    public void A_prune_that_cannot_make_its_scratch_file_fails_and_the_log_goes_on()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var file = Open(out var path);
        file.WriteLine(Line(DateTimeOffset.UtcNow.AddDays(-10), "old"));
        File.SetUnixFileMode(_folder.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (CanCreateIn(_folder.Path))
            {
                return; // running as root: the folder's permissions do not hold it back
            }

            Assert.False(file.Prune(keepDays: 3));
        }
        finally
        {
            File.SetUnixFileMode(_folder.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        file.WriteLine("still logging");
        Assert.Equal(2, LinesOf(path).Length);
    }

    private static bool CanCreateIn(string folder)
    {
        var probe = Path.Join(folder, ".probe");
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void A_prune_that_cannot_swap_the_cleaned_log_in_and_cannot_remove_its_scratch_file_still_fails_and_the_log_goes_on()
    {
        var files = new FaultingFileSystem()
            .Fail(FileCall.MoveFile, FaultingFileSystem.SharingViolation)
            .Fail(FileCall.DeleteFile, FaultingFileSystem.Refused);
        using var file = new WeirLogFile(_folder.Join("weir.log"), TimeProvider.System, files);
        file.WriteLine(Line(DateTimeOffset.UtcNow.AddDays(-10), "old"));
        file.WriteLine(Line(DateTimeOffset.UtcNow, "recent"));

        Assert.False(file.Prune(keepDays: 3));
        file.WriteLine("still logging");

        Assert.Equal(3, LinesOf(file.Path).Length);
        Assert.Single(Directory.GetFiles(_folder.Path, "*.prune"));
    }

    [Fact]
    public void A_snapshot_that_cannot_be_written_and_whose_leftover_cannot_be_removed_still_fails()
    {
        var files = new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.SharingViolation);
        using var file = new WeirLogFile(_folder.Join("weir.log"), TimeProvider.System, files);
        file.WriteLine("line");

        Assert.False(file.SnapshotTo(_folder.Join("missing-folder", "snapshot.log")));
        Assert.Contains(files.Calls, call => call.Call == FileCall.DeleteFile);
    }

    [Fact]
    public void A_first_line_that_is_not_a_log_line_is_pruned_with_the_rest()
    {
        using var file = Open(out var path);
        file.WriteLine("not json");
        file.WriteLine(Line(DateTimeOffset.UtcNow, "recent"));

        Assert.True(file.Prune(keepDays: 3));

        Assert.Single(LinesOf(path));
    }

    [Fact]
    public async Task A_log_disposed_while_it_is_being_read_waits_for_the_read_to_finish()
    {
        var file = Open(out _);
        file.WriteLine("line");
        using var reading = new ManualResetEventSlim();
        using var finishRead = new ManualResetEventSlim();
        var read = Task.Run(() => file.ReadLines(_ =>
        {
            reading.Set();
            finishRead.Wait();
        }));
        reading.Wait();
        Exception? thrown = null;
        var disposing = new Thread(() =>
        {
            try
            {
                file.Dispose();
            }
            catch (Exception exception)
            {
                thrown = exception;
            }
        });
        disposing.Start();

        await Eventually.ThatAsync(() => !disposing.IsAlive || disposing.ThreadState.HasFlag(ThreadState.WaitSleepJoin));
        var waited = disposing.IsAlive;
        finishRead.Set();
        disposing.Join();

        Assert.True(waited);
        Assert.Null(thrown);
        Assert.True(await read);
    }

    [Fact]
    public void A_log_that_cannot_be_opened_again_after_a_prune_goes_on_reading_pruning_and_closing_without_a_handle()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var files = new FaultingFileSystem().Before(FileCall.MoveFile, scratch => File.SetAttributes(scratch, FileAttributes.ReadOnly));
        var path = _folder.Join("weir.log");
        var file = new WeirLogFile(path, clock, files);
        var recent = Line(clock.GetUtcNow(), "recent");
        file.WriteLine(Line(clock.GetUtcNow().AddDays(-10), "old"));
        file.WriteLine(recent);
        try
        {
            if (file.Prune(keepDays: 3))
            {
                return; // running as a privileged user: the read-only log was opened for writing anyway
            }

            file.WriteLine("dropped: there is no handle to write it with");
            var read = new List<string>();
            var snapshot = _folder.Join("snapshot.log");
            Assert.True(file.ReadLines(read.Add));
            Assert.True(file.SnapshotTo(snapshot));
            clock.Advance(TimeSpan.FromDays(10));
            var secondPrune = Record.Exception(() => file.Prune(keepDays: 3));

            Assert.Equal([recent], read);
            Assert.Equal([recent], LinesOf(snapshot));
            Assert.Null(secondPrune);
            Assert.Null(Record.Exception(file.Dispose));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            file.Dispose();
        }
    }

    [WindowsFact("Another handle can lock a byte range against readers only on Windows")]
    [SupportedOSPlatform("windows")]
    public void A_snapshot_that_fails_part_way_removes_the_partial_copy()
    {
        using var file = Open(out var path);
        file.WriteLine("line");
        var snapshot = _folder.Join("snapshot.log");
        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        holder.Lock(0, holder.Length);

        Assert.False(file.SnapshotTo(snapshot));
        Assert.False(File.Exists(snapshot));
    }

    [Fact]
    public void A_log_disposed_twice_is_disposed_once()
    {
        var file = Open(out _);

        file.Dispose();

        Assert.Null(Record.Exception(file.Dispose));
    }

    [Fact]
    public void A_log_path_with_no_folder_above_it_cannot_be_opened()
    {
        var root = Path.GetPathRoot(_folder.Path)!;

        Assert.ThrowsAny<Exception>(() => new WeirLogFile(root, TimeProvider.System));
    }

    [Fact]
    public void A_log_with_nothing_in_it_has_nothing_to_prune()
    {
        using var file = Open(out var path);

        Assert.True(file.Prune(keepDays: 3));

        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void A_bounded_read_stops_at_the_length_it_was_given_and_supports_nothing_else()
    {
        using var source = new MemoryStream("0123456789"u8.ToArray());
        using var bounded = new WeirLogFile.BoundedReadStream(source, 4);
        var buffer = new byte[10];

        Assert.Equal((true, false, false), (bounded.CanRead, bounded.CanSeek, bounded.CanWrite));
        Assert.Equal(3, bounded.Read(buffer, 0, 3));
        Assert.Equal(1, bounded.Read(buffer, 3, 7));
        Assert.Equal(0, bounded.Read(buffer, 4, 6));
        Assert.Equal("0123", System.Text.Encoding.ASCII.GetString(buffer, 0, 4));
        bounded.Flush();
        Assert.Throws<NotSupportedException>(() => bounded.Length);
        Assert.Throws<NotSupportedException>(() => bounded.Position);
        Assert.Throws<NotSupportedException>(() => bounded.Position = 1);
        Assert.Throws<NotSupportedException>(() => bounded.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => bounded.SetLength(1));
        Assert.Throws<NotSupportedException>(() => bounded.Write(buffer, 0, 1));
    }

    [Theory]
    [InlineData("{\"timestamp\":\"2026-10-20T10:00:00Z\"}", true, "2026-10-20T10:00:00+00:00")]
    [InlineData("{\"timestamp\":\"2026-10-20T10:00:00+02:00\"}", true, "2026-10-20T10:00:00+02:00")]
    [InlineData("{\"timestamp\":\"yesterday\"}", false, null)]
    [InlineData("{\"timestamp\":5}", false, null)]
    [InlineData("{\"message\":\"no timestamp\"}", false, null)]
    [InlineData("[\"timestamp\"]", false, null)]
    [InlineData("not json", false, null)]
    public void A_line_gives_its_time_only_when_it_is_JSON_with_a_readable_timestamp(string raw, bool readable, string? expected)
    {
        Assert.Equal(readable, WeirLogFile.TryReadTimestamp(raw, out var at));
        if (readable)
        {
            Assert.Equal(DateTimeOffset.Parse(expected!, System.Globalization.CultureInfo.InvariantCulture), at);
        }
    }
}
