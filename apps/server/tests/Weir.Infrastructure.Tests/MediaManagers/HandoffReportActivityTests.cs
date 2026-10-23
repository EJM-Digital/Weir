using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// What Activity says about a report to the manager that handed a file over, in plain words: which kind of report it was, whether the
/// manager took it, and the next step when it did not.
/// </summary>
public sealed class HandoffReportActivityTests : IDisposable
{
    private const string Manager = "Deluno (rig)";

    private readonly StoreFixture _store = new();

    private static readonly HandoffReportTarget Target = new(
        new ManagerConnection("deluno", "rig", "http://manager.local", "key", 1), "http://manager.local/report", new Dictionary<string, string>());

    private static readonly HandoffReportDelivery Taken = new(Accepted: true, "accepted");

    public void Dispose() => _store.Dispose();

    private static WireObject Body(string json) => (WireObject)WireJsonParser.Parse(json);

    private async Task<(string Title, WireObject Detail)> RecordAsync(string bodyJson, HandoffReportDelivery delivery, string? relativePath, Func<string, string, string>? acceptedTitle = null)
    {
        await _store.WithUnitOfWork(async uow =>
        {
            await HandoffCompletionReporter.RecordHandoffReportAsync(uow, Target, Body(bodyJson), delivery, relativePath, acceptedTitle);
            return 0;
        });
        var title = await _store.ScalarText("SELECT title FROM activity_events ORDER BY id DESC LIMIT 1");
        var detail = await _store.ScalarText("SELECT detail FROM activity_events ORDER BY id DESC LIMIT 1");
        return (title!, (WireObject)WireJsonParser.Parse(detail!));
    }

    public static TheoryData<string, string> WhatTheManagerWasToldOfAFile() => new()
    {
        { """{"status":"completed"}""", "Told Deluno (rig) that film.mkv is ready to import" },
        { """{"disposition":"rejected","status":"failed"}""", "Told Deluno (rig) that film.mkv is a bad release and was removed" },
        { """{"status":"failed","failureClass":"source_gone"}""", "Told Deluno (rig) that film.mkv is no longer there" },
        { """{"status":"failed","failureClass":"superseded"}""", "Told Deluno (rig) that its newer send of film.mkv has the answer" },
        { """{"status":"failed","failureClass":"not_taken"}""", "Told Deluno (rig) that Weir did not start film.mkv, because it is working on it for another connection" },
        { """{"status":"failed"}""", "Told Deluno (rig) that Weir could not process film.mkv" },
        { "{}", "Told Deluno (rig) that Weir could not process film.mkv" },
        { """{"status":"failed","failureClass":"unreadable"}""", "Told Deluno (rig) that Weir could not process film.mkv" },
    };

    [Theory]
    [MemberData(nameof(WhatTheManagerWasToldOfAFile))]
    public async Task A_report_the_manager_took_is_worded_by_what_it_said_of_the_file(string body, string expected)
    {
        var (title, detail) = await RecordAsync(body, Taken, "Film/film.mkv");

        Assert.Equal(expected, title);
        Assert.Equal("success", WireConvert.Str(detail["result"]));
        Assert.False(detail.ContainsKey("next_action"));
    }

    [Fact]
    public async Task A_report_the_manager_took_can_be_worded_by_the_caller()
    {
        var (title, _) = await RecordAsync(
            """{"status":"failed"}""", Taken, "Film/film.mkv", (manager, file) => $"Told {manager} you chose to keep {file} without processing it");

        Assert.Equal($"Told {Manager} you chose to keep film.mkv without processing it", title);
    }

    [Fact]
    public async Task A_manager_that_had_already_stopped_waiting_is_said_to_have_stopped_and_nothing_is_called_a_failure()
    {
        var (title, detail) = await RecordAsync(
            """{"status":"completed"}""", new HandoffReportDelivery(Accepted: false, "dismissed", ManagerStoppedWaiting: true), "Film/film.mkv");

        Assert.Equal($"{Manager} had already stopped waiting for film.mkv", title);
        Assert.Equal("skipped", WireConvert.Str(detail["result"]));
        Assert.Equal($"{Manager} had already stopped waiting for film.mkv, so it did not queue an import.", WireConvert.Str(detail["user_message"]));
    }

    [Fact]
    public async Task A_report_the_manager_did_not_take_says_so_and_names_where_to_check_the_connection()
    {
        var (title, detail) = await RecordAsync("""{"status":"completed"}""", new HandoffReportDelivery(Accepted: false, "unreachable"), "Film/film.mkv");

        Assert.Equal($"Weir could not tell {Manager} about film.mkv", title);
        Assert.Equal("failed", WireConvert.Str(detail["result"]));
        Assert.Equal(
            $"Check that {Manager} is running and that its address and API key are right in Setup › Connections › Media managers.",
            WireConvert.Str(detail["next_action"]));
    }

    [Theory]
    [InlineData("""{"releaseName":"Some.Film.2020"}""", "Some.Film.2020")]
    [InlineData("""{"releaseName":""}""", "a handed-over file")]
    [InlineData("""{}""", "a handed-over file")]
    public async Task A_report_with_no_file_path_is_named_by_its_release_or_as_a_handed_over_file(string body, string expectedName)
    {
        var (title, _) = await RecordAsync(body, new HandoffReportDelivery(Accepted: false, "unreachable"), relativePath: null);

        Assert.Equal($"Weir could not tell {Manager} about {expectedName}", title);
    }
}
