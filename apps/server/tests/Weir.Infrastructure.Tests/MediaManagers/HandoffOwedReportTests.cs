using System.Net;
using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// What Weir keeps of a report it owes a manager, and what it tells the person while the report waits: how many files of a hand-off are
/// still going, what is written on the hand-off row, what is said on the files, and the output paths once the manager can be asked.
/// </summary>
public sealed class HandoffOwedReportTests : IDisposable
{
    private const string Callback = "/api/integrations/processors/events";
    private const string Manifest = "/api/integrations/external/manifest";
    private const string Manager = "Deluno on 192.0.2.30";
    private const string Folder = "Show.S01";

    private static readonly string[] Episodes = ["Show.S01/Show.S01E01.mkv", "Show.S01/Show.S01E02.mkv", "Show.S01/Show.S01E03.mkv"];

    private const string ManifestJson =
        """{"name":"Deluno","capabilities":[],"libraries":[{"id":"lib-1","name":"Shows","mediaType":"tv","rootPath":"/tv","importWorkflow":"refine-before-import","processorOutputPath":"D:\\Ready\\TV"}]}""";

    private readonly MediaManagerFixture _fixture = new();
    private long _library;

    private string Output => _fixture.Store.Home.Join("Refined");

    public void Dispose() => _fixture.Dispose();

    /// <summary>Deluno has handed over <paramref name="targets"/> as one hand-off, and Weir has a status line for each of them and for one more file.</summary>
    private async Task StartAsync(params string[] targets)
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        _library = await _fixture.LibraryAsync("tv", _fixture.Store.Home.Join("tv"));
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.Accepted));
        _fixture.DelunoListsNoLibraries();
        await _fixture.Db(async uow =>
        {
            var row = await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", _library, Folder);
            await _fixture.Targets.AddAsync(uow, row, targets);
            return 0;
        });
        foreach (var file in (string[])[.. targets, "Other/other.mkv"])
        {
            await _fixture.Store.Execute(
                $"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({_library}, '{file}', 'processed', 'Finished processing this file.')");
        }
    }

    private string Payload(string relative) =>
        $$$"""{"relative_media_path":"{{{relative}}}","library_id":{{{_library}}},"media_scope":"tv","origin":{"source_key":"deluno","handoff_id":"h1","library_id":"lib-1","callback_path":"{{{Callback}}}"}}""";

    private WireObject Delivered(string relative) => new WireObject()
        .Set("ok", true)
        .Set("outcome", "live_output_written")
        .Set("output_file", Path.Join([Output, .. relative.Split('/')]))
        .Set("relative_media_path", relative)
        .Set("processing_output_folder_resolved", Output);

    private Task<string> ReportAsync(string relative, WireObject result) =>
        _fixture.Db(uow => _fixture.Reporter.ReportHandoffCompletionAsync(uow, Payload(relative), result), commit: false);

    private async Task<string> ReportAfterTheRetriesAsync(string relative, WireObject result)
    {
        var report = ReportAsync(relative, result);
        await Eventually.ThatAdvancingAsync(_fixture.Store.Clock, TimeSpan.FromSeconds(1), () => report.IsCompleted);
        return await report;
    }

    private Task<string?> OwedJsonAsync() => _fixture.Store.ScalarText("SELECT pending_report_json FROM media_manager_handoffs WHERE handoff_id = 'h1'");

    private Task<string?> ReasonOfAsync(string file) =>
        _fixture.Store.ScalarText($"SELECT status_reason FROM files WHERE library_id = {_library} AND relative_path = '{file}'");

    private Task<WireObject> SentAsync(int index = 0) =>
        Task.FromResult((WireObject)_fixture.Http.RequestsTo(HttpMethod.Post, Callback)[index].Json!);

    [Fact]
    public async Task A_pass_that_is_not_the_last_says_how_many_other_files_of_its_hand_off_are_still_going()
    {
        await StartAsync(Episodes);

        Assert.Equal("skipped: 2 other files of this hand-off have not finished yet", await ReportAsync(Episodes[0], Delivered(Episodes[0])));
        Assert.Equal("skipped: 1 other file of this hand-off has not finished yet", await ReportAsync(Episodes[1], Delivered(Episodes[1])));
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, Callback));
    }

    [Fact]
    public async Task A_file_the_hand_off_does_not_cover_is_not_reported_for_it()
    {
        await StartAsync(Episodes);

        var status = await ReportAsync("Other/other.mkv", Delivered("Other/other.mkv"));

        Assert.Equal("skipped: this file is not one the hand-off covers", status);
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, Callback));
    }

    [Fact]
    public async Task A_report_the_manager_did_not_answer_is_kept_whole_and_only_the_files_it_covers_say_they_are_waiting_once()
    {
        await StartAsync(Episodes[0]);
        _fixture.Http.Throw(HttpMethod.Post, Callback, new HttpRequestException("refused"));

        await ReportAsync(Episodes[0], Delivered(Episodes[0]));

        var owed = (WireObject)WireJsonParser.Parse((await OwedJsonAsync())!);
        Assert.Equal(Callback, ((WireString)owed["callback_path"]).Value);
        Assert.Equal("lib-1", ((WireString)owed["manager_library_id"]).Value);
        Assert.Equal(_library, ((WireInteger)owed["library_id"]).Value);
        Assert.Equal(Episodes[0], ((WireString)owed["relative_media_path"]).Value);
        Assert.Equal([Episodes[0]], ((WireArray)owed["relative_media_paths"]).Items.Select(item => ((WireString)item).Value));
        Assert.Equal("completed", ((WireString)((WireObject)owed["body"])["status"]).Value);
        Assert.False(owed.ContainsKey("refusals"));
        Assert.False(owed.ContainsKey("first_refused_at"));
        Assert.False(owed.ContainsKey("not_before"));
        Assert.False(owed.ContainsKey("unplaced"));
        Assert.EndsWith(ManagerWaitMessages.ReportWaiting(Manager), (await ReasonOfAsync(Episodes[0]))!, StringComparison.Ordinal);
        Assert.Equal("Finished processing this file.", await ReasonOfAsync("Other/other.mkv"));

        // The heartbeat tries again and the manager is still down: the sentence is not added a second time.
        Assert.Equal(0, await _fixture.Db(uow => _fixture.Reporter.SendWaitingReportsAsync(uow, "deluno"), commit: false));
        Assert.Equal($"Finished processing this file. {ManagerWaitMessages.ReportWaiting(Manager)}", await ReasonOfAsync(Episodes[0]));
        Assert.Equal("Finished processing this file.", await ReasonOfAsync("Other/other.mkv"));
    }

    [Fact]
    public async Task A_report_the_manager_asked_to_send_again_later_remembers_how_often_it_was_refused()
    {
        await StartAsync(Episodes[0]);
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.ServiceUnavailable));

        await ReportAsync(Episodes[0], Delivered(Episodes[0]));

        var owed = (WireObject)WireJsonParser.Parse((await OwedJsonAsync())!);
        Assert.Equal(1, ((WireInteger)owed["refusals"]).Value);
        Assert.True(owed.ContainsKey("first_refused_at"));
        Assert.True(owed.ContainsKey("not_before"));
    }

    [Fact]
    public async Task A_report_saved_by_an_older_weir_with_no_list_of_files_still_replaces_the_waiting_line_of_the_file_it_names()
    {
        await StartAsync(Episodes[0]);
        _fixture.Http.Throw(HttpMethod.Post, Callback, new HttpRequestException("refused"));
        await ReportAsync(Episodes[0], Delivered(Episodes[0]));
        var owed = (WireObject)WireJsonParser.Parse((await OwedJsonAsync())!);
        owed.Remove("relative_media_paths");
        owed.Set("release_name", "Show.S01.1080p");
        await _fixture.Store.Execute($"UPDATE media_manager_handoffs SET pending_report_json = '{WireJsonWriter.Dumps(owed, WireJsonFormat.Compact).Replace("'", "''", StringComparison.Ordinal)}'");
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.Accepted));

        Assert.Equal(1, await _fixture.Db(uow => _fixture.Reporter.SendWaitingReportsAsync(uow, "deluno"), commit: false));

        Assert.Equal($"Finished processing this file. {ManagerWaitMessages.ReportDelivered(Manager)}", await ReasonOfAsync(Episodes[0]));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE pending_report_json IS NOT NULL"));
    }

    [Fact]
    public async Task A_folder_report_that_waited_for_the_manager_to_list_its_libraries_goes_with_every_file_and_the_folder_as_the_manager_sees_them()
    {
        await StartAsync(Episodes[0], Episodes[1]);
        _fixture.Http.Throw(HttpMethod.Get, Manifest, new HttpRequestException("refused"));

        await ReportAsync(Episodes[0], Delivered(Episodes[0]));
        var queued = await ReportAfterTheRetriesAsync(Episodes[1], Delivered(Episodes[1]));
        Assert.StartsWith("queued: the manager would not list its libraries", queued, StringComparison.Ordinal);
        Assert.Contains(
            (LogLevel.Warning, $"{Manager} would not list its libraries after 3 tries, so Weir cannot say where it sees the output; the report waits for it."),
            _fixture.ReporterLog.Messages);

        _fixture.Http.Json(HttpMethod.Get, Manifest, ManifestJson);
        Assert.Equal(1, await _fixture.Db(uow => _fixture.Reporter.SendWaitingReportsAsync(uow, "deluno"), commit: false));

        var sent = await SentAsync();
        Assert.Equal(@"D:\Ready\TV\Show.S01", ((WireString)sent["outputPath"]).Value);
        Assert.Equal(
            [@"D:\Ready\TV\Show.S01\Show.S01E01.mkv", @"D:\Ready\TV\Show.S01\Show.S01E02.mkv"],
            ((WireArray)sent["outputFiles"]).Items.Select(item => ((WireString)item).Value));
        Assert.Equal(@"D:\Ready\TV\Show.S01", await _fixture.Store.ScalarText("SELECT output_path FROM media_manager_handoffs WHERE handoff_id = 'h1'"));
    }

    [Fact]
    public async Task A_manager_whose_libraries_cannot_be_read_at_all_does_not_hold_the_report_back_and_the_log_says_why()
    {
        await StartAsync(Episodes[0]);
        _fixture.Http.Throw(HttpMethod.Get, Manifest, new NotSupportedException("the manifest could not be read"));

        var status = await ReportAsync(Episodes[0], Delivered(Episodes[0]));

        Assert.Equal($"reported completed to {Manager}", status);
        Assert.Equal(Path.Join(Output, "Show.S01", "Show.S01E01.mkv"), ((WireString)(await SentAsync())["outputPath"]).Value);
        Assert.Contains(
            (LogLevel.Warning, $"Could not read {Manager}'s libraries to place the output path."),
            _fixture.ReporterLog.Messages);
    }
}
