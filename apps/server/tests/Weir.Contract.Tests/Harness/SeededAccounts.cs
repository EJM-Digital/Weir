using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// The two accounts tests seed because no API creates a user without an admin: the admin <c>alice</c> and the read-only
/// viewer <c>bob</c>. The harness has no Argon2 of its own, so each password comes with a precomputed hash (Argon2id,
/// PHC format as <c>users.password_hash</c> holds it: 1 lane, 32-byte hash, 16-byte salt). They are made at the smallest
/// cost the server's verifier accepts (time 1, memory 8 KiB), because the server reads the cost from the stored hash and a
/// seeded account would otherwise spend about a second of every sign-in on a cost nothing here is testing. The cost the
/// product itself uses is what bootstrap, password change and recovery produce, and the tests of those sign in with it.
/// </summary>
public static class SeededAccounts
{
    public const string ViewerUsername = "bob";
    public const string ViewerPassword = "viewer-password-here";

    /// <summary>Hash of <see cref="ViewerPassword"/>.</summary>
    public const string ViewerPasswordHash =
        "$argon2id$v=19$m=8,t=1,p=1$CKLFy+69uhpu/wDhwA+ONQ$ODzXaenqMMKnLWrYR3BM1CObVzwGjmO22tK+WRTS5CM";

    /// <summary>Hash of <see cref="WeirClient.AdminPassword"/>.</summary>
    public const string AdminPasswordHash =
        "$argon2id$v=19$m=8,t=1,p=1$crmvJg0OQnYs9UpHYczjHg$z2lZc0sScTtRjmMABkRghwK/izA76Zub+n4ALQehTgQ";

    /// <summary>An operator, who signs in with <see cref="ViewerPassword"/>: the hash is the same.</summary>
    public const string OperatorUsername = "carol";

    /// <summary>Adds the viewer to a stopped database when it has none.</summary>
    public static void EnsureViewer(SqliteConnection connection)
    {
        if (!UserExists(connection, ViewerUsername))
        {
            SeedSql.InsertUser(connection, ViewerUsername, ViewerPasswordHash, "viewer");
        }
    }

    /// <summary>Adds the operator to a stopped database when it has none.</summary>
    public static void EnsureOperator(SqliteConnection connection)
    {
        if (!UserExists(connection, OperatorUsername))
        {
            SeedSql.InsertUser(connection, OperatorUsername, ViewerPasswordHash, "operator");
        }
    }

    /// <summary>Adds the admin to a stopped database when it has none. Bootstrap is refused once any user exists, so seeding the viewer alone leaves no way in.</summary>
    public static void EnsureAdmin(SqliteConnection connection)
    {
        if (!UserExists(connection, WeirClient.AdminUsername))
        {
            SeedSql.InsertUser(connection, WeirClient.AdminUsername, AdminPasswordHash, "admin");
        }
    }

    /// <summary>Seeds the viewer while the server is stopped; the server comes back on a new port, so create clients afterwards.</summary>
    public static async Task EnsureViewerAsync(WeirServer server)
    {
        await using var database = await server.StopForDatabaseAsync();
        EnsureViewer(database.Connection);
    }

    /// <summary>A client signed in as the viewer.</summary>
    public static async Task<WeirClient> SignInViewerAsync(WeirServer server)
    {
        var client = server.CreateClient();
        try
        {
            await client.LoginAsync(ViewerUsername, ViewerPassword);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return client;
    }

    private static bool UserExists(SqliteConnection connection, string username) =>
        Convert.ToInt64(
            SeedSql.Scalar(connection, "SELECT COUNT(*) FROM users WHERE username = $username", ("$username", username)),
            CultureInfo.InvariantCulture) != 0;
}
