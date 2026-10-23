using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>The folders output cleanup walks that are missing, empty, behind a link, or not Weir's to remove.</summary>
public sealed class OutputFolderCleanupEdgeTests : IDisposable
{
    private readonly PassFolders _folders = new();
    private readonly FakeCleanupData _data = new();

    public void Dispose() => _folders.Dispose();

    private OutputFolderCleanup Cleanup(IFileSystem? files = null) => new(_data, TimeProvider.System, NullLogger.Instance, 0, 0, files);

    private static ManagerLibraryTruth Reported(params string[] paths) =>
        new(new ManagerConnection("radarr", "Main", "http://x", "k"), SignalStatus.Reported, paths);

    private async Task<WireObject> RunMovie(string? finalOutputFile, IFileSystem? files = null)
    {
        var output = new WireObject();
        await Cleanup(files).RunMovieAsync(output, _folders.Runtime(), _folders.Watched, _folders.Source("Title/m.mkv"), finalOutputFile, "Title/m.mkv", 1, "movie", null, CancellationToken.None);
        return output;
    }

    [Fact]
    public void A_leading_slash_is_kept_when_relative_paths_are_normalised()
    {
        Assert.Equal("/foo/bar.mkv", OutputFolderCleanup.NormalizeRelativeForMatch("/foo//bar.mkv"));
    }

    [Fact]
    public void A_folder_that_is_not_there_has_no_media_and_no_timestamps()
    {
        var missing = _folders.Out("missing");

        Assert.Empty(OutputFolderCleanup.DirectChildMediaCandidates(missing));
        Assert.Null(OutputFolderCleanup.NewestModifiedUnderTree(missing));
    }

    [Fact]
    public async Task A_movie_folder_with_no_files_in_it_is_left_alone_and_says_so()
    {
        Directory.CreateDirectory(_folders.Out("Title"));
        _data.Truth.Add(Reported(_folders.Out(Path.Join("ManagerLibrary", "m.mkv"))));

        var output = await RunMovie(null);

        Assert.Equal(
            "Weir could not read file timestamps under the movie output folder, so nothing was removed for safety.",
            WireConvert.Str(output["movie_output_folder_skip_reason"]));
        Assert.True(Directory.Exists(_folders.Out("Title")));
    }

    [PosixFact("A symbolic link is created without special rights off Windows; the Windows link checks are in PathContainmentTests.")]
    public async Task A_movie_folder_behind_a_link_is_never_removed()
    {
        var real = Path.Join(_folders.Root, "elsewhere", "Title");
        Directory.CreateDirectory(real);
        var file = Path.Join(real, "m.mkv");
        File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
        Directory.CreateSymbolicLink(_folders.Out("Title"), real);
        _data.Truth.Add(Reported(_folders.Out(Path.Join("ManagerLibrary", "m.mkv"))));

        var output = await RunMovie(_folders.Out(Path.Join("Title", "m.mkv")));

        Assert.Equal(ReleaseFolderRemoval.LinkedFolderReason, WireConvert.Str(output["movie_output_folder_skip_reason"]));
        Assert.True(File.Exists(file));
    }

    private string AgedMovieOutput()
    {
        var final = _folders.Out(Path.Join("Title", "m.mkv"));
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        File.WriteAllText(final, "x");
        File.SetLastWriteTimeUtc(final, DateTime.UtcNow.AddDays(-3));
        _data.Truth.Add(Reported(_folders.Out(Path.Join("ManagerLibrary", "m.mkv"))));
        return final;
    }

    [Fact]
    public async Task A_movie_folder_the_system_will_not_describe_is_kept_as_if_it_were_behind_a_link()
    {
        var final = AgedMovieOutput();
        var files = new FaultingFileSystem().Fail(FileCall.GetAttributes, FaultingFileSystem.SharingViolation);

        var output = await RunMovie(final, files);

        Assert.Equal(ReleaseFolderRemoval.LinkedFolderReason, WireConvert.Str(output["movie_output_folder_skip_reason"]));
        Assert.True(File.Exists(final));
    }

    [Fact]
    public async Task A_movie_folder_whose_other_files_cannot_be_listed_is_kept_as_if_it_held_other_videos()
    {
        var final = AgedMovieOutput();
        var files = new FaultingFileSystem().Fail(FileCall.EnumerateFiles, FaultingFileSystem.Refused);

        var output = await RunMovie(final, files);

        Assert.Equal(ReleaseFolderRemoval.OtherVideosFolderKeptReason, WireConvert.Str(output["movie_output_folder_skip_reason"]));
        Assert.True(File.Exists(final));
    }

    [Fact]
    public void The_cascade_stops_and_says_so_when_the_system_refuses_to_remove_an_empty_parent()
    {
        var root = _folders.Out("lib");
        var empty = Directory.CreateDirectory(Path.Join(root, "a", "b")).FullName;
        var logger = new CapturingLogger<OutputFolderCleanup>();
        var deleted = new WireArray();
        var files = new FaultingFileSystem().Fail(FileCall.DeleteDirectory, FaultingFileSystem.Refused);

        OutputFolderCleanup.CascadeDeleteEmptyParents(empty, root, deleted, logger, files);

        Assert.Empty(deleted.Items);
        Assert.True(Directory.Exists(empty));
        Assert.True(logger.Logged(LogLevel.Warning, "could not remove an empty parent folder"));
    }

    [Fact]
    public async Task A_manager_path_that_cannot_be_resolved_does_not_clear_the_delete()
    {
        var final = _folders.Out(Path.Join("Title", "m.mkv"));
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        File.WriteAllText(final, "x");
        File.SetLastWriteTimeUtc(final, DateTime.UtcNow.AddDays(-3));
        _data.Truth.Add(Reported("bad\0name.mkv"));

        var output = await RunMovie(final);

        Assert.False(((WireBool)output["movie_output_folder_deleted"]).Value);
        Assert.True(File.Exists(final));
    }

    [Fact]
    public void The_cascade_stops_at_a_folder_outside_the_root_and_says_so()
    {
        var root = _folders.Out("lib");
        Directory.CreateDirectory(root);
        var outside = Directory.CreateDirectory(Path.Join(_folders.Root, "elsewhere")).FullName;
        var logger = new CapturingLogger<OutputFolderCleanup>();
        var deleted = new WireArray();

        OutputFolderCleanup.CascadeDeleteEmptyParents(outside, root, deleted, logger);

        Assert.Empty(deleted.Items);
        Assert.True(Directory.Exists(outside));
        Assert.True(logger.Logged(LogLevel.Warning, "outside the root"));
    }

    [Fact]
    public void The_cascade_stops_at_a_folder_that_is_gone_and_at_one_that_still_holds_something()
    {
        var root = _folders.Out("lib");
        var holding = Directory.CreateDirectory(Path.Join(root, "a")).FullName;
        File.WriteAllText(Path.Join(holding, "keep.txt"), "x");
        var deleted = new WireArray();

        OutputFolderCleanup.CascadeDeleteEmptyParents(Path.Join(root, "gone", "deeper"), root, deleted, NullLogger.Instance);
        OutputFolderCleanup.CascadeDeleteEmptyParents(holding, root, deleted, NullLogger.Instance);

        Assert.Empty(deleted.Items);
        Assert.True(File.Exists(Path.Join(holding, "keep.txt")));
    }

    [PosixFact("Only permission bits stop a folder being removed from its parent without a lock; Windows share modes are covered by the locked-folder test.")]
    public void The_cascade_stops_and_says_so_when_a_parent_cannot_be_removed()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return;
        }

        var root = _folders.Out("lib");
        var parent = Directory.CreateDirectory(Path.Join(root, "a")).FullName;
        var empty = Directory.CreateDirectory(Path.Join(parent, "b")).FullName;
        var logger = new CapturingLogger<OutputFolderCleanup>();
        var deleted = new WireArray();
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            OutputFolderCleanup.CascadeDeleteEmptyParents(empty, root, deleted, logger);
        }
        finally
        {
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Empty(deleted.Items);
        Assert.True(Directory.Exists(empty));
        Assert.True(logger.Logged(LogLevel.Warning, "could not remove an empty parent folder"));
    }

    [PosixFact("Only permission bits hide part of a release folder from the process on a path it can still name.")]
    public void A_release_folder_Weir_cannot_look_through_loses_only_the_file()
    {
        using var folder = new TempDirectory();
        var root = folder.Join("lib");
        var release = Directory.CreateDirectory(Path.Join(root, "Title")).FullName;
        var file = Path.Join(release, "m.mkv");
        File.WriteAllText(file, "x");
        var hidden = Directory.CreateDirectory(Path.Join(release, "extras")).FullName;
        using var denied = UnreadablePath.Create(hidden);
        if (denied is null)
        {
            return;
        }

        var removal = ReleaseFolderRemoval.Remove(root, file, null);

        Assert.Equal(ReleaseRemovalKind.FileOnly, removal.Kind);
        Assert.Equal(ReleaseFolderRemoval.UnreadableFolderReason, removal.Reason);
        Assert.False(File.Exists(file));
        Assert.True(Directory.Exists(release));
    }
}
