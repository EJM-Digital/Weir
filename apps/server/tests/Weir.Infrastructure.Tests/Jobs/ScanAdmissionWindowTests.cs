using Weir.Core.Processing;
using Weir.Core.Settings;
using Weir.Core.Time;
using Weir.Infrastructure.Jobs;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>What a scan is told about the library's schedule and the suite-wide pause before it looks at any file.</summary>
public sealed class ScanAdmissionWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    private static ScanAdmissionWindow Window(bool paused, DateTimeOffset? until)
    {
        var suite = new SuiteSettingsRecord
        {
            ProcessingPaused = paused,
            ProcessingPausedUntil = until is { } at ? Timestamp.FromUtc(at.UtcDateTime) : null,
        };
        return ProcessingWatchedFolderScanDispatchJobHandler.AdmissionWindow(new ProcessingLibraryRecord { Name = "Movies" }, suite, Now);
    }

    [Fact]
    public void A_pause_with_an_end_says_when_it_ends_one_without_an_end_does_not_and_a_finished_one_is_not_a_pause()
    {
        var until = Now.AddHours(2);

        var timed = Window(paused: true, until);
        Assert.True(timed.Paused);
        Assert.Equal(until, timed.PauseUntil);

        var open = Window(paused: true, until: null);
        Assert.True(open.Paused);
        Assert.Null(open.PauseUntil);

        var finished = Window(paused: true, Now.AddHours(-1));
        Assert.False(finished.Paused);
        Assert.Null(finished.PauseUntil);

        var notPaused = Window(paused: false, until);
        Assert.False(notPaused.Paused);
        Assert.Null(notPaused.PauseUntil);
    }
}
