using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The Browse button beside a folder field, shown by a workflow's Watched folder: the dialog lists the folders of the computer Weir runs on, opens
/// where the field points (or at the drives when that cannot be opened and says so), hands a chosen folder back to the field without saving it, and
/// closes without choosing on Close and on Escape.
/// </summary>
public sealed class FolderPickerTests(E2EServer server) : E2ETestBase(server)
{
    private const string Title = "Choose the watched folder";

    private static ILocator Dialog(IPage page) => page.GetByRole(AriaRole.Dialog, new() { Name = Title });

    private static ILocator Field(IPage page) => page.GetByTestId("processing-library-form").GetByRole(AriaRole.Textbox, new() { Name = "Watched folder" });

    private static ILocator Browse(IPage page) =>
        page.GetByTestId("processing-library-form").Locator(".mm-folder-field").Filter(new() { Has = page.GetByRole(AriaRole.Textbox, new() { Name = "Watched folder" }) }).GetByRole(AriaRole.Button, new() { Name = "Browse", Exact = true });

    private static ILocator CurrentFolder(IPage page) => Dialog(page).Locator(".mm-folder-picker__current");

    private async Task<(IPage Page, TemporaryFolder Rig, long WorkflowId)> OpenEditorAsync()
    {
        var rig = new TemporaryFolder();
        try
        {
            foreach (var name in new[] { "Alpha", "Beta", Path.Combine("Beta", "Inner") })
            {
                Directory.CreateDirectory(Path.Combine(rig.Path, name));
            }

            var page = await NewPageAsync();
            await Navigation.EnsureSignedInAsync(page, BaseUrl);
            var movies = await SetupState.FirstWorkflowAsync(page.Context, BaseUrl, "movie");
            var id = (long)movies["id"]!;
            await Navigation.OpenTabAsync(page, "Workflows", "File paths");
            await page.GetByTestId($"processing-library-{id}").GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
            await Expect(Field(page)).ToBeVisibleAsync();
            return (page, rig, id);
        }
        catch
        {
            rig.Dispose();
            throw;
        }
    }

    private static async Task OpenPickerAsync(IPage page)
    {
        await Browse(page).ClickAsync();
        await Expect(Dialog(page)).ToBeVisibleAsync();
        await Expect(Dialog(page).GetByText("Loading folders...")).ToHaveCountAsync(0);
    }

    private static string Normal(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static Task ExpectCurrentAsync(IPage page, string path) =>
        Expect(CurrentFolder(page)).ToHaveTextAsync(new Regex($@"^{Regex.Escape(Normal(path))}[\\/]?$", RegexOptions.IgnoreCase));

    [E2EFact]
    public async Task With_an_empty_field_it_opens_at_the_drives_and_offers_no_folder_to_use()
    {
        var (page, rig, _) = await OpenEditorAsync();
        using var folders = rig;

        await OpenPickerAsync(page);

        var dialog = Dialog(page);
        await Expect(dialog.GetByRole(AriaRole.Heading, new() { Name = Title, Exact = true })).ToBeVisibleAsync();
        await Expect(dialog).ToHaveAttributeAsync("aria-modal", "true");
        await Expect(CurrentFolder(page)).ToHaveTextAsync("Available drives");
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Use this folder", Exact = true })).ToHaveCountAsync(0);
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Up", Exact = true })).ToBeDisabledAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Drives", Exact = true })).ToBeDisabledAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Select", Exact = true }).First).ToBeVisibleAsync();
        await Axe.AssertNoViolationsAsync(page, "The folder picker at the drives");
    }

    [E2EFact]
    public async Task It_opens_where_the_field_points_walks_up_and_down_and_use_this_folder_fills_the_field_without_saving_it()
    {
        var (page, rig, _) = await OpenEditorAsync();
        using var folders = rig;
        var savedBefore = (string)(await SetupState.FirstWorkflowAsync(page.Context, BaseUrl, "movie"))["watched_folder"]!;
        await Field(page).FillAsync(rig.Path);

        await OpenPickerAsync(page);

        var dialog = Dialog(page);
        await ExpectCurrentAsync(page, rig.Path);
        await Expect(dialog.Locator(".mm-folder-picker__entry-name")).ToHaveTextAsync(["Alpha", "Beta"]);
        await Axe.AssertNoViolationsAsync(page, "The folder picker inside a folder");

        await dialog.Locator(".mm-folder-picker__entry").Filter(new() { HasText = "Beta" }).GetByRole(AriaRole.Button, new() { Name = "Open", Exact = true }).ClickAsync();
        await Expect(dialog.Locator(".mm-folder-picker__entry-name")).ToHaveTextAsync(["Inner"]);
        await ExpectCurrentAsync(page, Path.Combine(rig.Path, "Beta"));

        await dialog.GetByRole(AriaRole.Button, new() { Name = "Up", Exact = true }).ClickAsync();
        await Expect(dialog.Locator(".mm-folder-picker__entry-name")).ToHaveTextAsync(["Alpha", "Beta"]);
        await ExpectCurrentAsync(page, rig.Path);

        await dialog.GetByRole(AriaRole.Button, new() { Name = "Drives", Exact = true }).ClickAsync();
        await Expect(CurrentFolder(page)).ToHaveTextAsync("Available drives");

        await dialog.GetByRole(AriaRole.Textbox, new() { Name = "Folder path" }).FillAsync(Path.Combine(rig.Path, "Beta", "Inner"));
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Go to path", Exact = true }).ClickAsync();
        await ExpectCurrentAsync(page, Path.Combine(rig.Path, "Beta", "Inner"));
        await Expect(dialog.GetByText("No folders found here.", new() { Exact = true })).ToBeVisibleAsync();

        await dialog.GetByRole(AriaRole.Button, new() { Name = "Use this folder", Exact = true }).ClickAsync();

        await Expect(Dialog(page)).ToHaveCountAsync(0);
        await Expect(Browse(page)).ToBeFocusedAsync();
        Assert.Equal(Normal(Path.Combine(rig.Path, "Beta", "Inner")), Normal(await Field(page).InputValueAsync()), ignoreCase: true);
        // Choosing a folder only fills the field: the workflow is saved with Save, and until then it is as it was.
        Assert.Equal(savedBefore, (string)(await SetupState.FirstWorkflowAsync(page.Context, BaseUrl, "movie"))["watched_folder"]!);
    }

    [E2EFact]
    public async Task Select_on_a_folder_hands_that_folder_back_and_closes_the_dialog()
    {
        var (page, rig, _) = await OpenEditorAsync();
        using var folders = rig;
        await Field(page).FillAsync(rig.Path);
        await OpenPickerAsync(page);

        await Dialog(page).Locator(".mm-folder-picker__entry").Filter(new() { HasText = "Alpha" }).GetByRole(AriaRole.Button, new() { Name = "Select", Exact = true }).ClickAsync();

        await Expect(Dialog(page)).ToHaveCountAsync(0);
        Assert.Equal(Normal(Path.Combine(rig.Path, "Alpha")), Normal(await Field(page).InputValueAsync()), ignoreCase: true);
    }

    [E2EFact]
    public async Task Close_and_Escape_leave_the_field_as_it_was_and_hand_focus_back_to_Browse()
    {
        var (page, rig, _) = await OpenEditorAsync();
        using var folders = rig;
        await Field(page).FillAsync(rig.Path);

        await OpenPickerAsync(page);
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
        await Expect(Dialog(page)).ToHaveCountAsync(0);
        await Expect(Browse(page)).ToBeFocusedAsync();
        await Expect(Field(page)).ToHaveValueAsync(rig.Path);

        await OpenPickerAsync(page);
        await page.Keyboard.PressAsync("Escape");
        await Expect(Dialog(page)).ToHaveCountAsync(0);
        await Expect(Browse(page)).ToBeFocusedAsync();
        await Expect(Field(page)).ToHaveValueAsync(rig.Path);
    }

    [E2EFact]
    public async Task A_saved_folder_that_cannot_be_opened_falls_back_to_the_drives_and_says_so()
    {
        var (page, rig, _) = await OpenEditorAsync();
        using var folders = rig;
        var missing = Path.Combine(rig.Path, "Gone");
        await Field(page).FillAsync(missing);

        await OpenPickerAsync(page);

        await Expect(CurrentFolder(page)).ToHaveTextAsync("Available drives");
        await Expect(Dialog(page).GetByText($"\"{missing}\" could not be opened. Showing available drives instead.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Select", Exact = true }).First).ToBeVisibleAsync();
        await Axe.AssertNoViolationsAsync(page, "The folder picker after a folder that could not be opened");

        // Going to a path that is not there says the same, from wherever the person was.
        await Dialog(page).GetByRole(AriaRole.Textbox, new() { Name = "Folder path" }).FillAsync(Path.Combine(rig.Path, "Alpha"));
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Go to path", Exact = true }).ClickAsync();
        await ExpectCurrentAsync(page, Path.Combine(rig.Path, "Alpha"));
        await Expect(Dialog(page).GetByText("could not be opened", new() { Exact = false })).ToHaveCountAsync(0);
        await Dialog(page).GetByRole(AriaRole.Textbox, new() { Name = "Folder path" }).FillAsync(missing);
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Go to path", Exact = true }).ClickAsync();
        await Expect(Dialog(page).GetByText($"\"{missing}\" could not be opened. Showing available drives instead.", new() { Exact = true })).ToBeVisibleAsync();
    }
}
