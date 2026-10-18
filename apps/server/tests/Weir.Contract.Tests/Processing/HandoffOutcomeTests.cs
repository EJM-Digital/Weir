using System.Net;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// What a media manager says became of a file Weir handed back: a "will not import" keeps Weir's copy and a later "imported"
/// from the same manager replaces it; no other answer replaces an earlier one.
/// </summary>
[ContractArea("processing")]
public sealed class HandoffOutcomeTests
{
    private const string ImportedPath = "/deluno/library/movies/Film (2020)/Film (2020).mkv";

    [Fact]
    public async Task An_import_after_a_refusal_replaces_it_and_releases_the_copy_the_refusal_kept()
    {
        await using var scenario = await Scenario.StartAsync();
        var (copy, handoffId) = await FinishedHandoffAsync(scenario);

        var refused = await scenario.PostOutcomeAsync(handoffId, "not-imported", reason: "The import dead-lettered.");
        Assert.True(refused.Status == HttpStatusCode.OK, refused.ToString());
        Assert.Equal("not-imported", (string)refused.Fields["outcome"]!);
        Assert.True(File.Exists(copy));

        var imported = await scenario.PostOutcomeAsync(handoffId, "imported", ImportedPath);

        Assert.True(imported.Status == HttpStatusCode.OK, imported.ToString());
        Assert.Equal("imported", (string)imported.Fields["outcome"]!);
        Assert.True((bool)imported.Fields["released"]!, imported.ToString());
        Assert.False(File.Exists(copy), "Weir's own copy is released once the manager has the file");
        var titles = (await scenario.ActivityAsync("processing.handback_outcome")).Select(entry => (string)entry["title"]!).ToList();
        Assert.Contains("Deluno will not import film.mkv", titles);
        Assert.Contains("Deluno imported film.mkv after all", titles);

        var again = await scenario.PostOutcomeAsync(handoffId, "imported", ImportedPath);
        Assert.Equal((HttpStatusCode.OK, imported.Text), (again.Status, again.Text));
    }

    [Fact]
    public async Task A_refusal_after_an_import_is_a_409_that_changes_nothing()
    {
        await using var scenario = await Scenario.StartAsync();
        var (copy, handoffId) = await FinishedHandoffAsync(scenario);
        var imported = await scenario.PostOutcomeAsync(handoffId, "imported", ImportedPath);
        Assert.True(imported.Status == HttpStatusCode.OK, imported.ToString());
        Assert.False(File.Exists(copy));

        var refused = await scenario.PostOutcomeAsync(handoffId, "not-imported", reason: "Changed its mind.");

        Assert.Equal((HttpStatusCode.Conflict, "outcome_already_recorded"), (refused.Status, (string)refused.Fields["code"]!));
        var titles = (await scenario.ActivityAsync("processing.handback_outcome")).Select(entry => (string)entry["title"]!).ToList();
        Assert.Equal(["Deluno imported film.mkv"], titles);
    }

    [Fact]
    public async Task The_same_refusal_again_answers_the_same_and_another_managers_hand_off_is_never_found()
    {
        await using var scenario = await Scenario.StartAsync();
        var (copy, handoffId) = await FinishedHandoffAsync(scenario);
        var refused = await scenario.PostOutcomeAsync(handoffId, "not-imported", reason: "The import dead-lettered.");
        Assert.True(refused.Status == HttpStatusCode.OK, refused.ToString());

        var again = await scenario.PostOutcomeAsync(handoffId, "not-imported", reason: "The import dead-lettered.");
        Assert.Equal((HttpStatusCode.OK, refused.Text), (again.Status, again.Text));

        var other = await scenario.Admin.PostAsync(
            $"{WeirClient.Api}/intake/handoffs/sonarr/{handoffId}/outcome",
            new System.Text.Json.Nodes.JsonObject { ["outcome"] = "imported", ["occurredUtc"] = "2026-10-08T10:00:00Z" },
            new Dictionary<string, string> { ["X-Webhook-Secret"] = Scenario.WebhookSecret });
        Assert.Equal(HttpStatusCode.NotFound, other.Status);
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public async Task The_report_records_which_copy_it_named_so_a_later_copy_is_never_released_for_it()
    {
        await using var scenario = await Scenario.StartAsync();
        await FinishedHandoffAsync(scenario);

        await using var database = await scenario.StopForDatabaseAsync();
        var target = Assert.Single(SeedSql.Rows(
            database.Connection,
            "SELECT t.output_written_at AS named, h.written_at AS written FROM media_manager_handoff_targets t JOIN handbacks h ON h.relative_path = t.relative_path"));
        Assert.NotNull(target["named"]);
        Assert.Equal(target["written"], target["named"]);
    }

    /// <summary>A hand-off the fake Deluno made, which Weir finished and reported: the copy it handed back, and the hand-off's id.</summary>
    private static async Task<(string Copy, string HandoffId)> FinishedHandoffAsync(Scenario scenario)
    {
        var (fake, _) = await scenario.DelunoSetupAsync();
        var source = scenario.WriteRelease("Film.2020", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        const string handoffId = "outcome-1";
        await scenario.PostHandoffAsync(handoffId, source);
        await scenario.WaitForHandoffStateAsync(handoffId, "completed");
        await scenario.WaitForReportsAsync(fake, handoffId);
        var copy = Path.Combine(scenario.Folders.Output, "Film.2020", "film.mkv");
        Assert.True(File.Exists(copy));
        return (copy, handoffId);
    }
}
