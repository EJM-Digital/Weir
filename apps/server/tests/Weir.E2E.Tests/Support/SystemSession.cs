using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>
/// A server of the test's own with its administrator signed in, in a page on one tab of System and in a session of its own through the API, for a
/// test whose changes must not reach the tests after it. <see cref="Requests"/> records what the page sends from the moment the tab is open.
/// </summary>
public sealed class SystemSession : IAsyncDisposable
{
    private SystemSession(WeirServer server, IPage page, WeirClient admin, RequestLog requests)
    {
        Server = server;
        Page = page;
        Admin = admin;
        Requests = requests;
    }

    public WeirServer Server { get; }

    public IPage Page { get; }

    /// <summary>The administrator in a session of its own, which reads what the server holds and makes the changes another client would.</summary>
    public WeirClient Admin { get; }

    public RequestLog Requests { get; }

    /// <summary>Starts a server of its own (with <paramref name="environment"/> on top of the shared server's), signs in with the live stream open and opens <paramref name="tab"/>.</summary>
    public static async Task<SystemSession> OpenAsync(IPage page, string tab, params (string Name, string Value)[] environment) =>
        await OpenAsync(page, await E2EServer.StartOwnAsync(environment), tab);

    /// <summary>
    /// Signs in to <paramref name="server"/>, which the session takes over and stops when it is disposed, with the live stream open, and opens <paramref name="tab"/>.
    /// </summary>
    public static async Task<SystemSession> OpenAsync(IPage page, WeirServer server, string tab)
    {
        try
        {
            var baseUrl = server.BaseUrl.GetLeftPart(UriPartial.Authority);
            await page.RunAndWaitForResponseAsync(
                () => Navigation.EnsureSignedInAsync(page, baseUrl),
                response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
            var admin = await AdminSession.OpenAsync(baseUrl);
            var requests = RequestLog.Watch(page);
            await Navigation.OpenTabAsync(page, "System", tab);
            return new SystemSession(server, page, admin, requests);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>The server's own reading of <paramref name="path"/>, which must answer 200.</summary>
    public async Task<JsonObject> ReadAsync(string path)
    {
        var read = await Admin.GetAsync(path);
        Assert.True(read.Status == HttpStatusCode.OK, read.ToString());
        return read.Fields;
    }

    public async ValueTask DisposeAsync()
    {
        Admin.Dispose();
        await Server.DisposeAsync();
    }
}
