using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Processing;

internal sealed partial class Scenario
{
    /// <summary>
    /// Stops the server to read SQLite, once no worker holds a job. A file's row reaches its final status before the job that wrote it is
    /// released, and a server stopped in between requeues that job when it starts again. A job still waiting for its turn is not waited
    /// for: one booked for later (a look at a file that has gone) stays pending for as long as the test reads.
    /// </summary>
    public async Task<StoppedDatabase> StopForDatabaseAsync()
    {
        await WaitForNoJobHeldAsync();
        return await Server.StopForDatabaseAsync();
    }

    /// <summary>
    /// Runs SQL on the database with the server stopped, for a row only an earlier action of a person would have made (a file kept
    /// by choice, say). The server is started again, at a new address, and the scenario signs in to it.
    /// A job a worker still holds when the server stops is leased again at the next start and runs a second time, and one that is waiting for
    /// its turn can run after the seed, and either can write the row being seeded, so every job is waited for first.
    /// </summary>
    public async Task ExecuteAfterStopAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await WaitForNoJobInFlightAsync();
        await using (var database = await Server.StopForDatabaseAsync())
        {
            SeedSql.Execute(database.Connection, sql, parameters);
        }

        Admin.Dispose();
        Admin = Server.CreateClient();
        await Admin.LoginAsync();
    }
}
