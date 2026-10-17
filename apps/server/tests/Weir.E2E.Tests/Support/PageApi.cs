using System.Text.Json;
using Microsoft.Playwright;

namespace Weir.E2E.Tests.Support;

/// <summary>Reads what the server says, as the signed-in person in a browser context, so a test can compare what a screen shows with what the server holds.</summary>
public static class PageApi
{
    public static async Task<JsonElement> GetAsync(IBrowserContext context, string baseUrl, string path)
    {
        await using var response = await context.APIRequest.GetAsync($"{baseUrl}{path}");
        Assert.True(response.Ok, $"GET {path} answered {response.Status}.");
        return JsonDocument.Parse(await response.TextAsync()).RootElement.Clone();
    }
}
