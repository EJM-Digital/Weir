using Microsoft.Extensions.Logging;
using Weir.Infrastructure.Jobs;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>Start-up clean-up of left-behind temp files when a file is locked, refused or gone as the clean-up reaches it.</summary>
public sealed class StartupRecoveryFaultTests : IDisposable
{
    private const string TempName = "Paper.Lanterns.2023.processing.ab12cd34.mkv";

    private readonly TempDirectory _home = new();
    private readonly RecordingLogger<object> _logger = new();
    private readonly List<ProcessingLibraryFolderRow> _noLibraries = [];

    public void Dispose() => _home.Dispose();

    private string MovieWork()
    {
        var folder = ProcessingLibraryFolders.DefaultMovieWorkFolder(_home.Path);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Write(string folder, string name)
    {
        var path = Path.Join(folder, name);
        File.WriteAllText(path, "partial");
        return path;
    }

    [Fact]
    public void A_temp_file_another_program_holds_is_kept_and_the_clean_up_goes_on_to_the_next()
    {
        var work = MovieWork();
        var locked = Write(work, TempName);
        var other = Write(work, "The.Long.Tide.2024.processing.ef56gh78.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.SharingViolation, pathEndsWith: TempName);

        var removed = StartupRecovery.SweepWorkFolderTempFiles(_noLibraries, _home.Path, notNewerThanUtc: null, _logger, files);

        Assert.Equal(1, removed);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(other));
        Assert.Contains(_logger.At(LogLevel.Warning), message => message.Contains("could not remove interrupted temp file", StringComparison.Ordinal));
    }

    [Fact]
    public void A_temp_file_that_goes_before_its_age_is_read_is_left_alone_and_reported()
    {
        var work = MovieWork();
        var path = Write(work, TempName);
        var files = new FaultingFileSystem().Fail(FileCall.GetLastWriteTimeUtc, FaultingFileSystem.Refused);

        var removed = StartupRecovery.SweepWorkFolderTempFiles(_noLibraries, _home.Path, DateTime.UtcNow, _logger, files);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(path));
        Assert.Single(_logger.At(LogLevel.Warning), message => message.Contains(TempName, StringComparison.Ordinal));
    }

    [Fact]
    public void A_work_folder_that_cannot_be_listed_is_reported_and_the_other_work_folder_is_still_cleared()
    {
        var movies = MovieWork();
        var tvFolder = ProcessingLibraryFolders.DefaultTvWorkFolder(_home.Path);
        Directory.CreateDirectory(tvFolder);
        var kept = Write(movies, TempName);
        var cleared = Write(tvFolder, "Northbound.S01E02.processing.ij90kl12.mkv");
        var files = new FaultingFileSystem().Fail(FileCall.ListFiles, FaultingFileSystem.Refused, pathEndsWith: Path.GetFileName(movies));

        var removed = StartupRecovery.SweepWorkFolderTempFiles(_noLibraries, _home.Path, notNewerThanUtc: null, _logger, files);

        Assert.Equal(1, removed);
        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(cleared));
        Assert.Single(_logger.At(LogLevel.Warning), message => message.Contains("could not read work folder", StringComparison.Ordinal));
    }

    [Fact]
    public void The_temp_file_of_an_interrupted_job_that_is_locked_stays_and_is_not_counted()
    {
        var work = MovieWork();
        var locked = Write(work, TempName);
        var job = new InterruptedJob(7, "processing.file.remux_pass.v1", "{\"relative_media_path\": \"Paper Lanterns (2023)/Paper.Lanterns.2023.mkv\", \"media_scope\": \"movie\"}");
        var files = new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.SharingViolation);

        var removed = StartupRecovery.RemoveInterruptedRemuxTempFiles([job], _noLibraries, _home.Path, _logger, files);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(locked));
        Assert.Single(_logger.At(LogLevel.Warning), message => message.Contains(TempName, StringComparison.Ordinal));
    }

    [Fact]
    public void A_partial_output_that_cannot_be_removed_is_skipped_and_the_rest_are_removed()
    {
        var output = Path.Join(_home.Path, "processing-output", "Movie");
        Directory.CreateDirectory(output);
        var locked = Write(output, ".a.mkv.q1.partial");
        var free = Write(output, ".b.mkv.q2.partial");
        var files = new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.SharingViolation, pathEndsWith: ".a.mkv.q1.partial");

        var removed = StartupRecovery.CleanupPartialOutputFiles(_noLibraries, _home.Path, files);

        Assert.Equal(1, removed);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(free));
    }

    [Fact]
    public void An_output_folder_that_cannot_be_listed_is_skipped_without_stopping_start_up()
    {
        var output = Path.Join(_home.Path, "processing-output", "Movie");
        Directory.CreateDirectory(output);
        var partial = Write(output, ".a.mkv.q1.partial");
        var files = new FaultingFileSystem().Fail(FileCall.ListFiles, FaultingFileSystem.Refused);

        var removed = StartupRecovery.CleanupPartialOutputFiles(_noLibraries, _home.Path, files);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(partial));
    }
}
