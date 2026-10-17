using Weir.Contract.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>
/// A Weir whose Activity holds a file in every state a person meets there: one on hold, one that failed, one finished and imported, one being
/// written (its pass stays under way until <see cref="ProcessingRig.ReleasePass"/>, or for the two minutes the fake ffmpeg waits), and the extras that came with three of them and were left alone for being under the minimum size. Everything arrives the way a
/// media manager hands it over, and the server is read until each file is in its state before the rig is given to the test.
/// </summary>
public static class MixedActivity
{
    public const string Held = "Held Film 2019.mkv";
    public const string Failed = "Failed Film 2020.mkv";
    public const string Finished = "Finished Film 2017.mkv";
    public const string Working = "Working Film 2018.mkv";
    public const string ExtraOfFailed = "Extra C.mkv";
    public const string ExtraOfFinished = "Extra A.mkv";
    public const string ExtraOfWorking = "Extra B.mkv";

    private const int ArrivedBytes = 1_100_000;

    // Deluno is told once about each of the three hand-offs that came to an end: the held file, the failed one and the finished one.
    private const int ReportsExpected = 3;
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(3);

    /// <summary>Every file the rig lists, with the status the server gives it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Statuses = new Dictionary<string, string>
    {
        [Held] = "on_hold",
        [Failed] = "processing_failed",
        [Finished] = "processed",
        [Working] = "processing",
        [ExtraOfFailed] = "skipped",
        [ExtraOfFinished] = "skipped",
        [ExtraOfWorking] = "skipped",
    };

    /// <summary>The rig, with the workflow's minimum size at one megabyte and a failed file kept failed for the person to deal with.</summary>
    public static async Task<ProcessingRig> StartAsync(bool writesGiveUpQuickly = false)
    {
        var rig = await ProcessingRig.StartAsync(minimumFileSizeMb: 1, holdFailures: true, writesGiveUpQuickly: writesGiveUpQuickly);
        try
        {
            await rig.WithEvidenceAsync(async () =>
            {
                await rig.HandOffUnreadableAsync("h-held", Held, ArrivedBytes);
                await WaitForStatusAsync(rig, Held);
                await rig.HandOffReleaseAsync("h-failed", Failed, ExtraOfFailed, failFirstPass: true);
                await WaitForStatusAsync(rig, Failed);
                await rig.HandOffReleaseAsync("h-finished", Finished, ExtraOfFinished);
                rig.ReleasePass(Finished);
                await WaitForStatusAsync(rig, Finished);
                await Poll.UntilAsync(() => Task.FromResult(rig.ReportsSentToDeluno >= ReportsExpected), "Weir to tell Deluno how each hand-off went", Ceiling);
                await rig.ReportImportedAsync("h-finished");
                await rig.HandOffReleaseAsync("h-working", Working, ExtraOfWorking);
                foreach (var file in Statuses.Keys)
                {
                    await WaitForStatusAsync(rig, file);
                }
            });
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    private static async Task WaitForStatusAsync(ProcessingRig rig, string file) =>
        await Poll.UntilAsync(
            async () => (string?)(await rig.ListedFileAsync(file))?["status"] == Statuses[file],
            $"{file} to be {Statuses[file]}",
            Ceiling);
}
