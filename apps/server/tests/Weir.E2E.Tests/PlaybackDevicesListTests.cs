using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>Rules › Playback devices when the person keeps a list of their own in Weir's data folder: the page says where the list comes from, and says so when it is empty.</summary>
public sealed class PlaybackDevicesListTests(E2EServer server) : E2ETestBase(server)
{
    private const string Custom = "This list comes from your own direct-play-devices.json in the Weir data folder.";

    private string ListFile => Path.Combine(Server.Home, "direct-play-devices.json");

    private async Task<IPage> OpenDevicesAsync()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenTabAsync(page, "Rules", "Playback devices");
        await Expect(page.GetByTestId("processing-direct-play-section")).ToBeVisibleAsync();
        return page;
    }

    [E2EFact]
    public async Task The_shipped_list_does_not_say_it_is_the_persons_own()
    {
        var page = await OpenDevicesAsync();

        await Expect(page.GetByTestId("processing-direct-play-customised")).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Checkbox, new() { Name = "Apple TV 4K", Exact = true })).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task A_list_of_the_persons_own_replaces_the_shipped_one_and_says_where_it_comes_from()
    {
        await File.WriteAllTextAsync(ListFile, """{"devices":[{"id":"e2e_box","name":"E2E living room box","note":"A box made up for this test.","source":"builtin","containers":{"yes":["mkv"],"maybe":[]},"audio":{"yes":["aac"],"maybe":[]}}]}""");
        try
        {
            var page = await OpenDevicesAsync();

            await Expect(page.GetByTestId("processing-direct-play-customised")).ToHaveTextAsync(Custom);
            await Expect(page.GetByTestId("processing-direct-play-section").GetByRole(AriaRole.Checkbox)).ToHaveCountAsync(1);
            await Expect(page.GetByRole(AriaRole.Checkbox, new() { Name = "E2E living room box", Exact = true })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Checkbox, new() { Name = "Apple TV 4K", Exact = true })).ToHaveCountAsync(0);
        }
        finally
        {
            File.Delete(ListFile);
        }
    }

    [E2EFact]
    public async Task A_list_of_the_persons_own_with_no_devices_says_so()
    {
        await File.WriteAllTextAsync(ListFile, """{"devices":[]}""");
        try
        {
            var page = await OpenDevicesAsync();

            await Expect(page.GetByTestId("processing-direct-play-customised")).ToHaveTextAsync(Custom);
            await Expect(page.GetByTestId("processing-direct-play-section").GetByText("No devices are listed.", new() { Exact = true })).ToBeVisibleAsync();
            await Expect(page.GetByTestId("processing-direct-play-section").GetByRole(AriaRole.Checkbox)).ToHaveCountAsync(0);
        }
        finally
        {
            File.Delete(ListFile);
        }
    }
}
