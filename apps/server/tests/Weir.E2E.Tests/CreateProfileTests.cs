using System.Globalization;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Rules › Profiles, New profile: a profile made in the browser reaches the server as one request carrying what was typed and the
/// rules as Weir starts them, is shown saved and selected, and is what the server reads back afterwards.
/// </summary>
public sealed class CreateProfileTests(E2EServer server) : E2ETestBase(server)
{
    private const string Name = "E2E created profile";

    [E2EFact]
    public async Task A_new_profile_is_sent_once_shown_saved_and_selected_and_the_server_reads_back_what_was_typed()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var before = await WeirProfiles.IdsAsync(admin);
        long? created = null;
        try
        {
            var page = await NewPageAsync();
            await Navigation.EnsureSignedInAsync(page, BaseUrl);
            await Navigation.OpenTabAsync(page, "Rules", "Profiles");
            var workspace = page.GetByTestId("processing-rule-set-workspace");
            var sent = ChangesSent.Watch(page);

            await page.GetByRole(AriaRole.Button, new() { Name = "New profile", Exact = true }).ClickAsync();
            await workspace.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync($"  {Name}  ");
            await workspace.GetByRole(AriaRole.Combobox, new() { Name = "First choice", Exact = true }).SelectOptionAsync("fre");
            await workspace.GetByRole(AriaRole.Combobox, new() { Name = "Subtitle handling", Exact = true }).SelectOptionAsync("remove_all");
            await workspace.GetByRole(AriaRole.Button, new() { Name = "Create profile", Exact = true }).ClickAsync();

            await Expect(workspace.GetByRole(AriaRole.Status)).ToHaveTextAsync($"{Name} was saved.");
            Assert.Equal([$"POST {WeirClient.Api}/processing/rule-sets"], await sent.SoFarAsync());

            created = (await WeirProfiles.IdsAsync(admin)).Except(before).Single();
            await Expect(workspace.GetByRole(AriaRole.Combobox, new() { Name = "Profile", Exact = true }))
                .ToHaveValueAsync(created.Value.ToString(CultureInfo.InvariantCulture));
            await Expect(workspace.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true })).ToHaveValueAsync(Name);

            var listed = await admin.GetAsync($"{WeirClient.Api}/processing/rule-sets");
            var row = listed.Elements.Single(element => (long)element!["id"]! == created.Value)!;
            Assert.Equal(Name, (string)row["name"]!);
            Assert.Equal("fre", (string)row["primary_audio_lang"]!);
            Assert.Equal("remove_all", (string)row["subtitle_mode"]!);
            Assert.Equal(string.Empty, (string)row["secondary_audio_lang"]!);
        }
        finally
        {
            if (created is { } id)
            {
                await WeirProfiles.ClearAsync(admin, id);
            }
        }
    }
}
