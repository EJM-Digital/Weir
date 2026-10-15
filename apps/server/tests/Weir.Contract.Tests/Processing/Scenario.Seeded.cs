using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Processing;

internal sealed partial class Scenario
{
    /// <summary>
    /// Runs SQL on the database with the server stopped, for a row only an earlier action of a person would have made (a file kept
    /// by choice, say). The server is started again, at a new address, and the scenario signs in to it.
    /// </summary>
    public async Task ExecuteAfterStopAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using (var database = await Server.StopForDatabaseAsync())
        {
            SeedSql.Execute(database.Connection, sql, parameters);
        }

        Admin.Dispose();
        Admin = Server.CreateClient();
        await Admin.LoginAsync();
    }
}
