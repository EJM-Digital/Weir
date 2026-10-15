using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Auth;

/// <summary>
/// One server for the route walk, set up the way an install is: an administrator, an operator and a viewer, and browser origins that
/// are trusted, so a request from any other origin is one the server has to refuse. Each account has a browser-like session open
/// from the start (the trusted <c>Origin</c> and <c>X-Requested-With</c> on every request), and so does a caller who is signed in as nobody.
/// </summary>
public sealed class RouteAccessFixture : ServerFixture, IAsyncLifetime
{
    public const string TrustedOrigin = "http://localhost:9000";
    public const string ForeignOrigin = "http://elsewhere.test";

    private readonly List<WeirClient> _sessions = [];

    public WeirClient Anonymous { get; private set; } = null!;

    public WeirClient Viewer { get; private set; } = null!;

    public WeirClient Operator { get; private set; } = null!;

    public WeirClient Admin { get; private set; } = null!;

    protected override IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>
    {
        ["WEIR_TRUSTED_BROWSER_ORIGINS"] = TrustedOrigin,
    };

    async Task IAsyncLifetime.InitializeAsync()
    {
        await InitializeAsync();
        try
        {
            await using (var database = await Server.StopForDatabaseAsync())
            {
                SeededAccounts.EnsureAdmin(database.Connection);
                SeededAccounts.EnsureOperator(database.Connection);
                SeededAccounts.EnsureViewer(database.Connection);
            }

            Anonymous = Open();
            Admin = await SignInAsync(WeirClient.AdminUsername, WeirClient.AdminPassword);
            Operator = await SignInAsync(SeededAccounts.OperatorUsername, SeededAccounts.ViewerPassword);
            Viewer = await SignInAsync(SeededAccounts.ViewerUsername, SeededAccounts.ViewerPassword);
        }
        catch
        {
            await ((IAsyncLifetime)this).DisposeAsync();
            throw;
        }
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        foreach (var session in _sessions)
        {
            session.Dispose();
        }

        return DisposeAsync();
    }

    private WeirClient Open()
    {
        var client = Server.CreateClient(new Dictionary<string, string>
        {
            ["Origin"] = TrustedOrigin,
            ["X-Requested-With"] = "XMLHttpRequest",
        });
        _sessions.Add(client);
        return client;
    }

    private async Task<WeirClient> SignInAsync(string username, string password)
    {
        var client = Open();
        await client.LoginAsync(username, password);
        return client;
    }
}
