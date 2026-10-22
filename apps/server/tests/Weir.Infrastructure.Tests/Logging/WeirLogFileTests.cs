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
