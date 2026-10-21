using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Infrastructure.Tests;

namespace Weir.Api.Tests.Startup;

/// <summary>
/// The steps the server takes against its database once the schema is current. A database that cannot answer one of them is
/// a fault to log, not a reason to refuse to start: the person can still open Weir and see what is wrong.
/// </summary>
public sealed class StartupStepFailureTests
{
    [Theory]
    [InlineData("server_starts", "record_server_start")]
    [InlineData("user_sessions", "inactive_session_cleanup")]
    [InlineData("media_manager_connections", "connection_names")]
    [InlineData("users", "setup_code_gate")]
    public async Task A_start_up_step_the_database_cannot_answer_is_logged_and_the_server_still_starts(string missingTable, string step)
    {
        var logger = new CapturingLoggerFactory();

        await using var server = await WeirTestServer.StartAsync(
            prepareHome: home => DropTable(home, missingTable),
            configureServices: services => services.AddSingleton<ILoggerFactory>(logger));

        Assert.Contains(
            logger.Entries,
            entry => entry.Category == "Weir.Host.Startup" && entry.Level == LogLevel.Error && entry.Message.EndsWith($"step={step}", StringComparison.Ordinal));
        using var health = await server.Client.GetAsync("/health");
        Assert.True(health.IsSuccessStatusCode);
    }

    private static void DropTable(string home, string table)
    {
        var path = Path.Join(home, "data", "weir.sqlite3");
        MigratedDatabaseTemplate.CopyTo(path);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE {table}";
        command.ExecuteNonQuery();
    }
}
