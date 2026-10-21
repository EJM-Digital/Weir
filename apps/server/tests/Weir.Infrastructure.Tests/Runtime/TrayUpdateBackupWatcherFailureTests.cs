using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>
/// What the tray's update backup request does when something in the way is not as it should be: a folder that cannot be watched, a marker or an
/// answer that cannot be written, a database that cannot be copied, and a request that is not a request.
/// </summary>
public sealed class TrayUpdateBackupWatcherFailureTests : IDisposable
{
    private const string Id = "0a1b2c3d";

    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly StoreFixture _store = new();
    private readonly CapturingLogger<TrayUpdateBackupWatcher> _log = new();
    private TrayUpdateBackupWatcher? _watcher;

    private string Home => _store.Options.WeirHome;

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _watcher.Dispose();
        }

        _store.Dispose();
    }

    private async Task StartAsync(WeirOptions? options = null)
    {
        _watcher = new TrayUpdateBackupWatcher(options ?? _store.Options, _store.Database, _store.Clock, _log);
        await _watcher.StartAsync(CancellationToken.None);
    }

    private static string Request(string id = Id) =>
        $"{{\"id\": \"{id}\", \"requested_at\": \"2026-01-15T10:00:00Z\", \"target_version\": \"1.0.0-rc.13\"}}";

    private JsonNode? ResultFor(string id)
    {
        try
        {
            using var stream = new FileStream(Path.Join(Home, TrayUpdateBackupWatcher.ResultFileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var node = JsonNode.Parse(stream);
            return (string?)node?["id"] == id ? node : null;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task<JsonNode> FailedResultAsync()
    {
        JsonNode? result = null;
        await Eventually.ThatAsync(() =>
        {
            result = ResultFor(Id);
            return (string?)result?["state"] is "failed";
        });
        return result!;
    }

    [Fact]
    public async Task A_folder_that_cannot_be_watched_is_logged_and_the_server_goes_on_without_taking_requests()
    {
        var missing = WeirOptionsLoader.Load(new RuntimeEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["WEIR_HOME"] = Path.Join(_store.Home.Path, "not-made"),
                ["WEIR_SESSION_SECRET"] = "store-tests-session-secret-0123456789",
            },
            OperatingSystem.IsWindows(),
            _store.Home.Path,
            _store.Home.Path));

        await StartAsync(missing);
        await _watcher!.ExecuteTask!.WaitAsync(Ceiling);

        Assert.True(_log.Logged(LogLevel.Warning, "could not watch"));
        Assert.False(File.Exists(Path.Join(missing.WeirHome, TrayUpdateBackupWatcher.ReadyFileName)));
    }

    [Fact]
    public async Task A_marker_that_cannot_be_written_is_logged_and_requests_are_still_answered()
    {
        Directory.CreateDirectory(Path.Join(Home, TrayUpdateBackupWatcher.ReadyFileName));

        await StartAsync();
        await File.WriteAllTextAsync(Path.Join(Home, TrayUpdateBackupWatcher.RequestFileName), Request());
        await Eventually.ThatAsync(() => (string?)ResultFor(Id)?["state"] is "saved" or "failed");

        Assert.True(_log.Logged(LogLevel.Warning, "could not say that it can save a copy"));
    }

    [Fact]
    public async Task A_database_that_records_no_revision_cannot_be_copied_and_the_tray_is_told_in_plain_words()
    {
        using (var connection = _store.Database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM schema_version";
            await command.ExecuteNonQueryAsync();
        }

        await StartAsync();
        await File.WriteAllTextAsync(Path.Join(Home, TrayUpdateBackupWatcher.RequestFileName), Request());
        var result = await FailedResultAsync();

        Assert.Null(result["path"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)result["reason"]));
        Assert.DoesNotContain("Exception", (string)result["reason"]!, StringComparison.Ordinal);
        Assert.True(_log.Logged(LogLevel.Error, "could not save a copy of its data"));
    }

    [Fact]
    public async Task An_answer_that_cannot_be_written_is_tried_again_and_then_logged()
    {
        Directory.CreateDirectory(Path.Join(Home, TrayUpdateBackupWatcher.ResultFileName));

        await StartAsync();
        await File.WriteAllTextAsync(Path.Join(Home, TrayUpdateBackupWatcher.RequestFileName), Request());

        await Eventually.ThatAsync(() => _log.Logged(LogLevel.Warning, "could not write its answer"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"id\": \"\", \"requested_at\": \"2026-01-15T10:00:00Z\", \"target_version\": \"1.0.0\"}")]
    [InlineData("{\"id\": 7, \"requested_at\": \"2026-01-15T10:00:00Z\", \"target_version\": \"1.0.0\"}")]
    [InlineData("{\"id\": \"a\", \"requested_at\": \"last tuesday\", \"target_version\": \"1.0.0\"}")]
    [InlineData("{\"id\": \"a\", \"requested_at\": \"2026-01-15T10:00:00Z\", \"target_version\": \"\"}")]
    [InlineData("{\"id\": \"a\", \"requested_at\": \"2026-01-15T10:00:00Z\"}")]
    public async Task A_file_missing_something_a_request_needs_is_taken_away_unanswered(string text)
    {
        await StartAsync();
        var request = Path.Join(Home, TrayUpdateBackupWatcher.RequestFileName);

        await File.WriteAllTextAsync(request, text);
        await Eventually.ThatAsync(() => !File.Exists(request));

        Assert.False(File.Exists(Path.Join(Home, TrayUpdateBackupWatcher.ResultFileName)));
        Assert.True(_log.Logged(LogLevel.Information, "could not read"));
    }

    [Fact]
    public async Task A_request_still_being_written_is_read_again_when_it_is_finished()
    {
        await StartAsync();
        var request = Path.Join(Home, TrayUpdateBackupWatcher.RequestFileName);

        await File.WriteAllTextAsync(request, Request()[..20]);
        await File.WriteAllTextAsync(request, Request());

        await Eventually.ThatAsync(() => (string?)ResultFor(Id)?["state"] is "saved" or "failed");
    }

    [Fact]
    public async Task The_marker_names_this_process_only_until_it_is_disposed()
    {
        await StartAsync();
        var marker = Path.Join(Home, TrayUpdateBackupWatcher.ReadyFileName);
        Assert.True(File.Exists(marker));

        _watcher!.Dispose();

        Assert.False(File.Exists(marker));
    }
}
