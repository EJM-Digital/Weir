using Weir.Core.Processing;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// A source that another program holds, refuses or removes while Weir looks at it, and an output folder that refuses the
/// write: what Weir tells the operator, and that the source is never touched.
/// </summary>
public sealed class SourceAccessFaultTests : IDisposable
{
    private const string WriterSentence = "This file is still open for writing by another program.";

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(string relative, string content = "film")
    {
        var path = _dir.Join(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static ProcessingLibraryRecord Library() => new() { Name = "Movies" };

    [Fact]
    public void A_source_another_program_is_writing_cannot_be_reserved_and_Weir_says_it_will_wait()
    {
        var film = Write("watch/Film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.OpenFile, FaultingFileSystem.SharingViolation);

        var (guard, problem) = SourceFiles.AcquireReadGuard(film, files);

        Assert.Null(guard);
        Assert.Contains(WriterSentence, problem, StringComparison.Ordinal);
        Assert.Contains("will wait", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_the_system_refuses_to_open_cannot_be_reserved_and_the_system_reason_is_shown()
    {
        var film = Write("watch/Film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.OpenFile, FaultingFileSystem.Refused);

        var (guard, problem) = SourceFiles.AcquireReadGuard(film, files);

        Assert.Null(guard);
        Assert.StartsWith("Weir could not reserve this file for safe read-only processing, so it will wait.", problem, StringComparison.Ordinal);
        Assert.Contains("is denied", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_that_fails_to_open_for_another_reason_is_waited_on_and_not_called_a_writer()
    {
        var film = Write("watch/Film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.OpenFile, _ => new IOException("The disk is not ready."));

        var problem = SourceFiles.SourceWriterProblem(film, files);

        Assert.NotNull(problem);
        Assert.DoesNotContain(WriterSentence, problem, StringComparison.Ordinal);
        Assert.Contains("The disk is not ready", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_nobody_holds_is_reserved_and_released_again()
    {
        var film = Write("watch/Film.mkv");

        var (guard, problem) = SourceFiles.AcquireReadGuard(film);

        Assert.Null(problem);
        Assert.NotNull(guard);
        guard.Dispose();
        guard.Dispose();
        File.Delete(film);
        Assert.False(File.Exists(film));
    }

    [Fact]
    public void A_source_that_cannot_be_read_stops_the_check_before_anything_is_written_to_the_output_folder()
    {
        var film = Write("watch/Film.mkv");
        var output = _dir.Join("out");
        var files = new FaultingFileSystem().Fail(FileCall.OpenFile, FaultingFileSystem.SharingViolation, pathEndsWith: "Film.mkv");

        var problem = SourceFiles.CheckFileAccess(Library(), film, output, files);

        Assert.StartsWith("Weir could not open this file for reading", problem, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
        Assert.True(File.Exists(film));
    }

    [Fact]
    public void A_writer_that_starts_between_the_read_and_the_reservation_makes_the_source_wait()
    {
        var film = Write("watch/Film.mkv");
        var output = _dir.Join("out");
        var files = new FaultingFileSystem().Fail(FileCall.OpenFile, FaultingFileSystem.SharingViolation, onCall: 2, pathEndsWith: "Film.mkv");

        var problem = SourceFiles.CheckFileAccess(Library(), film, output, files);

        Assert.Contains(WriterSentence, problem, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void An_output_folder_that_cannot_be_made_is_named_and_the_source_is_left_alone() => AssertOutputFolderRefused(FileCall.CreateDirectory);

    [Fact]
    public void An_output_folder_that_refuses_the_write_test_is_named_and_the_source_is_left_alone() => AssertOutputFolderRefused(FileCall.WriteAllBytes);

    private void AssertOutputFolderRefused(FileCall refusing)
    {
        var film = Write("watch/Film.mkv");
        var output = _dir.Join("out");
        var files = new FaultingFileSystem().Fail(refusing, FaultingFileSystem.Refused);

        var problem = SourceFiles.CheckFileAccess(Library(), film, output, files);

        Assert.StartsWith($"Weir cannot write to the output folder ({output}), so it did not start work that would have nowhere to go.", problem, StringComparison.Ordinal);
        Assert.Contains("is denied", problem, StringComparison.Ordinal);
        Assert.Equal("film", File.ReadAllText(film));
        Assert.Contains(files.Calls, call => call.Call == FileCall.DeleteFile && Path.GetFileName(call.Path).StartsWith(".weir-write-test-", StringComparison.Ordinal));
    }

    [Fact]
    public void A_source_that_passes_every_check_leaves_no_test_file_in_the_output_folder()
    {
        var film = Write("watch/Film.mkv");
        var output = _dir.Join("out");

        Assert.Null(SourceFiles.CheckFileAccess(Library(), film, output));

        Assert.Empty(Directory.GetFileSystemEntries(output));
    }

    [WindowsFact("The device and file index come from a Windows handle.")]
    public void A_fingerprint_whose_handle_cannot_be_opened_still_has_the_size_and_time()
    {
        var film = Write("watch/Film.mkv", "twelve bytes");
        var files = new FaultingFileSystem().Fail(FileCall.OpenHandle, FaultingFileSystem.SharingViolation);

        var fingerprint = SourceFiles.Fingerprint(film, files);

        Assert.Equal(0UL, fingerprint.Device);
        Assert.Equal(0UL, fingerprint.Inode);
        Assert.Equal(12, fingerprint.SizeBytes);
        Assert.NotEqual(0, fingerprint.ModifiedTimeNs);
    }

    [Fact]
    public void A_fingerprint_tag_for_a_file_that_is_refused_is_the_fixed_unreadable_tag()
    {
        var film = Write("watch/Film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.GetLastWriteTimeUtc, FaultingFileSystem.Refused);

        Assert.Equal("unreadable", SourceFiles.DedupeFingerprintTag(film, files));
        Assert.NotEqual("unreadable", SourceFiles.DedupeFingerprintTag(film));
    }

    [Fact]
    public void A_rejected_file_whose_empty_folders_are_held_open_is_still_deleted()
    {
        var watched = _dir.Join("watch");
        var rejected = Write("watch/Film (2001)/Film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.DeleteDirectory, FaultingFileSystem.SharingViolation);

        var cleanup = RemuxPassPaths.CleanupRejectedFile(watched, rejected, "delete_file", files);

        Assert.True(cleanup.Deleted);
        Assert.False(File.Exists(rejected));
        Assert.True(Directory.Exists(Path.GetDirectoryName(rejected)));
        Assert.True(Directory.Exists(watched));
    }

    [Fact]
    public void A_rejected_file_that_is_refused_on_delete_stays_and_the_operator_is_told_it_is_locked()
    {
        var watched = _dir.Join("watch");
        var rejected = Write("watch/Film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.Refused);

        var cleanup = RemuxPassPaths.CleanupRejectedFile(watched, rejected, "delete_file", files);

        Assert.False(cleanup.Deleted);
        Assert.StartsWith("Weir could not delete the rejected file because it is locked or unavailable", cleanup.Detail, StringComparison.Ordinal);
        Assert.Equal("film", File.ReadAllText(rejected));
    }

    [Fact]
    public void A_rejected_file_that_is_a_folder_is_never_deleted()
    {
        var watched = _dir.Join("watch");
        var folder = _dir.Join("watch", "Film (2001)");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Join(folder, "Film.mkv"), "film");

        var cleanup = RemuxPassPaths.CleanupRejectedFile(watched, folder, "delete_file");

        Assert.False(cleanup.Deleted);
        Assert.Contains("not a regular file", cleanup.Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(folder, "Film.mkv")));
    }

    [Fact]
    public void A_rejected_file_that_is_not_where_the_row_says_is_reported_as_not_found()
    {
        var watched = _dir.Join("watch");
        Directory.CreateDirectory(watched);

        var cleanup = RemuxPassPaths.CleanupRejectedFile(watched, Path.Join(watched, "Gone.mkv"), "delete_file");

        Assert.False(cleanup.Deleted);
        Assert.EndsWith("could not be found.", cleanup.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_file_in_a_watched_folder_that_is_gone_is_reported_as_the_folder_missing()
    {
        var watched = _dir.Join("watch");

        var cleanup = RemuxPassPaths.CleanupRejectedFile(watched, Path.Join(watched, "Film.mkv"), "delete_file");

        Assert.False(cleanup.Deleted);
        Assert.Contains("the watched folder", cleanup.Detail, StringComparison.Ordinal);
    }
}
