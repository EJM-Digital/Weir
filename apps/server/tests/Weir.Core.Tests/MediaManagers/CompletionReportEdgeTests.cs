using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>The edges of the completion and folder hand-off reports: odd origins, missing failure reasons, manager path styles, and files with no result yet.</summary>
public sealed class CompletionReportEdgeTests
{
    private static WireObject Dict(string json) => (WireObject)WireJsonParser.Parse(json);

    private static readonly HandoffOrigin Origin = new("deluno", "handoff-1", "/events", "Film.2020");

    // --- the origin on a payload -------------------------------------------------------------

    [Theory]
    [InlineData("""{"origin":{"source_key":"deluno","handoff_id":"  h1  ","callback_path":"","release_name":0,"library_id":"   "}}""", "deluno", "h1", null, null, null)]
    [InlineData("""{"origin":{"source_key":"deluno","handoff_id":7,"release_name":"Film.2020"}}""", "deluno", "7", null, "Film.2020", null)]
    public void An_origin_keeps_only_the_fields_that_say_something(string payload, string source, string? handoffId, string? callback, string? release, string? library)
    {
        var origin = HandoffOrigin.FromPayload(WireJsonParser.Parse(payload));

        Assert.Equal(new HandoffOrigin(source, handoffId, callback, release, library), origin);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"origin":3}""")]
    [InlineData("""{"origin":{"source_key":"   "}}""")]
    [InlineData("""{"origin":{"source_key":0}}""")]
    public void A_payload_without_a_named_source_has_no_origin(string payload) =>
        Assert.Null(HandoffOrigin.FromPayload(WireJsonParser.Parse(payload)));

    [Fact]
    public void No_payload_has_no_origin() => Assert.Null(HandoffOrigin.FromPayload(null));

    // --- what a report says ------------------------------------------------------------------

    [Theory]
    [InlineData("""{"ok":false,"reason":"The disk is full.","output_completeness_note":"short"}""", "The disk is full.")]
    [InlineData("""{"ok":false,"reason":"   ","output_completeness_note":"The output was short."}""", "The output was short.")]
    [InlineData("""{"ok":false,"reason":4,"source_folder_skip_reason":"Not a film folder."}""", "Not a film folder.")]
    [InlineData("""{"ok":false}""", "The processing pass did not produce a usable output.")]
    [InlineData("""{"ok":true,"outcome":"live_output_written"}""", "Remux finished.")]
    [InlineData("""{"ok":true,"outcome":"live_output_written","removed_audio":["fre"]}""", "Removed 1 audio track.")]
    [InlineData("""{"ok":true,"outcome":"live_output_written","removed_subtitles":["fre","spa"]}""", "Removed 2 subtitle tracks.")]
    [InlineData("""{"ok":true,"outcome":"live_output_written","removed_audio":[],"removed_subtitles":[]}""", "Remux finished.")]
    public void A_report_says_why_a_pass_failed_or_what_a_remux_removed(string result, string expected) =>
        Assert.Equal(expected, CompletionReports.MessageFor(Dict(result)));

    [Fact]
    public void A_result_with_no_outcome_is_none_of_the_special_cases()
    {
        var result = Dict("""{"ok":true}""");

        Assert.False(CompletionReports.IsSuperseded(result));
        Assert.False(CompletionReports.IsNotTaken(result));
        Assert.False(CompletionReports.IsSourceGone(result));
        Assert.False(CompletionReports.IsSucceeded(result));
    }

    [Theory]
    [InlineData(409, """{"status":"dismissed"}""", true)]
    [InlineData(409, """{"status":"completed"}""", false)]
    [InlineData(409, """[]""", false)]
    [InlineData(500, """{"status":"dismissed"}""", false)]
    public void A_report_is_too_late_only_when_the_manager_says_it_stopped_waiting(int status, string answer, bool expected) =>
        Assert.Equal(expected, CompletionReports.IsTooLate(status, WireJsonParser.Parse(answer)));

    [Fact]
    public void A_report_with_no_answer_is_not_too_late() => Assert.False(CompletionReports.IsTooLate(409, null));

    // --- the manager's path style ------------------------------------------------------------

    [Theory]
    [InlineData(@"\\server\share\dir", @"\\server\share\dir\a\b")]
    [InlineData(@"\\server\share", @"\\server\share\a\b")]
    [InlineData(@"\\server", @"\\server\a\b")]
    [InlineData(@"C:\Refined", @"C:\Refined\a\b")]
    [InlineData(@"C:\", @"C:\a\b")]
    [InlineData("C:", "C:a\\b")]
    [InlineData("C:/Refined/", @"C:\Refined\a\b")]
    [InlineData(@"\data\refined", @"\data\refined\a\b")]
    [InlineData(@"relative\dir", @"relative\dir\a\b")]
    [InlineData("//host/share", "//host/share/a/b")]
    [InlineData("///data", "/data/a/b")]
    [InlineData("/data/./refined", "/data/refined/a/b")]
    [InlineData("rel/dir", "rel/dir/a/b")]
    [InlineData("  /data  ", "/data/a/b")]
    public void Parts_are_joined_in_the_managers_own_path_style(string folder, string expected) =>
        Assert.Equal(expected, CompletionReports.ManagerPathJoin(folder, ["a", "", ".", "b"]));

    [Theory]
    [InlineData("", ".")]
    [InlineData(".", ".")]
    [InlineData("/", "/")]
    [InlineData(@"C:\", @"C:\")]
    public void With_nothing_to_join_the_folder_comes_back_as_it_is(string folder, string expected) =>
        Assert.Equal(expected, CompletionReports.ManagerPathJoin(folder, []));

    // --- folder hand-offs --------------------------------------------------------------------

    private static HandoffTarget Done(string name, string result = HandoffLedgerRules.Completed, long? size = null) =>
        new(name, result, $"/out/{name}", null, SourceSize: size);

    [Fact]
    public void A_file_with_no_result_yet_keeps_the_hand_off_queued()
    {
        HandoffTarget[] targets = [new("film.mkv", null, null, null)];

        Assert.Equal(HandoffLedgerRules.Queued, FolderHandoffReports.State(targets));
    }

    [Fact]
    public void A_file_handed_back_unchanged_is_named_in_the_message()
    {
        HandoffTarget[] targets = [Done("film.mkv"), Done("extra.mkv", HandoffLedgerRules.PassedThrough)];

        var body = FolderHandoffReports.BuildBody(Origin, targets, "/out", ["/out/film.mkv", "/out/extra.mkv"]);

        Assert.Equal("completed", ((WireString)body["status"]).Value);
        Assert.Equal(
            "Weir finished 2 files, ready to import from the output folder. It could not process 1 file, so it handed that one back unchanged.",
            ((WireString)body["message"]).Value);
    }

    [Fact]
    public void Several_files_handed_back_unchanged_are_counted()
    {
        HandoffTarget[] targets = [Done("a.mkv", HandoffLedgerRules.PassedThrough), Done("b.mkv", HandoffLedgerRules.PassedThrough)];

        var body = FolderHandoffReports.BuildBody(Origin, targets, null, ["/out/a.mkv", "/out/b.mkv"]);

        Assert.EndsWith("It could not process 2 files, so it handed those back unchanged.", ((WireString)body["message"]).Value, StringComparison.Ordinal);
        Assert.False(body.ContainsKey("outputPath"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""not json""")]
    [InlineData("""{"a":1}""")]
    public void A_stored_list_that_is_missing_or_unreadable_is_no_list(string? stored) =>
        Assert.Null(HandoffOutputFiles.Parse(stored));

    [Fact]
    public void A_stored_list_keeps_only_its_text_entries() =>
        Assert.Equal(["/out/a.mkv"], HandoffOutputFiles.Parse("""["/out/a.mkv",3,null]"""));

    [Fact]
    public void An_output_list_that_is_not_known_is_null_on_the_wire()
    {
        Assert.Same(WireNull.Instance, HandoffOutputFiles.ToJsonOrNull(null));
        Assert.Equal("""["/out/a.mkv"]""", HandoffOutputFiles.Serialize(["/out/a.mkv"]));
        Assert.IsType<WireArray>(HandoffOutputFiles.ToJsonOrNull(["/out/a.mkv"]));
    }
}
