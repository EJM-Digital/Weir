using System.Text.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Jobs;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>
/// The refusals of the watched-folder scan guards, on real folders and a real database: a completion record that cannot be
/// trusted, a folder layout a scan cannot use, a source that will not go, an output folder that will not take a file.
/// </summary>
public sealed class WatchedFolderScanOpsGuardTests
{
    private const string MovieA = "movies/a.mkv";

    private static void AddCompletion(JobsTestDatabase db, string relativePath, string? detail) =>
        db.Execute(
            "INSERT INTO activity_events (module, event_type, title, detail, relative_path) " +
            "VALUES ('processing', 'processing.file_remux_pass_completed', 'x', @detail, @path)",
            ("@detail", detail),
            ("@path", relativePath));

    private static string Completion(Dictionary<string, object?> fields) => JsonSerializer.Serialize(fields);

    private static Dictionary<string, object?> Finished(string output) => new()
    {
        ["ok"] = true,
        ["relative_media_path"] = MovieA,
        ["output_file"] = output,
        ["source_deleted_after_success"] = false,
    };

    private static Dictionary<string, object?> With(Dictionary<string, object?> fields, string key, object? value)
    {
        var copy = new Dictionary<string, object?>(fields);
        if (value is null)
        {
            copy.Remove(key);
        }
        else
        {
            copy[key] = value;
        }

        return copy;
    }

    private static async Task<bool> OutputExistsAsync(JobsTestDatabase db, long? libraryId = 5, string? outputRoot = null, string? sourcePath = null)
    {
        await using var uow = await UnitOfWork.OpenAsync(db.Database);
        return await WatchedFolderScanOps.CompletedRemuxOutputExistsForRelativePathAsync(uow, MovieA, "movie", libraryId, outputRoot, sourcePath);
    }

    [Fact]
    public async Task Only_a_whole_matching_completion_record_with_an_output_on_disk_counts()
    {
        using var dir = new TempDirectory();
        using var db = new JobsTestDatabase();
        var output = dir.Join("out", "a.mkv");
        Directory.CreateDirectory(dir.Join("out"));
        File.WriteAllBytes(output, "done"u8.ToArray());
        File.WriteAllBytes(dir.Join("out", "empty.mkv"), []);
        var good = Finished(output);

        AddCompletion(db, MovieA, null);
        AddCompletion(db, MovieA, "   ");
        AddCompletion(db, MovieA, "not json {");
        AddCompletion(db, MovieA, "[1,2]");
        AddCompletion(db, MovieA, Completion(With(good, "ok", null)));
        AddCompletion(db, MovieA, Completion(With(good, "ok", false)));
        AddCompletion(db, MovieA, Completion(With(good, "source_deleted_after_success", null)));
        AddCompletion(db, MovieA, Completion(With(good, "source_deleted_after_success", true)));
        AddCompletion(db, MovieA, Completion(With(good, "relative_media_path", "movies/b.mkv")));
        AddCompletion(db, MovieA, Completion(With(good, "relative_media_path", null)));
        AddCompletion(db, MovieA, Completion(With(good, "relative_media_path", 7)));
        AddCompletion(db, MovieA, Completion(With(With(good, "library_id", 9), "media_scope", "movie")));
        AddCompletion(db, MovieA, Completion(With(good, "media_scope", "tv")));
        AddCompletion(db, MovieA, Completion(With(good, "output_file", null)));
        AddCompletion(db, MovieA, Completion(With(good, "output_file", "  ")));
        AddCompletion(db, MovieA, Completion(With(good, "output_file", dir.Join("out", "not-written.mkv"))));
        AddCompletion(db, MovieA, Completion(With(good, "output_file", dir.Join("out", "empty.mkv"))));

        Assert.False(await OutputExistsAsync(db));

        AddCompletion(db, MovieA, Completion(With(good, "library_id", 5)));

        Assert.True(await OutputExistsAsync(db));
        Assert.False(await OutputExistsAsync(db, libraryId: 6));
    }

    [Fact]
    public async Task A_completion_proves_the_source_only_when_it_names_the_same_file_of_the_same_size()
    {
        using var dir = new TempDirectory();
        using var db = new JobsTestDatabase();
        var output = dir.Join("out", "a.mkv");
        Directory.CreateDirectory(dir.Join("out"));
        File.WriteAllBytes(output, "done"u8.ToArray());
        var source = dir.Join("watch", "a.mkv");
        Directory.CreateDirectory(dir.Join("watch"));
        File.WriteAllBytes(source, "source"u8.ToArray());
        var good = Finished(output);

        AddCompletion(db, MovieA, Completion(good));
        Assert.False(await OutputExistsAsync(db, sourcePath: source));

        AddCompletion(db, MovieA, Completion(With(With(good, "inspected_source_path", ""), "source_size_bytes", 6)));
        Assert.False(await OutputExistsAsync(db, sourcePath: source));

        AddCompletion(db, MovieA, Completion(With(With(good, "inspected_source_path", dir.Join("watch", "other.mkv")), "source_size_bytes", 6)));
        Assert.False(await OutputExistsAsync(db, sourcePath: source));

        AddCompletion(db, MovieA, Completion(With(With(good, "inspected_source_path", source), "source_size_bytes", 6)));
        Assert.True(await OutputExistsAsync(db, sourcePath: source));

        File.Delete(source);
        Assert.False(await OutputExistsAsync(db, sourcePath: source));
    }

    [Fact]
    public async Task A_path_with_nothing_in_it_has_no_completed_output()
    {
        using var dir = new TempDirectory();
        using var db = new JobsTestDatabase();
        await using var uow = await UnitOfWork.OpenAsync(db.Database);

        Assert.False(await WatchedFolderScanOps.CompletedRemuxOutputExistsForRelativePathAsync(uow, "   ", "movie", null, dir.Path, null));
        Assert.False(await WatchedFolderScanOps.CompletedRemuxOutputExistsForRelativePathAsync(uow, "./", "movie", null, dir.Path, null));
    }

    [WindowsFact("Only Windows reads a drive letter as the start of a path part.")]
    public async Task A_drive_in_the_relative_path_never_reaches_outside_the_output_folder()
    {
        using var dir = new TempDirectory();
        using var db = new JobsTestDatabase();
        await using var uow = await UnitOfWork.OpenAsync(db.Database);
        var outside = dir.Join("elsewhere.mkv");
        File.WriteAllBytes(outside, "done"u8.ToArray());
        Directory.CreateDirectory(dir.Join("out"));

        Assert.False(await WatchedFolderScanOps.CompletedRemuxOutputExistsForRelativePathAsync(uow, outside.Replace('\\', '/'), "movie", null, dir.Join("out"), null));
    }

    [Fact]
    public void A_source_that_is_not_a_usable_path_is_not_removed_and_says_so()
    {
        using var dir = new TempDirectory();
        var watched = dir.Join("watch");
        Directory.CreateDirectory(watched);
        var sibling = dir.Join("elsewhere", "Movie.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.WriteAllBytes(sibling, [1]);

        var empty = WatchedFolderScanOps.RetryCompletedMovieSourceCleanup(watched, string.Empty, null, WorkflowManagerLinks.None);
        var outside = WatchedFolderScanOps.RetryCompletedMovieSourceCleanup(watched, sibling, null, WorkflowManagerLinks.None);

        Assert.False(empty.Ok);
        Assert.Contains("not safely under the watched folder", empty.Reason, StringComparison.Ordinal);
        Assert.False(outside.Ok);
        Assert.Equal("Source cleanup retry skipped because the path was not safely under the watched folder.", outside.Reason);
        Assert.True(File.Exists(sibling));
    }

    [Fact]
    public void A_release_folder_that_is_already_gone_counts_as_cleaned_and_any_other_failure_is_a_lock()
    {
        Assert.Equal((true, true, (string?)null), WatchedFolderScanOps.CleanupFailure(new DirectoryNotFoundException("gone")));

        var locked = WatchedFolderScanOps.CleanupFailure(new IOException("in use"));

        Assert.False(locked.Ok);
        Assert.False(locked.FolderRemoved);
        Assert.Contains("still locked or blocked (in use)", locked.Reason, StringComparison.Ordinal);
    }

    [WindowsFact("Only Windows refuses to delete a file another process holds open.")]
    public void A_release_folder_with_a_file_held_open_stays_and_the_reason_says_it_is_locked()
    {
        using var dir = new TempDirectory();
        var watched = dir.Join("watch");
        var release = Path.Combine(watched, "Movie 2026");
        Directory.CreateDirectory(release);
        var media = Path.Combine(release, "Movie 2026.mkv");
        File.WriteAllBytes(media, "source"u8.ToArray());
        using var holder = new FileStream(media, FileMode.Open, FileAccess.Read, FileShare.None);

        var (ok, folderRemoved, reason) = WatchedFolderScanOps.RetryCompletedMovieSourceCleanup(watched, media, null, WorkflowManagerLinks.None);

        Assert.False(ok);
        Assert.False(folderRemoved);
        Assert.Contains("still locked or blocked", reason, StringComparison.Ordinal);
        Assert.True(File.Exists(media));
    }

    [WindowsFact("Only Windows refuses to delete a read-only file.")]
    public void A_release_folder_with_a_read_only_file_stays_and_the_reason_says_it_is_blocked()
    {
        using var dir = new TempDirectory();
        var watched = dir.Join("watch");
        var release = Path.Combine(watched, "Movie 2026");
        Directory.CreateDirectory(release);
        var media = Path.Combine(release, "Movie 2026.mkv");
        File.WriteAllBytes(media, "source"u8.ToArray());
        File.SetAttributes(media, FileAttributes.ReadOnly);
        try
        {
            var (ok, _, reason) = WatchedFolderScanOps.RetryCompletedMovieSourceCleanup(watched, media, null, WorkflowManagerLinks.None);

            Assert.False(ok);
            Assert.Contains("still locked or blocked", reason, StringComparison.Ordinal);
            Assert.True(File.Exists(media));
        }
        finally
        {
            File.SetAttributes(media, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData(ProcessingMediaScopes.Movie, "Movies")]
    [InlineData(ProcessingMediaScopes.Tv, "TV")]
    public void A_workflow_without_a_name_is_called_by_its_kind_of_media(string mediaType, string label)
    {
        var library = new ProcessingLibraryRecord { Name = "  ", MediaType = mediaType, WatchedFolder = string.Empty };

        var (runtime, error) = WatchedFolderScanOps.ResolvePathRuntimeForLibrary(library, Path.GetTempPath());

        Assert.Null(runtime);
        Assert.StartsWith($"The {label} workflow has no watched folder set.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_watched_folder_that_is_not_on_disk_is_refused()
    {
        using var dir = new TempDirectory();
        var library = new ProcessingLibraryRecord { Name = "Films", MediaType = ProcessingMediaScopes.Movie, WatchedFolder = dir.Join("missing") };

        var (runtime, error) = WatchedFolderScanOps.ResolvePathRuntimeForLibrary(library, dir.Path);

        Assert.Null(runtime);
        Assert.Equal("The Films workflow's watched folder must be an existing directory.", error);
    }

    [Fact]
    public void An_output_folder_that_is_not_set_or_not_on_disk_is_refused()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Join("watch"));
        var library = new ProcessingLibraryRecord { Name = "Films", MediaType = ProcessingMediaScopes.Movie, WatchedFolder = dir.Join("watch"), OutputFolder = " " };

        var unset = WatchedFolderScanOps.ResolvePathRuntimeForLibrary(library, dir.Path);
        var missing = WatchedFolderScanOps.ResolvePathRuntimeForLibrary(library with { OutputFolder = dir.Join("missing") }, dir.Path);

        Assert.Null(unset.Runtime);
        Assert.StartsWith("The Films workflow has no output folder set.", unset.Error, StringComparison.Ordinal);
        Assert.Null(missing.Runtime);
        Assert.Equal("The Films workflow's output folder must be an existing directory.", missing.Error);
    }

    [Fact]
    public void A_custom_work_folder_that_is_not_on_disk_is_refused_but_the_default_one_is_not_needed_yet()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Join("watch"));
        Directory.CreateDirectory(dir.Join("out"));
        var library = new ProcessingLibraryRecord
        {
            Name = "Films",
            MediaType = ProcessingMediaScopes.Movie,
            WatchedFolder = dir.Join("watch"),
            OutputFolder = dir.Join("out"),
            WorkFolder = dir.Join("no-work-here"),
        };

        var custom = WatchedFolderScanOps.ResolvePathRuntimeForLibrary(library, dir.Path);
        var standard = WatchedFolderScanOps.ResolvePathRuntimeForLibrary(library with { WorkFolder = string.Empty }, dir.Path);

        Assert.Equal("The Films workflow's work/temp folder must be an existing directory when set to a custom path.", custom.Error);
        Assert.Null(standard.Error);
        Assert.False(Directory.Exists(standard.Runtime!.WorkFolderEffective));
    }

    [Theory]
    [InlineData("watch", "out/work", "out", "The work/temp folder and output folder must be separate")]
    [InlineData("watch", "work", "work/out", "The work/temp folder and output folder must be separate")]
    [InlineData("watch", "same", "same", "The work/temp folder and output folder must be separate")]
    [InlineData("out/watch", "work", "out", "The watched folder and output folder must be separate")]
    [InlineData("watch", "work", "watch/out", "The watched folder and output folder must be separate")]
    [InlineData("watch", "watch/work", "out", "The watched folder and work/temp folder must be separate")]
    [InlineData("work/watch", "work", "out", "The watched folder and work/temp folder must be separate")]
    public void Folders_that_overlap_are_refused_whichever_way_they_nest(string watched, string work, string output, string message)
    {
        using var dir = new TempDirectory();
        foreach (var folder in new[] { watched, work, output })
        {
            Directory.CreateDirectory(dir.Join(folder.Split('/')));
        }

        var library = new ProcessingLibraryRecord
        {
            Name = "Films",
            MediaType = ProcessingMediaScopes.Movie,
            WatchedFolder = dir.Join(watched.Split('/')),
            OutputFolder = dir.Join(output.Split('/')),
            WorkFolder = dir.Join(work.Split('/')),
        };

        var (runtime, error) = WatchedFolderScanOps.ResolvePathRuntimeForLibrary(library, dir.Path);

        Assert.Null(runtime);
        Assert.StartsWith(message, error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_output_folder_that_takes_a_file_is_fine_and_no_probe_is_left_behind()
    {
        using var dir = new TempDirectory();

        var problem = WatchedFolderScanOps.OutputFolderProblem(dir.Join("not", "yet", "there"));

        Assert.Null(problem);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Join("not", "yet", "there")));
    }

    [Fact]
    public void An_output_folder_that_is_really_a_file_is_refused_with_the_system_reason()
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(dir.Join("out"), [1]);

        var problem = WatchedFolderScanOps.OutputFolderProblem(dir.Join("out"));

        Assert.StartsWith($"Weir cannot write to the output folder ({dir.Join("out")}), so it did not start work that would have nowhere to go. The system reported: ", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_output_folder_where_the_probe_cannot_be_written_is_refused_and_the_blocker_is_left_alone()
    {
        using var dir = new TempDirectory();
        var blocker = dir.Join($".weir-write-test-{Environment.ProcessId}");
        Directory.CreateDirectory(blocker);

        var problem = WatchedFolderScanOps.OutputFolderProblem(dir.Path);

        Assert.StartsWith("Weir cannot write to the output folder", problem, StringComparison.Ordinal);
        Assert.True(Directory.Exists(blocker));
    }

    [WindowsFact("Only Windows refuses to write a file another process holds open.")]
    public void An_output_folder_whose_probe_is_held_open_is_refused()
    {
        using var dir = new TempDirectory();
        using var holder = new FileStream(dir.Join($".weir-write-test-{Environment.ProcessId}"), FileMode.Create, FileAccess.Write, FileShare.None);

        var problem = WatchedFolderScanOps.OutputFolderProblem(dir.Path);

        Assert.StartsWith("Weir cannot write to the output folder", problem, StringComparison.Ordinal);
    }
}
