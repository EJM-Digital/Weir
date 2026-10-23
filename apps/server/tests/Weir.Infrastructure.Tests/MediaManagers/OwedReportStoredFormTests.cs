using System.Net;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// An owed report is kept as JSON on its hand-off row: what Weir does with one that cannot be read, one written by an older
/// Weir, one for a manager that is switched off, and one the person told Weir to give up on.
/// </summary>
public sealed class OwedReportStoredFormTests : IDisposable
{
    private const string Callback = "/api/integrations/processors/events";
    private const string Relative = "Film/film.mkv";

    private readonly MediaManagerFixture _fixture = new();
    private long _library;

    public void Dispose() => _fixture.Dispose();

    private async Task OweAsync(string reportJson, string handoffId = "h1")
    {
        _library = await _fixture.LibraryAsync("movie", _fixture.Store.Home.Join("watched"));
        await _fixture.Db(async uow =>
        {
            await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", handoffId, _library, Relative);
            return 0;
        });
        await _fixture.Store.Execute($"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({_library}, '{Relative}', 'processed', 'Finished processing this file.')");
        await _fixture.Db(async uow =>
        {
            await HandoffLedgerStore.SetPendingReportAsync(uow, "deluno", handoffId, reportJson);
            return 0;
        });
    }

    private Task<int> SendWaitingAsync() => _fixture.Db(uow => _fixture.Reporter.SendWaitingReportsAsync(uow, "deluno"), commit: false);

    private Task<long> OwedAsync() => _fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE pending_report_json IS NOT NULL");

    private Task<string?> StoredAsync() => _fixture.Store.ScalarText("SELECT pending_report_json FROM media_manager_handoffs WHERE handoff_id = 'h1'");

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    [InlineData("""{"callback_path":"/x","body":"text"}""")]
    public async Task A_report_that_cannot_be_read_is_dropped_not_kept_forever(string stored)
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await OweAsync(stored);

        Assert.Equal(0, await SendWaitingAsync());

        Assert.Equal(0, await OwedAsync());
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, Callback));
    }

    [Fact]
    public async Task A_report_for_a_manager_that_is_switched_off_stays_owed_untouched()
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1", enabled: false);
        var stored = $$"""{"callback_path":"{{Callback}}","body":{"status":"completed"},"relative_media_path":"{{Relative}}"}""";
        await OweAsync(stored);

        Assert.Equal(0, await SendWaitingAsync());

        Assert.Equal(stored, await StoredAsync());
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, Callback));
    }

    [Fact]
    public async Task A_report_an_older_Weir_saved_with_only_its_own_file_and_no_library_is_still_sent()
    {
        _fixture.DelunoListsNoLibraries();
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.OK));
        await OweAsync($$"""{"callback_path":"{{Callback}}","release_name":"Film.2026","manager_library_id":"","body":{"status":"completed"},"relative_media_path":"{{Relative}}"}""");

        Assert.Equal(1, await SendWaitingAsync());

        Assert.Equal(0, await OwedAsync());
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE title LIKE 'Told % that film.mkv is ready to import'"));
        Assert.Equal("Finished processing this file.", await _fixture.Store.ScalarText($"SELECT status_reason FROM files WHERE library_id = {_library}"));
    }

    [Fact]
    public async Task A_report_refused_before_keeps_when_it_was_first_refused_and_counts_another_refusal()
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.ServiceUnavailable));
        await OweAsync(
            $$"""{"callback_path":"{{Callback}}","body":{"status":"completed"},"relative_media_path":"{{Relative}}","relative_media_paths":[7,"{{Relative}}"],"refusals":2,"first_refused_at":"2026-01-15T09:00:00Z","not_before":"2026-01-15T09:30:00Z"}""");

        Assert.Equal(0, await SendWaitingAsync());

        var stored = (WireObject)WireJsonParser.Parse((await StoredAsync())!);
        Assert.Equal(3, ((WireInteger)stored["refusals"]).Value);
        Assert.StartsWith("2026-01-15T09:00:00", ((WireString)stored["first_refused_at"]).Value, StringComparison.Ordinal);
        Assert.Equal(Relative, ((WireString)((WireArray)stored["relative_media_paths"]).Items.Single()).Value);
    }

    [Fact]
    public async Task A_refusal_time_that_is_not_a_time_counts_as_none()
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        _fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.ServiceUnavailable));
        await OweAsync($$"""{"callback_path":"{{Callback}}","body":{"status":"completed"},"refusals":1,"first_refused_at":"soon","not_before":5}""");

        Assert.Equal(0, await SendWaitingAsync());

        var stored = (WireObject)WireJsonParser.Parse((await StoredAsync())!);
        Assert.Equal(2, ((WireInteger)stored["refusals"]).Value);
        Assert.NotEqual("soon", ((WireString)stored["first_refused_at"]).Value);
    }

    [Fact]
    public async Task Giving_up_stops_owing_each_report_and_tells_the_files_it_covered_that_Weir_will_not_tell_the_manager()
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await OweAsync("not json");
        var waiting = ManagerWaitMessages.ReportWaiting("Deluno");
        await _fixture.Store.Execute($"UPDATE files SET status_reason = 'Finished processing this file. {waiting}'");
        var covering = $$"""{"callback_path":"{{Callback}}","body":{"status":"completed"},"library_id":{{_library}},"relative_media_paths":["{{Relative}}"]}""";
        var withoutLibrary = $$"""{"callback_path":"{{Callback}}","body":{"status":"completed"},"relative_media_path":"{{Relative}}"}""";
        var owed = new List<OwedReportRow>
        {
            new("deluno", "h1", Relative, "not json"),
            new("deluno", "h1", Relative, withoutLibrary),
            new("deluno", "h1", Relative, covering),
        };

        await _fixture.Db(async uow =>
        {
            await _fixture.Reporter.GiveUpOwedReportsAsync(uow, "Deluno", owed);
            return 0;
        });

        Assert.Equal(0, await OwedAsync());
        Assert.Equal(
            $"Finished processing this file. {ManagerWaitMessages.ReportGivenUp("Deluno")}",
            await _fixture.Store.ScalarText($"SELECT status_reason FROM files WHERE library_id = {_library}"));
    }

    [Fact]
    public async Task A_saved_report_that_never_named_an_output_path_is_placed_as_it_is_and_keeps_a_refusal_count_without_times()
    {
        _fixture.DelunoListsNoLibraries();
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        _fixture.Http.Throw(HttpMethod.Post, Callback, new HttpRequestException("refused"));
        await OweAsync(
            """{"callback_path":"CALLBACK","body":{"status":"failed"},"relative_media_path":"RELATIVE","refusals":2,"unplaced":{"local_folder":"/out","state":"failed"}}"""
                .Replace("CALLBACK", Callback, StringComparison.Ordinal)
                .Replace("RELATIVE", Relative, StringComparison.Ordinal));

        Assert.Equal(0, await SendWaitingAsync());

        var stored = (WireObject)WireJsonParser.Parse((await StoredAsync())!);
        Assert.Equal(2, ((WireInteger)stored["refusals"]).Value);
        Assert.IsType<WireNull>(stored["first_refused_at"]);
        Assert.IsType<WireNull>(stored["not_before"]);
        Assert.False(stored.ContainsKey("unplaced"));
        Assert.Equal("failed", await _fixture.Store.ScalarText("SELECT state FROM media_manager_handoffs WHERE handoff_id = 'h1'"));
    }
}
