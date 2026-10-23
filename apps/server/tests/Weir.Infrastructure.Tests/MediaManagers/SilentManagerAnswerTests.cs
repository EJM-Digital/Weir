using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>A manager that does not answer and gives no words for it is described in Weir's own: "did not answer", or what Weir could not ask.</summary>
public sealed class SilentManagerAnswerTests : IDisposable
{
    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private MediaManagerConnectionService ConnectionsOver(ScriptedManagerPorts ports) =>
        new(_fixture.Store.Options, _fixture.Cipher, ports, _fixture.ConnectionStore);

    private static ScriptedManagerPorts Silent() => new() { Describe = connection => ScriptedManagerPorts.Silent(connection) };

    private static ManagerDescription Reported(ManagerConnection connection) =>
        new(connection, SignalStatus.Reported, new ManagerCapabilities(new HashSet<string>(StringComparer.Ordinal), false, false, "none"), [], []);

    [Fact]
    public async Task A_workflow_asking_what_a_silent_manager_can_do_is_told_Weir_could_not_ask_it()
    {
        var connection = new ManagerConnection("deluno", "Films", "http://manager.local", "key");

        var result = await new RejectSupportEvaluator(Silent()).EvaluateAsync([connection]);

        Assert.False(result.Available);
        Assert.Equal($"Weir could not ask {connection.Label} what it can do.", result.Reason);
    }

    [Fact]
    public async Task A_library_change_for_a_manager_that_will_not_describe_itself_is_retried_and_warned_of_with_a_plain_reason()
    {
        await _fixture.AddConnectionAsync("sonarr");
        var log = new ListLogger<LibraryFileChangeNotifier>();
        var notifier = new LibraryFileChangeNotifier(
            ConnectionsOver(Silent()), _fixture.Store.Database, new SqliteActivityWriter(_fixture.Store.Database), _fixture.Store.Clock, log, (_, _) => Task.CompletedTask);

        await notifier.NotifyAsync(new LibraryFileChange("tv", "/tv/Show/e01.mkv"));

        Assert.EndsWith(" did not answer.", log.Exceptions[^1].Message, StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'library.file_change_notify_warning'"));
    }

    [Fact]
    public async Task A_library_change_for_a_manager_that_will_not_list_its_files_is_retried_and_warned_of_with_a_plain_reason()
    {
        await _fixture.AddConnectionAsync("sonarr");
        var ports = new ScriptedManagerPorts
        {
            Describe = Reported,
            ListFiles = (connection, _) => new ManagerLibraryFilesSignal(connection, SignalStatus.Unreachable, []),
        };
        var log = new ListLogger<LibraryFileChangeNotifier>();
        var notifier = new LibraryFileChangeNotifier(
            ConnectionsOver(ports), _fixture.Store.Database, new SqliteActivityWriter(_fixture.Store.Database), _fixture.Store.Clock, log, (_, _) => Task.CompletedTask);

        await notifier.NotifyAsync(new LibraryFileChange("tv", "/tv/Show/e01.mkv"));

        Assert.EndsWith(" did not answer.", log.Exceptions[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_setup_check_of_a_deluno_that_will_not_describe_itself_says_it_did_not_answer()
    {
        await _fixture.AddConnectionAsync("deluno");
        var check = new ManagerSetupCheck(
            ConnectionsOver(Silent()), _fixture.ConnectionStore, _fixture.Http, new FilesystemFolderProbe(), NullLogger<ManagerSetupCheck>.Instance);

        var entries = await _fixture.Db(
            uow => check.CheckAsync(uow, "movie", _fixture.Store.Home.Join("watched"), _fixture.Store.Home.Join("out"), linkedConnectionIds: null), commit: false);

        var line = (WireObject)Assert.Single(((WireArray)Assert.Single(entries).Get("lines")!).Items);
        Assert.EndsWith(" did not answer.", ((WireString)line.Get("text")!).Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Importing_from_a_manager_that_will_not_describe_itself_says_it_did_not_answer_when_asked_what_it_manages()
    {
        var id = await _fixture.AddConnectionAsync("deluno");
        var row = (await _fixture.Db(uow => _fixture.ConnectionStore.GetAsync(uow, id)))!;
        var service = new LibraryDiscoveryService(ConnectionsOver(Silent()), Silent(), _fixture.Libraries);

        var failure = await Assert.ThrowsAsync<ProcessingDiscoveryException>(() => _fixture.Db(uow => service.DiscoverableLibrariesAsync(uow, row), commit: false));

        Assert.EndsWith(" did not answer when asked what it manages.", failure.Message, StringComparison.Ordinal);
    }
}
