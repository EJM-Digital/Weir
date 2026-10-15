using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// Deluno stopping waiting for a hand-off, replayed with Deluno's own recorded DELETE: Weir drops one it has not started (204, in
/// <c>DelunoRequestsTests</c> and <c>RecordedHandoffTests</c>), refuses one it has finished (409, in <c>RecordedHandoffTests</c>), and
/// here refuses one it is working on, with the same refusal naming the state this hand-off is in. The job is left as it is and the
/// pass carries on to the end.
/// </summary>
[ContractArea("media_managers")]
public sealed class RecordedHandoffCancelTests
{
    private const string Source = @"C:\Downloads\Completed\Movies\Recorded.Weir.Q8.mkv";

    /// <summary>The id of the hand-off the recorded refusal is about; the replay maps it onto the id this test hands over.</summary>
    private const string RecordedId = "0f24ba4d-794e-4f24-9dfb-437fe97f0ff7";

    [Fact]
    public async Task A_hand_off_Weir_is_working_on_is_refused_as_recorded_for_a_finished_one_and_the_pass_carries_on()
    {
        await using var world = await RecordedWorld.StartAsync();
        var release = world.HoldEveryPass();
        var source = Untouched.Of(world.PutFilm(Source, needsWork: true));
        var id = world.Translation.Id(RecordedId);
        await world.Scenario.PostHandoffAsync(id, world.Translation.Apply(Source));
        await world.WaitUntilWorkingAsync(id);

        // Deluno recorded the refusal of a hand-off that had finished: 409, the same member, the state in its sentence.
        await world.Replay.SendAsync(
            Recording.Load("handoff-cancel-refused"),
            0,
            new ExpectedDifference(
                "#1068",
                "$.detail",
                "the recording is of a finished hand-off; the same refusal names the state this one is in",
                JsonValue.Create("This hand-off is working, so Weir did not cancel it.")));

        Assert.Equal("leased", await RemuxJobs.StatusForHandoffAsync(world.Scenario.Admin, id));
        Assert.Equal("working", (string)(await world.Scenario.HandoffStatusAsync(id))["state"]!);
        Assert.Empty(await world.ActivityTitlesAsync("processing.handoff_cancelled"));

        await File.WriteAllTextAsync(release, string.Empty);
        await world.Scenario.WaitForHandoffStateAsync(id, "completed");
        Assert.Equal("completed", await RemuxJobs.StatusForHandoffAsync(world.Scenario.Admin, id));
        Assert.Empty(await world.ActivityTitlesAsync("processing.handoff_cancelled"));
        source.AssertStill();
    }
}
