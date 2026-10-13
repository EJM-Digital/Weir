using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Xunit.Sdk;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// The replay is only worth its word if a reply that no longer matches fails, and says which recording and which field. These
/// tests serve a recorded reply with one thing changed from a fake and replay it, so the failure asserted is the one a developer sees.
/// </summary>
[ContractArea("media_managers")]
public sealed class RecordedComparisonTests
{
    private const string Status = "status-completed";
    private const string File = "apps/server/tests/fixtures/recorded/deluno/1.0.0-rc.13/status-completed.json";

    private static ExpectedDifference InventedPin => new("#9999", "$.state", "an invented change for this test", JsonValue.Create("working"));

    [Fact]
    public async Task The_recorded_reply_itself_passes()
    {
        using var served = Serve(Status, _ => { });

        await served.Replay.SendAsync(served.Recording);
    }

    [Fact]
    public async Task A_changed_field_fails_naming_the_recording_file_and_the_field()
    {
        using var served = Serve(Status, body => body["state"] = "working");

        var failure = await Assert.ThrowsAsync<XunitException>(() => served.Replay.SendAsync(served.Recording));

        Assert.Contains(File, failure.Message, StringComparison.Ordinal);
        Assert.Contains("reply.body.state: recorded \"completed\", Weir answered \"working\"", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_changed_item_in_a_list_is_named_by_its_position()
    {
        using var served = Serve(Status, body => body["outputFiles"]![0] = "elsewhere.mkv");

        var failure = await Assert.ThrowsAsync<XunitException>(() => served.Replay.SendAsync(served.Recording));

        Assert.Contains("reply.body.outputFiles[0]: recorded", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Weir answered \"elsewhere.mkv\"", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_field_Weir_stopped_sending_and_one_it_started_sending_are_both_named()
    {
        using var served = Serve(Status, body =>
        {
            body.Remove("message");
            body["disposition"] = "kept";
        });

        var failure = await Assert.ThrowsAsync<XunitException>(() => served.Replay.SendAsync(served.Recording));

        Assert.Contains("reply.body.message: recorded", failure.Message, StringComparison.Ordinal);
        Assert.Contains("but Weir's reply has no such field", failure.Message, StringComparison.Ordinal);
        Assert.Contains("reply.body.disposition: Weir's reply has \"kept\", which the recording does not have", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_field_that_changes_each_time_is_held_to_its_type_not_its_value()
    {
        using var different = Serve(Status, body => body["lastChangedUtc"] = "2030-01-01T00:00:00.5Z");
        await different.Replay.SendAsync(different.Recording);

        using var wrong = Serve(Status, body => body["lastChangedUtc"] = "yesterday");
        var failure = await Assert.ThrowsAsync<XunitException>(() => wrong.Replay.SendAsync(wrong.Recording));

        Assert.Contains("reply.body.lastChangedUtc: it changes each time, but it must be a UTC time", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_different_status_fails_before_the_body_is_compared()
    {
        using var served = Serve(Status, _ => { }, status: 500);

        var failure = await Assert.ThrowsAsync<XunitException>(() => served.Replay.SendAsync(served.Recording));

        Assert.Contains("reply.status: recorded 200, Weir answered 500", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_difference_pinned_to_an_issue_passes_only_while_Weir_answers_the_pinned_value()
    {
        using var pinned = Serve(Status, body => body["state"] = "working");
        await pinned.Replay.SendAsync(pinned.Recording, InventedPin);

        using var other = Serve(Status, body => body["state"] = "failed");
        var wrong = await Assert.ThrowsAsync<XunitException>(() => other.Replay.SendAsync(other.Recording, InventedPin));
        Assert.Contains(
            "reply.body.state: recorded \"completed\"; #9999 (an invented change for this test) makes it \"working\"; Weir answered \"failed\"",
            wrong.Message,
            StringComparison.Ordinal);

        using var recorded = Serve(Status, _ => { });
        var stale = await Assert.ThrowsAsync<XunitException>(() => recorded.Replay.SendAsync(recorded.Recording, InventedPin));
        Assert.Contains("the difference pinned for #9999", stale.Message, StringComparison.Ordinal);
        Assert.Contains("is stale and must be removed", stale.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pin_does_not_excuse_any_other_field()
    {
        using var served = Serve(Status, body =>
        {
            body["state"] = "working";
            body["queuePosition"] = 3;
        });

        var failure = await Assert.ThrowsAsync<XunitException>(() => served.Replay.SendAsync(served.Recording, InventedPin));

        Assert.Contains("reply.body.queuePosition: recorded null, Weir answered 3", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("reply.body.state", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A fake that answers the recording's request with its recorded body, changed by <paramref name="change"/>.</summary>
    private static Served Serve(string state, Action<JsonObject> change, int? status = null)
    {
        var recording = Recording.Load(state);
        var translation = new Translation();
        var fake = FakeManager.StartDeluno();
        var body = translation.Apply(recording.Only.Reply.Body)!.AsObject();
        change(body);
        fake.Route(recording.Only.Request.Method, translation.Apply(recording.Only.Request.Path), body, status ?? recording.Only.Reply.Status);
        return new Served(recording, fake, new Replay(() => new WeirClient(new Uri(fake.BaseUrl)), "unused", translation));
    }

    private sealed class Served(Recording recording, FakeManager fake, Replay replay) : IDisposable
    {
        public Recording Recording => recording;

        public Replay Replay => replay;

        public void Dispose() => fake.Dispose();
    }
}
