using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// The existing output or the release folder is held, refused or removed while Weir compares or deletes: the decision says
/// what Weir could not do, and nothing is replaced or removed on a guess.
/// </summary>
public sealed class OutputRaceFaultTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(string relative, string content = "film")
    {
        var path = _dir.Join(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void An_existing_output_that_cannot_be_dated_is_kept_when_the_policy_is_to_replace_if_newer()
    {
        var final = Write("out/Film.mkv");
        var source = Write("watch/Film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.GetLastWriteTimeUtc, FaultingFileSystem.SharingViolation, pathEndsWith: Path.Join("out", "Film.mkv"));

        var decision = OutputCollision.Decide(final, source: source, policy: OutputCollision.ReplaceIfNewer, files: files);

        Assert.Equal("skip", decision.Action);
        Assert.False(decision.ReplacedExisting);
        Assert.Equal("Weir could not compare timestamps with the existing Film.mkv, so it kept the existing one rather than replacing a file it could not date.", decision.Reason);
    }

    [Fact]
    public void A_source_that_is_not_there_to_date_keeps_the_existing_output_when_the_policy_is_to_replace_if_newer()
    {
        var final = Write("out/Film.mkv");

        var decision = OutputCollision.Decide(final, source: _dir.Join("watch", "Gone.mkv"), policy: OutputCollision.ReplaceIfNewer);

        Assert.Equal("skip", decision.Action);
        Assert.Contains("could not compare timestamps", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_output_that_is_not_there_to_measure_keeps_the_existing_output_when_the_policy_is_to_replace_if_larger()
    {
        var final = Write("out/Film.mkv");

        var decision = OutputCollision.Decide(final, staged: _dir.Join("work", "Gone.mkv"), policy: OutputCollision.ReplaceIfLarger);

        Assert.Equal("skip", decision.Action);
        Assert.Contains("could not compare the new output", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_output_that_is_a_folder_is_dated_by_the_folder_and_is_kept_when_the_source_is_older()
    {
        var final = _dir.Join("out", "Film.mkv");
        Directory.CreateDirectory(final);
        var source = Write("watch/Film.mkv");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-30));

        var decision = OutputCollision.Decide(final, source: source, policy: OutputCollision.ReplaceIfNewer);

        Assert.Equal("skip", decision.Action);
        Assert.Contains("is not newer", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_output_that_is_a_folder_is_measured_as_empty_when_the_policy_is_to_replace_if_larger()
    {
        var final = _dir.Join("out", "Film.mkv");
        Directory.CreateDirectory(final);
        var staged = Write("work/Film.mkv", "four");

        var decision = OutputCollision.Decide(final, staged: staged, policy: OutputCollision.ReplaceIfLarger);

        Assert.Equal("write", decision.Action);
        Assert.Contains("(4 bytes against 0)", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_output_removed_while_it_is_measured_is_no_collision_and_the_new_output_is_written()
    {
        var final = Write("out/Film.mkv");
        var staged = Write("work/Film.mkv", "four bytes");
        var files = new FaultingFileSystem().Before(FileCall.GetLength, File.Delete, pathEndsWith: Path.Join("out", "Film.mkv"));

        var decision = OutputCollision.Decide(final, staged: staged, policy: OutputCollision.ReplaceIfLarger, files: files);

        Assert.Equal("write", decision.Action);
        Assert.Equal(final, decision.Destination);
        Assert.False(decision.ReplacedExisting);
        Assert.Equal("No file existed at the output path, so Weir wrote it there.", decision.Reason);
    }

    [Fact]
    public void An_existing_output_removed_while_it_is_dated_is_no_collision_and_the_new_output_is_written()
    {
        var final = Write("out/Film.mkv");
        var source = Write("watch/Film.mkv");
        var files = new FaultingFileSystem()
            .Before(FileCall.GetLastWriteTimeUtc, File.Delete, pathEndsWith: Path.Join("out", "Film.mkv"))
            .Fail(FileCall.GetLastWriteTimeUtc, FaultingFileSystem.Vanished, pathEndsWith: Path.Join("out", "Film.mkv"));

        var decision = OutputCollision.Decide(final, source: source, policy: OutputCollision.ReplaceIfNewer, files: files);

        Assert.Equal("write", decision.Action);
        Assert.False(decision.ReplacedExisting);
    }

    [Fact]
    public void The_next_free_numbered_name_skips_a_name_taken_by_a_file_or_a_folder_and_gives_up_at_the_limit()
    {
        var final = Write("out/Film.mkv");
        Write("out/Film (2).mkv");
        Directory.CreateDirectory(_dir.Join("out", "Film (3).mkv"));

        Assert.Null(OutputCollision.NextFreePath(final, limit: 3));
        Assert.Equal(Path.Join(_dir.Path, "out", "Film (4).mkv"), OutputCollision.NextFreePath(final, limit: 4));
    }

    [Fact]
    public void Every_numbered_copy_taken_means_the_existing_output_is_kept_and_nothing_new_is_written()
    {
        var final = Write("out/Film.mkv");
        for (var number = 2; number <= 999; number++)
        {
            Write($"out/Film ({number}).mkv", "");
        }

        var decision = OutputCollision.Decide(final, policy: OutputCollision.KeepBoth);

        Assert.Equal("skip", decision.Action);
        Assert.Equal(final, decision.Destination);
        Assert.Contains("so does every numbered copy of it", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_with_no_extension_is_numbered_after_the_whole_name()
    {
        var final = Write("out/Film");

        Assert.Equal(Path.Join(_dir.Path, "out", "Film (2)"), OutputCollision.NextFreePath(final));
    }

    [Fact]
    public void A_release_folder_the_system_refuses_to_delete_is_the_error_the_system_gave_and_nothing_is_removed()
    {
        var root = _dir.Join("movies");
        var film = Write("movies/Film (2001)/Film.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.DeleteDirectory, FaultingFileSystem.SharingViolation);

        Assert.Throws<IOException>(() => ReleaseFolderRemoval.Remove(root, film, null, files));

        Assert.True(File.Exists(film));
    }

    [Fact]
    public void A_file_in_a_pack_that_another_program_holds_is_the_error_the_system_gave_and_the_pack_is_intact()
    {
        var root = _dir.Join("movies");
        var film = Write("movies/Pack/One.mkv");
        var other = Write("movies/Pack/Two.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.Refused);

        Assert.Throws<UnauthorizedAccessException>(() => ReleaseFolderRemoval.Remove(root, film, null, files));

        Assert.True(File.Exists(film));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void A_release_folder_that_is_removed_by_someone_else_before_Weir_deletes_it_counts_as_removed()
    {
        var root = _dir.Join("movies");
        var film = Write("movies/Film (2001)/Film.mkv");
        var folder = Path.GetDirectoryName(film)!;
        var files = new FaultingFileSystem()
            .Before(FileCall.DeleteDirectory, _ => Directory.Delete(folder, recursive: true))
            .Fail(FileCall.DeleteDirectory, FaultingFileSystem.FolderVanished);

        var removal = ReleaseFolderRemoval.Remove(root, film, null, files);

        Assert.Equal(ReleaseRemovalKind.FolderRemoved, removal.Kind);
        Assert.Null(removal.Reason);
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void A_pack_file_that_is_removed_by_someone_else_before_Weir_deletes_it_counts_as_removed()
    {
        var root = _dir.Join("movies");
        var film = Write("movies/Pack/One.mkv");
        var other = Write("movies/Pack/Two.mkv");
        var files = new FaultingFileSystem()
            .Before(FileCall.DeleteFile, File.Delete)
            .Fail(FileCall.DeleteFile, FaultingFileSystem.Vanished);

        var removal = ReleaseFolderRemoval.Remove(root, film, null, files);

        Assert.Equal(ReleaseRemovalKind.FileOnly, removal.Kind);
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void A_sample_that_vanishes_while_its_size_is_read_costs_only_the_file_not_the_release_folder()
    {
        var root = _dir.Join("movies");
        var film = Write("movies/Film (2001)/Film.mkv");
        var sample = Write("movies/Film (2001)/Sample/sample.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.GetLength, FaultingFileSystem.Vanished, pathEndsWith: "sample.mkv");

        var removal = ReleaseFolderRemoval.Remove(root, film, null, files);

        Assert.Equal(ReleaseRemovalKind.FileOnly, removal.Kind);
        Assert.Equal(ReleaseFolderRemoval.UnreadableFolderReason, removal.Reason);
        Assert.False(File.Exists(film));
        Assert.True(File.Exists(sample));
    }

    [Fact]
    public void A_file_outside_the_workflow_folder_is_not_removed_and_the_reason_says_so()
    {
        var root = _dir.Join("movies");
        Directory.CreateDirectory(root);
        var elsewhere = Write("elsewhere/Film.mkv");

        var removal = ReleaseFolderRemoval.Remove(root, elsewhere, null);

        Assert.Equal(ReleaseRemovalKind.NothingRemoved, removal.Kind);
        Assert.Equal("The release folder is not inside the workflow's folder, so Weir did not change it.", removal.Reason);
        Assert.True(File.Exists(elsewhere));
    }
}
