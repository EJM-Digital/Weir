using System.Net;
using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.MediaManagers.Recorded.DelunoCalls;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// The answers to Deluno's status poll and its cancel that no real Deluno has been seen to hear: a hand-off that failed, a source Weir does
/// not know, a hand-off Weir never received, and a cancel carrying the Origin header of a browser. Deluno's own recorded poll and cancel
/// are sent, for a hand-off or a source the recordings do not hold, and Weir's answers are held to the members and the words Weir agreed
/// with Deluno (<c>docs/deluno-weir-contract.md</c>). No recording holds these answers: Deluno asks about a hand-off only while it waits
/// for Weir, and Weir tells it of a failure in its report.
/// </summary>
[ContractArea("media_managers")]
public sealed class RecordedStatusAndCancelTests
{
    [Fact]
    public async Task A_hand_off_Weir_failed_reads_failed_in_Weirs_words_with_the_members_Deluno_recorded()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        await world.Scenario.UpdateLibraryAsync(world.Movies, ("min_file_size_mb", 50));
        var hand = DelunoSide.HandoffLike("a-hand-off-weir-fails", "Gprecfailedstatus01.2020.1080p.WEB-DL.x264-GOLDEN.mkv");
        world.PutFilm(hand.SourcePath(), needsWork: true);
        Assert.Equal(HttpStatusCode.OK, (await world.Replay.SendCallAsync(hand)).Status);
        await world.Scenario.WaitForHandoffStateAsync(hand.HandoffId(), "failed");

        var polled = await world.Replay.SendCallAsync(Poll(hand.HandoffId()));

        Assert.True(polled.Status == HttpStatusCode.OK, polled.ToString());
        Assert.Equal("failed", (string)polled.Fields["state"]!);
        Assert.Null(polled.Fields["outputPath"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)polled.Fields["message"]));
        var recorded = Recording.Load("status-completed").Only.Reply.Body!.AsObject();
        Assert.Equal(recorded.Select(member => member.Key).Order(), polled.Fields.Select(member => member.Key).Order());
    }

    [Fact]
    public async Task A_source_Weir_does_not_know_is_a_404_for_the_poll_and_the_cancel_whatever_secret_is_sent()
    {
        await using var world = await DelunoSide.StartWorldAsync();

        foreach (var credential in new[] { Credential.Right, Credential.Wrong, Credential.None })
        {
            foreach (var call in new[] { Poll("a-hand-off", "nowhere"), Cancel("a-hand-off", "nowhere") })
            {
                var answer = await world.Replay.SendCallAsync(call, credential);

                Assert.True(answer.Status == HttpStatusCode.NotFound, $"{call.Method} with {credential}: {answer}");
                Assert.StartsWith("Unknown media manager source 'nowhere'.", (string)answer.Fields["detail"]!, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task A_cancel_for_a_hand_off_Weir_never_received_is_a_404_in_the_words_Deluno_recorded_for_the_poll()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var never = (string)Recording.Load("no-record").Exchanges[0].Reply.Body!["detail"]!;

        var cancelled = await world.Replay.SendCallAsync(Cancel("a-hand-off-weir-never-had"));

        Assert.True(cancelled.Status == HttpStatusCode.NotFound, cancelled.ToString());
        Assert.Equal(never, (string)cancelled.Fields["detail"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await world.Replay.SendCallAsync(Poll("a-hand-off-weir-never-had"))).Status);
    }

    [Fact]
    public async Task A_cancel_that_carries_an_Origin_and_no_X_Requested_With_is_refused_403_and_the_hand_off_stays_queued()
    {
        await using var world = await DelunoSide.StartWorldAsync();
        var hand = await HandOffQueuedAsync(world, "a-hand-off-cancelled-from-a-page", "Gprecoriginstatus01.2020.1080p.WEB-DL.x264-GOLDEN.mkv");
        var cancel = Cancel(hand.HandoffId());
        var fromAPage = cancel with { Headers = new Dictionary<string, string>(cancel.Headers) { ["Origin"] = "http://localhost:5099" } };

        var refused = await world.Replay.SendCallAsync(fromAPage);

        Assert.True(refused.Status == HttpStatusCode.Forbidden, refused.ToString());
        Assert.StartsWith("Missing X-Requested-With header", (string)refused.Fields["detail"]!, StringComparison.Ordinal);
        Assert.Equal("queued", await StateOfAsync(world, hand.HandoffId()));
        Assert.Equal(HttpStatusCode.NoContent, (await world.Replay.SendCallAsync(cancel)).Status);
        Assert.Equal("cancelled", await StateOfAsync(world, hand.HandoffId()));
    }
}
