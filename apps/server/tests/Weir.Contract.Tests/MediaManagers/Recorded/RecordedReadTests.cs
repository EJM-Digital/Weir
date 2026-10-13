using System.Net;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// Deluno's recordings of the calls that change nothing in Weir: what Weir can do, a secret that is missing or wrong, a hand-off
/// Weir never had, a hand-off with nothing to hand over and one for a folder no workflow watches. Each reply is replayed and compared
/// field by field; for the calls that are refused or ignored, Weir afterwards holds no trace of the hand-off.
/// </summary>
[ContractArea("media_managers")]
public sealed class RecordedReadTests(RecordedWorldFixture fixture) : IClassFixture<RecordedWorldFixture>
{
    private RecordedWorld World => fixture.World;

    [Fact]
    public async Task The_capabilities_are_what_Deluno_recorded() =>
        await World.Replay.SendAsync(Recording.Load("capabilities"));

    [Fact]
    public async Task The_capabilities_are_refused_without_the_secret_as_recorded() =>
        await World.Replay.SendAsync(Recording.Load("capabilities-no-secret"), Credential.None);

    [Fact]
    public async Task The_capabilities_are_refused_with_a_wrong_secret_as_recorded() =>
        await World.Replay.SendAsync(Recording.Load("capabilities-wrong-secret"), Credential.Wrong);

    [Fact]
    public async Task A_status_poll_and_an_outcome_for_a_hand_off_Weir_never_had_are_both_404_as_recorded()
    {
        var recording = Recording.Load("no-record");

        await World.Replay.SendAsync(recording, 0);
        await World.Replay.SendAsync(recording, 1);

        await AssertNoTraceAsync("9c911dd4-098c-4cd4-902c-4995b2aaf5b7");
    }

    [Fact]
    public async Task A_hand_off_with_no_source_is_ignored_as_recorded()
    {
        await World.Replay.SendAsync(Recording.Load("handoff-ignored-no-source"));

        await AssertNoTraceAsync("24b945f9-33d8-4c2e-a1fb-8af9ba22361c");
    }

    [Fact]
    public async Task A_hand_off_with_a_wrong_secret_is_refused_as_recorded()
    {
        await World.Replay.SendAsync(Recording.Load("handoff-refused-wrong-secret"), Credential.Wrong);

        await AssertNoTraceAsync("5f6060a3-79fa-46cc-887a-1995972de4de");
    }

    [Fact]
    public async Task A_hand_off_for_a_folder_no_workflow_watches_is_refused_as_recorded_naming_the_workflows()
    {
        await World.Replay.SendAsync(Recording.Load("handoff-refused-not-watched"));

        await AssertNoTraceAsync("dc5b76c8-fc1c-4826-b748-9dd041534d3e");
    }

    /// <summary>Weir does not know the hand-off, and has no job for it.</summary>
    private async Task AssertNoTraceAsync(string recordedId)
    {
        var id = World.Translation.Id(recordedId);
        using var deluno = World.Scenario.Server.CreateClient();
        var status = await deluno.GetAsync(
            $"{WeirClient.Api}/intake/handoffs/deluno/{id}", new Dictionary<string, string> { ["X-Webhook-Secret"] = Processing.Scenario.WebhookSecret });
        Assert.Equal(HttpStatusCode.NotFound, status.Status);
        Assert.Empty(await RemuxJobs.ForHandoffAsync(World.Scenario.Admin, id));
    }
}
