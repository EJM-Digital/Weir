using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>
/// A server whose database stays locked past its busy timeout answers a media manager with a retryable 503, and any other failure
/// the server did not foresee with a JSON 500, so a manager can tell "Weir is busy, try again" from "Weir is broken" (#964).
/// The busy timeout is the shortest the server accepts, one second, so the wait is short; the lock is held by this test, outside
/// the server, the way a backup tool would hold it.
/// </summary>
[ContractArea("media_managers")]
public sealed class DatabaseBusyTests(DatabaseBusyFixture fixture) : IClassFixture<DatabaseBusyFixture>
{
    private const string BusyCode = "database_busy";
    private const string InternalCode = "internal_error";
    private const string UnhandledLogLine = "Unhandled request failure";

    private WeirServer Server => fixture.Server;

    private static JsonObject Outcome() => new()
    {
        ["outcome"] = "imported",
        ["occurredUtc"] = "2026-10-14T09:00:00Z",
    };

    [Fact]
    public async Task A_hand_off_sent_while_the_database_is_locked_is_answered_503_with_a_retry_after()
    {
        var movies = await fixture.MoviesAsync();
        var handoffId = Handoffs.NewId();
        var source = Path.Combine(movies.Watched, handoffId, "film.mkv");
        WeirResponse busy;
        using (HeldWriteLock.Take(Server))
        {
            busy = await Handoffs.HandOffResponseAsync(Server, handoffId, source);
        }

        AssertBusy(busy);

        // Retryable: the same hand-off is accepted once the lock is gone.
        var retried = await Handoffs.HandOffResponseAsync(Server, handoffId, source);
        Assert.True(retried.Status == HttpStatusCode.OK, retried.ToString());
    }

    [Fact]
    public async Task A_hand_off_no_workflow_watches_is_refused_400_before_it_writes_whether_or_not_the_database_is_locked()
    {
        var movies = await fixture.MoviesAsync();
        var handoffId = Handoffs.NewId();
        var nowhere = Path.Combine(Path.GetDirectoryName(movies.Watched)!, "no-workflow-watches-this", "film.mkv");
        var unlocked = await Handoffs.HandOffResponseAsync(Server, handoffId, nowhere);
        WeirResponse locked;
        using (HeldWriteLock.Take(Server))
        {
            locked = await Handoffs.HandOffResponseAsync(Server, handoffId, nowhere);
        }

        // The refusal is about the folder, so it is a client error with or without the lock, and says so in the same words; a busy
        // database is 503 with a Retry-After and the busy code, never this.
        Assert.True(unlocked.Status == HttpStatusCode.BadRequest, unlocked.ToString());
        Assert.True(locked.Status == HttpStatusCode.BadRequest, locked.ToString());
        Assert.Equal((string?)unlocked.Fields["detail"], (string?)locked.Fields["detail"]);
        Assert.Null(locked.Fields["code"]);
        Assert.Null(locked.Header("Retry-After"));
        Assert.Equal(HttpStatusCode.NotFound, (await Handoffs.StatusResponseAsync(Server, handoffId)).Status);
    }

    [Fact]
    public async Task An_outcome_sent_while_the_database_is_locked_is_answered_503_with_a_retry_after()
    {
        WeirResponse busy;
        using (HeldWriteLock.Take(Server))
        {
            using var manager = Server.CreateClient();
            busy = await manager.PostAsync($"{Handoffs.StatusPath("any-hand-off")}/outcome", Outcome(), Handoffs.Secret);
        }

        AssertBusy(busy);
    }

    [Fact]
    public async Task A_busy_database_is_logged_once_as_a_warning_and_never_as_an_unhandled_failure()
    {
        var marker = $"busy-{Guid.NewGuid():N}";
        using (HeldWriteLock.Take(Server))
        {
            using var manager = Server.CreateClient();
            var busy = await manager.PostAsync(
                $"{Handoffs.StatusPath(marker)}/outcome",
                Outcome(),
                new Dictionary<string, string>(Handoffs.Secret) { ["X-Request-ID"] = marker });
            AssertBusy(busy);
        }

        var lines = Server.LogText()
            .Split(Environment.NewLine)
            .Where(line => line.Contains(marker, StringComparison.Ordinal))
            .ToList();
        var warning = Assert.Single(lines, line => line.Contains("WARNING", StringComparison.Ordinal));
        Assert.Contains("/intake/handoffs/{source_key}/{handoff_id}/outcome", warning, StringComparison.Ordinal);
        Assert.Contains("waited", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(UnhandledLogLine, Server.LogText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_the_server_did_not_foresee_is_answered_500_in_JSON_with_nothing_of_the_failure_in_it()
    {
        await using var server = await WeirServer.StartNewAsync(Handoffs.SecretEnvironment);
        await using (var database = await server.StopForDatabaseAsync())
        {
            SeedSql.Execute(database.Connection, "PRAGMA foreign_keys=OFF");
            SeedSql.Execute(database.Connection, "DROP TABLE media_manager_handoffs");
        }

        using var manager = server.CreateClient();
        var failed = await manager.PostAsync($"{Handoffs.StatusPath("any-hand-off")}/outcome", Outcome(), Handoffs.Secret);

        Assert.True(failed.Status == HttpStatusCode.InternalServerError, failed.ToString());
        Assert.Equal("application/json", failed.Header("Content-Type"));
        Assert.Equal(InternalCode, (string?)failed.Fields["code"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)failed.Fields["detail"]));
        Assert.Equal(["code", "detail"], failed.Fields.Select(field => field.Key).Order().ToArray());
        Assert.DoesNotContain("media_manager_handoffs", failed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SQLite", failed.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(UnhandledLogLine, server.LogText(), StringComparison.Ordinal);
    }

    private static void AssertBusy(WeirResponse busy)
    {
        Assert.True(busy.Status == HttpStatusCode.ServiceUnavailable, busy.ToString());
        Assert.Equal("application/json", busy.Header("Content-Type"));
        var retryAfter = Assert.IsType<string>(busy.Header("Retry-After"));
        Assert.True(int.TryParse(retryAfter, out var seconds) && seconds is >= 1 and <= 10, $"Retry-After was '{retryAfter}'");
        Assert.Equal(BusyCode, (string?)busy.Fields["code"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)busy.Fields["detail"]));
        Assert.Equal(["code", "detail"], busy.Fields.Select(field => field.Key).Order().ToArray());
        Assert.DoesNotContain("SQLite", busy.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(busy.Header("X-Request-ID"));
    }
}

/// <summary>
/// A server with an instance-wide webhook secret, a Movies library that watches a folder of the fixture's, and the shortest busy
/// timeout the server accepts.
/// </summary>
public sealed class DatabaseBusyFixture() : ManagerServerFixture(new Dictionary<string, string>(Handoffs.SecretEnvironment)
{
    ["WEIR_SQLITE_BUSY_TIMEOUT_SECONDS"] = "1",
})
{
    private LibraryFolders? _movies;

    internal async Task<LibraryFolders> MoviesAsync()
    {
        if (_movies is { } made)
        {
            return made;
        }

        var movies = Library("movies");
        using var admin = await Server.CreateAdminClientAsync();
        await ProcessingLibraries.EnsureAsync(admin, "Movies", "movie", movies);
        return _movies = movies;
    }
}
