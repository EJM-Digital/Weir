using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>System › About, Time zone: the zone every time in Weir is read in. Save appears only while the chosen zone differs from the saved one, and saving keeps the other app settings.</summary>
public sealed partial class TimeZoneTests(E2EServer server) : E2ETestBase(server)
{
    private const string Settings = $"{WeirClient.Api}/suite/settings";
    private const string Zone = "Australia/Sydney";

    [E2EFact]
    public async Task Save_appears_only_while_another_zone_is_chosen_and_saving_keeps_the_other_settings_and_moves_the_clock()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var before = (await admin.GetAsync(Settings)).Fields;
        await Navigation.OpenTabAsync(page, "System", "About");
        var log = RequestLog.Watch(page);
        var picker = page.GetByRole(AriaRole.Button, new() { Name = "Time zone", Exact = true });
        var save = page.GetByTestId("schedule-save-timezone");
        await Expect(picker).ToContainTextAsync("Coordinated Universal Time");
        await Expect(save).ToHaveCountAsync(0);

        // Choosing another zone offers Save; choosing the saved one again takes it away, and nothing is sent either way.
        var mark = log.Mark();
        await ChooseAsync(page, picker, Sydney());
        await Expect(save).ToBeVisibleAsync();
        await ChooseAsync(page, picker, new Regex("^UTC — Coordinated"));
        await Expect(save).ToHaveCountAsync(0);
        log.AssertNothingSentSince(mark);

        await ChooseAsync(page, picker, Sydney());
        mark = log.Mark();
        await save.ClickAsync();
        await Expect(save).ToHaveCountAsync(0);

        var sent = Assert.Single(log.Since(mark));
        Assert.Equal($"PUT {Settings}", $"{sent.Method} {sent.PathAndQuery}");
        var body = JsonNode.Parse(sent.Body!)!.AsObject();
        Assert.False(string.IsNullOrEmpty((string?)body["csrf_token"]), "The request carried no CSRF token.");
        Assert.Equal(Zone, (string)body["app_timezone"]!);
        Assert.Equal(before["setup_wizard_state"]?.ToJsonString(), body["setup_wizard_state"]?.ToJsonString());
        Assert.Equal(before["log_retention_days"]?.ToJsonString(), body["log_retention_days"]?.ToJsonString());
        var stored = (await admin.GetAsync(Settings)).Fields;
        Assert.Equal(Zone, (string)stored["app_timezone"]!);
        Assert.Equal(before["setup_wizard_state"]?.ToJsonString(), stored["setup_wizard_state"]?.ToJsonString());
        Assert.Equal(before["log_retention_days"]?.ToJsonString(), stored["log_retention_days"]?.ToJsonString());

        // The clock under the picker is now Sydney's, to the minute, and the Schedule tab reads its hours in that zone.
        var now = page.Locator("p.mm-sys-note").Filter(new() { HasText = "Every time across Weir is in this zone." });
        await Expect(now).ToContainTextAsync(NowText());
        var shown = NowText().Match(await now.InnerTextAsync());
        var shownMinutes = (Convert.ToInt32(shown.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) * 60) + Convert.ToInt32(shown.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        var sydneyMinutes = await page.EvaluateAsync<int>(
            "zone => { const at = new Intl.DateTimeFormat('en-GB', { timeZone: zone, hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).formatToParts(new Date()); const part = type => Number(at.find(p => p.type === type).value); return part('hour') * 60 + part('minute'); }",
            Zone);
        var difference = Math.Abs(shownMinutes - sydneyMinutes);
        Assert.True(Math.Min(difference, 1440 - difference) <= 1, $"The clock shows {shown.Value} but Sydney is at minute {sydneyMinutes} of the day.");
        await Navigation.OpenTabAsync(page, "Workflows", "Schedule");
        await Expect(page.GetByTestId("processing-schedules-section")).ToContainTextAsync($"When each workflow may start work, in {Zone}.");
    }

    private static Regex Sydney() => new("^Australia — Sydney");

    private static async Task ChooseAsync(IPage page, ILocator picker, Regex label)
    {
        await picker.ClickAsync();
        await page.GetByRole(AriaRole.Option).Filter(new() { HasTextRegex = label }).First.ClickAsync();
        await Expect(picker).ToContainTextAsync(label);
    }

    [GeneratedRegex(@"Now (Mon|Tue|Wed|Thu|Fri|Sat|Sun) (\d\d):(\d\d)")]
    private static partial Regex NowText();
}
