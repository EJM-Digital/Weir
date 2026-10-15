using System.Net;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// The four ways a state-changing request is turned away before it can do anything: no session, a viewer's session,
/// no confirmation token, and a token that is not the session's. Each request carries the body the web sends.
/// </summary>
internal static class OperatorPostRefusals
{
    /// <summary>Sends the request each of the four ways and checks the answer; the caller then checks that nothing changed.</summary>
    /// <param name="server">A server with the admin account seeded and no viewer yet.</param>
    /// <param name="path">The route under test.</param>
    /// <param name="bodyFor">The request body the web sends, given the confirmation token to put in it.</param>
    public static async Task AssertAsync(WeirTestServer server, string path, Func<string, object> bodyFor)
    {
        await TestDatabase.SeedViewerAsync(server);

        var anonymous = new ApiTestClient(server);
        using (var response = await anonymous.PostAsync(path, bodyFor(await anonymous.CsrfAsync())))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var viewer = new ApiTestClient(server);
        await viewer.SignInAsync("bob", ApiTestClient.ViewerPassword);
        using (var response = await viewer.PostAsync(path, bodyFor(await viewer.CsrfAsync())))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("Forbidden.", await ApiTestClient.Detail(response));
        }

        var admin = new ApiTestClient(server);
        await admin.SignInAsync();
        using (var response = await admin.PostAsync(path, bodyFor("not-the-session-token")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("Your confirmation token expired. Refresh the page and try again.", await ApiTestClient.Detail(response));
        }

        using (var response = await admin.PostAsync(path, new { }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }
    }
}
