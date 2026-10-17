using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// A file Weir works on ends, and the alerts a person set up for it really arrive: one post per channel that is switched on and
/// subscribed, none for a channel that is switched off, a Discord embed in the colour of the outcome, and a plain line in the server
/// log, and no second try, when the other end refuses.
/// </summary>
[ContractArea("processing")]
public sealed class AlertOnJobOutcomeTests : IAsyncLifetime, IDisposable
{
    private const string Channels = WeirClient.Api + "/suite/notification-channels";
    private const string Broken = "Conversion failed: the fake ffmpeg was told to fail";
    private const int Green = 0x2ECC71;
    private const int Red = 0xE74C3C;

    private readonly FakeAlertReceiver _receiver = new();
    private Scenario _scenario = null!;
    private JsonObject _library = null!;

    public async Task InitializeAsync()
    {
        _scenario = await Scenario.StartAsync([.. _receiver.Env.Select(setting => (setting.Key, setting.Value))]);
        _library = await _scenario.CreateLibraryAsync(("max_attempts", 1), ("failure_policy", "hold"));
    }

    public async Task DisposeAsync() => await _scenario.DisposeAsync();

    public void Dispose() => _receiver.Dispose();

    private async Task<long> CreateChannelAsync(string provider, string address, params string[] events)
    {
        var created = await _scenario.Admin.PostWithCsrfAsync(
            Channels,
            new JsonObject
            {
                ["label"] = $"Alert {address}",
                ["provider"] = provider,
                ["url"] = $"http://hooks.example.com{address}",
                ["events"] = new JsonArray([.. events.Select(name => (JsonNode)JsonValue.Create(name)!)]),
            });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        return (long)created.Fields["id"]!;
    }

    private async Task SwitchAsync(long id, bool enabled)
    {
        var path = $"{Channels}/{id}";
        var current = (await _scenario.Admin.GetAsync(Channels)).Fields["items"]!.AsArray().Single(item => (long)item!["id"]! == id)!.AsObject();
        var updated = await _scenario.Admin.PutWithCsrfAsync(
            path,
            new JsonObject
            {
                ["label"] = (string?)current["label"],
                ["provider"] = (string?)current["provider"],
                ["url"] = (string?)current["url"],
                ["events"] = current["events"]!.DeepClone(),
                ["enabled"] = enabled,
            });
        Assert.True(updated.Status == HttpStatusCode.OK, updated.ToString());
    }

    /// <summary>A file the fake tools fail on: its pass fails for good, since the workflow allows one attempt.</summary>
    private async Task FailAFileAsync(string name)
    {
        _scenario.FakeTools.SetFileRule($"{name}.mkv", new FileRule { RemuxError = Broken });
        _scenario.WriteRelease(name, $"{name}.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        await _scenario.EnqueueFilePassAsync($"{name}/{name}.mkv", _library);
    }

    /// <summary>A file the fake tools clean without trouble.</summary>
    private async Task FinishAFileAsync(string name)
    {
        _scenario.WriteRelease(name, $"{name}.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        await _scenario.EnqueueFilePassAsync($"{name}/{name}.mkv", _library);
    }

    private async Task<HashSet<long>> PassJobsAsync() =>
        (await _scenario.JobsAsync(Scenario.RemuxKind)).Select(job => (long)job["id"]!).ToHashSet();

    [Fact]
    public async Task A_file_that_fails_for_good_alerts_each_channel_switched_on_and_never_one_switched_off()
    {
        // The channel switched off is made first, so a post wrongly sent to it would reach the receiver before the other channel's.
        var off = await CreateChannelAsync("webhook", "/off", "processing_job_failed");
        await SwitchAsync(off, enabled: false);
        await CreateChannelAsync("webhook", "/on", "processing_job_failed");

        await FailAFileAsync("First.Broken.2022");
        var first = Assert.Single(await _receiver.WaitForPostsAsync("/on", 1)).Json!.AsObject();
        Assert.Empty(_receiver.PostsTo("/off"));
        Assert.Equal("processing_job_failed", (string?)first["event"]);
        Assert.Equal("processing.file.remux_pass.v1", (string?)first["job_kind"]);
        Assert.Contains((long)first["job_id"]!, await PassJobsAsync());
        Assert.Equal($"Job {(long)first["job_id"]!} (processing.file.remux_pass.v1) failed, and Weir has stopped trying.", (string?)first["detail"]);

        await SwitchAsync(off, enabled: true);
        await FailAFileAsync("Second.Broken.2022");
        var second = Assert.Single(await _receiver.WaitForPostsAsync("/off", 1)).Json!.AsObject();
        Assert.Equal(2, (await _receiver.WaitForPostsAsync("/on", 2)).Count);
        Assert.NotEqual((long)first["job_id"]!, (long)second["job_id"]!);
        Assert.Contains((long)second["job_id"]!, await PassJobsAsync());
    }

    [Fact]
    public async Task A_channel_hears_only_the_outcomes_it_asked_for()
    {
        await CreateChannelAsync("webhook", "/failures", "processing_job_failed");
        await CreateChannelAsync("webhook", "/finished", "processing_job_completed");

        await FinishAFileAsync("Fine.2022");
        var finished = Assert.Single(await _receiver.WaitForPostsAsync("/finished", 1)).Json!.AsObject();
        await FailAFileAsync("Broken.2022");
        var failed = Assert.Single(await _receiver.WaitForPostsAsync("/failures", 1)).Json!.AsObject();

        Assert.Equal("processing_job_completed", (string?)finished["event"]);
        Assert.Equal("processing_job_failed", (string?)failed["event"]);
        Assert.Single(_receiver.PostsTo("/finished"));
        Assert.Single(_receiver.PostsTo("/failures"));
        Assert.NotEqual((long)finished["job_id"]!, (long)failed["job_id"]!);
    }

    [Fact]
    public async Task A_discord_channel_gets_a_green_embed_for_a_finished_file_and_a_red_one_for_a_failure()
    {
        await CreateChannelAsync("discord", "/discord", "processing_job_completed", "processing_job_failed");

        await FinishAFileAsync("Fine.2022");
        var green = Assert.Single(await _receiver.WaitForPostsAsync("/discord", 1)).Json!.AsObject();
        await FailAFileAsync("Broken.2022");
        var red = (await _receiver.WaitForPostsAsync("/discord", 2))[1].Json!.AsObject();

        var finishedEmbed = Assert.Single(green["embeds"]!.AsArray())!;
        var failedEmbed = Assert.Single(red["embeds"]!.AsArray())!;
        Assert.Equal(Green, (int?)finishedEmbed["color"]);
        Assert.Equal(Red, (int?)failedEmbed["color"]);
        Assert.EndsWith("job completed", (string?)finishedEmbed["title"], StringComparison.Ordinal);
        Assert.EndsWith("job failed", (string?)failedEmbed["title"], StringComparison.Ordinal);
        var failedJob = long.Parse((string)failedEmbed["fields"]![1]!["value"]!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(failedJob, await PassJobsAsync());
        Assert.Equal(("Module", "processing"), ((string?)failedEmbed["fields"]![0]!["name"], (string?)failedEmbed["fields"]![0]!["value"]));
        Assert.Equal("Job ID", (string?)failedEmbed["fields"]![1]!["name"]);
        Assert.Equal($"Job {failedJob} (processing.file.remux_pass.v1) failed, and Weir has stopped trying.", (string?)failedEmbed["description"]);
        Assert.StartsWith("Weir on ", (string?)failedEmbed["footer"]!["text"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_receiver_that_refuses_is_told_once_and_the_log_says_so_plainly(HttpStatusCode answer)
    {
        await CreateChannelAsync("webhook", "/refusing", "processing_job_failed");
        _receiver.Answers("/refusing", answer);

        await FailAFileAsync("Broken.2022");
        Assert.Single(await _receiver.WaitForPostsAsync("/refusing", 1));
        await _scenario.Server.WaitForLogLineAsync(
            line => line.Contains("was not delivered", StringComparison.Ordinal) && line.Contains($"(HTTP {(int)answer})", StringComparison.Ordinal),
            "the log line saying the alert was refused");

        Assert.Contains(
            $"The alert \"Alert /refusing\" was not delivered, and Weir will not send it again. The notification destination answered with an error (HTTP {(int)answer}), so it did not take the message. Check the address.",
            _scenario.Server.LogText(),
            StringComparison.Ordinal);

        // The next file's alert comes after the first one's only: nothing in between was a second try.
        _receiver.Answers("/refusing", HttpStatusCode.NoContent);
        await FailAFileAsync("Broken.Again.2022");
        var posts = await _receiver.WaitForPostsAsync("/refusing", 2);
        Assert.NotEqual((long)posts[0].Json!["job_id"]!, (long)posts[1].Json!["job_id"]!);
        Assert.Equal(2, _receiver.PostsTo("/refusing").Count);
    }
}
