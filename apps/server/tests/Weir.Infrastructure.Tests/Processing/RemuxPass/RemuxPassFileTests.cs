using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>Output name collisions, on real temporary folders.</summary>
public sealed class OutputCollisionTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    /// <summary>(source, staged, final) with the final already occupied.</summary>
    private (string Source, string Staged, string Final) Paths()
    {
        var source = Write(_root.Join("src", "Film.mkv"), 100);
        var staged = Write(_root.Join("work", "Film.staged.mkv"), 200);
        var final = Write(_root.Join("out", "Film (2001).mkv"), 150);
        return (source, staged, final);
    }

    private static string Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Replace_is_the_default_and_every_policy_is_known()
    {
        Assert.Equal(["replace", "skip", "keep_both", "replace_if_larger", "replace_if_newer"], OutputCollision.Policies);
        Assert.Equal("replace", OutputCollision.NormalizePolicy(null));
        Assert.Equal("replace", OutputCollision.NormalizePolicy(string.Empty));
        Assert.Equal("replace", OutputCollision.NormalizePolicy("something-invented"));
        Assert.Equal("keep_both", OutputCollision.NormalizePolicy(" KEEP_BOTH "));
    }

    [Fact]
    public void No_existing_file_is_never_a_collision()
    {
        var final = _root.Join("out", "Film.mkv");

        var decision = OutputCollision.Decide(final, policy: "skip");

        Assert.True(decision.Wrote);
        Assert.False(decision.ReplacedExisting);
        Assert.Equal(final, decision.Destination);
        Assert.Equal("No file existed at the output path, so Weir wrote it there.", decision.Reason);
    }

    [Fact]
    public void Replace_overwrites_and_says_so()
    {
        var (source, staged, final) = Paths();

        var decision = OutputCollision.Decide(final, source, staged, "replace");

        Assert.True(decision.Wrote);
        Assert.True(decision.ReplacedExisting);
        Assert.Contains("was replaced", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Skip_leaves_the_existing_output_alone()
    {
        var (source, staged, final) = Paths();

        var decision = OutputCollision.Decide(final, source, staged, "skip");

        Assert.False(decision.Wrote);
        Assert.False(decision.ReplacedExisting);
        Assert.Contains("kept the one that was already there", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Keep_both_writes_alongside_with_a_deterministic_suffix_and_keeps_counting()
    {
        var (source, staged, final) = Paths();

        Assert.Equal("Film (2001) (2).mkv", Path.GetFileName(OutputCollision.Decide(final, source, staged, "keep_both").Destination));
        Write(_root.Join("out", "Film (2001) (2).mkv"), 1);
        var decision = OutputCollision.Decide(final, source, staged, "keep_both");
        Assert.Equal("Film (2001) (3).mkv", Path.GetFileName(decision.Destination));
        Assert.True(decision.Wrote);
        Assert.False(decision.ReplacedExisting);
    }

    [Fact]
    public void Keep_both_with_every_numbered_name_taken_keeps_what_is_there_and_writes_nothing_over_it()
    {
        var (source, staged, final) = Paths();
        for (var number = 2; number <= 999; number++)
        {
            Write(_root.Join("out", $"Film (2001) ({number}).mkv"), 1);
        }

        var decision = OutputCollision.Decide(final, source, staged, "keep_both");

        Assert.False(decision.Wrote);
        Assert.Equal(final, decision.Destination);
        Assert.False(decision.ReplacedExisting);
        Assert.Contains("every numbered copy", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Replace_if_larger_compares_sizes_and_keeps_the_existing_output_when_it_cannot()
    {
        var (source, staged, final) = Paths();

        var larger = OutputCollision.Decide(final, source, staged, "replace_if_larger");
        Assert.True(larger.ReplacedExisting);
        Assert.Contains("200 bytes against 150", larger.Reason, StringComparison.Ordinal);

        File.WriteAllBytes(final, new byte[500]);
        var smaller = OutputCollision.Decide(final, source, staged, "replace_if_larger");
        Assert.False(smaller.Wrote);
        Assert.Contains("kept the existing one", smaller.Reason, StringComparison.Ordinal);

        var unknown = OutputCollision.Decide(final, source, null, "replace_if_larger");
        Assert.False(unknown.Wrote);
        Assert.Contains("could not compare", unknown.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Replace_if_newer_compares_the_source_with_the_existing_output()
    {
        var (source, staged, final) = Paths();
        File.SetLastWriteTimeUtc(final, DateTime.UnixEpoch.AddSeconds(1_000_000_000));
        File.SetLastWriteTimeUtc(source, DateTime.UnixEpoch.AddSeconds(2_000_000_000));
        Assert.True(OutputCollision.Decide(final, source, staged, "replace_if_newer").ReplacedExisting);

        File.SetLastWriteTimeUtc(final, DateTime.UnixEpoch.AddSeconds(2_000_000_000));
        File.SetLastWriteTimeUtc(source, DateTime.UnixEpoch.AddSeconds(1_000_000_000));
        var older = OutputCollision.Decide(final, source, staged, "replace_if_newer");
        Assert.False(older.Wrote);
        Assert.Contains("not newer", older.Reason, StringComparison.Ordinal);

        Assert.Contains("could not compare timestamps", OutputCollision.Decide(final, null, staged, "replace_if_newer").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_decision_carries_a_reason_and_its_policy()
    {
        var (source, staged, final) = Paths();
        foreach (var policy in OutputCollision.Policies)
        {
            var decision = OutputCollision.Decide(final, source, staged, policy);
            Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
            Assert.Equal(policy, decision.Policy);
        }
    }
}

/// <summary>Moving a source's sidecar files alongside the published output.</summary>
public sealed class SidecarMigrationTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly string _source;
    private readonly string _output;

    public SidecarMigrationTests()
    {
        _source = _root.Join("watched", "Film.2001.1080p.BluRay", "Film.2001.1080p.BluRay.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(_source)!);
        File.WriteAllText(_source, "video");
        _output = _root.Join("out", "Film (2001)", "Film (2001).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(_output)!);
        File.WriteAllText(_output, "video");
    }

    public void Dispose() => _root.Dispose();

    private string Beside(string name) => Path.Join(Path.GetDirectoryName(_source), name);

    private string Out(string name) => Path.Join(Path.GetDirectoryName(_output), name);

    [Fact]
    public void Patterns_are_normalised_and_deduplicated_and_empty_migrates_nothing()
    {
        Assert.Equal([".srt", ".nfo"], SidecarMigration.ParsePatterns("srt, .NFO ,srt, "));
        Assert.Empty(SidecarMigration.ParsePatterns(string.Empty));
        Assert.Empty(SidecarMigration.ParsePatterns(null));
        Assert.Contains(".idx", SidecarMigration.DefaultPatterns);
        Assert.Contains(".sub", SidecarMigration.DefaultPatterns);
        Assert.Contains(".nfo", SidecarMigration.DefaultPatterns);
    }

    [Fact]
    public void Only_files_matching_the_video_stem_are_found_including_multi_part_suffixes()
    {
        File.WriteAllText(Beside("Film.2001.1080p.BluRay.srt"), "mine");
        File.WriteAllText(Beside("Film.2001.1080p.BluRay.en.srt"), "mine too");
        File.WriteAllText(Beside("Other.Film.srt"), "not mine");

        Assert.Equal(
            ["Film.2001.1080p.BluRay.en.srt", "Film.2001.1080p.BluRay.srt"],
            SidecarMigration.FindSidecars(_source, [".srt"]).Select(Path.GetFileName));
        Assert.Empty(SidecarMigration.FindSidecars(_source, [".mkv"]));
        Assert.Empty(SidecarMigration.FindSidecars(_source, []));
        Assert.Empty(SidecarMigration.FindSidecars(_root.Join("gone", "film.mkv"), [".srt"]));
    }

    [Fact]
    public void A_sidecar_is_renamed_to_the_output_stem() =>
        Assert.Equal(Out("Film (2001).en.srt"), SidecarMigration.DestinationFor(Beside("Film.2001.1080p.BluRay.en.srt"), _source, _output));

    [Fact]
    public async Task A_sidecar_is_copied_and_renamed_and_the_original_stays()
    {
        File.WriteAllText(Beside("Film.2001.1080p.BluRay.srt"), "subs");

        var result = await SidecarMigration.MigrateAsync(_source, _output, [".srt"]);

        Assert.False(result.BlocksSourceDeletion);
        Assert.Equal(["Film (2001).srt"], result.Migrated.Select(m => Path.GetFileName(m.Destination)));
        Assert.Equal("subs", File.ReadAllText(Out("Film (2001).srt")));
        Assert.True(File.Exists(Beside("Film.2001.1080p.BluRay.srt")));
    }

    [Fact]
    public async Task No_sidecars_or_no_patterns_migrate_and_block_nothing()
    {
        var none = await SidecarMigration.MigrateAsync(_source, _output, SidecarMigration.DefaultPatterns);
        Assert.Empty(none.Migrated);
        Assert.False(none.BlocksSourceDeletion);

        File.WriteAllText(Beside("Film.2001.1080p.BluRay.srt"), "subs");
        var off = await SidecarMigration.MigrateAsync(_source, _output, []);
        Assert.Empty(off.Migrated);
        Assert.False(File.Exists(Out("Film (2001).srt")));
    }

    [Fact]
    public async Task An_existing_destination_is_left_alone_and_reported()
    {
        File.WriteAllText(Beside("Film.2001.1080p.BluRay.srt"), "release version");
        File.WriteAllText(Out("Film (2001).srt"), "my edited version");

        var result = await SidecarMigration.MigrateAsync(_source, _output, [".srt"]);

        Assert.Empty(result.Migrated);
        Assert.Contains(result.Skipped, s => s.Contains("already in the output folder", StringComparison.Ordinal));
        Assert.Equal("my edited version", File.ReadAllText(Out("Film (2001).srt")));
        Assert.False(result.BlocksSourceDeletion);
    }

    [Fact]
    public async Task A_directory_in_the_way_is_a_failure_that_blocks_the_source_deletion()
    {
        File.WriteAllText(Beside("Film.2001.1080p.BluRay.srt"), "subs");
        Directory.CreateDirectory(Out("Film (2001).srt"));

        var result = await SidecarMigration.MigrateAsync(_source, _output, [".srt"]);

        Assert.Empty(result.Skipped);
        Assert.Empty(result.Migrated);
        Assert.True(result.BlocksSourceDeletion);
        Assert.Contains("did not remove the source folder", result.BlockingReason, StringComparison.Ordinal);
        Assert.Contains("could not copy 1 file that was set to travel with the video: ", result.BlockingReason, StringComparison.Ordinal);
        Assert.Contains("nothing is lost", result.BlockingReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_uncopied_sidecars_read_in_the_plural()
    {
        var result = new SidecarMigrationResult();
        result.Failures.Add("a.srt: in use");
        result.Failures.Add("b.srt: in use");

        Assert.Equal(
            "Weir did not remove the source folder because it could not copy 2 files that were set to travel with the video: "
            + "a.srt: in use; b.srt: in use. The source is left in place so nothing is lost.",
            result.BlockingReason);
    }

    [Fact]
    public async Task An_interrupted_copy_never_exposes_a_partial_sidecar()
    {
        var sidecar = Beside("Film.2001.1080p.BluRay.srt");
        File.WriteAllText(sidecar, "subs");
        using (new FileStream(sidecar, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await SidecarMigration.MigrateAsync(_source, _output, [".srt"]);
            Assert.True(result.BlocksSourceDeletion);
            Assert.Empty(result.Migrated);
        }

        Assert.False(File.Exists(Out("Film (2001).srt")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_output)!, "*.partial"));
    }

    [Fact]
    public async Task Several_sidecars_travel_together()
    {
        foreach (var suffix in new[] { ".idx", ".sub", ".nfo" })
        {
            File.WriteAllText(Beside("Film.2001.1080p.BluRay" + suffix), suffix);
        }

        var result = await SidecarMigration.MigrateAsync(_source, _output, SidecarMigration.DefaultPatterns);

        Assert.Equal([".idx", ".nfo", ".sub"], result.Migrated.Select(m => Path.GetExtension(m.Destination)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Original_timestamps_are_applied_and_a_problem_is_reported_not_raised()
    {
        var old = DateTime.UnixEpoch.AddSeconds(1_000_000_000);
        File.SetLastWriteTimeUtc(_source, old);
        Assert.Null(SidecarMigration.ApplyOriginalTimestamps(_source, _output));
        Assert.Equal(old, File.GetLastWriteTimeUtc(_output));

        var problem = SidecarMigration.ApplyOriginalTimestamps(_root.Join("gone.mkv"), _root.Join("also-gone.mkv"));
        Assert.NotNull(problem);
        Assert.Contains("timestamps", problem, StringComparison.Ordinal);

        var sidecar = Beside("Film.2001.1080p.BluRay.srt");
        File.WriteAllText(sidecar, "subs");
        File.SetLastWriteTimeUtc(sidecar, old);
        var result = await SidecarMigration.MigrateAsync(_source, _output, [".srt"], preserveTimestamps: true);
        Assert.Equal(old, File.GetLastWriteTimeUtc(Assert.Single(result.Migrated).Destination));
    }
}

/// <summary>Source file access and open-writer checks used for settling, plus the pass's read guard.</summary>
public sealed class SourceFilesTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private static ProcessingLibraryRecord Library(bool skip = false) => new() { Name = "Movies", SkipAccessTests = skip };

    [Fact]
    public void An_unreadable_file_is_a_wait_with_a_reason()
    {
        var problem = SourceFiles.CheckFileAccess(Library(), _root.Join("not-there.mkv"), _root.Path);

        Assert.Contains("could not open this file for reading", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unwritable_output_folder_is_reported_before_any_work_starts()
    {
        var source = _root.Join("film.mkv");
        File.WriteAllText(source, "data");

        var problem = SourceFiles.CheckFileAccess(Library(), source, Path.Join(source, "output"));

        Assert.Contains("cannot write to the output folder", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_readable_file_and_writable_output_pass_and_leave_no_probe_behind()
    {
        var source = _root.Join("film.mkv");
        File.WriteAllText(source, "data");
        var output = Directory.CreateDirectory(_root.Join("out")).FullName;

        Assert.Null(SourceFiles.CheckFileAccess(Library(), source, output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        Assert.Null(SourceFiles.CheckFileAccess(Library(skip: true), _root.Join("not-there.mkv"), _root.Join("nope")));
    }

    [Fact]
    public void A_file_open_for_writing_by_another_program_makes_the_pass_wait()
    {
        var source = _root.Join("downloading.mkv");
        File.WriteAllBytes(source, new byte[4096]);
        using (new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            // A normal reader can open it, which is why a plain read check passes.
            using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
            }

            var (guard, problem) = SourceFiles.AcquireReadGuard(source);
            if (OperatingSystem.IsWindows())
            {
                Assert.Null(guard);
                Assert.Contains("still open for writing", problem, StringComparison.Ordinal);
            }
            else
            {
                guard?.Dispose();
            }
        }

        var (released, none) = SourceFiles.AcquireReadGuard(source);
        Assert.NotNull(released);
        Assert.Null(none);
        using (released)
        {
            if (OperatingSystem.IsWindows())
            {
                // The held guard keeps a writer from starting mid-pass.
                Assert.Throws<IOException>(() => new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
            }
        }
    }

    [Fact]
    public void The_fingerprint_changes_when_the_source_does()
    {
        var source = _root.Join("film.mkv");
        File.WriteAllBytes(source, new byte[10]);
        var before = SourceFiles.Fingerprint(source);
        Assert.Equal(before, SourceFiles.Fingerprint(source));
        Assert.Equal(10, before.SizeBytes);

        File.WriteAllBytes(source, new byte[11]);
        Assert.NotEqual(before, SourceFiles.Fingerprint(source));
        Assert.Throws<FileNotFoundException>(() => SourceFiles.Fingerprint(_root.Join("gone.mkv")));
    }
}

/// <summary>Remux pass path resolution and the library-folder rules.</summary>
public sealed class RemuxPassPathsTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Parent_segments_and_escapes_are_refused()
    {
        var media = Directory.CreateDirectory(_root.Join("media")).FullName;
        File.WriteAllText(Path.Join(media, "a.mkv"), "x");

        Assert.Contains("parent", Assert.Throws<ArgumentException>(() => RemuxPassPaths.ResolveMediaFileUnderRoot(media, "../a.mkv")).Message, StringComparison.Ordinal);
        Assert.Equal("relative_media_path is required", Assert.Throws<ArgumentException>(() => RemuxPassPaths.ResolveMediaFileUnderRoot(media, "  ")).Message);
        Assert.Equal(
            "The watched folder (saved settings) must be an existing directory",
            Assert.Throws<ArgumentException>(() => RemuxPassPaths.ResolveMediaFileUnderRoot(_root.Join("nope"), "a.mkv")).Message);
    }

    [Fact]
    public void A_nul_character_is_refused()
    {
        var media = Directory.CreateDirectory(_root.Join("media")).FullName;

        Assert.Equal(
            "relative_media_path must not contain a NUL character or a colon",
            Assert.Throws<ArgumentException>(() => RemuxPassPaths.ResolveMediaFileUnderRoot(media, "a\0.mkv")).Message);
    }

    [Fact]
    public void A_colon_is_refused_on_windows_only()
    {
        var media = Directory.CreateDirectory(_root.Join("media")).FullName;

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(
                "relative_media_path must not contain a NUL character or a colon",
                Assert.Throws<ArgumentException>(() => RemuxPassPaths.ResolveMediaFileUnderRoot(media, "a.mkv:hidden")).Message);
        }
        else
        {
            File.WriteAllText(Path.Join(media, "a.mkv:hidden"), "x");
            Assert.Equal(Path.GetFullPath(Path.Join(media, "a.mkv:hidden")), RemuxPassPaths.ResolveMediaFileUnderRoot(media, "a.mkv:hidden"));
        }
    }

    [Fact]
    public void A_file_under_the_root_resolves()
    {
        var media = Directory.CreateDirectory(_root.Join("media")).FullName;
        Directory.CreateDirectory(Path.Join(media, "sub"));
        var file = Path.Join(media, "sub", "a.mkv");
        File.WriteAllText(file, "x");

        Assert.Equal(Path.GetFullPath(file), RemuxPassPaths.ResolveMediaFileUnderRoot(media, "sub\\a.mkv".Replace('\\', '/')));
        Assert.Equal(Path.Join("sub", "a.mkv"), RemuxPassPaths.RelativeTo(file, media));
        Assert.Null(RemuxPassPaths.RelativeTo(_root.Path, media));
    }

    [Fact]
    public void A_library_runtime_explains_every_folder_problem()
    {
        ProcessingLibraryRecord Library(string watched, string output, string work = "") => new() { Name = "Movies", WatchedFolder = watched, OutputFolder = output, WorkFolder = work };
        var watched = Directory.CreateDirectory(_root.Join("watched")).FullName;
        var output = Directory.CreateDirectory(_root.Join("output")).FullName;

        Assert.Contains("has no watched folder set", RemuxPassPaths.RuntimeForLibrary(Library(string.Empty, output), _root.Path, WorkflowManagerLinks.None).Problem, StringComparison.Ordinal);
        Assert.Equal("The Movies workflow's watched folder must be an existing directory.", RemuxPassPaths.RuntimeForLibrary(Library(_root.Join("gone"), output), _root.Path, WorkflowManagerLinks.None).Problem);
        Assert.Contains("has no output folder set", RemuxPassPaths.RuntimeForLibrary(Library(watched, string.Empty), _root.Path, WorkflowManagerLinks.None).Problem, StringComparison.Ordinal);
        Assert.Equal(
            "The watched folder and output folder must be separate (no overlap or containment).",
            RemuxPassPaths.RuntimeForLibrary(Library(watched, watched), _root.Path, WorkflowManagerLinks.None).Problem);
        Assert.Equal(
            "The Movies workflow's work/temp folder must be an existing directory when set to a custom path.",
            RemuxPassPaths.RuntimeForLibrary(Library(watched, output, _root.Join("work-gone")), _root.Path, WorkflowManagerLinks.None).Problem);

        var (runtime, problem) = RemuxPassPaths.RuntimeForLibrary(Library(watched, output), _root.Path, WorkflowManagerLinks.None);
        Assert.Null(problem);
        Assert.True(runtime!.WorkFolderIsDefault);
        Assert.Equal(Path.Join(_root.Path, "processing", "processing-movie-work"), runtime.WorkFolderEffective);
    }

    [Fact]
    public void A_rejected_file_is_deleted_only_inside_the_watched_folder_with_its_empty_parents()
    {
        var watched = Directory.CreateDirectory(_root.Join("watched")).FullName;
        var release = Directory.CreateDirectory(Path.Join(watched, "Release", "Nested")).FullName;
        var file = Path.Join(release, "audio-only.mpg");
        File.WriteAllText(file, "x");
        var outside = _root.Join("outside.mpg");
        File.WriteAllText(outside, "x");

        Assert.False(RemuxPassPaths.CleanupRejectedFile(watched, file, "leave").Deleted);
        Assert.Contains("not safely inside the watched folder", RemuxPassPaths.CleanupRejectedFile(watched, outside, "delete_file").Detail, StringComparison.Ordinal);
        Assert.Contains("not a regular file", RemuxPassPaths.CleanupRejectedFile(watched, release, "delete_file").Detail, StringComparison.Ordinal);

        var deleted = RemuxPassPaths.CleanupRejectedFile(watched, file, "delete_file");
        Assert.True(deleted.Deleted);
        Assert.False(Directory.Exists(Path.Join(watched, "Release")));
        Assert.True(Directory.Exists(watched));
        Assert.True(File.Exists(outside));
    }

    /// <summary>
    /// A lexical prefix match is not enough: a junction (Windows) or symlink (elsewhere) placed under the watched
    /// folder can point straight out of it, and deleting through it would remove a file that actually lives
    /// somewhere else (#786 review of #785). Shared by the automatic reject job and Activity's remove-dialog "delete".
    /// </summary>
    [Fact]
    public void A_rejected_file_reached_through_a_linked_folder_is_left_alone()
    {
        var watched = Directory.CreateDirectory(_root.Join("watched")).FullName;
        var elsewhere = Directory.CreateDirectory(_root.Join("elsewhere", "Release")).FullName;
        var real = Path.Join(elsewhere, "film.mkv");
        File.WriteAllText(real, "x");
        var linked = Path.Join(watched, "Release");
        FolderLinks.Create(linked, elsewhere);

        var result = RemuxPassPaths.CleanupRejectedFile(watched, Path.Join(linked, "film.mkv"), "delete_file");

        Assert.False(result.Deleted);
        Assert.Contains("reached through a link", result.Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(real));
    }
}

/// <summary>The guarded file lifecycle writes the pass publishes through.</summary>
public sealed class FileLifecycleTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task A_copy_that_fails_validation_leaves_nothing_behind()
    {
        var source = _root.Join("src.mkv");
        File.WriteAllBytes(source, new byte[100]);
        var final = _root.Join("out", "final.mkv");

        await Assert.ThrowsAsync<InvalidOperationException>(() => FileLifecycle.SafeCopyToFinalAsync(source, final, _ => throw new InvalidOperationException("no")));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_root.Join("out")));
    }

    [Fact]
    public async Task A_cancelled_copy_leaves_nothing_behind()
    {
        var source = _root.Join("src.mkv");
        File.WriteAllBytes(source, new byte[100]);
        var final = _root.Join("out", "final.mkv");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileLifecycle.SafeCopyToFinalAsync(source, final, cancellationToken: cancelled.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_root.Join("out")));
    }

    [Fact]
    public async Task A_copy_larger_than_one_chunk_arrives_whole()
    {
        var source = _root.Join("src.mkv");
        var content = new byte[(8 * 1024 * 1024) + 12345];
        new Random(7).NextBytes(content);
        File.WriteAllBytes(source, content);
        var final = _root.Join("out", "final.mkv");

        await FileLifecycle.SafeCopyToFinalAsync(source, final);

        Assert.Equal(content, await File.ReadAllBytesAsync(final));
    }

    [Fact]
    public async Task A_validated_copy_replaces_the_destination_and_reports_progress()
    {
        var source = _root.Join("src.mkv");
        File.WriteAllBytes(source, new byte[100]);
        var final = _root.Join("out", "final.mkv");
        Directory.CreateDirectory(_root.Join("out"));
        File.WriteAllText(final, "old");
        var progress = new List<(long, long)>();

        await FileLifecycle.SafeCopyToFinalAsync(source, final, _ => Task.CompletedTask, (copied, total) => progress.Add((copied, total)));

        Assert.Equal(100, new FileInfo(final).Length);
        Assert.Equal((100, 100), progress[^1]);
        Assert.Single(Directory.EnumerateFileSystemEntries(_root.Join("out")));
    }

    [Fact]
    public async Task A_hard_link_is_validated_before_it_is_exposed()
    {
        var source = _root.Join("src.mkv");
        File.WriteAllBytes(source, new byte[100]);
        var final = _root.Join("out", "final.mkv");
        string? staged = null;

        var linked = await FileLifecycle.TryHardlinkToFinalAsync(source, final, path =>
        {
            staged = path;
            Assert.False(File.Exists(final));
            return Task.CompletedTask;
        });

        Assert.True(linked);
        Assert.EndsWith(".link", staged, StringComparison.Ordinal);
        Assert.Equal(100, new FileInfo(final).Length);
    }

    [Fact]
    public async Task A_validated_copy_that_cannot_be_published_raises_a_lifecycle_failure_and_removes_the_partial()
    {
        var source = _root.Join("src.mkv");
        File.WriteAllBytes(source, new byte[100]);
        var final = BlockedFinal();

        var failure = await Assert.ThrowsAsync<FileLifecycleException>(() => FileLifecycle.SafeCopyToFinalAsync(source, final, _ => Task.CompletedTask));

        AssertNothingPublished(failure, final);
        Assert.Equal(100, new FileInfo(source).Length);
    }

    [Fact]
    public async Task A_hard_link_that_cannot_be_published_raises_a_lifecycle_failure_and_removes_the_link_but_not_the_source()
    {
        var source = _root.Join("src.mkv");
        File.WriteAllBytes(source, new byte[100]);
        var final = BlockedFinal();

        var failure = await Assert.ThrowsAsync<FileLifecycleException>(() => FileLifecycle.TryHardlinkToFinalAsync(source, final, _ => Task.CompletedTask));

        AssertNothingPublished(failure, final);
        Assert.Equal(100, new FileInfo(source).Length);
    }

    [Fact]
    public void A_finalised_file_that_cannot_be_published_raises_a_lifecycle_failure_and_removes_the_partial()
    {
        var staged = _root.Join("work", "t.mkv");
        Directory.CreateDirectory(_root.Join("work"));
        File.WriteAllText(staged, "new");
        var final = BlockedFinal();

        var failure = Assert.Throws<FileLifecycleException>(() => FileLifecycle.SafeFinalizeFile(staged, final));

        AssertNothingPublished(failure, final);
    }

    [Fact]
    public void A_lifecycle_failure_reads_in_plain_words_and_keeps_the_system_text_for_technical_detail()
    {
        var failure = new FileLifecycleException("Could not safely publish the validated copy at /out/final.mkv: Access to the path is denied.");

        var plain = ToolFailureText.Plain(failure);

        Assert.Equal(ToolFailureText.OutputNotPlaced, plain);
        Assert.DoesNotContain("/out/final.mkv", plain, StringComparison.Ordinal);
        Assert.Contains("/out/final.mkv", ToolFailureText.Technical(failure), StringComparison.Ordinal);
    }

    /// <summary>A folder with a file in it where the output file belongs, so the last step has nothing it may replace.</summary>
    private string BlockedFinal()
    {
        var final = _root.Join("out", "final.mkv");
        Directory.CreateDirectory(final);
        File.WriteAllText(Path.Combine(final, "keep.txt"), "not Weir's to remove");
        return final;
    }

    /// <summary>The blocking folder is as it was, and nothing else, hidden or not, is beside it.</summary>
    private void AssertNothingPublished(FileLifecycleException failure, string final)
    {
        Assert.Contains(final, failure.Message, StringComparison.Ordinal);
        Assert.Equal([final], Directory.EnumerateFileSystemEntries(_root.Join("out")));
        Assert.Equal([Path.Combine(final, "keep.txt")], Directory.EnumerateFileSystemEntries(final));
    }

    [Fact]
    public void A_finalised_file_moves_into_place()
    {
        var staged = _root.Join("work", "t.mkv");
        Directory.CreateDirectory(_root.Join("work"));
        File.WriteAllText(staged, "new");
        var final = _root.Join("out", "deep", "final.mkv");

        FileLifecycle.SafeFinalizeFile(staged, final);

        Assert.Equal("new", File.ReadAllText(final));
        Assert.False(File.Exists(staged));
        Assert.Equal([final], Directory.EnumerateFiles(_root.Join("out", "deep")));
    }

    /// <summary>
    /// Across volumes .NET's <c>File.Move</c> copies straight to the name it is given (dotnet/runtime
    /// <c>FileSystem.Unix.cs</c> <c>MoveFile</c>, the <c>EXDEV</c> branch). A media manager importing from the output folder
    /// (Sonarr/Radarr through a remote path mapping) must never find a half-written file at the final name, so the copying
    /// move has to land on a hidden partial and only a same-directory rename may produce the final name.
    /// </summary>
    [Fact]
    public void A_cross_volume_finalise_never_writes_the_final_name_until_the_file_is_complete()
    {
        var staged = _root.Join("work", "t.mkv");
        Directory.CreateDirectory(_root.Join("work"));
        File.WriteAllText(staged, "the whole cleaned file");
        var final = _root.Join("out", "Show.S01E01", "Show.S01E01.mkv");
        var moves = new List<(string From, string To)>();

        void CrossVolumeMove(string from, string to, bool overwrite)
        {
            moves.Add((from, to));
            if (Path.GetDirectoryName(from) == Path.GetDirectoryName(to))
            {
                File.Move(from, to, overwrite);
                return;
            }

            // What .NET does when rename(2) fails with EXDEV: open the destination, copy into it, then delete the source.
            Assert.NotEqual(final, to);
            var bytes = File.ReadAllBytes(from);
            using (var output = new FileStream(to, overwrite ? FileMode.Create : FileMode.CreateNew))
            {
                output.Write(bytes, 0, bytes.Length / 2);
                Assert.False(File.Exists(final), "a half-copied file must never be visible at its final name");
                output.Write(bytes, bytes.Length / 2, bytes.Length - (bytes.Length / 2));
            }

            File.Delete(from);
        }

        FileLifecycle.SafeFinalizeFile(staged, final, null, CrossVolumeMove);

        Assert.Equal("the whole cleaned file", File.ReadAllText(final));
        Assert.False(File.Exists(staged));
        Assert.Equal(2, moves.Count);
        var partial = moves[0].To;
        Assert.Equal(Path.GetDirectoryName(final), Path.GetDirectoryName(partial));
        Assert.StartsWith(".Show.S01E01.mkv.", Path.GetFileName(partial), StringComparison.Ordinal);
        Assert.EndsWith(".partial", partial, StringComparison.Ordinal);
        Assert.Equal((partial, final), moves[1]);
        Assert.Equal([final], Directory.EnumerateFiles(Path.GetDirectoryName(final)!));
    }

    [Fact]
    public void A_staged_file_that_cannot_be_moved_is_copied_to_the_partial_and_still_published_atomically()
    {
        var staged = _root.Join("work", "held.mkv");
        Directory.CreateDirectory(_root.Join("work"));
        File.WriteAllText(staged, "held open elsewhere");
        var final = _root.Join("out", "held.mkv");
        var calls = 0;

        void RefuseTheFirstMove(string from, string to, bool overwrite)
        {
            if (calls++ == 0)
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            File.Move(from, to, overwrite);
        }

        FileLifecycle.SafeFinalizeFile(staged, final, null, RefuseTheFirstMove);

        Assert.Equal("held open elsewhere", File.ReadAllText(final));
        Assert.False(File.Exists(staged));
        Assert.Equal([final], Directory.EnumerateFiles(_root.Join("out")));
    }

    [Fact]
    public void The_disk_space_guardrail_can_be_off_and_reports_a_shortfall()
    {
        Assert.True(FileLifecycle.CheckMinimumFreeDiskSpace(_root.Join("x", "y.mkv"), 0).Ok);

        var low = FileLifecycle.CheckMinimumFreeDiskSpace(_root.Join("x", "y.mkv"), 5120, _ => 100L * 1024 * 1024);
        Assert.False(low.Ok);
        Assert.Equal(_root.Path, low.CheckedPath);
        Assert.Equal((100.0, 5120), (low.FreeMb, low.RequiredMb));
    }
}
