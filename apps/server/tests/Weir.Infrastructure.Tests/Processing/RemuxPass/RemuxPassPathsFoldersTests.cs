using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// The folders a workflow's passes use, and the clean-up of a rejected file, on real folders: which set-ups are refused and with what
/// sentence, and what is deleted (a plain file inside the watched folder, and nothing else).
/// </summary>
public sealed class RemuxPassPathsFoldersTests : IDisposable
{
    private readonly TempDirectory _home = new();
    private readonly string _watched;
    private readonly string _output;
    private readonly string _work;

    public RemuxPassPathsFoldersTests()
    {
        _watched = Make("watched");
        _output = Make("output");
        _work = Make("work");
    }

    public void Dispose() => _home.Dispose();

    private string Make(string name)
    {
        var path = _home.Join(name);
        Directory.CreateDirectory(path);
        return path;
    }

    private ProcessingLibraryRecord Library(string? watched = null, string? output = null, string? work = null, string name = "Movies", string mediaType = "movie") => new()
    {
        Name = name,
        MediaType = mediaType,
        WatchedFolder = watched ?? _watched,
        OutputFolder = output ?? _output,
        WorkFolder = work ?? _work,
    };

    private (ProcessingPathRuntime? Runtime, string? Problem) Runtime(ProcessingLibraryRecord library) =>
        RemuxPassPaths.RuntimeForLibrary(library, _home.Path, WorkflowManagerLinks.None);

    [Fact]
    public void The_folders_of_a_good_workflow_are_resolved_with_the_defaults_for_what_it_leaves_unset()
    {
        var (runtime, problem) = Runtime(Library() with { OutputCollisionPolicy = string.Empty, FfmpegStrictness = string.Empty });

        Assert.Null(problem);
        Assert.Equal(RemuxPassPaths.Resolve(_watched), runtime!.WatchedFolder);
        Assert.Equal(RemuxPassPaths.Resolve(_output), runtime.OutputFolder);
        Assert.Equal(RemuxPassPaths.Resolve(_work), runtime.WorkFolderEffective);
        Assert.False(runtime.WorkFolderIsDefault);
        Assert.Equal("replace", runtime.OutputCollisionPolicy);
        Assert.Equal("normal", runtime.FfmpegStrictness);
    }

    [Fact]
    public void A_workflow_with_no_work_folder_uses_the_default_one_for_its_kind_even_before_it_exists()
    {
        var (movie, _) = Runtime(Library() with { WorkFolder = "  " });
        var (tv, _) = Runtime(Library(name: "Shows", mediaType: "tv") with { WorkFolder = string.Empty });

        Assert.True(movie!.WorkFolderIsDefault);
        Assert.Equal(RemuxPassPaths.DefaultWorkFolder(_home.Path, "movie"), movie.WorkFolderEffective);
        Assert.Equal(RemuxPassPaths.DefaultWorkFolder(_home.Path, "tv"), tv!.WorkFolderEffective);
        Assert.NotEqual(movie.WorkFolderEffective, tv.WorkFolderEffective);
    }

    [Fact]
    public void The_work_folder_is_a_workflow_s_own_when_set_and_the_scope_default_when_not()
    {
        Assert.Equal((_work, false), RemuxPassPaths.EffectiveWorkFolder(Library(), _home.Path));
        var blank = RemuxPassPaths.EffectiveWorkFolder(Library() with { WorkFolder = " ", MediaType = "TV" }, _home.Path);
        Assert.True(blank.IsDefault);
        Assert.Equal(RemuxPassPaths.DefaultWorkFolder(_home.Path, "tv"), blank.WorkFolder);
        Assert.Throws<ArgumentNullException>(() => RemuxPassPaths.EffectiveWorkFolder(null!, _home.Path));
    }

    [Fact]
    public void The_entry_points_refuse_a_missing_argument()
    {
        Assert.Throws<ArgumentNullException>(() => RemuxPassPaths.RuntimeForLibrary(null!, _home.Path, WorkflowManagerLinks.None));
        Assert.Throws<ArgumentNullException>(() => RemuxPassPaths.RuntimeForLibrary(Library(), _home.Path, null!));
        Assert.Throws<ArgumentNullException>(() => RemuxPassPaths.RulesConfigFor(null!));
    }

    [Fact]
    public void A_workflow_with_no_name_is_called_by_its_kind_in_what_is_said_about_it()
    {
        var movie = Runtime(Library(name: " ") with { WatchedFolder = "  " });
        var tv = Runtime(Library(name: string.Empty, mediaType: "tv") with { WatchedFolder = string.Empty });

        Assert.StartsWith("The Movies workflow has no watched folder set.", movie.Problem, StringComparison.Ordinal);
        Assert.StartsWith("The TV workflow has no watched folder set.", tv.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_watched_folder_that_is_not_there_is_refused()
    {
        var problem = Runtime(Library(watched: Path.Join(_home.Path, "missing"))).Problem;

        Assert.Equal("The Movies workflow's watched folder must be an existing directory.", problem);
    }

    [Fact]
    public void An_output_folder_that_is_unset_or_not_there_is_refused()
    {
        var unset = Runtime(Library() with { OutputFolder = string.Empty }).Problem;
        var missing = Runtime(Library(output: Path.Join(_home.Path, "missing"))).Problem;

        Assert.StartsWith("The Movies workflow has no output folder set.", unset, StringComparison.Ordinal);
        Assert.Equal("The Movies workflow's output folder must be an existing directory.", missing);
    }

    [Fact]
    public void Folders_that_overlap_are_refused_whichever_is_inside_which()
    {
        var workInOutput = Runtime(Library(work: Make("output/work"))).Problem;
        var outputInWatched = Runtime(Library(output: Make("watched/out"))).Problem;
        var watchedInOutput = Runtime(Library(watched: Make("output/in"))).Problem;
        var workInWatched = Runtime(Library(work: Make("watched/work"))).Problem;

        Assert.Equal("The work/temp folder and output folder must be separate (no overlap or containment).", workInOutput);
        Assert.Equal("The watched folder and output folder must be separate (no overlap or containment).", outputInWatched);
        Assert.Equal("The watched folder and output folder must be separate (no overlap or containment).", watchedInOutput);
        Assert.Equal("The watched folder and work/temp folder must be separate (no overlap or containment).", workInWatched);
    }

    [Fact]
    public void A_custom_work_folder_must_exist_but_the_default_one_is_made_when_needed()
    {
        var custom = Runtime(Library(work: Path.Join(_home.Path, "missing-work"))).Problem;

        Assert.Equal("The Movies workflow's work/temp folder must be an existing directory when set to a custom path.", custom);
    }

    [Fact]
    public void A_path_starting_with_a_tilde_means_the_person_s_home_folder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(RemuxPassPaths.Resolve(profile), RemuxPassPaths.Resolve("~"));
        Assert.Equal(RemuxPassPaths.Resolve(Path.Join(profile, "films")), RemuxPassPaths.Resolve("~/films"));
        Assert.Equal(RemuxPassPaths.Resolve(Path.Join(profile, "films")), RemuxPassPaths.Resolve("~\\films"));
        Assert.Equal(Path.GetFullPath("."), RemuxPassPaths.Resolve(string.Empty));
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;
        Assert.Equal(root, RemuxPassPaths.Resolve(root));
    }

    [Fact]
    public void A_rejected_file_is_left_alone_unless_the_workflow_deletes_them()
    {
        var file = Path.Join(_watched, "bad.mkv");
        File.WriteAllText(file, "bad");

        foreach (var action in new string?[] { null, "leave", "something else" })
        {
            var result = RemuxPassPaths.CleanupRejectedFile(_watched, file, action);
            Assert.False(result.Deleted);
            Assert.Contains("Leave in place", result.Detail, StringComparison.Ordinal);
        }

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void A_rejected_file_is_deleted_with_the_empty_folders_it_leaves_up_to_the_watched_folder()
    {
        var folder = Path.Join(_watched, "Release", "Subs");
        Directory.CreateDirectory(folder);
        var file = Path.Join(folder, "bad.mkv");
        File.WriteAllText(file, "bad");

        var result = RemuxPassPaths.CleanupRejectedFile(_watched, file, " DELETE_FILE ");

        Assert.True(result.Deleted);
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(Path.Join(_watched, "Release")));
        Assert.True(Directory.Exists(_watched));
    }

    [Fact]
    public void A_folder_that_still_holds_something_is_kept_when_its_rejected_file_goes()
    {
        var folder = Path.Join(_watched, "Release");
        Directory.CreateDirectory(folder);
        var file = Path.Join(folder, "bad.mkv");
        File.WriteAllText(file, "bad");
        File.WriteAllText(Path.Join(folder, "good.mkv"), "good");

        var result = RemuxPassPaths.CleanupRejectedFile(_watched, file, "delete_file");

        Assert.True(result.Deleted);
        Assert.True(File.Exists(Path.Join(folder, "good.mkv")));
    }

    [Fact]
    public void Nothing_is_deleted_that_is_not_a_plain_file_inside_a_watched_folder_that_exists()
    {
        var file = Path.Join(_watched, "bad.mkv");
        File.WriteAllText(file, "bad");
        var elsewhere = Path.Join(_output, "other.mkv");
        File.WriteAllText(elsewhere, "other");
        var folder = Path.Join(_watched, "Release");
        Directory.CreateDirectory(folder);

        var noWatched = RemuxPassPaths.CleanupRejectedFile(Path.Join(_home.Path, "missing"), file, "delete_file");
        var noFile = RemuxPassPaths.CleanupRejectedFile(_watched, Path.Join(_watched, "gone.mkv"), "delete_file");
        var outside = RemuxPassPaths.CleanupRejectedFile(_watched, elsewhere, "delete_file");
        var aFolder = RemuxPassPaths.CleanupRejectedFile(_watched, folder, "delete_file");
        var illegal = RemuxPassPaths.CleanupRejectedFile(_watched, "bad\0name.mkv", "delete_file");

        Assert.Contains("watched folder", noWatched.Detail, StringComparison.Ordinal);
        Assert.Contains("could not be found", noWatched.Detail, StringComparison.Ordinal);
        Assert.Contains("gone.mkv", noFile.Detail, StringComparison.Ordinal);
        Assert.Contains("not safely inside the watched folder", outside.Detail, StringComparison.Ordinal);
        Assert.Contains("not a regular file", aFolder.Detail, StringComparison.Ordinal);
        Assert.Contains("not safely inside the watched folder", illegal.Detail, StringComparison.Ordinal);
        Assert.All(new[] { noWatched, noFile, outside, aFolder, illegal }, result => Assert.False(result.Deleted));
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(elsewhere));
        Assert.True(Directory.Exists(folder));
    }

    [WindowsFact("Another program holding a file open stops its deletion only where Windows enforces share modes.")]
    public void A_rejected_file_another_program_is_holding_is_reported_not_thrown()
    {
        var file = Path.Join(_watched, "bad.mkv");
        File.WriteAllText(file, "bad");
        using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = RemuxPassPaths.CleanupRejectedFile(_watched, file, "delete_file");

        Assert.False(result.Deleted);
        Assert.Contains("locked or unavailable", result.Detail, StringComparison.Ordinal);
    }
}
