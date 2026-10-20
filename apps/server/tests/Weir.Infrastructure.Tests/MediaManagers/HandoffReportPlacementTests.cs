using System.Net;
using Weir.Core.Json;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// The output path in a report is the one Deluno sees (<c>processorOutputPath</c> of its library), which Weir reads from Deluno's
/// manifest as the file finishes. A manifest Deluno will not give at that moment must not turn the report into one that names Weir's own
/// folder: Deluno cannot open it.
/// </summary>
public sealed class HandoffReportPlacementTests
{
    private const string Callback = "/api/integrations/processors/events";
    private const string Manifest = "/api/integrations/external/manifest";
    private const string DelunoPath = @"D:\Ready\Movies\Film\film.mkv";
    private const string Relative = "Film/film.mkv";

    private const string ManifestJson =
        """{"name":"Deluno","capabilities":[],"libraries":[{"id":"lib-1","name":"Movies","mediaType":"movies","rootPath":"/movies","importWorkflow":"refine-before-import","processorOutputPath":"D:\\Ready\\Movies"}]}""";

    [Fact]
    public async Task A_manifest_that_fails_once_is_read_again_before_the_report_goes_so_it_names_the_folder_Deluno_sees()
    {
        using var world = await ReportWorld.StartAsync();
        var reads = 0;
        world.Fixture.Http.Route(HttpMethod.Get, Manifest, _ =>
            Interlocked.Increment(ref reads) == 1 ? throw Refused() : FakeManagerHttp.Response(HttpStatusCode.OK, ManifestJson));

        var report = world.ReportAsync();
        await Eventually.ThatAdvancingAsync(world.Fixture.Store.Clock, TimeSpan.FromSeconds(1), () => report.IsCompleted);

        Assert.StartsWith("reported completed", await report, StringComparison.Ordinal);
        Assert.Equal(2, reads);
        Assert.Equal(DelunoPath, world.SentPaths().Single());
    }

    [Fact]
    public async Task A_report_waits_for_a_manifest_that_will_not_come_and_goes_with_the_folder_Deluno_sees_once_it_does()
    {
        using var world = await ReportWorld.StartAsync();
        world.Fixture.Http.Throw(HttpMethod.Get, Manifest, Refused());

        var report = world.ReportAsync();
        await Eventually.ThatAdvancingAsync(world.Fixture.Store.Clock, TimeSpan.FromSeconds(1), () => report.IsCompleted);

        Assert.StartsWith("queued", await report, StringComparison.Ordinal);
        Assert.Empty(world.Fixture.Http.RequestsTo(HttpMethod.Post, Callback));
        Assert.Equal(1, await world.OwedAsync());
        Assert.Null(await world.LedgerOutputPathAsync());
        Assert.Contains("Waiting for Deluno on 192.0.2.30", await world.ReasonAsync(), StringComparison.Ordinal);

        // Still no manifest: the report stays owed, unsent, and nothing about the hand-off changes.
        var stillWaiting = world.SendWaitingAsync();
        await Eventually.ThatAdvancingAsync(world.Fixture.Store.Clock, TimeSpan.FromSeconds(1), () => stillWaiting.IsCompleted);
        Assert.Equal(0, await stillWaiting);
        Assert.Empty(world.Fixture.Http.RequestsTo(HttpMethod.Post, Callback));
        Assert.Equal(1, await world.OwedAsync());

        world.Fixture.Http.Json(HttpMethod.Get, Manifest, ManifestJson);
        Assert.Equal(1, await world.SendWaitingAsync());

        Assert.Equal(DelunoPath, world.SentPaths().Single());
        Assert.Equal(0, await world.OwedAsync());
        Assert.Equal(DelunoPath, await world.LedgerOutputPathAsync());
        Assert.Equal("completed", await world.LedgerStateAsync());
        Assert.Contains("Deluno on 192.0.2.30 is answering again, and Weir has told it this file is ready", await world.ReasonAsync(), StringComparison.Ordinal);
    }

    private static HttpRequestException Refused() => new("No connection could be made because the target machine actively refused it.");

    /// <summary>A Deluno hand-off that Weir has finished, and the report of it.</summary>
    private sealed class ReportWorld : IDisposable
    {
        private readonly string _output;
        private readonly string _payload;

        private ReportWorld(MediaManagerFixture fixture, string payload)
        {
            Fixture = fixture;
            _payload = payload;
            _output = fixture.Store.Home.Join("Refined");
            fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.Accepted));
        }

        public MediaManagerFixture Fixture { get; }

        public static async Task<ReportWorld> StartAsync()
        {
            var fixture = new MediaManagerFixture();
            await fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
            var library = await fixture.LibraryAsync("movie", fixture.Store.Home.Join("watched"));
            await fixture.Db(async uow =>
            {
                var row = await fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", library, Relative);
                await fixture.Targets.AddAsync(uow, row, [Relative]);
                return 0;
            });
            await fixture.Store.Execute(
                $"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({library}, '{Relative}', 'processed', 'Finished processing this file.')");
            var payload =
                $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"media_scope":"movie","origin":{"source_key":"deluno","handoff_id":"h1","library_id":"lib-1","callback_path":"{{{Callback}}}"}}""";
            return new ReportWorld(fixture, payload);
        }

        public Task<string> ReportAsync()
        {
            var result = new WireObject()
                .Set("ok", true)
                .Set("outcome", "live_output_written")
                .Set("output_file", Path.Join(_output, "Film", "film.mkv"))
                .Set("relative_media_path", Relative)
                .Set("processing_output_folder_resolved", _output);
            return Fixture.Db(uow => Fixture.Reporter.ReportHandoffCompletionAsync(uow, _payload, result), commit: false);
        }

        public Task<int> SendWaitingAsync() => Fixture.Db(uow => Fixture.Reporter.SendWaitingReportsAsync(uow, "deluno"), commit: false);

        public List<string> SentPaths() =>
            [.. Fixture.Http.RequestsTo(HttpMethod.Post, Callback).Select(request => ((WireString)((WireObject)request.Json!)["outputPath"]).Value)];

        public Task<long> OwedAsync() => Fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE pending_report_json IS NOT NULL");

        public Task<string?> LedgerOutputPathAsync() =>
            Fixture.Db(async uow => await uow.ScalarAsync("SELECT output_path FROM media_manager_handoffs WHERE handoff_id = 'h1'") as string, commit: false);

        public Task<string?> ReasonAsync() =>
            Fixture.Db(async uow => await uow.ScalarAsync($"SELECT status_reason FROM files WHERE relative_path = '{Relative}'") as string, commit: false);

        public Task<string?> LedgerStateAsync() =>
            Fixture.Db(async uow => await uow.ScalarAsync("SELECT state FROM media_manager_handoffs WHERE handoff_id = 'h1'") as string, commit: false);

        public void Dispose() => Fixture.Dispose();
    }
}
