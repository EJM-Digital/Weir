using System.Net;
using Weir.Core.Json;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// #652 item 5: when the manager is not answering, the hand-back report is kept, the file's Activity says Weir is waiting
/// for it in plain words, and the report goes once the manager answers again (the heartbeat calls
/// <see cref="HandoffCompletionReporter.SendWaitingReportsAsync"/>).
/// </summary>
public sealed class HandoffWaitingReportTests
{
    private const string Callback = "/api/integrations/processors/events";

    [Fact]
    public async Task A_report_the_manager_did_not_answer_waits_in_plain_words_and_goes_when_it_answers()
    {
        using var fixture = new MediaManagerFixture();
        await fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        var library = await fixture.LibraryAsync("movie", fixture.Store.Home.Join("watched"));
        await fixture.Db(async uow =>
        {
            await fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", library, "Film/film.mkv");
            return 0;
        });
        await fixture.Store.Execute(
            $"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({library}, 'Film/film.mkv', 'processed', 'Finished processing this file.')");
        fixture.Http.Throw(HttpMethod.Post, Callback, new HttpRequestException("No connection could be made because the target machine actively refused it."));
        var payload =
            $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"media_scope":"movie","origin":{"source_key":"deluno","handoff_id":"h1","library_id":"lib-movies","callback_path":"{{{Callback}}}"}}""";
        var result = (WireObject)WireJsonParser.Parse("""{"ok":true,"outcome":"live_output_written","output_file":"/out/Film/film.mkv","relative_media_path":"Film/film.mkv"}""");

        var status = await fixture.Db(uow => fixture.Reporter.ReportHandoffCompletionAsync(uow, payload, result), commit: false);

        Assert.Equal("failed: could not reach Deluno on 192.0.2.30", status);
        Assert.Equal(
            "Finished processing this file. Waiting for Deluno on 192.0.2.30, which is not answering. Weir will tell it this file is ready when it answers.",
            await ReasonAsync(fixture));
        Assert.Equal(1, await fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE pending_report_json IS NOT NULL"));

        // Still not answering: the report stays owed, and nothing changes.
        Assert.Equal(0, await fixture.Db(uow => fixture.Reporter.SendWaitingReportsAsync(uow, "deluno"), commit: false));
        Assert.Equal(1, await fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE pending_report_json IS NOT NULL"));

        // It answers again.
        fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.OK));
        Assert.Equal(1, await fixture.Db(uow => fixture.Reporter.SendWaitingReportsAsync(uow, "deluno"), commit: false));

        Assert.Equal(0, await fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE pending_report_json IS NOT NULL"));
        Assert.Equal(
            "Finished processing this file. Deluno on 192.0.2.30 is answering again, and Weir has told it this file is ready.",
            await ReasonAsync(fixture));
        var delivered = fixture.Http.RequestsTo(HttpMethod.Post, Callback)[^1];
        Assert.Equal("completed", ((WireString)((WireObject)delivered.Json!)["status"]).Value);
        Assert.Equal(1, await fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE title = 'Told Deluno on 192.0.2.30 that film.mkv is ready to import'"));
        Assert.Equal(0, await fixture.Db(uow => fixture.Reporter.SendWaitingReportsAsync(uow, "deluno"), commit: false));
    }

    private static Exception Failure(string kind) => kind switch
    {
        "refused" => new HttpRequestException("No connection could be made because the target machine actively refused it."),
        "cut off" => new IOException("The response ended prematurely."),
        _ => new TaskCanceledException("The request timed out.", new TimeoutException()),
    };

    [Theory]
    [InlineData("refused")]
    [InlineData("cut off")]
    [InlineData("timed out")]
    public async Task A_report_the_manager_never_answered_for_any_reason_stays_owed_and_goes_when_it_answers(string kind)
    {
        using var fixture = new MediaManagerFixture();
        await fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        var library = await fixture.LibraryAsync("movie", fixture.Store.Home.Join("watched"));
        await fixture.Db(async uow =>
        {
            await fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h1", library, "Film/film.mkv");
            return 0;
        });
        fixture.Http.Throw(HttpMethod.Post, Callback, Failure(kind));
        var payload =
            $$$"""{"relative_media_path":"Film/film.mkv","library_id":{{{library}}},"media_scope":"movie","origin":{"source_key":"deluno","handoff_id":"h1","library_id":"lib-movies","callback_path":"{{{Callback}}}"}}""";
        var result = (WireObject)WireJsonParser.Parse("""{"ok":true,"outcome":"live_output_written","output_file":"/out/Film/film.mkv","relative_media_path":"Film/film.mkv"}""");

        var status = await fixture.Db(uow => fixture.Reporter.ReportHandoffCompletionAsync(uow, payload, result), commit: false);

        Assert.Equal("failed: could not reach Deluno on 192.0.2.30", status);
        Assert.Equal(1, await fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE pending_report_json IS NOT NULL"));
        Assert.Equal(0, await fixture.Db(uow => fixture.Reporter.SendWaitingReportsAsync(uow, "deluno"), commit: false));

        fixture.Http.Route(HttpMethod.Post, Callback, _ => FakeManagerHttp.Response(HttpStatusCode.Accepted));
        Assert.Equal(1, await fixture.Db(uow => fixture.Reporter.SendWaitingReportsAsync(uow, "deluno"), commit: false));
        Assert.Equal(0, await fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE pending_report_json IS NOT NULL"));
    }

    private static async Task<string> ReasonAsync(MediaManagerFixture fixture) =>
        await fixture.Db(async uow => (string)(await uow.ScalarAsync("SELECT status_reason FROM files WHERE relative_path = 'Film/film.mkv'"))!, commit: false);
}
