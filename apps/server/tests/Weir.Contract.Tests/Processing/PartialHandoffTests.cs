using System.Net;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// A folder hand-off is settled by what Weir delivered. An extra the workflow's own rules leave alone (under its minimum size)
/// does not fail the hand-off, so the manager's "imported" is accepted and settles the file; a hand-off whose main video failed
/// is still failed, and Weir still refuses an "imported" for it because it handed back no file.
/// </summary>
[ContractArea("processing")]
public sealed class PartialHandoffTests
{
    private const string ImportedPath = "/deluno/library/movies/Film (2020)/Film (2020).mkv";
    private const string Release = "Film.2020";
    private const string Main = "film.mkv";
    private const string Extra = "Gallery.mkv";
    private const int MinimumMb = 1;

    [Fact]
    public async Task A_main_film_delivered_with_an_extra_under_the_minimum_is_completed_and_the_import_settles_it()
    {
        await using var scenario = await Scenario.StartAsync();
        var (fake, library) = await scenario.DelunoSetupAsync(library: [("min_file_size_mb", MinimumMb)]);
        var release = WriteRelease(scenario, mainFilm: FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"]), padding: 2 * 1024 * 1024));

        await scenario.PostHandoffAsync("partial-1", release);

        var status = await scenario.WaitForHandoffStateAsync("partial-1", "completed");
        var copy = Path.Combine(scenario.Folders.Output, Release, Main);
        Assert.True(File.Exists(copy));
        Assert.False(File.Exists(Path.Combine(scenario.Folders.Output, Release, Extra)));
        var extra = await scenario.WaitForFileStatusAsync(library, $"{Release}/{Extra}", "skipped");
        Assert.Contains("under the 1 MB minimum", (string)extra["status_reason"]!, StringComparison.Ordinal);

        var report = Assert.Single(await scenario.WaitForReportsAsync(fake, "partial-1"));
        Assert.Equal("completed", (string)report["status"]!);
        // The manager is told the copy under its own processor output folder, not Weir's local one.
        Assert.Equal(
            [$"{Scenario.DelunoOutputRoot}/{Release}/{Main}"],
            report["outputFiles"]!.AsArray().Select(file => ((string)file!).Replace('\\', '/')));
        Assert.Contains($"{Extra}: Skipped because this file is", (string)report["message"]!, StringComparison.Ordinal);
        Assert.Single(status["outputFiles"]!.AsArray());

        var imported = await scenario.PostOutcomeAsync("partial-1", "imported", ImportedPath);

        Assert.True(imported.Status == HttpStatusCode.OK, imported.ToString());
        Assert.True((bool)imported.Fields["released"]!, imported.ToString());
        Assert.False(File.Exists(copy), "Weir's own copy is released once the manager has the file");
        var handback = (await scenario.FileRowAsync(library, $"{Release}/{Main}"))!["handback"]!;
        Assert.Equal(("imported", "Deluno"), ((string)handback["outcome"]!, (string)handback["outcome_by"]!));
        Assert.NotNull(handback["released_at"]);
        Assert.Contains(
            "Deluno imported film.mkv",
            (await scenario.ActivityAsync("processing.handback_outcome")).Select(entry => (string)entry["title"]!));
    }

    [Fact]
    public async Task A_hand_off_whose_main_film_failed_is_failed_and_the_import_is_refused()
    {
        await using var scenario = await Scenario.StartAsync();
        var (fake, _) = await scenario.DelunoSetupAsync(
            library: [("min_file_size_mb", MinimumMb), ("failure_policy", "hold"), ("max_attempts", 1)]);
        scenario.FakeTools.SetFileRule(Main, new FileRule { RemuxError = "Conversion failed: the fake ffmpeg was told to fail" });
        var release = WriteRelease(scenario, mainFilm: FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"]), padding: 2 * 1024 * 1024));

        await scenario.PostHandoffAsync("partial-2", release);

        await scenario.WaitForHandoffStateAsync("partial-2", "failed");
        var report = Assert.Single(await scenario.WaitForReportsAsync(fake, "partial-2"));
        Assert.Equal("failed", (string)report["status"]!);
        Assert.Empty(report["outputFiles"]!.AsArray());

        var imported = await scenario.PostOutcomeAsync("partial-2", "imported", ImportedPath);

        Assert.Equal((HttpStatusCode.Conflict, "handoff_ended"), (imported.Status, (string)imported.Fields["code"]!));
        Assert.Equal("This hand-off ended failed, so Weir handed back no file to import.", (string)imported.Fields["detail"]!);
    }

    private static string WriteRelease(Scenario scenario, byte[] mainFilm)
    {
        scenario.WriteRelease(Release, Main, mainFilm);
        scenario.WriteRelease(Release, Extra, FakeMedia.Bytes(FakeMedia.Probe()));
        return Path.Combine(scenario.Folders.Watched, Release);
    }
}
