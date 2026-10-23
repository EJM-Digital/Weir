using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>The setup check for a manager Weir cannot ask: a connection with no key saved, and a manager that does not answer one question.</summary>
public sealed class ManagerSetupCheckEdgeTests : IDisposable
{
    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private ManagerSetupCheck Check() =>
        new(_fixture.Connections, _fixture.ConnectionStore, _fixture.Http, new FilesystemFolderProbe(), NullLogger<ManagerSetupCheck>.Instance);

    [Fact]
    public async Task A_manager_with_no_key_saved_is_a_problem_that_says_where_to_add_it()
    {
        var id = await _fixture.AddConnectionAsync("radarr", "http://192.0.2.40:7878", apiKey: null);
        var watched = _fixture.Store.Home.Join("watched");
        var output = _fixture.Store.Home.Join("out");

        var entries = await _fixture.Db(uow => Check().CheckAsync(uow, "movie", watched, output, linkedConnectionIds: null), commit: false);

        var entry = Assert.Single(entries);
        Assert.Equal(id, ((WireInteger)entry.Get("connection_id")!).Value);
        Assert.False(((WireBool)entry.Get("ready")!).Value);
        var line = (WireObject)Assert.Single(((WireArray)entry.Get("lines")!).Items);
        Assert.Contains("has no address or API key saved, so Weir cannot check it", ((WireString)line.Get("text")!).Value, StringComparison.Ordinal);
        Assert.Contains("Setup › Connections › Media managers", ((WireString)line.Get("text")!).Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manager_that_answers_nothing_is_not_ready_and_the_check_still_finishes()
    {
        var id = await _fixture.AddConnectionAsync("radarr", "http://192.0.2.43:7878");
        var watched = _fixture.Store.Home.Join("watched");
        var output = _fixture.Store.Home.Join("out");

        var entries = await _fixture.Db(uow => Check().CheckAsync(uow, "movie", watched, output, linkedConnectionIds: null), commit: false);

        var entry = Assert.Single(entries);
        Assert.Equal(id, ((WireInteger)entry.Get("connection_id")!).Value);
        Assert.False(((WireBool)entry.Get("ready")!).Value);
    }

    [Theory]
    [InlineData(500, "{}")]
    [InlineData(200, "[]")]
    [InlineData(200, "{\"enableCompletedDownloadHandling\":\"yes\"}")]
    public async Task A_completed_download_setting_the_manager_will_not_give_is_left_unknown_and_the_check_still_finishes(int status, string body)
    {
        await _fixture.AddConnectionAsync("radarr", "http://192.0.2.44:7878");
        _fixture.Http
            .Json(HttpMethod.Get, ManagerSetupRules.RemotePathMappingPath, "[]")
            .Json(HttpMethod.Get, ManagerSetupRules.DownloadClientPath, "[]")
            .Json(HttpMethod.Get, ManagerSetupRules.DownloadClientConfigPath, body, (System.Net.HttpStatusCode)status);

        var entries = await _fixture.Db(
            uow => Check().CheckAsync(uow, "movie", _fixture.Store.Home.Join("watched"), _fixture.Store.Home.Join("out"), linkedConnectionIds: null), commit: false);

        var entry = Assert.Single(entries);
        Assert.NotEmpty(((WireArray)entry.Get("lines")!).Items);
        Assert.Contains(_fixture.Http.Requests, request => request.PathAndQuery == ManagerSetupRules.DownloadClientConfigPath);
    }

    [Fact]
    public async Task A_manager_that_does_not_answer_contributes_no_download_clients()
    {
        await _fixture.AddConnectionAsync("radarr", "http://192.0.2.42:7878");

        var clients = await _fixture.Db(uow => Check().DownloadClientsUsedAsync(uow, "movie", linkedConnectionIds: null, CancellationToken.None), commit: false);

        Assert.Empty(clients);
        Assert.NotEmpty(_fixture.Http.Requests);
    }

    [Fact]
    public async Task A_manager_with_no_key_saved_gets_no_folder_suggestion_and_the_reason()
    {
        var id = await _fixture.AddConnectionAsync("sonarr", "http://192.0.2.41:8989", apiKey: null);

        var suggestions = await _fixture.Db(uow => Check().SuggestFoldersAsync(uow, "tv"), commit: false);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(id, suggestion.ConnectionId);
        Assert.Null(suggestion.WatchedFolder);
        Assert.Null(suggestion.OutputFolder);
        Assert.Contains("has no address or API key saved", suggestion.Problem, StringComparison.Ordinal);
    }
}
