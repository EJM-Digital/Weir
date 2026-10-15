using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>A server for the download client tests, with the admin and the viewer already seeded (seeding the viewer restarts the server, so the admin comes first).</summary>
public sealed class DownloadClientServerFixture() : ManagerServerFixture(ManagerEnvironment.NoWebhookSecret)
{
    protected override async Task ServerStartedAsync(WeirServer server)
    {
        using (await server.CreateAdminClientAsync())
        {
        }

        await SeededAccounts.EnsureViewerAsync(server);
    }
}
