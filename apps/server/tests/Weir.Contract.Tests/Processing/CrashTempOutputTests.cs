using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// A crash mid-remux must not leave the half-written temp output in the library's work folder. The fake ffmpeg writes a
/// partial output into the work folder before its delay, exactly where Weir's remux temp file lives
/// (<c>{stem}.processing.XXXXXXXX{suffix}</c>), so killing the server during that delay reproduces the orphan. Startup recovery
/// removes Weir's own temp names for interrupted jobs and leaves every other file in the work folder alone.
/// </summary>
[ContractArea("processing")]
public sealed class CrashTempOutputTests
{
    [Fact]
    public async Task A_crash_mid_remux_leaves_no_temp_output_and_the_job_still_finishes()
    {
        await using var scenario = await Scenario.StartAsync();
        var (fake, _) = await scenario.DelunoSetupAsync();
        scenario.FakeTools.SetFileRule("film.mkv", new FileRule { RemuxDelaySeconds = 120 });
        var source = scenario.WriteRelease("Crash.Temp.534", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        var operatorNote = Path.Combine(scenario.Folders.Work, "film.processing.notes.txt");
        await File.WriteAllTextAsync(operatorNote, "an operator's own file that happens to look similar");

        await scenario.PostHandoffAsync("handoff-temp-534", source);
        await scenario.WaitWhileWorkedOnAsync(
            () => Task.FromResult(TempOutputs(scenario).Length > 0),
            "the remux to start writing its temp output in the work folder");

        await scenario.Server.StopAsync();
        Assert.True(TempOutputs(scenario).Length > 0, "the kill must leave the half-written temp output behind");
        scenario.FakeTools.SetFileRule("film.mkv");
        await scenario.RestartServerAsync();

        await scenario.WaitForHandoffStateAsync("handoff-temp-534", "completed");
        Assert.True(File.Exists(Path.Combine(scenario.Folders.Output, "Crash.Temp.534", "film.mkv")));
        Assert.True(TempOutputs(scenario).Length == 0, "the interrupted job's temp output must be removed");
        Assert.True(File.Exists(operatorNote), "only Weir's own temp names are ever deleted");
        Assert.Equal(["completed"], Scenario.Callbacks(fake, "handoff-temp-534").Select(report => (string)report["status"]!));
    }

    private static string[] TempOutputs(Scenario scenario) => Directory.GetFiles(scenario.Folders.Work, "film.processing.*.mkv");
}
