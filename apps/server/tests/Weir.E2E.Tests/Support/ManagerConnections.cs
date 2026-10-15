using System.Net;
using Weir.Contract.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>Removes a media manager connection the way a person has to: unlink the workflows that come from it, then remove it.</summary>
public static class ManagerConnections
{
    /// <summary>
    /// A connection a workflow still comes from is refused, so its workflows are unlinked first (they stay, switched off). The caller
    /// must have no hand-off from the connection in progress.
    /// </summary>
    public static async Task RemoveAsync(WeirClient client, long connectionId)
    {
        var route = $"{WeirClient.Api}/media-managers/connections/{connectionId}";
        var unlinked = await client.PostWithCsrfAsync($"{route}/unlink-workflows");
        Assert.True(unlinked.Status == HttpStatusCode.OK, unlinked.ToString());
        var removed = await client.DeleteWithCsrfBodyAsync(route);
        Assert.True(removed.Status == HttpStatusCode.NoContent, removed.ToString());
    }
}
