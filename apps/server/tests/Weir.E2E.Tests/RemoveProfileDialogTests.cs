using System.Globalization;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Rules › Profiles, Remove profile: the dialog that asks before a profile goes. Keep it and Escape never remove the profile and never
/// send a request; only Remove profile does, and a profile that workflows use cannot be removed at all.
/// </summary>
public sealed class RemoveProfileDialogTests(E2EServer server) : E2ETestBase(server)
{
    private const string DialogId = "remove-rule-set-dialog";
    private const string Name = "E2E profile to keep";
    private const string OtherName = "E2E other profile";

    [E2EFact]
    public async Task Keep_it_and_Escape_leave_the_profile_selected_and_send_nothing()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var profile = await WeirProfiles.CreateAsync(admin, Name);
        try
        {
            var page = await NewPageAsync();
            var before = await SetupSnapshot.TakeAsync(admin);
            var tab = await OpenProfilesAsync(page, profile);
            var sent = ChangesSent.Watch(page);

            await OpenDialogAsync(tab, Name);
            await page.GetByTestId($"{DialogId}-cancel").ClickAsync();
            await Expect(page.GetByTestId(DialogId)).ToHaveCountAsync(0);
            await AssertKeptAsync(tab, profile);

            await OpenDialogAsync(tab, Name);
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.GetByTestId(DialogId)).ToHaveCountAsync(0);
            await AssertKeptAsync(tab, profile);

            Assert.Empty(await sent.SoFarAsync());
            Assert.Equal(before, await SetupSnapshot.TakeAsync(admin));
        }
        finally
        {
            await WeirProfiles.ClearAsync(admin, profile);
        }
    }

    [E2EFact]
    public async Task Remove_profile_sends_one_request_says_so_and_opens_the_next_profile()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var profile = await WeirProfiles.CreateAsync(admin, Name);
        var other = await WeirProfiles.CreateAsync(admin, OtherName);
        try
        {
            var page = await NewPageAsync();
            var tab = await OpenProfilesAsync(page, profile);
            var sent = ChangesSent.Watch(page);

            await OpenDialogAsync(tab, Name);
            await page.GetByTestId($"{DialogId}-confirm").ClickAsync();

            await Expect(page.GetByTestId(DialogId)).ToHaveCountAsync(0);
            await Expect(tab.GetByRole(AriaRole.Status)).ToHaveTextAsync($"{Name} was removed.");
            Assert.Equal([$"DELETE {WeirClient.Api}/processing/rule-sets/{profile}"], await sent.SoFarAsync());
            var remaining = await WeirProfiles.IdsAsync(admin);
            Assert.DoesNotContain(profile, remaining);
            Assert.Contains(other, remaining);
            await AssertSelectedAsync(tab, remaining[0]);
        }
        finally
        {
            await WeirProfiles.ClearAsync(admin, profile);
            await WeirProfiles.ClearAsync(admin, other);
        }
    }

    [E2EFact]
    public async Task A_profile_that_workflows_use_has_its_remove_button_locked_and_says_why()
    {
        using var folders = new TemporaryFolder();
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        var profile = await WeirProfiles.CreateAsync(admin, Name);
        var first = await WeirWorkflowApi.CreateAsync(page.Context, BaseUrl, "E2E first user", Folder(folders, "a-watched"), Folder(folders, "a-output"));
        var second = await WeirWorkflowApi.CreateAsync(page.Context, BaseUrl, "E2E second user", Folder(folders, "b-watched"), Folder(folders, "b-output"));
        try
        {
            await WeirProfiles.UseInWorkflowAsync(admin, first, profile);
            await WeirProfiles.UseInWorkflowAsync(admin, second, profile);
            var before = await SetupSnapshot.TakeAsync(admin);
            var tab = await OpenProfilesAsync(page, profile);
            var sent = ChangesSent.Watch(page);

            var remove = tab.GetByRole(AriaRole.Button, new() { Name = "Remove profile", Exact = true });
            await Expect(remove).ToBeDisabledAsync();
            await Expect(remove).ToHaveAttributeAsync("title", "Detach this profile from every workflow before removing it.");
            await Expect(tab).ToContainTextAsync("Used by 2 workflows; removal is locked.");
            Assert.Empty(await sent.SoFarAsync());
            Assert.Equal(before, await SetupSnapshot.TakeAsync(admin));
        }
        finally
        {
            await WeirProfiles.ClearAsync(admin, profile, first, second);
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, first);
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, second);
        }
    }

    private static string Folder(TemporaryFolder folders, string name) => Directory.CreateDirectory(Path.Join(folders.Path, name)).FullName;

    /// <summary>Rules › Profiles with this profile chosen.</summary>
    private async Task<ILocator> OpenProfilesAsync(IPage page, long profile)
    {
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "Rules", "Profiles");
        var workspace = page.GetByTestId("processing-rule-set-workspace");
        await workspace.GetByRole(AriaRole.Combobox, new() { Name = "Profile" }).SelectOptionAsync(profile.ToString(CultureInfo.InvariantCulture));
        return workspace;
    }

    private static async Task OpenDialogAsync(ILocator tab, string name)
    {
        await tab.GetByRole(AriaRole.Button, new() { Name = "Remove profile", Exact = true }).ClickAsync();
        await Expect(tab.Page.GetByTestId(DialogId)).ToContainTextAsync($"Remove the profile “{name}”?");
        await Expect(tab.Page.GetByTestId($"{DialogId}-cancel")).ToBeFocusedAsync();
    }

    private static async Task AssertSelectedAsync(ILocator tab, long profile) =>
        await Expect(tab.GetByRole(AriaRole.Combobox, new() { Name = "Profile" })).ToHaveValueAsync(profile.ToString(CultureInfo.InvariantCulture));

    private static async Task AssertKeptAsync(ILocator tab, long profile)
    {
        await AssertSelectedAsync(tab, profile);
        await Expect(tab.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true })).ToHaveValueAsync(Name);
    }
}
