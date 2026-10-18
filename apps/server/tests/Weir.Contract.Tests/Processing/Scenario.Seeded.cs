using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Processing;

internal sealed partial class Scenario
{
    /// <summary>
    /// Runs SQL on the database with the server stopped, for a row only an earlier action of a person would have made (a file kept
    /// by choice, say). The server is started again, at a new address, and the scenario signs in to it.
    /// A job a worker still holds when the server stops is leased again at the next start and runs a second time, and it can write the
    /// row being seeded, so the jobs are waited for first.
    /// </summary>
    public async Task ExecuteAfterStopAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        IReadOnlyList<string> statuses = [];
        await new JobWait().UntilAsync(
            async () => statuses = await JobStatusesAsync(),
            () => Task.FromResult(statuses.All(status => status is not ("pending" or "leased")) ? "idle" : null),
            "the jobs to finish before the server is stopped");
        await using (var database = await Server.StopForDatabaseAsync())
        {
            SeedSql.Execute(database.Connection, sql, parameters);
        }

        Admin.Dispose();
        Admin = Server.CreateClient();
        await Admin.LoginAsync();
    }
}
