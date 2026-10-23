using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>When Weir does not report a finished hand-off, and how it tells a manager that is down from one that said no.</summary>
public sealed class HandoffCompletionReporterEdgeTests : IDisposable
{
    private const string Callback = "/api/integrations/processors/events";

    private readonly MediaManagerFixture _fixture = new();
    private readonly CapturingLogger<HandoffCompletionReporter> _log = new();

    public void Dispose() => _fixture.Dispose();

    private HandoffCompletionReporter Reporter() =>
        new(_fixture.Connections, _fixture.ConnectionStore, _fixture.Ledger, _fixture.Targets, _fixture.Libraries, _fixture.Http, _fixture.Store.Clock, _log);

    private static WireObject Finished() =>
        (WireObject)WireJsonParser.Parse("""{"ok":true,"outcome":"live_output_written","output_file":"/out/Film/film.mkv","relative_media_path":"Film/film.mkv"}""");

    private Task<string> ReportAsync(string origin) =>
        _fixture.Db(
            uow => Reporter().ReportHandoffCompletionAsync(uow, $$$"""{"relative_media_path":"Film/film.mkv","media_scope":"movie","origin":{{{origin}}}}""", Finished()),
            commit: false);

    [Theory]
    [InlineData(true, "failed: Deluno answered HTTP 503", false, false)]
    [InlineData(false, "failed: Deluno answered HTTP 500", true, false)]
    [InlineData(false, "failed: Deluno answered HTTP 408", true, false)]
    [InlineData(false, "failed: Deluno answered HTTP 429", true, false)]
    [InlineData(false, "failed: Deluno answered HTTP 404", false, false)]
    [InlineData(false, "failed: Deluno answered HTTP soon", false, false)]
    [InlineData(false, "failed: could not reach Deluno on 192.0.2.30", true, true)]
    [InlineData(true, "failed: could not reach Deluno on 192.0.2.30", false, false)]
    [InlineData(false, "failed: the key was refused", false, false)]
    public void A_manager_that_is_down_is_told_from_one_that_said_no(bool accepted, string status, bool transient, bool notAnswering)
    {
        var delivery = new HandoffReportDelivery(accepted, status);

        Assert.Equal(transient, HandoffCompletionReporter.IsTransientFailure(delivery));
        Assert.Equal(notAnswering, HandoffCompletionReporter.IsNotAnswering(delivery));
    }

    [Fact]
    public async Task A_hand_off_that_named_no_callback_path_is_not_reported()
    {
        var status = await ReportAsync("""{"source_key":"deluno","handoff_id":"h1"}""");

        Assert.Equal("skipped: the hand-off named no callback path", status);
    }

    [Fact]
    public async Task A_hand_off_that_named_no_callback_path_has_no_one_to_report_to()
    {
        var origin = HandoffOrigin.FromPayload(WireJsonParser.Parse("""{"origin":{"source_key":"deluno","handoff_id":"h1"}}"""))!;

        var (target, reason) = await _fixture.Db(uow => Reporter().ResolveHandoffTargetAsync(uow, origin), commit: false);

        Assert.Null(target);
        Assert.Equal("the hand-off named no callback path", reason);
    }

    [Fact]
    public async Task A_result_that_cannot_be_recorded_is_not_reported_and_the_reason_is_logged()
    {
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await _fixture.Store.Execute("ALTER TABLE media_manager_handoffs RENAME TO media_manager_handoffs_unavailable");

        var status = await ReportAsync($"{{\"source_key\":\"deluno\",\"handoff_id\":\"h1\",\"callback_path\":\"{Callback}\"}}");

        Assert.Equal("skipped: this file's result could not be recorded", status);
        Assert.True(_log.Logged(LogLevel.Warning, "could not record the result of Film/film.mkv on hand-off h1"));
    }
}
