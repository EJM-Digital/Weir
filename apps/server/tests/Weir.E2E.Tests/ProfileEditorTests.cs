using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Rules › Profiles against the real server: a new profile's starting rules, renaming and saving a profile, the track name templates, the
/// advanced track ordering and the profile list following the server while a draft is open. Each save is checked as the request the page
/// sent and the profile the server reads back.
/// </summary>
public sealed class ProfileEditorTests(E2EServer server) : E2ETestBase(server)
{
    private const string Name = "E2E editable profile";
    private const string RuleSets = $"{WeirClient.Api}/processing/rule-sets";

    private static readonly string[] ServerOwned = ["id", "used_by_library_count", "created_at", "updated_at"];

    private static ILocator Workspace(IPage page) => page.GetByTestId("processing-rule-set-workspace");

    private static ILocator ProfileSelect(IPage page) => Workspace(page).GetByRole(AriaRole.Combobox, new() { Name = "Profile", Exact = true });

    private static ILocator NameField(IPage page) => Workspace(page).GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true });

    private static ILocator SaveButton(IPage page) => Workspace(page).GetByRole(AriaRole.Button, new() { Name = "Save profile", Exact = true });

    private static ILocator Notice(IPage page) => Workspace(page).GetByRole(AriaRole.Status);

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private async Task<IPage> OpenProfileAsync(long profile)
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "Rules", "Profiles");
        await ProfileSelect(page).SelectOptionAsync(Id(profile));
        await Expect(NameField(page)).ToHaveValueAsync(Name);
        return page;
    }

    private static async Task<JsonNode> ReadAsync(WeirClient admin, long profile)
    {
        var listed = await admin.GetAsync(RuleSets);
        return listed.Elements.Single(element => (long)element!["id"]! == profile)!;
    }

    [E2EFact]
    public async Task New_profile_opens_a_blank_workspace_with_the_starting_rules_and_refuses_a_save_without_a_name()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "Rules", "Profiles");
        var sent = ChangesSent.Watch(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "New profile", Exact = true }).ClickAsync();

        await Expect(NameField(page)).ToHaveValueAsync(string.Empty);
        await Expect(NameField(page)).ToHaveAttributeAsync("placeholder", "English feature films");
        await Expect(Workspace(page).GetByTestId("rule-set-profile-bar")).ToContainTextAsync("A new profile. Choose it in a workflow’s editor once it is saved.");
        await Expect(ProfileSelect(page)).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "New profile", Exact = true })).ToHaveCountAsync(0);
        await Expect(Workspace(page).GetByRole(AriaRole.Button, new() { Name = "Remove profile", Exact = true })).ToHaveCountAsync(0);
        await Expect(Workspace(page).GetByRole(AriaRole.Combobox, new() { Name = "First choice", Exact = true })).ToHaveValueAsync("eng");
        await Expect(Workspace(page).GetByRole(AriaRole.Combobox, new() { Name = "Second choice", Exact = true })).ToHaveValueAsync(string.Empty);
        await Expect(Workspace(page).GetByRole(AriaRole.Combobox, new() { Name = "Subtitle handling", Exact = true })).ToHaveValueAsync("keep_all");
        await Expect(Workspace(page).GetByRole(AriaRole.Checkbox, new() { Name = "Remove commentary tracks" })).ToBeCheckedAsync();

        await Workspace(page).GetByRole(AriaRole.Button, new() { Name = "Create profile", Exact = true }).ClickAsync();

        await Expect(Notice(page)).ToHaveTextAsync("Give this profile a name before saving it.");
        Assert.Empty(await sent.SoFarAsync());
    }

    [E2EFact]
    public async Task Renaming_a_profile_sends_the_trimmed_name_and_the_list_and_the_server_show_it()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var profile = await WeirProfiles.CreateAsync(admin, Name);
        try
        {
            var page = await OpenProfileAsync(profile);
            var sent = ChangesSent.Watch(page);

            await NameField(page).FillAsync("  E2E renamed profile  ");
            await SaveButton(page).ClickAsync();

            await Expect(Notice(page)).ToHaveTextAsync("E2E renamed profile was saved.");
            Assert.Equal([$"PUT {RuleSets}/{profile}"], await sent.SoFarAsync());
            Assert.Equal("E2E renamed profile", (string)(await sent.BodiesAsync($"PUT {RuleSets}/{profile}")).Single()["name"]!);
            await Expect(NameField(page)).ToHaveValueAsync("E2E renamed profile");
            await Expect(ProfileSelect(page).Locator("option:checked")).ToContainTextAsync("E2E renamed profile · 0 workflows");
            Assert.Equal("E2E renamed profile", (string)(await ReadAsync(admin, profile))["name"]!);
        }
        finally
        {
            await WeirProfiles.ClearAsync(admin, profile);
        }
    }

    [E2EFact]
    public async Task Saving_a_profile_sends_every_rule_without_the_fields_the_server_owns_and_keeps_what_was_not_edited()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var profile = await WeirProfiles.CreateAsync(admin, Name);
        try
        {
            var before = await ReadAsync(admin, profile);
            var page = await OpenProfileAsync(profile);
            var sent = ChangesSent.Watch(page);

            await Workspace(page).GetByRole(AriaRole.Combobox, new() { Name = "First choice", Exact = true }).SelectOptionAsync("fre");
            await Workspace(page).GetByRole(AriaRole.Combobox, new() { Name = "Subtitle handling", Exact = true }).SelectOptionAsync("remove_all");
            await Workspace(page).GetByRole(AriaRole.Checkbox, new() { Name = "Remove commentary tracks" }).UncheckAsync();
            await SaveButton(page).ClickAsync();

            await Expect(Notice(page)).ToHaveTextAsync($"{Name} was saved.");
            Assert.Equal([$"PUT {RuleSets}/{profile}"], await sent.SoFarAsync());
            var body = (await sent.BodiesAsync($"PUT {RuleSets}/{profile}")).Single();
            foreach (var owned in ServerOwned)
            {
                Assert.False(body.ContainsKey(owned), $"The page sent {owned}, which the server owns.");
            }

            var after = await ReadAsync(admin, profile);
            var rules = before.AsObject().Select(pair => pair.Key).Where(key => !ServerOwned.Contains(key)).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(rules, body.Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal("fre", (string)after["primary_audio_lang"]!);
            Assert.Equal("remove_all", (string)after["subtitle_mode"]!);
            Assert.False((bool)after["remove_commentary"]!);
            foreach (var key in rules.Except(["primary_audio_lang", "subtitle_mode", "remove_commentary"]))
            {
                Assert.Equal(before[key]!.ToJsonString(), after[key]!.ToJsonString());
            }

            await Expect(Workspace(page).GetByRole(AriaRole.Combobox, new() { Name = "First choice", Exact = true })).ToHaveValueAsync("fre");
            await Expect(SaveButton(page)).ToBeEnabledAsync();
        }
        finally
        {
            await WeirProfiles.ClearAsync(admin, profile);
        }
    }

    [E2EFact]
    public async Task The_four_track_name_overrides_each_preview_their_own_flag_and_are_saved_with_the_profile()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var profile = await WeirProfiles.CreateAsync(admin, Name);
        try
        {
            var page = await OpenProfileAsync(profile);
            var sent = ChangesSent.Watch(page);
            var naming = Workspace(page).Locator("details").Filter(new() { HasText = "Track naming and chapters" });
            await naming.Locator("summary").ClickAsync();
            await naming.GetByRole(AriaRole.Checkbox, new() { Name = "Standardize audio and subtitle track names" }).CheckAsync();

            (string Label, string Key, string Flag)[] overrides =
            [
                ("Forced tracks", "forced", "Forced"),
                ("Hearing-impaired tracks", "hearing_impaired", "Hearing Impaired"),
                ("Commentary tracks", "commentary", "Commentary"),
                ("Audio description tracks", "audio_description", "Audio Description"),
            ];
            foreach (var (label, _, flag) in overrides)
            {
                var field = naming.Locator("label").Filter(new() { HasText = label }).Locator("input");
                await Expect(field).ToHaveValueAsync("{language} {flags}");
                await Expect(field.Locator("xpath=..")).ToContainTextAsync($"Preview: ");
                await Expect(field.Locator("xpath=..")).ToContainTextAsync($"Preview: English {flag}");
                await field.FillAsync($"{{language}} custom {flag.ToLowerInvariant()}");
                await Expect(field.Locator("xpath=..")).ToContainTextAsync($"custom {flag.ToLowerInvariant()}");
            }

            await SaveButton(page).ClickAsync();

            await Expect(Notice(page)).ToHaveTextAsync($"{Name} was saved.");
            Assert.Equal([$"PUT {RuleSets}/{profile}"], await sent.SoFarAsync());
            var saved = (await ReadAsync(admin, profile))["track_name_overrides"]!;
            foreach (var (_, key, flag) in overrides)
            {
                Assert.Equal($"{{language}} custom {flag.ToLowerInvariant()}", (string)saved[key]!);
            }
        }
        finally
        {
            await WeirProfiles.ClearAsync(admin, profile);
        }
    }

    [E2EFact]
    public async Task A_track_name_template_with_a_placeholder_Weir_does_not_know_is_flagged_on_its_field_until_it_is_fixed()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var profile = await WeirProfiles.CreateAsync(admin, Name);
        try
        {
            var page = await OpenProfileAsync(profile);
            var naming = Workspace(page).Locator("details").Filter(new() { HasText = "Track naming and chapters" });
            await naming.Locator("summary").ClickAsync();
            await naming.GetByRole(AriaRole.Checkbox, new() { Name = "Standardize audio and subtitle track names" }).CheckAsync();
            var field = naming.Locator("label").Filter(new() { HasText = "Forced tracks" });

            await field.Locator("input").FillAsync("{language} {nonsense}");

            await Expect(field.GetByRole(AriaRole.Alert)).ToContainTextAsync("Unknown placeholder '{nonsense}'");
            await Expect(field.Locator("input")).ToHaveAttributeAsync("aria-invalid", "true");

            await field.Locator("input").FillAsync("{language} {flags}");

            await Expect(field.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
            await Expect(field).ToContainTextAsync("Preview: English Forced");
        }
        finally
        {
            await WeirProfiles.ClearAsync(admin, profile);
        }
    }

    [E2EFact]
    public async Task Advanced_track_ordering_opens_and_hides_closes_on_another_profile_and_stays_open_after_a_save()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var profile = await WeirProfiles.CreateAsync(admin, Name);
        var other = await WeirProfiles.CreateAsync(admin, "E2E other editable profile");
        try
        {
            var page = await OpenProfileAsync(profile);
            var toggle = Workspace(page).GetByRole(AriaRole.Button, new() { Name = "Advanced track ordering" });
            var audioOrder = Workspace(page).GetByRole(AriaRole.Heading, new() { Name = "Audio order" });
            await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
            await Expect(toggle).ToContainTextAsync("Edit");
            await Expect(audioOrder).ToHaveCountAsync(0);

            await toggle.ClickAsync();
            await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
            await Expect(toggle).ToContainTextAsync("Hide");
            await Expect(audioOrder).ToBeVisibleAsync();
            await Expect(Workspace(page).GetByRole(AriaRole.Heading, new() { Name = "Subtitle order" })).ToBeVisibleAsync();

            await toggle.ClickAsync();
            await Expect(audioOrder).ToHaveCountAsync(0);
            await toggle.ClickAsync();
            await Expect(audioOrder).ToBeVisibleAsync();

            await NameField(page).FillAsync("E2E editable profile again");
            await SaveButton(page).ClickAsync();
            await Expect(Notice(page)).ToContainTextAsync("was saved.");
            await Expect(audioOrder).ToBeVisibleAsync();
            await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");

            await ProfileSelect(page).SelectOptionAsync(Id(other));
            await Expect(NameField(page)).ToHaveValueAsync("E2E other editable profile");
            await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
            await Expect(audioOrder).ToHaveCountAsync(0);
        }
        finally
        {
            await WeirProfiles.ClearAsync(admin, profile);
            await WeirProfiles.ClearAsync(admin, other);
        }
    }

    [E2EFact]
    public async Task The_profile_list_and_who_uses_it_follow_the_server_while_a_draft_is_open()
    {
        using var folders = new TemporaryFolder();
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var profile = await WeirProfiles.CreateAsync(admin, Name);
        long? other = null;
        long? workflow = null;
        var page = await OpenProfileAsync(profile);
        try
        {
            await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");
            await Expect(Workspace(page).GetByTestId("rule-set-used-by")).ToContainTextAsync("No workflow uses it yet.");
            await NameField(page).FillAsync("A draft nobody saved");

            other = await WeirProfiles.CreateAsync(admin, "E2E profile made elsewhere");
            workflow = await WeirWorkflowApi.CreateAsync(
                page.Context,
                BaseUrl,
                "E2E using the profile",
                Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName,
                Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName);
            await WeirProfiles.UseInWorkflowAsync(admin, workflow.Value, profile);

            await Expect(ProfileSelect(page).Locator("option", new() { HasText = "E2E profile made elsewhere" })).ToHaveCountAsync(1);
            await Expect(Workspace(page).GetByTestId("rule-set-used-by")).ToHaveTextAsync("Used by E2E using the profile");
            await Expect(ProfileSelect(page).Locator("option:checked")).ToContainTextAsync($"{Name} · 1 workflow");
            await Expect(NameField(page)).ToHaveValueAsync("A draft nobody saved");
            Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
        }
        finally
        {
            await WeirProfiles.ClearAsync(admin, profile, workflow ?? 0);
            if (other is { } id)
            {
                await WeirProfiles.ClearAsync(admin, id);
            }

            if (workflow is { } workflowId)
            {
                await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, workflowId);
            }
        }
    }
}
