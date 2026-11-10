using System.Net;
using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// The report of a hand-off of several files, and of a single one, when the output folder is unknown, the manager will not list its
/// libraries, or there is nobody to report to: what is sent, what waits, and what the ledger says.
/// </summary>
public sealed class HandoffReportFolderTests : IDisposable
{
    private const string Callback = "/api/integrations/processors/events";
    private const string Manifest = "/api/integrations/external/manifest";
    private const string Folder = "Show.S01";

    private static readonly string[] Episodes = ["Show.S01/Show.S01E01.mkv", "Show.S01/Show.S01E02.mkv"];

    private readonly MediaManagerFixture _fixture = new();

    private string Output => _fixture.Store.Home.Join("Refined");

    public void Dispose() => _fixture.Dispose();

    /// <summary>Deluno has handed over a folder of two episodes; the library the ledger names is <paramref name="libraryId"/>.</summary>
    private async Task StartAsync(long? libraryId, bool connected = true)
    {
        if (connected)
        {
            await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        }

        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.Accepted));
        await _fixture.Db(async uow =>
        {
            var row = await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", libraryId, Folder);
            await _fixture.Targets.AddAsync(uow, row, Episodes);
            return 0;
        });
    }

    private static string Payload(string relative, long? libraryId)
    {
        var library = libraryId is { } id ? $"\"library_id\":{id}," : string.Empty;
        var callback = $",\"callback_path\":\"{Callback}\"";
        return $$$"""{"relative_media_path":"{{{relative}}}",{{{library}}}"media_scope":"tv","origin":{"source_key":"deluno","handoff_id":"h1","library_id":"lib-1"{{{callback}}}}}""";
    }

    private WireObject Delivered(string relative, bool withFolder = true)
    {
        var result = new WireObject()
            .Set("ok", true)
            .Set("outcome", "live_output_written")
            .Set("output_file", Path.Join([Output, .. relative.Split('/')]))
            .Set("relative_media_path", relative);
        return withFolder ? result.Set("processing_output_folder_resolved", Output) : result;
    }

    private static WireObject Failed(string relative) => new WireObject()
        .Set("ok", false)
        .Set("outcome", "failed_execution")
        .Set("reason", "the remux failed")
        .Set("retry_scheduled", false)
        .Set("pass_through_queued", false)
        .Set("reject_queued", false)
        .Set("relative_media_path", relative);

    private Task<string> ReportAsync(string relative, WireObject result, long? libraryId) =>
        _fixture.Db(uow => _fixture.Reporter.ReportHandoffCompletionAsync(uow, Payload(relative, libraryId), result), commit: false);

    /// <summary>A report that waits for a manager that will not list its libraries retries on the clock, so the clock is moved until it ends.</summary>
    private async Task<string> ReportAfterTheRetriesAsync(string relative, WireObject result, long? libraryId)
    {
        var report = ReportAsync(relative, result, libraryId);
        await Eventually.ThatAdvancingAsync(_fixture.Store.Clock, TimeSpan.FromSeconds(1), () => report.IsCompleted);
        return await report;
    }

    private Task<long> OwedAsync() => _fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE pending_report_json IS NOT NULL");

    private Task<string?> LedgerStateAsync() => _fixture.Store.ScalarText("SELECT state FROM media_manager_handoffs WHERE handoff_id = 'h1'");

    [Fact]
    public async Task A_folder_hand_off_whose_manager_will_not_list_its_libraries_keeps_its_report_until_the_output_can_be_placed()
    {
        var library = await _fixture.LibraryAsync("tv", _fixture.Store.Home.Join("tv"));
        await StartAsync(library);
        _fixture.Http.Throw(HttpMethod.Get, Manifest, new HttpRequestException("refused"));

        Assert.StartsWith("skipped:", await ReportAsync(Episodes[0], Delivered(Episodes[0]), library), StringComparison.Ordinal);
        var status = await ReportAfterTheRetriesAsync(Episodes[1], Delivered(Episodes[1]), library);

        Assert.StartsWith("queued: the manager would not list its libraries", status, StringComparison.Ordinal);
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, Callback));
        Assert.Equal(1, await OwedAsync());
        Assert.Equal("queued", await LedgerStateAsync());
    }

    [Fact]
    public async Task A_folder_hand_off_with_nothing_delivered_is_reported_even_when_the_manager_will_not_list_its_libraries()
    {
        var library = await _fixture.LibraryAsync("tv", _fixture.Store.Home.Join("tv"));
        await StartAsync(library);
        _fixture.Http.Throw(HttpMethod.Get, Manifest, new HttpRequestException("refused"));

        Assert.StartsWith("skipped:", await ReportAsync(Episodes[0], Failed(Episodes[0]), library), StringComparison.Ordinal);
        var status = await ReportAfterTheRetriesAsync(Episodes[1], Failed(Episodes[1]), library);

        Assert.Equal("reported failed to Deluno on 192.0.2.30", status);
        Assert.Equal("failed", await LedgerStateAsync());
    }

    [Fact]
    public async Task A_folder_hand_off_with_no_manager_to_report_to_is_not_reported_and_still_settles_in_the_ledger()
    {
        var library = await _fixture.LibraryAsync("tv", _fixture.Store.Home.Join("tv"));
        await StartAsync(library, connected: false);

        await ReportAsync(Episodes[0], Delivered(Episodes[0]), library);
        var status = await ReportAsync(Episodes[1], Delivered(Episodes[1]), library);

        Assert.StartsWith("skipped: no enabled deluno connection", status, StringComparison.Ordinal);
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, Callback));
        Assert.Equal("completed", await LedgerStateAsync());
    }

    [Theory]
    [InlineData("no library on the hand-off")]
    [InlineData("a library with no output folder")]
    public async Task A_folder_hand_off_that_failed_has_no_output_folder_to_name_when_its_library_cannot_give_one(string situation)
    {
        var library = await _fixture.LibraryAsync("tv", _fixture.Store.Home.Join("tv"));
        long? ledgerLibrary = situation switch
        {
            "no library on the hand-off" => null,
            _ => library,
        };
        if (situation == "a library with no output folder")
        {
            await _fixture.Store.Execute($"UPDATE libraries SET output_folder = '' WHERE id = {library}");
        }

        await StartAsync(ledgerLibrary);
        _fixture.DelunoListsNoLibraries();

        await ReportAsync(Episodes[0], Failed(Episodes[0]), ledgerLibrary);
        var status = await ReportAsync(Episodes[1], Failed(Episodes[1]), ledgerLibrary);

        Assert.Equal("reported failed to Deluno on 192.0.2.30", status);
        var body = (WireObject)Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, Callback)).Json!;
        Assert.Empty(((WireArray)body["outputFiles"]).Items);
    }

    [Fact]
    public async Task A_folder_hand_off_whose_passes_gave_no_output_folder_names_the_one_its_library_has()
    {
        var library = await _fixture.LibraryAsync("tv", _fixture.Store.Home.Join("tv"));
        await _fixture.Store.Execute($"UPDATE libraries SET output_folder = '{Output.Replace("'", "''", StringComparison.Ordinal)}' WHERE id = {library}");
        await StartAsync(library);
        _fixture.DelunoListsNoLibraries();

        await ReportAsync(Episodes[0], Delivered(Episodes[0], withFolder: false), library);
        await ReportAsync(Episodes[1], Delivered(Episodes[1], withFolder: false), library);

        var body = (WireObject)Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, Callback)).Json!;
        Assert.Equal(Path.Join(Output, Folder), ((WireString)body["outputPath"]).Value);
    }

    [Theory]
    [InlineData("delivered", "completed", "completed")]
    [InlineData("failed", "failed", "failed")]
    [InlineData("needed no remux", "completed", "completed")]
    [InlineData("handed back unchanged", "completed", "passed-through")]
    public async Task A_single_file_report_without_an_output_folder_names_the_path_Weir_wrote_and_settles_the_ledger(string kind, string reported, string state)
    {
        var library = await _fixture.LibraryAsync("movie", _fixture.Store.Home.Join("watched"));
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.Accepted));
        _fixture.DelunoListsNoLibraries();
        const string relative = "Film/film.mkv";
        await _fixture.Db(async uow =>
        {
            var row = await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", library, relative);
            await _fixture.Targets.AddAsync(uow, row, [relative]);
            return 0;
        });
        var result = kind switch
        {
            "delivered" => Delivered(relative, withFolder: false),
            "failed" => Failed(relative),
            "needed no remux" => new WireObject().Set("ok", true).Set("outcome", "live_skipped_not_required").Set("relative_media_path", relative),
            _ => Delivered(relative, withFolder: false).Set("passed_through_after_failure", true),
        };

        var status = await ReportAsync(relative, result, library);

        Assert.Equal($"reported {reported} to Deluno on 192.0.2.30", status);
        Assert.Equal(state, await LedgerStateAsync());
    }

    [Fact]
    public async Task A_single_file_report_waits_for_the_manager_to_list_its_libraries_even_when_the_hand_off_has_no_library()
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        _fixture.Http.Throw(HttpMethod.Get, Manifest, new HttpRequestException("refused"));
        const string relative = "Film/film.mkv";
        await _fixture.Db(async uow =>
        {
            var row = await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", null, relative);
            await _fixture.Targets.AddAsync(uow, row, [relative]);
            return 0;
        });

        var status = await ReportAfterTheRetriesAsync(relative, Delivered(relative), null);

        Assert.StartsWith("queued: the manager would not list its libraries", status, StringComparison.Ordinal);
        Assert.Equal(1, await OwedAsync());
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, Callback));
    }

    private WireObject FailedInTheOutputFolder(string relative) => Failed(relative).Set("processing_output_folder_resolved", Output);

    [Fact]
    public async Task A_folder_hand_off_that_failed_where_the_output_folder_is_known_is_reported_at_once_though_the_manager_will_not_list_its_libraries()
    {
        var library = await _fixture.LibraryAsync("tv", _fixture.Store.Home.Join("tv"));
        await StartAsync(library);
        _fixture.Http.Throw(HttpMethod.Get, Manifest, new HttpRequestException("refused"));

        await ReportAsync(Episodes[0], FailedInTheOutputFolder(Episodes[0]), library);
        var status = await ReportAfterTheRetriesAsync(Episodes[1], FailedInTheOutputFolder(Episodes[1]), library);

        Assert.Equal("reported failed to Deluno on 192.0.2.30", status);
        Assert.Equal("failed", await LedgerStateAsync());
        Assert.Equal(0, await OwedAsync());
        Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, Callback));
    }

    [Fact]
    public async Task A_folder_hand_off_delivered_with_no_output_folder_anywhere_names_no_folder()
    {
        var library = await _fixture.LibraryAsync("tv", _fixture.Store.Home.Join("tv"));
        await _fixture.Store.Execute($"UPDATE libraries SET output_folder = '' WHERE id = {library}");
        await StartAsync(library);
        _fixture.DelunoListsNoLibraries();

        await ReportAsync(Episodes[0], Delivered(Episodes[0], withFolder: false), library);
        await ReportAsync(Episodes[1], Delivered(Episodes[1], withFolder: false), library);

        var body = (WireObject)Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, Callback)).Json!;
        Assert.Equal("completed", ((WireString)body["status"]).Value);
        Assert.False(body.ContainsKey("outputPath"));
    }

    [Fact]
    public async Task A_file_that_needed_no_work_is_counted_delivered_but_lists_no_output_file()
    {
        var library = await _fixture.LibraryAsync("tv", _fixture.Store.Home.Join("tv"));
        await StartAsync(library);
        _fixture.DelunoListsNoLibraries();
        var needsNothing = new WireObject()
            .Set("ok", true)
            .Set("outcome", "live_skipped_not_required")
            .Set("relative_media_path", Episodes[1])
            .Set("processing_output_folder_resolved", Output);

        await ReportAsync(Episodes[0], Delivered(Episodes[0]), library);
        await ReportAsync(Episodes[1], needsNothing, library);

        var body = (WireObject)Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, Callback)).Json!;
        Assert.Equal("completed", ((WireString)body["status"]).Value);
        Assert.Equal(
            [Path.Join(Output, "Show.S01", "Show.S01E01.mkv")],
            ((WireArray)body["outputFiles"]).Items.Select(item => ((WireString)item).Value));
    }

    [Fact]
    public async Task A_folder_hand_off_whose_files_were_all_taken_over_by_a_newer_send_is_cancelled_in_the_ledger()
    {
        var library = await _fixture.LibraryAsync("tv", _fixture.Store.Home.Join("tv"));
        await StartAsync(library);
        _fixture.DelunoListsNoLibraries();

        await ReportAsync(Episodes[0], CompletionReports.SupersededResult(Episodes[0]), library);
        await ReportAsync(Episodes[1], CompletionReports.SupersededResult(Episodes[1]), library);

        Assert.Equal(HandoffLedgerRules.Cancelled, await LedgerStateAsync());
        var body = (WireObject)Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, Callback)).Json!;
        Assert.Equal(CompletionReports.SupersededFailureClass, ((WireString)body["failureClass"]).Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_report_that_waits_for_the_manager_says_so_on_the_files_of_the_library_the_pass_named_before_the_ledgers(bool passNamesALibrary)
    {
        var ledgerLibrary = await _fixture.LibraryAsync("tv", _fixture.Store.Home.Join("tv"));
        var passLibrary = await _fixture.LibraryAsync("tv", _fixture.Store.Home.Join("tv-2"), name: "Second TV");
        await StartAsync(ledgerLibrary);
        var withFiles = passNamesALibrary ? passLibrary : ledgerLibrary;
        foreach (var episode in Episodes)
        {
            await _fixture.Store.Execute(
                $"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({withFiles}, '{episode}', 'processed', 'Finished processing this file.')");
        }

        _fixture.Http.Throw(HttpMethod.Get, Manifest, new HttpRequestException("refused"));
        long? named = passNamesALibrary ? passLibrary : null;

        await ReportAsync(Episodes[0], Delivered(Episodes[0]), named);
        await ReportAfterTheRetriesAsync(Episodes[1], Delivered(Episodes[1]), named);

        foreach (var episode in Episodes)
        {
            var reason = await _fixture.Store.ScalarText($"SELECT status_reason FROM files WHERE library_id = {withFiles} AND relative_path = '{episode}'");
            Assert.Contains("Waiting for Deluno on 192.0.2.30", reason, StringComparison.Ordinal);
        }
    }
}
