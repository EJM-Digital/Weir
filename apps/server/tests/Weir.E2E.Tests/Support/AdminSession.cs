using Weir.Contract.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>A session of its own, signed in as the administrator the browser tests use, for a test that arranges or reads what the screens show.</summary>
public static class AdminSession
{
    /// <summary>Creates the administrator if this is the first thing the test does, and signs in. Dispose it with the test.</summary>
    public static async Task<WeirClient> OpenAsync(string baseUrl)
    {
        var client = new WeirClient(new Uri(baseUrl));
        try
        {
            await client.EnsureAdminAsync(Navigation.BootstrapUser, Navigation.BootstrapPassword);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
