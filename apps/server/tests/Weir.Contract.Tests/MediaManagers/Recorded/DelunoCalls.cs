using System.Text.Json.Nodes;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// The calls Deluno makes about a hand-off, in the shape Deluno recorded sending them, for a hand-off, a source or a body the recordings
/// do not hold: the recorded call with its path and, for an outcome, the members the test names, and nothing else changed.
/// </summary>
internal static class DelunoCalls
{
    private const string Handoffs = "/api/v1/intake/handoffs";

    /// <summary>The status poll Deluno sent, for <paramref name="handoffId"/> under <paramref name="source"/>.</summary>
    public static RecordedCall Poll(string handoffId, string source = "deluno") =>
        DelunoSide.Request("status-ask-queued").Exchanges[0].Request with { Path = $"{Handoffs}/{source}/{handoffId}" };

    /// <summary>The cancel Deluno sent when a person stopped waiting, for <paramref name="handoffId"/> under <paramref name="source"/>.</summary>
    public static RecordedCall Cancel(string handoffId, string source = "deluno") =>
        DelunoSide.Request("cancel-queued").Exchanges[0].Request with { Path = $"{Handoffs}/{source}/{handoffId}" };

    /// <summary>
    /// The outcome Deluno sent when it imported a file, for <paramref name="handoffId"/> under <paramref name="source"/>, with the members
    /// <paramref name="change"/> sets or removes.
    /// </summary>
    public static RecordedCall Outcome(string handoffId, Action<JsonObject>? change = null, string source = "deluno")
    {
        var recorded = DelunoSide.Request("outcome-imported").Exchanges[0].Request;
        var body = recorded.Body!.DeepClone().AsObject();
        change?.Invoke(body);
        return recorded with { Path = $"{Handoffs}/{source}/{handoffId}/outcome", Body = body };
    }

    /// <summary>Weir paused, the film in place, and the hand-off made; it waits for its turn.</summary>
    public static async Task<RecordedCall> HandOffQueuedAsync(RecordedWorld world, string handoffId, string release)
    {
        await world.Scenario.SetPauseAsync(paused: true);
        var hand = DelunoSide.HandoffLike(handoffId, release);
        world.PutFilm(hand.SourcePath(), needsWork: true);
        var accepted = await world.Replay.SendCallAsync(hand);
        Assert.True(accepted.Status == System.Net.HttpStatusCode.OK, accepted.ToString());
        Assert.Equal("queued", await StateOfAsync(world, hand.HandoffId()));
        return hand;
    }

    public static async Task<string> StateOfAsync(RecordedWorld world, string handoffId) =>
        (string)(await world.Scenario.HandoffStatusAsync(handoffId))["state"]!;
}
