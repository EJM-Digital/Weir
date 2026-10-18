using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Processing;

internal sealed partial class Scenario
{
    /// <summary>
    /// Stops the server to read or change SQLite, once no worker holds a job. A file's row reaches its final status before the job that
    /// wrote it is released, and a server stopped in between requeues that job when it starts again, so the pass runs a second time and
    /// writes over what the test went to the database for.
    /// </summary>
    public async Task<StoppedDatabase> StopForDatabaseAsync()
    {
        await new JobWait().UntilAsync(
            async () => [.. (await JobsAsync()).Select(row => (string)row["status"]!)],
            async () => (await JobsAsync()).Any(row => (string)row["status"]! == "leased") ? null : string.Empty,
            "the workers to put down their jobs");
        return await Server.StopForDatabaseAsync();
    }

    /// <summary>
    /// Runs SQL on the database with the server stopped, for a row only an earlier action of a person would have made (a file kept
    /// by choice, say). The server is started again, at a new address, and the scenario signs in to it.
    /// </summary>
    public async Task ExecuteAfterStopAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using (var database = await StopForDatabaseAsync())
        {
            SeedSql.Execute(database.Connection, sql, parameters);
        }

        Admin.Dispose();
        Admin = Server.CreateClient();
        await Admin.LoginAsync();
    }
}
