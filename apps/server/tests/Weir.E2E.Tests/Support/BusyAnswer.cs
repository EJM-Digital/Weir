using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Weir.E2E.Tests.Support;

/// <summary>The answer a write gets when it gives up on a database held busy: a 503 with the busy code and its own sentence, which the pages show as it is.</summary>
internal static class BusyAnswer
{
    public static async Task<string> DetailAsync(IResponse refused)
    {
        Assert.Equal(503, refused.Status);
        var body = JsonNode.Parse(await refused.TextAsync())!;
        Assert.Equal("database_busy", (string)body["code"]!);
        return (string)body["detail"]!;
    }
}
