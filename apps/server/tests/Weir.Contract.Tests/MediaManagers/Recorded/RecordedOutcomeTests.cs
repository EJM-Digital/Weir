using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.MediaManagers.Recorded.DelunoCalls;
using static Weir.Contract.Tests.MediaManagers.Recorded.DelunoReportSteps;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// What Weir answers to the outcomes no real Deluno has been seen to send, or to hear refused: one that comes before Weir has finished, one
/// for a hand-off that ended, one with a wrong secret or a body Deluno never sends, an import that carries a note, and a refusal to import
/// that gives no reason. Deluno's own recorded outcome is sent, for a hand-off the recordings do not hold, with only the members the test
/// names changed, and Weir's answers are held to the codes and words agreed in <c>docs/deluno-weir-contract.md</c>. Where Deluno recorded
/// the words of Weir's 200, the answer is held to them.
/// </summary>
[ContractArea("media_managers")]
public sealed class RecordedOutcomeTests
{
    private const string Release = "Gprecoutcome.2020.1080p.WEB-DL.x264-GOLDEN.mkv";

    private static async Task<(string Code, string Detail)> RefusedAsync(RecordedWorld world, RecordedCall outcome)
    {
        var answer = await world.Replay.SendCallAsync(outcome);
        Assert.True(answer.Status == HttpStatusCode.Conflict, answer.ToString());
        return ((string)answer.Fields["code"]!, (string)answer.Fields["detail"]!);
    }

    private static async Task<RecordedCall> FinishedAsync(RecordedWorld world, string handoffId)
    {
        var hand = DelunoSide.HandoffLike(handoffId, Release);
        world.PutFilm(hand.SourcePath(), needsWork: true);
        await HandOffAsync(world, hand);
        await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "completed");
        return hand;
    }

    [Fact]
    public async Task An_outcome_sent_before_Weir_has_finished_is_a_409_to_send_again_whether_the_hand_off_is_queued_or_working()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var release = world.HoldEveryPass();
        var hand = await HandOffQueuedAsync(world, "an-outcome-too-early", Release);
        var outcome = Outcome(hand.HandoffId());

        Assert.Equal(
            ("handoff_not_finished", "Weir has not finished this hand-off yet (it is queued), so there is no file to import."),
            await RefusedAsync(world, outcome));

        await world.Scenario.SetPauseAsync(paused: false);
        await world.WaitUntilWorkingAsync(hand.HandoffId());
        Assert.Equal(
            ("handoff_not_finished", "Weir has not finished this hand-off yet (it is working), so there is no file to import."),
            await RefusedAsync(world, outcome));

        await File.WriteAllTextAsync(release, string.Empty);
        await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "completed");
        Assert.Equal(HttpStatusCode.OK, (await world.Replay.SendCallAsync(outcome)).Status);
    }

    [Fact]
    public async Task An_outcome_for_a_hand_off_that_was_cancelled_or_failed_is_a_409_that_says_it_ended_and_changes_nothing()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        await world.Scenario.UpdateLibraryAsync(world.Movies, ("min_file_size_mb", 50));
        var stopped = await HandOffQueuedAsync(world, "an-outcome-after-a-cancel", Release);
        Assert.Equal(HttpStatusCode.NoContent, (await world.Replay.SendCallAsync(Cancel(stopped.HandoffId()))).Status);
        var failed = DelunoSide.HandoffLike("an-outcome-after-a-failure", "Gprecoutcomefailed.2020.1080p.WEB-DL.x264-GOLDEN.mkv");
        world.PutFilm(failed.SourcePath(), needsWork: true);
        await HandOffAsync(world, failed);
        await world.Scenario.SetPauseAsync(paused: false);
        await world.Scenario.WaitForHandoffStateAsync(failed.HandoffId(), "failed");

        Assert.Equal(
            ("handoff_ended", "This hand-off ended cancelled, so Weir handed back no file to import."),
            await RefusedAsync(world, Outcome(stopped.HandoffId())));
        Assert.Equal(
            ("handoff_ended", "This hand-off ended failed, so Weir handed back no file to import."),
            await RefusedAsync(world, Outcome(failed.HandoffId())));

        Assert.Equal("cancelled", await StateOfAsync(world, stopped.HandoffId()));
        Assert.Equal("failed", await StateOfAsync(world, failed.HandoffId()));
        Assert.Empty(await world.ActivityTitlesAsync("processing.handback_outcome"));
    }

    [Fact]
    public async Task An_outcome_with_a_wrong_secret_or_none_is_a_401_in_the_words_Deluno_recorded_for_a_hand_off_and_records_nothing()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var hand = await FinishedAsync(world, "an-outcome-without-the-secret");
        var refusal = (string)Recording.Load("handoff-refused-wrong-secret").Only.Reply.Body!["detail"]!;

        foreach (var credential in new[] { Credential.Wrong, Credential.None })
        {
            var answer = await world.Replay.SendCallAsync(Outcome(hand.HandoffId()), credential);

            Assert.True(answer.Status == HttpStatusCode.Unauthorized, $"{credential}: {answer}");
            Assert.Equal(refusal, (string)answer.Fields["detail"]!);
        }

        Assert.Empty(await world.ActivityTitlesAsync("processing.handback_outcome"));
    }

    [Fact]
    public async Task An_outcome_body_Deluno_never_sends_is_refused_422_with_a_list_of_what_is_wrong_or_415_with_a_sentence_before_the_secret_is_looked_at()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var hand = await FinishedAsync(world, "an-outcome-that-is-malformed");
        var path = Outcome(hand.HandoffId()).Path;
        using var deluno = world.Scenario.Server.CreateClient();
        var noSecret = new Dictionary<string, string>();

        foreach (var (name, change) in new (string, Action<JsonObject>)[]
        {
            ("an unknown outcome word", body => body["outcome"] = "maybe"),
            ("no outcome", body => body.Remove("outcome")),
        })
        {
            var answer = await world.Replay.SendCallAsync(Outcome(hand.HandoffId(), change), Credential.None);

            Assert.True(answer.Status == HttpStatusCode.UnprocessableEntity, $"{name}: {answer}");
            Assert.NotEmpty(Assert.IsType<JsonArray>(answer.Fields["detail"]));
        }

        var notJson = await deluno.PostTextAsync(path, "this is not json", noSecret);
        Assert.True(notJson.Status == HttpStatusCode.UnprocessableEntity, notJson.ToString());
        Assert.NotEmpty(Assert.IsType<JsonArray>(notJson.Fields["detail"]));
        var notDeclaredJson = await deluno.RequestRawAsync(HttpMethod.Post, path, "outcome=imported"u8.ToArray(), "text/plain", noSecret);
        Assert.True(notDeclaredJson.Status == HttpStatusCode.UnsupportedMediaType, notDeclaredJson.ToString());
        Assert.Equal("Send the body as application/json.", (string)notDeclaredJson.Fields["detail"]!);
        Assert.Empty(await world.ActivityTitlesAsync("processing.handback_outcome"));
    }

    [Fact]
    public async Task An_import_that_carries_a_note_is_a_200_with_the_members_Deluno_recorded_and_Weir_keeps_the_note()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var seasonPack = RealStackSide.Load("real-season-pack");
        var note = (string)seasonPack.Exchanges.First(exchange => exchange.Request.Path.EndsWith("/outcome", StringComparison.Ordinal)).Request.Body!["reason"]!;
        var hand = await FinishedAsync(world, "an-import-with-a-note");
        var recorded = DelunoSide.Request("outcome-imported").Exchanges[0].Reply.Body!.AsObject();

        var answer = await world.Replay.SendCallAsync(Outcome(hand.HandoffId(), body => body["reason"] = note));

        Assert.True(answer.Status == HttpStatusCode.OK, answer.ToString());
        Assert.Equal(recorded.Select(member => member.Key).Order(), answer.Fields.Select(member => member.Key).Order());
        Assert.Equal("imported", (string)answer.Fields["outcome"]!);
        var handback = await world.HandbackAsync(Release);
        Assert.Equal(("imported", "Deluno", note), ((string)handback["outcome"]!, (string)handback["outcome_by"]!, (string)handback["outcome_reason"]!));
    }

    [Fact]
    public async Task A_refusal_to_import_with_no_reason_is_a_200_in_the_words_Deluno_recorded_and_Weir_keeps_its_copy()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var hand = await FinishedAsync(world, "a-refusal-with-no-reason");
        var recorded = DelunoSide.Request("outcome-not-imported-removed").Exchanges[0].Reply.Body!.AsObject();

        var answer = await world.Replay.SendCallAsync(Outcome(hand.HandoffId(), body =>
        {
            body["outcome"] = "not-imported";
            body["importedPath"] = null;
            body["reason"] = null;
        }));

        Assert.True(answer.Status == HttpStatusCode.OK, answer.ToString());
        Assert.Equal((string)recorded["message"]!, (string)answer.Fields["message"]!);
        Assert.False((bool)answer.Fields["released"]!);
        var handback = await world.HandbackAsync(Release);
        Assert.Equal(("not-imported", "Deluno"), ((string)handback["outcome"]!, (string)handback["outcome_by"]!));
        Assert.Null(handback["outcome_reason"]);
        Assert.Equal("Deluno will not import this file. Weir kept its copy in the hand-back folder.", (string)handback["release_note"]!);
        Assert.Equal(["Deluno will not import " + Release], await world.ActivityTitlesAsync("processing.handback_outcome"));
    }
}
