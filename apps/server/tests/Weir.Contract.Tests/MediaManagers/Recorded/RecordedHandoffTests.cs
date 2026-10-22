using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// Deluno's recordings of hand-offs, replayed against a Weir that works the files: each recorded call gets the reply Deluno recorded
/// (any difference fails, naming the recording and the field), and what Weir holds afterwards is what the call implies. A recording is
/// named for the state Deluno meant to reach; where its reply shows a different one, the test follows the reply and says so.
/// </summary>
[ContractArea("media_managers")]
public sealed class RecordedHandoffTests
{
    private const string SourceA = @"C:\Downloads\Completed\Movies\Recorded.Weir.A.mkv";
    private const string SourceQ8 = @"C:\Downloads\Completed\Movies\Recorded.Weir.Q8.mkv";
    private const string SourceR = @"C:\Downloads\Completed\Movies\Recorded.Weir.R.mkv";
    private const string BusySource = @"C:\Downloads\Completed\Movies\Recorded.Weir.Busy.mkv";
    private const string IdQ8 = "0f24ba4d-794e-4f24-9dfb-437fe97f0ff7";
    private const string IdEarlierResent = "0b3372c0-0298-41ce-9f47-b3e81ad40ac1";

    /// <summary>The id of the hand-off the recording's first exchange names, in the call or the path.</summary>
    private static string IdOf(Recording recording) =>
        recording.Exchanges[0].Request.Body?["handoffId"]?.GetValue<string>() ?? recording.Exchanges[0].Request.HandoffIdInPath();

    /// <summary>
    /// #956: when a release is sent again, the new send takes the pass over and is read as completed with the output, never
    /// without one. The rc.13 recordings read no output for it.
    /// </summary>
    private static ExpectedDifference[] ResendIsReadWithItsOutput(RecordedWorld world)
    {
        const string why = "a resent release is answered by the new send as completed with the output, never without one";
        var output = world.Translation.Apply(@"C:\Weir\Ready\Movies\Recorded.Weir.R.mkv");
        return
        [
            new("#956", "$.outputPath", why, JsonValue.Create(output)),
            new("#956", "$.outputFiles", why, new JsonArray(JsonValue.Create(output))),
        ];
    }

    // --- after Weir has finished ---------------------------------------------------------------------

    [Fact]
    public async Task A_single_file_in_the_watched_folder_is_accepted_finished_and_reported_imported_as_recorded()
    {
        await using var world = await RecordedWorld.StartAsync();
        var source = Untouched.Of(world.PutFilm(SourceA, needsWork: true));

        await world.Replay.SendAsync(Recording.Load("handoff-accepted"));
        await world.Scenario.WaitForHandoffStateAsync(world.Translation.Id(IdOf(Recording.Load("handoff-accepted"))), "completed");
        await world.Replay.SendAsync(Recording.Load("status-completed"));
        await world.Replay.SendAsync(Recording.Load("outcome-delivered"));
        await world.Replay.SendAsync(Recording.Load("outcome-already-recorded"));

        // Deluno imported Weir's own copy, so Weir left it where it is; the same report twice is one line in Activity.
        var handback = await world.HandbackAsync("Recorded.Weir.A.mkv");
        Assert.Equal(("imported", "Deluno"), ((string)handback["outcome"]!, (string)handback["outcome_by"]!));
        Assert.Null(handback["released_at"]);
        Assert.True(File.Exists(world.Translation.Apply(@"C:\Weir\Ready\Movies\Recorded.Weir.A.mkv")));
        Assert.Equal(["Deluno imported Recorded.Weir.A.mkv"], await world.ActivityTitlesAsync("processing.handback_outcome"));
        source.AssertStill();
    }

    [Fact]
    public async Task An_import_from_the_watched_folder_releases_Weirs_copy_and_leaves_the_watched_folder_alone()
    {
        await using var world = await RecordedWorld.StartAsync();
        var source = Untouched.Of(world.PutFilm(SourceQ8));
        var id = world.Translation.Id(IdQ8);
        await world.Scenario.PostHandoffAsync(id, world.Translation.Apply(SourceQ8));
        await world.Scenario.WaitForHandoffStateAsync(id, "completed");

        // The recording is named for an outcome sent before Weir finished, but its reply is the 200 of a hand-off that had.
        await world.Replay.SendAsync(Recording.Load("outcome-before-finished-200"));
        await world.Replay.SendAsync(Recording.Load("handoff-cancel-refused"), 0);
        await world.Replay.SendAsync(Recording.Load("handoff-cancel-refused"), 1);
        await world.Replay.SendAsync(Recording.Load("outcome-outcome-already-recorded"));

        var handback = await world.HandbackAsync("Recorded.Weir.Q8.mkv");
        Assert.Equal(("imported", "Deluno"), ((string)handback["outcome"]!, (string)handback["outcome_by"]!));
        Assert.Equal("Weir removed its copy from the hand-back folder, because Deluno has the file now.", (string)handback["release_note"]!);
        Assert.Null(handback["outcome_reason"]);
        Assert.False(File.Exists(world.Translation.Apply(@"C:\Weir\Ready\Movies\Recorded.Weir.Q8.mkv")));
        Assert.Equal(["Deluno imported Recorded.Weir.Q8.mkv"], await world.ActivityTitlesAsync("processing.handback_outcome"));
        Assert.Empty(await world.ActivityTitlesAsync("processing.handoff_cancelled"));
        source.AssertStill();
    }

    [Fact]
    public async Task A_resent_release_after_the_first_send_finished_is_accepted_and_answered_with_the_output()
    {
        await using var world = await RecordedWorld.StartAsync();
        var source = Untouched.Of(world.PutFilm(SourceR, needsWork: true));
        const string first = "first-send-of-the-release";
        await world.Scenario.PostHandoffAsync(first, world.Translation.Apply(SourceR));
        await world.Scenario.WaitForHandoffStateAsync(first, "completed");
        var resend = Recording.Load("handoff-resend-accepted");

        await world.Replay.SendAsync(resend);
        await world.Scenario.WaitForHandoffStateAsync(world.Translation.Id(IdOf(resend)), "completed");
        await world.Replay.SendAsync(Recording.Load("resend-status-completed"));

        var output = world.Translation.Apply(@"C:\Weir\Ready\Movies\Recorded.Weir.R.mkv");
        Assert.True(File.Exists(output), "the resent release's output is in the output folder");
        source.AssertStill();
    }

    [Fact]
    public async Task A_resent_release_after_the_first_send_finished_is_imported_as_the_first_session_recorded()
    {
        await using var world = await RecordedWorld.StartAsync(RecordedWorld.EarlierMoviesKey);
        var source = Untouched.Of(world.PutFilm(SourceR));
        const string first = "first-send-of-the-release";
        await world.Scenario.PostHandoffAsync(first, world.Translation.Apply(SourceR));
        await world.Scenario.WaitForHandoffStateAsync(first, "completed");

        await world.Replay.SendAsync(Recording.LoadEarlier("handoff-resend-accepted"));
        await world.Scenario.WaitForHandoffStateAsync(world.Translation.Id(IdEarlierResent), "completed");
        var pins = ResendIsReadWithItsOutput(world);
        await world.Replay.SendAsync(Recording.LoadEarlier("resend-status-completed"), 0, pins);
        await world.Replay.SendAsync(Recording.LoadEarlier("completed-without-output-path"), 0, pins);
        await world.Replay.SendAsync(Recording.Load("outcome-delivered-without-output-path"));

        var handback = await world.HandbackAsync("Recorded.Weir.R.mkv");
        Assert.Equal(("imported", "Deluno"), ((string)handback["outcome"]!, (string)handback["outcome_by"]!));
        Assert.False(File.Exists(world.Translation.Apply(@"C:\Weir\Ready\Movies\Recorded.Weir.R.mkv")));
        source.AssertStill();
    }

    // --- while Weir is working -----------------------------------------------------------------------

    [Fact]
    public async Task A_resent_release_while_the_first_send_is_still_in_Weir_is_accepted_and_takes_it_over()
    {
        await using var world = await RecordedWorld.StartAsync();
        var release = world.HoldEveryPass();
        var source = Untouched.Of(world.PutFilm(SourceR, needsWork: true));
        const string first = "first-send-of-the-release";
        await world.Scenario.PostHandoffAsync(first, world.Translation.Apply(SourceR));
        await world.WaitUntilWorkingAsync(first);

        var resend = Recording.Load("handoff-resend-accepted");
        await world.Replay.SendAsync(resend);
        await File.WriteAllTextAsync(release, string.Empty);

        var resent = await world.Scenario.WaitForHandoffStateAsync(world.Translation.Id(IdOf(resend)), "completed");
        var output = world.Translation.Apply(@"C:\Weir\Ready\Movies\Recorded.Weir.R.mkv");
        Assert.Equal(output, (string)resent["outputPath"]!);
        Assert.Equal([output], resent["outputFiles"]!.AsArray().Select(file => (string)file!).ToArray());
        Assert.True(File.Exists((string)resent["outputPath"]!));

        // #956: the send that was replaced is closed with its own last answer, and is never read as completed without an output.
        var replaced = await world.Scenario.HandoffStatusAsync(first);
        Assert.Equal("cancelled", (string)replaced["state"]!);
        Assert.Null(replaced["outputPath"]);
        Assert.Null(replaced["outputFiles"]);
        source.AssertStill();
    }

    [Fact]
    public async Task A_hand_off_for_a_file_not_yet_in_the_watched_folder_is_accepted_and_reads_working()
    {
        await using var world = await RecordedWorld.StartAsync();
        var missing = world.Translation.Apply(@"C:\Downloads\Completed\Movies\Recorded.Weir.D.mkv");

        await world.Replay.SendAsync(Recording.Load("handoff-accepted-missing-file"));
        await world.Scenario.WaitForHandoffStateAsync(world.Translation.Id(IdOf(Recording.Load("handoff-accepted-missing-file"))), "working");
        await world.Replay.SendAsync(Recording.Load("missing-file-status-working"));

        Assert.False(File.Exists(missing), "Weir wrote nothing where the file should have been");
        Assert.Empty(Directory.EnumerateFileSystemEntries(world.Translation.Apply(@"C:\Weir\Ready\Movies")));
    }

    // --- before Weir starts --------------------------------------------------------------------------

    [Fact]
    public async Task A_hand_off_waiting_for_its_turn_is_cancelled_with_a_204_and_never_worked()
    {
        await using var world = await RecordedWorld.StartAsync();
        var release = world.HoldEveryPass();
        world.PutFilm(BusySource, needsWork: true);
        var source = Untouched.Of(world.PutFilm(SourceQ8, needsWork: true));
        const string busy = "the-hand-off-ahead";
        await world.Scenario.PostHandoffAsync(busy, world.Translation.Apply(BusySource));
        await world.WaitUntilWorkingAsync(busy);
        var cancel = Recording.Load("handoff-cancelled");
        var waiting = world.Translation.Id(IdOf(cancel));
        await world.Scenario.PostHandoffAsync(waiting, world.Translation.Apply(SourceQ8));
        Assert.Equal("queued", (string)(await world.Scenario.HandoffStatusAsync(waiting))["state"]!);

        await world.Replay.SendAsync(cancel, 0);
        await File.WriteAllTextAsync(release, string.Empty);
        await world.Replay.SendAsync(cancel, 1);

        Assert.Equal("cancelled", (string)(await world.Scenario.HandoffStatusAsync(waiting))["state"]!);
        Assert.Equal("cancelled", await RemuxJobs.StatusForHandoffAsync(world.Scenario.Admin, waiting));
        Assert.Equal(["Deluno cancelled its hand-off of Recorded.Weir.Q8.mkv"], await world.ActivityTitlesAsync("processing.handoff_cancelled"));
        source.AssertStill();
    }
}
