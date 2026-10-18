using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests.Support;

/// <summary>Setup › Workflows › File paths as a person works it: the Add workflow button, a row's Edit, and the editor's folded groups.</summary>
public static class WorkflowEditor
{
    public const string FormId = "processing-library-form";

    /// <summary>Opens the File paths tab and waits for the list.</summary>
    public static async Task OpenFilePathsAsync(IPage page, string baseUrl)
    {
        await Navigation.EnsureSignedInAsync(page, baseUrl);
        await Navigation.OpenTabAsync(page, "Workflows", "File paths");
        await Expect(page.GetByTestId("processing-libraries-section")).ToBeVisibleAsync();
    }

    public static ILocator Form(IPage page) => page.GetByTestId(FormId);

    public static ILocator Row(IPage page, long id) => page.GetByTestId($"processing-library-{id}");

    /// <summary>Add workflow, Local folders, Continue: the editor for a new Weir-only workflow.</summary>
    public static async Task<ILocator> OpenNewAsync(IPage page)
    {
        await page.GetByTestId("processing-library-add").ClickAsync();
        var choice = page.GetByTestId("add-workflow-choice");
        await Expect(choice).ToBeVisibleAsync();
        await choice.GetByRole(AriaRole.Radio, new() { Name = "Local folders" }).CheckAsync();
        await page.GetByTestId("add-workflow-continue").ClickAsync();
        var form = Form(page);
        await Expect(form).ToBeVisibleAsync();
        await Expect(form.GetByRole(AriaRole.Heading, new() { Name = "Add workflow", Exact = true })).ToBeVisibleAsync();
        return form;
    }

    /// <summary>The row's Edit button, and the editor it opens.</summary>
    public static async Task<ILocator> OpenEditAsync(IPage page, long id)
    {
        await Row(page, id).GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
        var form = Form(page);
        await Expect(form.GetByRole(AriaRole.Heading, new() { Name = "Edit workflow", Exact = true })).ToBeVisibleAsync();
        return form;
    }

    /// <summary>Opens a folded group of the editor by its heading, unless it is already open.</summary>
    public static async Task OpenGroupAsync(ILocator form, string title)
    {
        var group = form.Locator("details").Filter(new() { Has = form.Page.GetByRole(AriaRole.Heading, new() { Name = title, Exact = true }) }).First;
        if (!await group.EvaluateAsync<bool>("element => element.open"))
        {
            await group.GetByRole(AriaRole.Heading, new() { Name = title, Exact = true }).ClickAsync();
        }

        await Expect(group).ToHaveAttributeAsync("open", string.Empty);
    }

    public static ILocator Text(ILocator form, string label) => form.GetByLabel(label, new() { Exact = true });

    public static ILocator Check(ILocator form, string label) => form.GetByRole(AriaRole.Checkbox, new() { Name = label });

    public static ILocator Choice(ILocator form, string label) => form.GetByRole(AriaRole.Combobox, new() { Name = label });

    public static ILocator Save(IPage page) => page.GetByTestId("processing-library-save");

    /// <summary>The workflow the server holds, read back as it is stored.</summary>
    public static async Task<JsonObject> ReadAsync(WeirClient client, long id)
    {
        var read = await client.GetAsync($"{WeirClient.Api}/processing/libraries/{id}");
        Assert.True(read.Status == System.Net.HttpStatusCode.OK, read.ToString());
        return read.Fields;
    }

    /// <summary>The id of the workflow of this name, once the server lists it.</summary>
    public static async Task<long> IdOfAsync(WeirClient client, string name)
    {
        var listed = await client.GetAsync($"{WeirClient.Api}/processing/libraries");
        Assert.True(listed.Status == System.Net.HttpStatusCode.OK, listed.ToString());
        return (long)listed.Elements.Select(row => row!.AsObject()).Single(row => (string)row["name"]! == name)["id"]!;
    }
}
