using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// A file held as gone that comes back starts the look waiting at it at once, so an idle file slot is woken to take it and the
/// look does not wait for the slots' next poll. A look that is not started wakes nobody. The clock does not move in these tests.
/// </summary>
public sealed partial class HeldFileReturnTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_sweep_finding_a_held_file_back_wakes_the_file_slots(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await HandOffAsync("first");
        await RunPassWhileGoneAsync();
        Advance(TimeSpan.FromMinutes(2));
        BringBack();
        var wake = _fixture.WakeSignals.NextWake(WorkLane.Files, 0);

        await SweepAsync();

        Assert.True(wake.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_sweep_finding_a_held_file_still_away_wakes_nobody(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await HandOffAsync("first");
        await RunPassWhileGoneAsync();
        Advance(TimeSpan.FromMinutes(2));
        var wake = _fixture.WakeSignals.NextWake(WorkLane.Files, 0);

        await SweepAsync();

        Assert.False(wake.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_resend_for_a_file_that_is_back_wakes_the_file_slots_for_the_held_pass_it_takes_over(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await _fixture.Store.Execute($"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({_libraryId}, '{Relative}', 'unprocessed', '')");
        var payload = new WireObject().Set("relative_media_path", Relative).Set("library_id", _libraryId).Set("media_scope", "movie").Set("trigger", "manual");
        await _fixture.Jobs.EnqueueOrGetAsync("scan-queued", RemuxPassOutcomes.JobKind, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));
        await RunPassWhileGoneAsync();
        Advance(TimeSpan.FromSeconds(30));
        BringBack();
        var wake = _fixture.WakeSignals.NextWake(WorkLane.Files, 0);

        await HandOffAsync("second");

        Assert.True(wake.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_that_is_back_by_the_time_the_pass_books_its_look_wakes_the_file_slots(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await HandOffAsync("first");
        TakeAway();
        // The file comes back right after the look that finds it gone, before the pass books the look it waits for.
        _goneConfirmed = () =>
        {
            BringBack();
            return Task.CompletedTask;
        };
        var wake = _fixture.WakeSignals.NextWake(WorkLane.Files, 0);

        await DrainAsync();

        Assert.True(wake.IsCompletedSuccessfully);
    }
}
