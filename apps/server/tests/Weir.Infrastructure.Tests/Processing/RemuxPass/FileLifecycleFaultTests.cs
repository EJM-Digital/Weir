using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// Publishing an output while the destination, the work file or the source is held, refused or gone: the result is a clear
/// error, and the source, the file already at the final name and the work folder hold exactly what they did before.
/// </summary>
public sealed class FileLifecycleFaultTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly string _work;
    private readonly string _out;

    public FileLifecycleFaultTests()
    {
        _work = _dir.Join("work");
        _out = _dir.Join("out");
        Directory.CreateDirectory(_work);
        Directory.CreateDirectory(_out);
    }

    public void Dispose() => _dir.Dispose();

    private string Staged(string content = "new output") => Put(Path.Join(_work, "Film.mkv"), content);

    private string Existing(string content = "old output") => Put(Path.Join(_out, "Film.mkv"), content);

    private static string Put(string path, string content)
    {
        File.WriteAllText(path, content);
        return path;
    }

    private string Final => Path.Join(_out, "Film.mkv");

    private void AssertNoPartialsLeft() => Assert.Empty(Directory.GetFiles(_out, ".Film.mkv.*"));

    private static readonly string StagedEnd = Path.Join("work", "Film.mkv");

    [Fact]
    public async Task A_copy_whose_source_vanishes_is_reported_and_leaves_the_existing_output_and_no_partial()
    {
        var source = Staged();
        var existing = Existing();
        var files = new FaultingFileSystem().Fail(FileCall.OpenFile, FaultingFileSystem.Vanished, pathEndsWith: StagedEnd);

        var error = await Assert.ThrowsAsync<FileLifecycleException>(() => FileLifecycle.SafeCopyToFinalAsync(source, Final, files: files));

        Assert.StartsWith("Could not safely copy", error.Message, StringComparison.Ordinal);
        Assert.IsType<FileNotFoundException>(error.InnerException);
        Assert.Equal("old output", File.ReadAllText(existing));
        Assert.Equal("new output", File.ReadAllText(source));
        AssertNoPartialsLeft();
    }

    [Fact]
    public async Task A_copy_whose_times_cannot_be_set_is_reported_and_leaves_no_partial()
    {
        var source = Staged();
        var existing = Existing();
        var files = new FaultingFileSystem().Fail(FileCall.SetLastWriteTimeUtc, FaultingFileSystem.Refused);

        await Assert.ThrowsAsync<FileLifecycleException>(() => FileLifecycle.SafeCopyToFinalAsync(source, Final, files: files));

        Assert.Equal("old output", File.ReadAllText(existing));
        AssertNoPartialsLeft();
    }

    [Fact]
    public async Task A_copy_the_final_name_will_not_take_is_reported_and_the_old_output_is_untouched()
    {
        var source = Staged();
        var existing = Existing();
        var files = new FaultingFileSystem().Fail(FileCall.MoveFile, FaultingFileSystem.SharingViolation, pathEndsWith: ".partial");

        var error = await Assert.ThrowsAsync<FileLifecycleException>(() => FileLifecycle.SafeCopyToFinalAsync(source, Final, files: files));

        Assert.StartsWith("Could not safely publish the validated copy", error.Message, StringComparison.Ordinal);
        Assert.Equal("old output", File.ReadAllText(existing));
        Assert.Equal("new output", File.ReadAllText(source));
        AssertNoPartialsLeft();
    }

    [Fact]
    public async Task A_copy_that_fails_its_check_is_removed_and_the_check_error_is_what_the_caller_sees()
    {
        var source = Staged();
        var existing = Existing();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => FileLifecycle.SafeCopyToFinalAsync(source, Final, _ => throw new InvalidDataException("not a valid video")));

        Assert.Equal("not a valid video", error.Message);
        Assert.Equal("old output", File.ReadAllText(existing));
        AssertNoPartialsLeft();
    }

    [Fact]
    public async Task A_copy_that_is_cancelled_part_way_leaves_no_partial_and_no_new_output()
    {
        var source = Staged();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FileLifecycle.SafeCopyToFinalAsync(source, Final, cancellationToken: cancel.Token));

        Assert.False(File.Exists(Final));
        AssertNoPartialsLeft();
    }

    [Fact]
    public async Task A_progress_reporter_that_throws_does_not_stop_the_copy()
    {
        var source = Staged();

        await FileLifecycle.SafeCopyToFinalAsync(source, Final, progressCallback: (_, _) => throw new InvalidOperationException("the reporter broke"));

        Assert.Equal("new output", File.ReadAllText(Final));
        AssertNoPartialsLeft();
    }

    [Fact]
    public async Task A_hard_link_for_an_output_with_no_owner_to_give_is_published_as_it_is()
    {
        var source = Staged();

        var linked = await FileLifecycle.TryHardlinkToFinalAsync(source, Final);

        Assert.True(linked);
        Assert.Equal("new output", File.ReadAllText(Final));
        AssertNoPartialsLeft();
    }

    [Fact]
    public async Task A_hard_link_for_an_output_with_an_owner_to_give_has_it_applied_to_the_published_file()
    {
        var ownership = new RecordingOwnership();

        var linked = await FileLifecycle.TryHardlinkToFinalAsync(Staged(), Final, ownership: ownership);

        Assert.True(linked);
        Assert.Equal([Path.GetFullPath(Final)], ownership.Files);
    }

    [Fact]
    public void A_drive_that_is_not_there_is_its_own_nearest_existing_parent_and_every_folder_above_a_new_output_is_made()
    {
        var root = Path.GetPathRoot(_dir.Path)!;
        var chain = new List<string>();
        for (var folder = (string?)_out; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            chain.Add(folder);
        }

        var files = new FaultingFileSystem().Hide([.. chain]);

        Assert.Equal(root, FileLifecycle.NearestExistingParent(root, files));
        FileLifecycle.EnsureOutputDirectory(Path.Join(_out, "a", "b"), ownership: null, files);
        Assert.True(Directory.Exists(Path.Join(_out, "a", "b")));
    }

    [Fact]
    public async Task A_hard_link_to_a_source_that_is_gone_is_declined_and_nothing_is_left_behind()
    {
        var existing = Existing();

        var linked = await FileLifecycle.TryHardlinkToFinalAsync(Path.Join(_work, "Gone.mkv"), Final);

        Assert.False(linked);
        Assert.Equal("old output", File.ReadAllText(existing));
        AssertNoPartialsLeft();
    }

    [Fact]
    public async Task A_hard_link_that_fails_its_check_is_removed_and_the_source_keeps_its_one_name()
    {
        var source = Staged();
        var existing = Existing();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => FileLifecycle.TryHardlinkToFinalAsync(source, Final, _ => throw new InvalidDataException("not a valid video")));

        Assert.Equal("old output", File.ReadAllText(existing));
        Assert.Equal("new output", File.ReadAllText(source));
        AssertNoPartialsLeft();
    }

    [Fact]
    public async Task A_hard_link_the_final_name_will_not_take_is_reported_and_removed()
    {
        var source = Staged();
        var existing = Existing();
        var files = new FaultingFileSystem().Fail(FileCall.MoveFile, FaultingFileSystem.SharingViolation, pathEndsWith: ".link");

        var error = await Assert.ThrowsAsync<FileLifecycleException>(() => FileLifecycle.TryHardlinkToFinalAsync(source, Final, files: files));

        Assert.StartsWith("Could not safely publish the validated link", error.Message, StringComparison.Ordinal);
        Assert.Equal("old output", File.ReadAllText(existing));
        Assert.Equal("new output", File.ReadAllText(source));
        AssertNoPartialsLeft();
    }

    [Fact]
    public void A_staged_file_another_program_holds_is_copied_instead_and_the_old_output_is_replaced()
    {
        var staged = Staged();
        Existing();
        var files = new FaultingFileSystem().Fail(FileCall.MoveFile, FaultingFileSystem.SharingViolation, pathEndsWith: StagedEnd);

        FileLifecycle.SafeFinalizeFile(staged, Final, files: files);

        Assert.Equal("new output", File.ReadAllText(Final));
        Assert.False(File.Exists(staged));
        AssertNoPartialsLeft();
    }

    [Fact]
    public void A_staged_file_that_can_be_neither_moved_nor_copied_is_reported_and_stays_where_it_was()
    {
        var staged = Staged();
        var existing = Existing();
        var files = new FaultingFileSystem()
            .Fail(FileCall.MoveFile, FaultingFileSystem.SharingViolation, pathEndsWith: StagedEnd)
            .Fail(FileCall.CopyFile, FaultingFileSystem.Refused);

        var error = Assert.Throws<FileLifecycleException>(() => FileLifecycle.SafeFinalizeFile(staged, Final, files: files));

        Assert.StartsWith("Could not safely finalize", error.Message, StringComparison.Ordinal);
        Assert.Equal("new output", File.ReadAllText(staged));
        Assert.Equal("old output", File.ReadAllText(existing));
        AssertNoPartialsLeft();
    }

    [Fact]
    public void A_final_name_that_will_not_take_the_output_is_reported_and_the_old_output_is_untouched()
    {
        var staged = Staged();
        var existing = Existing();
        var files = new FaultingFileSystem().Fail(FileCall.MoveFile, FaultingFileSystem.SharingViolation, pathEndsWith: ".partial");

        var error = Assert.Throws<FileLifecycleException>(() => FileLifecycle.SafeFinalizeFile(staged, Final, files: files));

        Assert.StartsWith("Could not safely finalize", error.Message, StringComparison.Ordinal);
        Assert.Equal("old output", File.ReadAllText(existing));
        AssertNoPartialsLeft();
    }

    [Fact]
    public void A_work_file_name_that_is_already_taken_draws_another_and_leaves_the_taken_one_alone()
    {
        var files = new FaultingFileSystem()
            .Before(FileCall.OpenFile, taken => File.WriteAllText(taken, "someone else's"))
            .Fail(FileCall.OpenFile, taken => new IOException($"The file '{taken}' already exists."));

        var made = FileLifecycle.CreateTempFile(_work, ".Film.", ".partial", files);

        var taken = Assert.Single(files.Calls, call => call.Call == FileCall.OpenFile && call.Path != made).Path;
        Assert.Equal("someone else's", File.ReadAllText(taken));
        Assert.True(File.Exists(made));
        Assert.Empty(File.ReadAllText(made));
    }

    [Fact]
    public void A_work_file_that_cannot_be_created_for_any_other_reason_is_the_error_the_system_gave()
    {
        var files = new FaultingFileSystem().Fail(FileCall.OpenFile, FaultingFileSystem.Refused);

        Assert.Throws<UnauthorizedAccessException>(() => FileLifecycle.CreateTempFile(_work, ".Film.", ".partial", files));

        Assert.Empty(Directory.GetFiles(_work));
    }

    [Fact]
    public void A_disk_space_check_the_system_cannot_answer_is_the_error_the_system_gave()
    {
        var files = new FaultingFileSystem().Fail(FileCall.GetAvailableFreeSpace, FaultingFileSystem.FolderVanished);

        Assert.Throws<DirectoryNotFoundException>(() => FileLifecycle.CheckMinimumFreeDiskSpace(_out, 1, files: files));
    }

    [Fact]
    public void A_requirement_of_zero_asks_the_disk_nothing()
    {
        var files = new FaultingFileSystem().Fail(FileCall.GetAvailableFreeSpace, FaultingFileSystem.Refused);

        var check = FileLifecycle.CheckMinimumFreeDiskSpace(_out, 0, files: files);

        Assert.True(check.Ok);
        Assert.DoesNotContain(files.Calls, call => call.Call == FileCall.GetAvailableFreeSpace);
    }

    [Fact]
    public void An_output_folder_that_cannot_be_made_is_the_error_the_system_gave_and_no_owner_is_applied()
    {
        var files = new FaultingFileSystem().Fail(FileCall.CreateDirectory, FaultingFileSystem.Refused);
        var ownership = new RecordingOwnership();

        Assert.Throws<UnauthorizedAccessException>(() => FileLifecycle.EnsureOutputDirectory(Path.Join(_out, "a", "b"), ownership, files));

        Assert.Empty(ownership.Directories);
    }

    [Fact]
    public void Each_folder_made_for_an_output_is_given_the_owner_topmost_first_and_an_existing_one_is_left_alone()
    {
        var ownership = new RecordingOwnership();

        FileLifecycle.EnsureOutputDirectory(Path.Join(_out, "a", "b"), ownership);

        Assert.Equal([Path.Join(_out, "a"), Path.Join(_out, "a", "b")], ownership.Directories);
    }

    [PosixFact("Only a system that is not Windows asks libc for a link.")]
    public void A_link_is_made_when_libc_says_it_was()
    {
        Assert.True(FileLifecycle.CreateHardLink(_dir.Join("link.mkv"), _dir.Join("film.mkv"), (_, _) => 0));
        Assert.False(FileLifecycle.CreateHardLink(_dir.Join("link.mkv"), _dir.Join("film.mkv"), (_, _) => -1));
    }

    [PosixFact("Only a system that is not Windows asks libc for a link.")]
    public void A_system_with_no_libc_to_ask_cannot_link()
    {
        Assert.False(FileLifecycle.CreateHardLink(_dir.Join("link.mkv"), _dir.Join("film.mkv"), (_, _) => throw new DllNotFoundException("libc")));
        Assert.False(FileLifecycle.CreateHardLink(_dir.Join("link.mkv"), _dir.Join("film.mkv"), (_, _) => throw new EntryPointNotFoundException("link")));
    }

    private sealed class RecordingOwnership : IOutputOwnership
    {
        public List<string> Directories { get; } = [];

        public void ApplyToDirectory(string path) => Directories.Add(path);

        public List<string> Files { get; } = [];

        public void ApplyToFile(string path) => Files.Add(path);
    }
}
