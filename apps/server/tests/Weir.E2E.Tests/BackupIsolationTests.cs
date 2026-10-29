using System.Net;
using Microsoft.Data.Sqlite;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;

namespace Weir.E2E.Tests;

/// <summary>
/// A test starts with no configuration backups. A scheduled backup an earlier test's schedule made, landing whenever the periodic tick ran,
/// once stayed for the next tests and failed the one that expects "No automatic snapshots yet."
/// </summary>
public sealed class BackupIsolationTests(E2EServer server) : E2ETestBase(server)
{
    [E2EFact]
    public async Task Resetting_between_tests_forgets_the_backups_an_earlier_test_made()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var made = await admin.PostWithCsrfAsync($"{WeirClient.Api}/suite/configuration-backups");
        Assert.True(made.Status == HttpStatusCode.Created, made.ToString());
        var folder = Path.Join(Path.GetDirectoryName(Path.GetDirectoryName(Server.DatabasePath)), "backups", "suite-configuration");
        Assert.NotEmpty(Directory.GetFiles(folder));

        E2EDatabase.ResetPerTestState(Server.DatabasePath);

        Assert.Empty(Directory.GetFiles(folder));
        using var connection = new SqliteConnection($"Data Source={Server.DatabasePath};Pooling=False");
        connection.Open();
        Assert.Equal(0L, SeedSql.Scalar(connection, "SELECT count(*) FROM suite_configuration_backup"));
    }
}
