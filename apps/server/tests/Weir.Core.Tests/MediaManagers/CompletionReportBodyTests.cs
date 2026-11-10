using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>The exact results Weir files for a replaced or refused send, and the fields a report leaves out when its source text says nothing.</summary>
public sealed class CompletionReportBodyTests
{
    private static readonly HandoffOrigin Origin = new("deluno", "handoff-1", "/events", "Film.2020");

    private static WireObject Dict(string json) => (WireObject)WireJsonParser.Parse(json);

    [Fact]
    public void The_result_for_a_send_that_a_newer_one_replaced_is_not_ok_and_names_the_file()
    {
        Assert.Equal(
            """{"ok":false,"outcome":"superseded","relative_media_path":"Film/a.mkv"}""",
            WireJsonWriter.Dumps(CompletionReports.SupersededResult("Film/a.mkv"), WireJsonFormat.Response));
    }

    [Fact]
    public void The_result_for_a_send_Weir_did_not_take_is_not_ok_and_names_the_file()
    {
        Assert.Equal(
            """{"ok":false,"outcome":"not_taken","relative_media_path":"Film/a.mkv"}""",
            WireJsonWriter.Dumps(CompletionReports.NotTakenResult("Film/a.mkv"), WireJsonFormat.Response));
    }

    [Theory]
    [InlineData("""{"ok":true,"outcome":"live_output_written","output_file":"   "}""")]
    [InlineData("""{"ok":true,"outcome":"live_output_written","output_file":""}""")]
    public void An_output_file_with_no_name_is_not_reported_as_an_output(string result)
    {
        var body = CompletionReports.BuildCompletionBody(Origin, Dict(result));

        Assert.Equal("completed", WireConvert.Str(body["status"]));
        Assert.False(body.ContainsKey("outputPath"));
        Assert.Empty(((WireArray)body["outputFiles"]).Items);
    }

    [Fact]
    public void An_output_file_is_reported_without_the_blanks_round_its_name()
    {
        var body = CompletionReports.BuildCompletionBody(Origin, Dict("""{"ok":true,"outcome":"live_output_written","output_file":"  /out/a.mkv  "}"""));

        Assert.Equal("/out/a.mkv", WireConvert.Str(body["outputPath"]));
    }

    [Fact]
    public void A_failure_class_that_is_only_blanks_is_not_reported()
    {
        var body = CompletionReports.BuildCompletionBody(Origin, Dict("""{"ok":false,"failure_class":"   ","reason":"The disk is full."}"""));

        Assert.Equal("failed", WireConvert.Str(body["status"]));
        Assert.False(body.ContainsKey("failureClass"));
    }

    [Fact]
    public void A_failure_class_is_reported_without_the_blanks_round_it()
    {
        var body = CompletionReports.BuildCompletionBody(Origin, Dict("""{"ok":false,"failure_class":"  disk_full  ","reason":"The disk is full."}"""));

        Assert.Equal("disk_full", WireConvert.Str(body["failureClass"]));
    }
}
