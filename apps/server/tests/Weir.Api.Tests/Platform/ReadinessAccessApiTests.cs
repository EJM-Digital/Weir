using System.Net;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>The detailed readiness report is for signed-in accounts, and an account whose role Weir does not know is turned away with the reason.</summary>
public sealed class ReadinessAccessApiTests
{
    private const string Readiness = "/api/v1/system/readiness";

    [Fact]
    public async Task Anyone_signed_in_may_read_the_detailed_report_and_nobody_else()
    {
        await using var server = await StartServerAsync();

        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Get, Readiness, Callers.Viewer);
    }

    [Fact]
    public async Task An_account_with_a_role_weir_does_not_know_is_refused_with_that_reason()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedViewerAsync(server);
        var viewer = new ApiTestClient(server);
        await viewer.SignInAsync("bob", ViewerPassword);
        await TestDatabase.ExecuteAsync(server, "UPDATE users SET role = 'superuser' WHERE username = 'bob'");

        using var response = await viewer.GetAsync(Readiness);

        Assert.Equal((HttpStatusCode.Forbidden, "Invalid account role."), (response.StatusCode, await Detail(response)));
    }
}
