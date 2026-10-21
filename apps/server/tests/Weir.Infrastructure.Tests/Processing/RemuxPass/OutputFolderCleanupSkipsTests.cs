using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// Why output-folder cleanup leaves a folder alone, on real folders: where the output folder is, which other passes are heading for the
/// same place, and what a malformed record of one does not stop.
/// </summary>
public sealed class OutputFolderCleanupSkipsTests : IDisposable
{
    private const string MovieRelative = "Title/m.mkv";
    private const string TvRelative = "Show/S01/ep.mkv";

    private readonly PassFolders _folders = new();
    private readonly FakeCleanupData _data = new();

    public void Dispose() => _folders.Dispose();

    private OutputFolderCleanup Cleanup() => new(_data, TimeProvider.System, NullLogger.Instance, 0, 0);

    private static string Reason(WireObject output, string key) => output[key] is WireString text ? text.Value : string.Empty;

    private string OldFile(string relative)
    {
        var file = _folders.Out(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
        return file;
    }

    private async Task<string> MovieSkipReasonAsync(ProcessingPathRuntime runtime, string source, string? finalOutput, long? currentJob = 1)
    {
        var output = new WireObject();
        await Cleanup().RunMovieAsync(output, runtime, _folders.Watched, source, finalOutput, MovieRelative, currentJob, "movie", null, CancellationToken.None);
        return Reason(output, "movie_output_folder_skip_reason");
    }

    private async Task<string> TvSkipReasonAsync(ProcessingPathRuntime runtime, string source, string? finalOutput, long? currentJob = 1)
    {
        var output = new WireObject();
        await Cleanup().RunTvAsync(output, runtime, _folders.Watched, source, finalOutput, currentJob, "tv", null, CancellationToken.None);
        return Reason(output, "tv_output_season_folder_skip_reason");
    }

    public static TheoryData<string, string, string> WhyTheOutputFolderCannotBeFound() => new()
    {
        { "no output folder is set", "movie", "No Movies output folder is configured, so output-folder cleanup was not evaluated." },
        { "no output folder is set", "tv", "No TV output folder is configured, so TV output-folder cleanup was not evaluated." },
        { "the output folder is only spaces", "movie", "No Movies output folder is configured, so output-folder cleanup was not evaluated." },
        { "the output folder is missing", "movie", "The Movies output folder is missing on disk, so output-folder cleanup was skipped." },
        { "the output folder is missing", "tv", "The TV output folder is missing on disk, so TV output-folder cleanup was skipped." },
        { "the source is not under the watched folder", "movie", "The source file is not under the saved Movies watched folder, so output-folder cleanup was skipped." },
        { "the source is not under the watched folder", "tv", "The source file is not under the saved TV watched folder, so TV output-folder cleanup was skipped." },
        { "the result would be outside the output folder", "tv", "The expected TV episode file path would sit outside the TV output folder, so TV output-folder cleanup was skipped." },
        { "the result is the output folder itself", "movie", "The movie output folder would sit outside the Movies output root, so it was not changed." },
        { "the result is the output folder itself", "tv", "The TV season output folder would sit outside the TV output root, so it was not changed." },
        { "the result sits directly in the output folder", "tv", "The episode file sits directly in the TV output folder root, so a season folder is not removed here." },
    };

    [Theory]
    [MemberData(nameof(WhyTheOutputFolderCannotBeFound))]
    public async Task A_folder_cleanup_cannot_place_is_left_alone_and_the_reason_is_in_plain_words(string situation, string scope, string expected)
    {
        var relative = scope == "tv" ? TvRelative : MovieRelative;
        var runtime = _folders.Runtime();
        var source = _folders.Source(relative);
        string? finalOutput = OldFile(relative);
        switch (situation)
        {
            case "no output folder is set":
                runtime = runtime with { OutputFolder = string.Empty };
                break;
            case "the output folder is only spaces":
                runtime = runtime with { OutputFolder = "   " };
                break;
            case "the output folder is missing":
                runtime = runtime with { OutputFolder = Path.Join(_folders.Root, "no-such-output") };
                break;
            case "the source is not under the watched folder":
                source = Path.Join(_folders.Root, "elsewhere", Path.GetFileName(relative));
                break;
            case "the result would be outside the output folder":
                finalOutput = Path.Join(_folders.Root, "elsewhere", "ep.mkv");
                break;
            case "the result is the output folder itself":
                finalOutput = _folders.Output;
                break;
            case "the result sits directly in the output folder":
                finalOutput = OldFile("ep.mkv");
                break;
        }

        var reason = scope == "tv"
            ? await TvSkipReasonAsync(runtime, source, finalOutput)
            : await MovieSkipReasonAsync(runtime, source, finalOutput);

        Assert.Equal(expected, reason);
    }

    [Fact]
    public async Task With_no_final_output_the_folder_is_where_the_watched_path_would_land_in_the_output_folder()
    {
        var kept = OldFile(MovieRelative);
        _data.Truth.Add(new ManagerLibraryTruth(new ManagerConnection("radarr", "Main", "http://x", "k"), SignalStatus.Reported, [kept]));
        var output = new WireObject();

        await Cleanup().RunMovieAsync(output, _folders.Runtime(), _folders.Watched, _folders.Source(MovieRelative), null, MovieRelative, 1, "movie", null, CancellationToken.None);

        Assert.Equal(Path.GetDirectoryName(kept), Reason(output, "movie_output_folder_path"));
        Assert.True(File.Exists(kept));
    }

    private const string AnotherMoviePass = "Another Movies video pass";
    private const string AnotherTvPass = "Another TV video pass";

    public static TheoryData<string?, long, bool> WhichRecordedPassesStopAMovieFolderGoing() => new()
    {
        { """{"relative_media_path":"Title/m.mkv"}""", 7, true },
        { """{"relative_media_path":".\\Title\\m.mkv","media_scope":"movie"}""", 7, true },
        { """{"relative_media_path":"Title/m.mkv","media_scope":"movie"}""", 1, false },
        { """{"relative_media_path":"Title/m.mkv","media_scope":"tv"}""", 7, false },
        { """{"relative_media_path":"Other/m.mkv"}""", 7, false },
        { """{"relative_media_path":"  "}""", 7, false },
        { """{"media_scope":"movie"}""", 7, false },
        { "not json", 7, false },
        { "[1,2]", 7, false },
        { "   ", 7, false },
        { null, 7, false },
    };

    [Theory]
    [MemberData(nameof(WhichRecordedPassesStopAMovieFolderGoing))]
    public async Task Another_movie_pass_for_the_same_watched_file_holds_the_folder_back_and_a_record_that_cannot_be_read_does_not(string? payload, long jobId, bool holds)
    {
        var final = OldFile(MovieRelative);
        _data.Truth.Add(new ManagerLibraryTruth(new ManagerConnection("radarr", "Main", "http://x", "k"), SignalStatus.Reported, [_folders.Out("ManagerLibrary/m.mkv")]));
        _data.ActiveJobs.Add(new ActiveRemuxJob(jobId, payload));

        var reason = await MovieSkipReasonAsync(_folders.Runtime(), _folders.Source(MovieRelative), final);

        Assert.Equal(holds, reason.StartsWith(AnotherMoviePass, StringComparison.Ordinal));
        Assert.Equal(holds, File.Exists(final));
    }

    [Fact]
    public async Task A_record_that_cannot_be_read_does_not_hide_a_later_pass_for_the_same_file()
    {
        var final = OldFile(MovieRelative);
        _data.ActiveJobs.Add(new ActiveRemuxJob(5, "not json"));
        _data.ActiveJobs.Add(new ActiveRemuxJob(6, """{"relative_media_path":"Title/m.mkv"}"""));

        var reason = await MovieSkipReasonAsync(_folders.Runtime(), _folders.Source(MovieRelative), final);

        Assert.StartsWith(AnotherMoviePass, reason, StringComparison.Ordinal);
        Assert.True(File.Exists(final));
    }

    public static TheoryData<string?, long, bool> WhichRecordedPassesStopASeasonFolderGoing() => new()
    {
        { """{"relative_media_path":"Show/S01/ep2.mkv","media_scope":"tv"}""", 7, true },
        { """{"relative_media_path":"Show\\S01\\ep3.mkv","media_scope":"tv"}""", 7, true },
        { """{"relative_media_path":"Show/S01/ep2.mkv","media_scope":" TV "}""", 7, true },
        { """{"relative_media_path":"Show/S01/ep2.mkv","media_scope":"tv"}""", 1, false },
        { """{"relative_media_path":"Show/S01/ep2.mkv"}""", 7, false },
        { """{"relative_media_path":"Show/S01/ep2.mkv","media_scope":"movie"}""", 7, false },
        { """{"relative_media_path":"Show/S02/ep.mkv","media_scope":"tv"}""", 7, false },
        { """{"relative_media_path":"","media_scope":"tv"}""", 7, false },
        { """{"media_scope":"tv"}""", 7, false },
        { """{"relative_media_path":"../../escape/ep.mkv","media_scope":"tv"}""", 7, false },
        { "not json", 7, false },
        { "[1,2]", 7, false },
        { "", 7, false },
        { null, 7, false },
    };

    [Theory]
    [MemberData(nameof(WhichRecordedPassesStopASeasonFolderGoing))]
    public async Task Another_pass_for_an_episode_of_the_same_season_holds_the_folder_back_and_a_record_that_cannot_be_read_does_not(string? payload, long jobId, bool holds)
    {
        var episode = OldFile(TvRelative);
        _data.Truth.Add(new ManagerLibraryTruth(new ManagerConnection("sonarr", "Main", "http://x", "k"), SignalStatus.Reported, [_folders.Out("ManagerLibrary/ep.mkv")]));
        _data.ActiveJobs.Add(new ActiveRemuxJob(jobId, payload));

        var reason = await TvSkipReasonAsync(_folders.Runtime(), _folders.Source(TvRelative), episode);

        Assert.Equal(holds, reason.StartsWith(AnotherTvPass, StringComparison.Ordinal));
        Assert.Equal(holds, File.Exists(episode));
    }
}
