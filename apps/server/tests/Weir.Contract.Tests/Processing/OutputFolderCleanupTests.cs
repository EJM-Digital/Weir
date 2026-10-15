using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// Per-title output-folder cleanup after a successful pass. "The manager reports no library files inside this folder" is not proof
/// the folder is safe to remove, because that is exactly what a manager that has not scanned or finished importing yet looks like
/// (or one that imports by copy and scans later). The folder is deleted only once a manager has positive evidence of the release,
/// or the hand-off ledger has recorded the outcome as delivered.
/// </summary>
[ContractArea("processing")]
public sealed class OutputFolderCleanupTests
{
    // Comfortably past the movie output-cleanup minimum age, which the server floors at one hour regardless of configuration
    // (WEIR_PROCESSING_MOVIE_OUTPUT_CLEANUP_MIN_AGE_SECONDS is clamped to 3600s..30d). "Pass through unchanged" forces the
    // copy-without-remux path (a Windows hard link, or a copy that preserves metadata), which carries the source's own
    // modification time onto the output, so backdating the source before enqueueing reaches that age immediately instead of
    // making the test wait an hour.
    private static readonly TimeSpan OldEnough = TimeSpan.FromHours(2);

    [Fact]
    public async Task Output_folder_is_kept_when_the_manager_has_not_imported_yet()
    {
        // The default minimum age (48h) would make this test wait that long for real; the setting is clamped to
        // 3600s..30d, so this is as low as it can go.
        await using var scenario = await Scenario.StartAsync(("WEIR_PROCESSING_MOVIE_OUTPUT_CLEANUP_MIN_AGE_SECONDS", "3600"));
        var (radarr, library) = await scenario.RadarrSetupAsync();
        WriteOldRelease(scenario, "Contract.Movie.2024", "Contract.Movie.2024.mkv");

        // The fake Radarr answers (it is reachable and reporting) but its own library listing stays empty: exactly what a
        // manager that has not scanned or finished importing this release yet looks like. radarr.Library is never populated here.
        await scenario.EnqueueFilePassAsync("Contract.Movie.2024/Contract.Movie.2024.mkv", library, passThroughUnchanged: true);
        await WaitForThePassToFinishAsync(scenario);

        Assert.NotEmpty(radarr.RequestsTo("GET", "/api/v3/movie"));
        var outputFolder = Path.Combine(scenario.Folders.Output, "Contract.Movie.2024");
        Assert.True(
            Directory.Exists(outputFolder),
            "the per-title output folder must not be deleted before a manager confirms this release was imported "
            + "(or the hand-off ledger records the outcome as delivered): reporting no *conflicting* files in the folder "
            + "is not the same as reporting this release as imported");
        Assert.True(File.Exists(Path.Combine(outputFolder, "Contract.Movie.2024.mkv")));
    }

    /// <summary>
    /// Same shape as above, but the manager's library now names this release (moved to its own path, as a manager that renames or
    /// reorganises on import would record it): the positive evidence the folder-cleanup gate looks for, so the folder is removed.
    /// </summary>
    [Fact]
    public async Task Output_folder_is_removed_once_the_manager_confirms_the_import()
    {
        await using var scenario = await Scenario.StartAsync(("WEIR_PROCESSING_MOVIE_OUTPUT_CLEANUP_MIN_AGE_SECONDS", "3600"));
        var (radarr, library) = await scenario.RadarrSetupAsync();
        WriteOldRelease(scenario, "Confirmed.Movie.2024", "Confirmed.Movie.2024.mkv");
        radarr.Library.Add(new JsonObject
        {
            ["movieFile"] = new JsonObject { ["path"] = Path.Combine(scenario.Root, "library", "Confirmed.Movie.2024.mkv") },
        });

        await scenario.EnqueueFilePassAsync("Confirmed.Movie.2024/Confirmed.Movie.2024.mkv", library, passThroughUnchanged: true);
        await WaitForThePassToFinishAsync(scenario);

        var outputFolder = Path.Combine(scenario.Folders.Output, "Confirmed.Movie.2024");
        Assert.False(Directory.Exists(outputFolder), "a manager that confirmed the import should let the folder be removed");
    }

    private static string WriteOldRelease(Scenario scenario, string releaseFolder, string fileName)
    {
        var source = scenario.WriteRelease(releaseFolder, fileName, FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng"])));
        var old = DateTime.UtcNow - OldEnough;
        File.SetLastWriteTimeUtc(source, old);
        File.SetLastAccessTimeUtc(source, old);
        return source;
    }

    // A manual enqueue with no prior scan may leave no Files row for GET /processing/files to report on, so this watches the
    // job itself, as the Radarr-queue scenario in FailurePoliciesTests does.
    private static async Task WaitForThePassToFinishAsync(Scenario scenario) =>
        await scenario.WaitWhileWorkedOnAsync(
            async () => (await scenario.JobsAsync(Scenario.RemuxKind)).Any(job => (string)job["status"]! == "completed"),
            "the manual pass to finish");
}
