using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Connections › Media managers, Remove: the question before a connection goes, and the list of what still needs it (workflows that
/// come from it, reports Weir owes it) with a step for each. Keep it, Escape and a click outside any of these dialogs change nothing
/// and send nothing; a step happens only when it is chosen.
/// </summary>
public sealed class RemoveConnectionDialogTests(E2EServer server) : E2ETestBase(server)
{
    private const string AskId = "media-manager-remove-confirm";
    private const string InUseId = "media-manager-in-use";
    private const string Route = $"{WeirClient.Api}/media-managers/connections";

    [E2EFact]
    public async Task Keep_it_Escape_and_a_click_outside_leave_the_connection_and_its_workflows_alone()
    {
        using var rig = new TemporaryFolder();
        await using var linked = await LinkedDeluno.StartAsync(BaseUrl, rig);
        var page = await NewPageAsync();
        var before = await SetupSnapshot.TakeAsync(linked.Client);
        var card = await OpenMediaManagersAsync(page, BaseUrl);
        var sent = ChangesSent.Watch(page);

        // The first question: nothing is sent whichever way it is turned down.
        await CloseTheQuestionEachWayAsync(page, card, linked.Name);
        Assert.Empty(await sent.SoFarAsync());
        Assert.Equal(before, await SetupSnapshot.TakeAsync(linked.Client));

        // Asked to go ahead, Weir says the connection is still needed, which changes nothing; then the list is turned down each way.
        await OpenInUseAsync(page, card, linked.Name);
        await Expect(page.GetByTestId($"{InUseId}-workflows")).ToContainTextAsync("Movies");
        await page.GetByTestId($"{InUseId}-keep").ClickAsync();
        await Expect(page.GetByTestId(InUseId)).ToHaveCountAsync(0);

        await OpenInUseAsync(page, card, linked.Name);
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.GetByTestId(InUseId)).ToHaveCountAsync(0);

        await OpenInUseAsync(page, card, linked.Name);
        await Dialogs.ClickOutsideAsync(page.GetByTestId(InUseId));
        await Expect(page.GetByTestId(InUseId)).ToBeVisibleAsync();
        await page.GetByTestId($"{InUseId}-keep").ClickAsync();
        await Expect(page.GetByTestId(InUseId)).ToHaveCountAsync(0);

        // The only requests were the three confirmations, and the server refused each.
        var refusal = $"DELETE {Route}/{linked.DelunoId}";
        Assert.Equal([refusal, refusal, refusal], await sent.SoFarAsync());
        Assert.Equal(before, await SetupSnapshot.TakeAsync(linked.Client));
    }

    [E2EFact]
    public async Task Unlinking_the_workflows_happens_only_when_chosen_and_the_question_that_follows_can_still_be_turned_down()
    {
        using var rig = new TemporaryFolder();
        await using var linked = await LinkedDeluno.StartAsync(BaseUrl, rig);
        var page = await NewPageAsync();
        var card = await OpenMediaManagersAsync(page, BaseUrl);
        var sent = ChangesSent.Watch(page);

        await OpenInUseAsync(page, card, linked.Name);
        await page.GetByTestId($"{InUseId}-unlink").ClickAsync();

        await Expect(page.GetByTestId("media-manager-remove-outcome")).ToContainTextAsync("no longer linked to");
        await Expect(page.GetByTestId(AskId)).ToContainTextAsync($"Remove {linked.Name}?");
        Assert.Equal([$"DELETE {Route}/{linked.DelunoId}", $"POST {Route}/{linked.DelunoId}/unlink-workflows"], await sent.SoFarAsync());
        await AssertConnectionKeptAsync(linked);

        await page.Keyboard.PressAsync("Escape");
        await Expect(page.GetByTestId(AskId)).ToHaveCountAsync(0);
        await OpenAskAsync(page, card, linked.Name);
        await Dialogs.ClickOutsideAsync(page.GetByTestId(AskId));
        await Expect(page.GetByTestId(AskId)).ToBeVisibleAsync();
        await page.GetByTestId($"{AskId}-cancel").ClickAsync();
        await Expect(page.GetByTestId(AskId)).ToHaveCountAsync(0);

        Assert.Equal(2, (await sent.SoFarAsync()).Count);
        await AssertConnectionKeptAsync(linked);
    }

    [E2EFact]
    public async Task Giving_up_on_a_report_happens_only_when_chosen_and_turning_the_list_down_keeps_the_report_owed()
    {
        await using var rig = await ProcessingRig.StartAsync();
        const string file = "Owed.2001.mkv";
        await rig.HandOffAsync("h-owed", file);
        await rig.WaitUntilWorkingAsync("h-owed");
        rig.StopDeluno();
        rig.ReleasePass(file);
        await rig.WaitForOwedReportAsync();
        var connection = await LinkMoviesToTheConnectionAsync(rig);
        var name = (string)(await rig.Admin.GetAsync($"{Route}/{connection}")).Fields["name"]!;
        var before = await SetupSnapshot.TakeAsync(rig.Admin);
        var page = await NewPageAsync();
        var card = await OpenMediaManagersAsync(page, rig.BaseUrl);
        var sent = ChangesSent.Watch(page);

        await OpenInUseAsync(page, card, name);
        await Expect(page.GetByTestId($"{InUseId}-workflows")).ToContainTextAsync("Movies");
        await Expect(page.GetByTestId($"{InUseId}-reports")).ToContainTextAsync("Owed");
        await page.GetByTestId($"{InUseId}-keep").ClickAsync();
        await Expect(page.GetByTestId(InUseId)).ToHaveCountAsync(0);
        await OpenInUseAsync(page, card, name);
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.GetByTestId(InUseId)).ToHaveCountAsync(0);
        await OpenInUseAsync(page, card, name);
        await Dialogs.ClickOutsideAsync(page.GetByTestId(InUseId));
        await Expect(page.GetByTestId(InUseId)).ToBeVisibleAsync();
        await page.GetByTestId($"{InUseId}-keep").ClickAsync();
        await Expect(page.GetByTestId(InUseId)).ToHaveCountAsync(0);

        var refusal = $"DELETE {Route}/{connection}";
        Assert.Equal([refusal, refusal, refusal], await sent.SoFarAsync());
        Assert.Equal(1, rig.OwedReports);
        Assert.Equal(before, await SetupSnapshot.TakeAsync(rig.Admin));

        await OpenInUseAsync(page, card, name);
        await page.GetByTestId($"{InUseId}-give-up-reports").ClickAsync();

        await Expect(page.GetByTestId("media-manager-remove-outcome")).ToContainTextAsync("gave up on the report it owed");
        Assert.Equal(0, rig.OwedReports);
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.GetByTestId(AskId)).ToHaveCountAsync(0);
        Assert.Equal([refusal, refusal, refusal, refusal, $"POST {Route}/{connection}/give-up-reports"], await sent.SoFarAsync());
        var kept = await rig.Admin.GetAsync(Route);
        Assert.Contains(kept.Elements, row => (long)row!["id"]! == connection);
    }

    /// <summary>Links the Movies workflow to the rig's connection, as choosing the manager in the workflow's editor does; returns the connection's id.</summary>
    private static async Task<long> LinkMoviesToTheConnectionAsync(ProcessingRig rig)
    {
        var connections = await rig.Admin.GetAsync(Route);
        Assert.True(connections.Status == HttpStatusCode.OK, connections.ToString());
        var connection = (long)Assert.Single(connections.Elements)!["id"]!;
        var libraries = await rig.Admin.GetAsync($"{WeirClient.Api}/processing/libraries");
        var movies = libraries.Elements.Select(row => row!.AsObject()).First(row => (string?)row["media_type"] == "movie");
        var body = LibraryBodies.Unchanged(movies);
        body["manager_connection_ids"] = new JsonArray(connection);
        var saved = await rig.Admin.PutWithCsrfAsync($"{WeirClient.Api}/processing/libraries/{(long)movies["id"]!}", body);
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
        return connection;
    }

    private static async Task<ILocator> OpenMediaManagersAsync(IPage page, string baseUrl)
    {
        await Navigation.EnsureSignedInAsync(page, baseUrl);
        await Navigation.OpenTabAsync(page, "Connections", "Media managers");
        var card = page.GetByTestId("media-manager-card");
        await Expect(card).ToHaveCountAsync(1);
        return card;
    }

    private static async Task OpenAskAsync(IPage page, ILocator card, string name)
    {
        await card.GetByTestId("media-manager-remove").ClickAsync();
        await Expect(page.GetByTestId(AskId)).ToContainTextAsync($"Remove {name}?");
        await Expect(page.GetByTestId($"{AskId}-cancel")).ToBeFocusedAsync();
    }

    /// <summary>Asks to remove the connection and confirms; Weir answers that it is still needed, which opens the list of what needs it.</summary>
    private static async Task OpenInUseAsync(IPage page, ILocator card, string name)
    {
        await OpenAskAsync(page, card, name);
        await page.GetByTestId($"{AskId}-confirm").ClickAsync();
        await Expect(page.GetByTestId(InUseId)).ToContainTextAsync($"{name} is still needed");
        await Expect(page.GetByTestId($"{InUseId}-keep")).ToBeFocusedAsync();
    }

    private static async Task CloseTheQuestionEachWayAsync(IPage page, ILocator card, string name)
    {
        var ask = page.GetByTestId(AskId);
        await OpenAskAsync(page, card, name);
        await page.GetByTestId($"{AskId}-cancel").ClickAsync();
        await Expect(ask).ToHaveCountAsync(0);

        await OpenAskAsync(page, card, name);
        await page.Keyboard.PressAsync("Escape");
        await Expect(ask).ToHaveCountAsync(0);

        await OpenAskAsync(page, card, name);
        await Dialogs.ClickOutsideAsync(ask);
        await Expect(ask).ToBeVisibleAsync();
        await page.GetByTestId($"{AskId}-cancel").ClickAsync();
        await Expect(ask).ToHaveCountAsync(0);
    }

    /// <summary>The connection is still there.</summary>
    private static async Task AssertConnectionKeptAsync(LinkedDeluno linked)
    {
        var connections = await linked.Client.GetAsync(Route);
        Assert.Contains(connections.Elements, row => (long)row!["id"]! == linked.DelunoId);
    }
}
