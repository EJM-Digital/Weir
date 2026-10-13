using System.Text.Json;
using Microsoft.Data.Sqlite;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Tests;

/// <summary>
/// A start that stops somewhere before the server answers must leave in weir.log how far it got. The log is the only thing an
/// operator has when the tray reports that the server never answered, so a step that blocks has to be preceded by a line that
/// says it is about to run.
/// </summary>
public sealed class StartupTraceTests
{
    [Fact]
    public async Task A_start_says_it_has_begun_and_that_it_is_opening_the_database_before_the_database_is_opened()
    {
        await using var server = await WeirTestServer.StartAsync(freshDatabase: true);

        var messages = LogMessages(server.Home);

        var begun = messages.FindIndex(message => message.StartsWith("Weir ", StringComparison.Ordinal) && message.Contains("is starting", StringComparison.Ordinal));
        var opening = messages.FindIndex(message => message.StartsWith("Opening the database", StringComparison.Ordinal));
        var opened = messages.FindIndex(message => message.StartsWith("Created database schema", StringComparison.Ordinal));
        Assert.True(begun >= 0, $"The log never says Weir began to start: {string.Join(" | ", messages)}");
        Assert.True(opening > begun, $"'Opening the database' does not follow the start: {string.Join(" | ", messages)}");
        Assert.True(opened > opening, $"The database was reported open before the line that says it is being opened: {string.Join(" | ", messages)}");
    }

    [Fact]
    public async Task A_start_that_is_refused_for_its_database_leaves_the_start_and_the_reason_in_the_log()
    {
        var home = Path.Join(Path.GetTempPath(), "weir-api-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<DatabaseSchemaMismatchException>(() => WeirTestServer.StartAsync(home: home, prepareHome: folder =>
            {
                var databasePath = Path.Join(folder, "data", "weir.sqlite3");
                Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
                using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE alembic_version (version_num VARCHAR(32) NOT NULL); INSERT INTO alembic_version VALUES ('0100_future');";
                command.ExecuteNonQuery();
            }));

            // The progress file is left where the start stopped, which is what the tray reads to say where it was.
            Assert.Contains("\"phase\":\"opening_database\"", File.ReadAllText(Path.Join(home, "startup-progress.json")), StringComparison.Ordinal);
            var messages = LogMessages(home);
            Assert.Contains(messages, message => message.Contains("is starting", StringComparison.Ordinal));
            Assert.Contains(messages, message => message.StartsWith("Opening the database", StringComparison.Ordinal));
            Assert.Contains(messages, message => message.StartsWith("Weir cannot open the database", StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(home, recursive: true);
        }
    }

    private static List<string> LogMessages(string home)
    {
        using var stream = new FileStream(Path.Join(home, "logs", "weir.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var messages = new List<string>();
        while (reader.ReadLine() is { Length: > 0 } line)
        {
            using var document = JsonDocument.Parse(line);
            messages.Add(document.RootElement.GetProperty("message").GetString() ?? string.Empty);
        }

        return messages;
    }
}
