using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Xunit.Sdk;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// The replay of Deluno's own side is only worth its word if a recording that stops matching fails, and says which file and which JSON
/// path. These serve or send a recorded body with one thing changed and hold the failure a developer would see.
/// </summary>
[ContractArea("media_managers")]
public sealed class DelunoRecordingComparisonTests
{
    private const string Folder = "apps/server/tests/fixtures/recorded/deluno-side/1.0.0-rc.30.30/";

    // --- what Weir sends ------------------------------------------------------------------------------

    [Fact]
    public void A_report_that_is_the_recorded_one_passes_whatever_its_sentence_says()
    {
        var recording = DelunoSide.Request("events-failed-source-gone");
        var sent = Recorded(recording);
        sent["message"] = "Another sentence entirely.";

        EmittedReport.AssertMatches(recording, 0, sent);
    }

    [Fact]
    public void A_changed_enum_word_fails_naming_the_recording_file_and_the_member()
    {
        var recording = DelunoSide.Request("events-failed-source-gone");
        var sent = Recorded(recording);
        sent["failureClass"] = "sourcegone";

        var failure = Assert.Throws<XunitException>(() => EmittedReport.AssertMatches(recording, 0, sent));

        Assert.Contains($"{Folder}events-failed-source-gone.json, exchange 0: POST /api/integrations/processors/events", failure.Message, StringComparison.Ordinal);
        Assert.Contains("$.failureClass: recorded \"source_gone\", Weir sent \"sourcegone\"", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_of_another_type_fails_naming_both_types()
    {
        var recording = DelunoSide.Request("events-failed-under-minimum");
        var sent = Recorded(recording);
        sent["sourceRemoved"] = "false";

        var failure = Assert.Throws<XunitException>(() => EmittedReport.AssertMatches(recording, 0, sent));

        Assert.Contains("$.sourceRemoved: recorded false (True), Weir sent \"false\" (String)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_that_is_missing_and_one_that_is_new_are_both_named()
    {
        var recording = DelunoSide.Request("events-completed");
        var sent = Recorded(recording);
        sent.Remove("processorName");
        sent["disposition"] = "held";

        var failure = Assert.Throws<XunitException>(() => EmittedReport.AssertMatches(recording, 0, sent));

        Assert.Contains("$.processorName: recorded as \"Weir\", but Weir sent no such member", failure.Message, StringComparison.Ordinal);
        Assert.Contains("$.disposition: Weir sent \"held\", which the recording does not have", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Members_in_another_order_fail_with_both_orders()
    {
        var recording = DelunoSide.Request("events-completed");
        var recorded = Recorded(recording);
        var sent = new JsonObject();
        foreach (var name in new[] { "status", "handoffId" }.Concat(recorded.Select(member => member.Key).Where(name => name is not ("status" or "handoffId"))))
        {
            sent[name] = recorded[name]!.DeepClone();
        }

        var failure = Assert.Throws<XunitException>(() => EmittedReport.AssertMatches(recording, 0, sent));

        Assert.Contains("$: the recording has the members in the order [handoffId, status, processorName,", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Weir sent them in the order [status, handoffId, processorName,", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_changed_output_path_and_a_changed_list_item_are_named_by_their_position()
    {
        var recording = DelunoSide.Request("events-completed");
        var sent = Recorded(recording);
        sent["outputPath"] = "C:\\Elsewhere\\film.mkv";
        sent["outputFiles"]![0] = "C:\\Elsewhere\\film.mkv";

        var failure = Assert.Throws<XunitException>(() => EmittedReport.AssertMatches(recording, 0, sent));

        Assert.Contains("$.outputPath: recorded", failure.Message, StringComparison.Ordinal);
        Assert.Contains("$.outputFiles[0]: recorded", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_recorded_in_Compose_that_stops_matching_fails_naming_its_file_and_the_member()
    {
        var recording = RealStackSide.Load("real-rejected-audio-only");
        var report = RealStackSide.ReportIndex(recording, "failed");
        var sent = recording.Exchanges[report].Request.Body!.DeepClone().AsObject();
        sent["disposition"] = "held";

        var failure = Assert.Throws<XunitException>(() => EmittedReport.AssertMatches(recording, report, sent));

        Assert.Contains($"apps/server/tests/fixtures/recorded/deluno-side/{Recording.DelunoSideRealStackVersion}/real-rejected-audio-only.json, exchange {report}: POST /api/integrations/processors/events", failure.Message, StringComparison.Ordinal);
        Assert.Contains("$.disposition: recorded \"rejected\", Weir sent \"held\"", failure.Message, StringComparison.Ordinal);
    }

    // --- what Weir answers ----------------------------------------------------------------------------

    [Fact]
    public async Task A_reply_that_lost_a_capability_fails_naming_the_recording_file_and_the_list_position()
    {
        var recording = DelunoSide.Request("capabilities-ask");
        var capabilities = recording.Only.Reply.Body!["capabilities"]!.AsArray();
        using var fake = FakeManager.StartDeluno();
        var body = recording.Only.Reply.Body.DeepClone().AsObject();
        body["capabilities"]!.AsArray().RemoveAt(capabilities.Count - 1);
        fake.Route("GET", recording.Only.Request.Path, body);
        var replay = new Replay(() => new WeirClient(new Uri(fake.BaseUrl)), "unused", new Translation());

        var failure = await Assert.ThrowsAsync<XunitException>(() => replay.SendAsync(recording));

        Assert.Contains($"{Folder}capabilities-ask.json, exchange 0: GET /api/v1/intake/capabilities", failure.Message, StringComparison.Ordinal);
        Assert.Contains("reply.body.capabilities[4]: recorded as \"library-folders\", but Weir's reply has no such field", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The body of the recording's only report, as Weir would send it.</summary>
    private static JsonObject Recorded(Recording recording) => recording.Exchanges[0].Request.Body!.DeepClone().AsObject();
}
