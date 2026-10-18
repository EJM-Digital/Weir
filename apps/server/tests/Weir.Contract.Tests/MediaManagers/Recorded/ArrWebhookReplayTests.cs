using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Tests.Shared;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// The webhooks a real Sonarr and a real Radarr post (<c>fixtures/recorded/sonarr/*/webhook-*.json</c>, recorded by
/// <c>apps/server/tools/Weir.ArrRecorder</c>), posted to the running Weir's intake exactly as the app sent them. The app's request is the
/// recorded one, body and path; Weir's answer is Weir's own.
/// </summary>
[ContractArea("media_managers")]
public sealed class ArrWebhookReplayTests(IntakeFixture fixture) : IClassFixture<IntakeFixture>
{
    public static TheoryData<string, string> RecordedEvents
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var app in new[] { "sonarr", "radarr" })
            {
                foreach (var state in ArrRecording.States(app, "webhook-"))
                {
                    data.Add(app, state);
                }
            }

            return data;
        }
    }

    private WeirServer Server => fixture.Server;

    [Theory]
    [MemberData(nameof(RecordedEvents))]
    public async Task An_event_the_app_posts_is_answered_by_the_route_the_app_was_told_to_use_and_changes_nothing(string app, string state)
    {
        var recording = ArrRecording.Load(app, state);
        var posted = recording.Only.Request;
        var eventType = (string)posted.Body!["eventType"]!;
        using var admin = await Server.CreateAdminClientAsync();
        var jobsBefore = (await RemuxJobs.ListAsync(admin)).Count;

        var response = await admin.PostAsync(posted.Path, posted.Body);

        Assert.Equal("POST", posted.Method);
        Assert.Equal($"/api/v1/intake/webhook/{app}", posted.Path);
        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.Equal(app, (string)response.Fields["source"]!);
        Assert.Equal("ignored", (string)response.Fields["status"]!);
        if (eventType == "Download")
        {
            Assert.Equal("imported", (string)response.Fields["event"]!);
        }
        else
        {
            Assert.False(response.Fields.ContainsKey("event"), response.ToString());
        }

        Assert.Equal(jobsBefore, (await RemuxJobs.ListAsync(admin)).Count);
    }

    [Theory]
    [InlineData("sonarr")]
    [InlineData("radarr")]
    public void The_recordings_hold_the_test_the_grab_and_the_import_each_as_the_app_sent_it(string app)
    {
        var states = ArrRecording.States(app, "webhook-");

        Assert.Contains("webhook-test", states);
        Assert.Contains("webhook-grab", states);
        Assert.Contains("webhook-download", states);
        foreach (var state in states)
        {
            var recorded = ArrRecording.Load(app, state).Only;
            Assert.Equal((int)HttpStatusCode.OK, recorded.Reply.Status);
            Assert.StartsWith(char.ToUpperInvariant(app[0]).ToString(), recorded.Request.Headers["user-agent"], StringComparison.Ordinal);
            Assert.Equal("application/json", recorded.Request.Headers["content-type"]);
            Assert.IsType<JsonObject>(recorded.Request.Body);
        }
    }
}
