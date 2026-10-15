using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// A Weir with a hand-off from Deluno, and a Deluno over real HTTP that Weir reports to. The heartbeat is the production task, driven by
/// hand against the fixture's clock, so a minute passes when the test says it does.
/// </summary>
internal sealed class OwedReportWorld : IDisposable
{
    public const string Relative = "Film/film.mkv";

    private readonly SocketsManagerHttpHandlerFactory _handlers = new();
    private readonly string _output;
    private long _library;

    private OwedReportWorld(MediaManagerFixture fixture, FakeDelunoServer deluno)
    {
        Fixture = fixture;
        Deluno = deluno;
        _output = fixture.Store.Home.Join("Refined");
        var reporter = new HandoffCompletionReporter(
            fixture.Connections, fixture.ConnectionStore, fixture.Ledger, fixture.Targets, fixture.Libraries, _handlers, fixture.Store.Clock);
        Heartbeat = new ManagerHeartbeatTask(
            fixture.Store.Database, fixture.Connections, fixture.ConnectionStore, _handlers, fixture.Store.Clock, NullLogger<ManagerHeartbeatTask>.Instance, reporter);
        Reporter = reporter;
    }

    public MediaManagerFixture Fixture { get; }

    public FakeDelunoServer Deluno { get; }

    public ManagerHeartbeatTask Heartbeat { get; }

    public HandoffCompletionReporter Reporter { get; }

    public static async Task<OwedReportWorld> StartAsync()
    {
        var fixture = new MediaManagerFixture();
        var deluno = new FakeDelunoServer();
        var world = new OwedReportWorld(fixture, deluno);
        await fixture.AddConnectionAsync("deluno", deluno.BaseUrl, "k1");
        var watched = fixture.Store.Home.Join("watched");
        world._library = await fixture.LibraryAsync("movie", watched);
        await fixture.Store.Execute(
            $"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({world._library}, '{Relative}', 'processed', 'Finished processing this file.')");
        return world;
    }

    /// <summary>Deluno hands over the film, Weir finishes it, and Weir reports to Deluno once. Returns how that one delivery went.</summary>
    public async Task<string> FinishHandoffAsync(string handoffId)
    {
        var watched = Fixture.Store.Home.Join("watched");
        await Fixture.Db(uow => Fixture.Intake.EnqueueRefineAsync(
            uow,
            new MediaManagerImportEvent
            {
                SourceKey = "deluno",
                EventKind = "handoff",
                MediaScope = "movie",
                FilePath = Path.Join(watched, "Film", "film.mkv"),
                HandoffId = handoffId,
                CallbackPath = FakeDelunoServer.EventsPath,
                LibraryId = "lib-1",
            }));
        var job = (await Fixture.Jobs.ListAsync()).Single(candidate => candidate.PayloadJson!.Contains(handoffId, StringComparison.Ordinal));
        var delivered = new WireObject()
            .Set("ok", true)
            .Set("outcome", "live_output_written")
            .Set("output_file", Path.Join(_output, "Film", "film.mkv"))
            .Set("relative_media_path", Relative)
            .Set("processing_output_folder_resolved", _output);
        return await Fixture.Db(uow => Reporter.ReportHandoffCompletionAsync(uow, job.PayloadJson, delivered), commit: false);
    }

    /// <summary>One run of the heartbeat.</summary>
    public Task BeatAsync() => Heartbeat.RunOnceAsync(CancellationToken.None);

    /// <summary>Let <paramref name="time"/> pass, then run the heartbeat.</summary>
    public async Task BeatAfterAsync(TimeSpan time)
    {
        Fixture.Store.Clock.Advance(time);
        await BeatAsync();
    }

    /// <summary>How many reports Weir still owes.</summary>
    public Task<long> OwedAsync() => Fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE pending_report_json IS NOT NULL");

    public Task<long> ActivityAsync(string titlePattern) =>
        Fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE title LIKE '{titlePattern}'");

    public Task<string> ReasonAsync() =>
        Fixture.Db(async uow => (string)(await uow.ScalarAsync($"SELECT status_reason FROM files WHERE relative_path = '{Relative}'"))!, commit: false);

    public void Dispose()
    {
        Deluno.Dispose();
        _handlers.Dispose();
        Fixture.Dispose();
    }
}
