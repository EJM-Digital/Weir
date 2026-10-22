using Weir.Core.Jobs;

namespace Weir.Core.Tests.Jobs;

/// <summary>What a person is told about a job: the file it was for, and the plain explanation of each kind of failure.</summary>
public sealed class OperatorJobStatusWordingTests
{
    private const string Payload = """{"relative_media_path": "Films\\Heat (1995)\\Heat.mkv"}""";

    [Theory]
    [InlineData("""{"relative_media_path": "Films\\Heat.mkv"}""", " for Heat.mkv")]
    [InlineData("""{"relative_media_path": "Films/Heat.mkv"}""", " for Heat.mkv")]
    [InlineData("""{"relative_media_path": "Heat.mkv"}""", " for Heat.mkv")]
    [InlineData("""{"relative_media_path": "   "}""", "")]
    [InlineData("""{"relative_media_path": 7}""", "")]
    [InlineData("""{"other": "x"}""", "")]
    [InlineData("""["not", "an", "object"]""", "")]
    [InlineData("not json at all", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void A_job_is_named_by_the_file_in_its_payload_when_there_is_one(string? payload, string subject)
    {
        var status = OperatorJobStatus.Build("processing", "remux", "pending", lastError: null, payload);

        Assert.Equal($"Queued{subject}.", status.Message);
    }

    [Theory]
    [InlineData("pending", "Queued for Heat.mkv.")]
    [InlineData("leased", "Running for Heat.mkv.")]
    [InlineData("completed", "Finished for Heat.mkv.")]
    [InlineData("cancelled", "Cancelled before it started for Heat.mkv.")]
    [InlineData("handler_ok_finalize_failed", "The work finished for Heat.mkv, but its result could not be saved.")]
    [InlineData("something_new", "Needs a review for Heat.mkv.")]
    public void Each_state_of_a_job_has_its_own_plain_message(string status, string message)
    {
        var built = OperatorJobStatus.Build("processing", "remux", status, lastError: null, Payload);

        Assert.Equal(message, built.Message);
        Assert.Null(built.TechnicalDetail);
    }

    [Fact]
    public void A_job_in_a_state_nobody_planned_for_points_at_the_logs()
    {
        var built = OperatorJobStatus.Build("processing", "remux", "something_new", lastError: null, payloadJson: null);

        Assert.Equal("Open it in System › Logs to see why.", built.NextAction);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("error")]
    public void A_job_that_failed_for_no_known_reason_keeps_its_error_for_the_person_to_read(string status)
    {
        var built = OperatorJobStatus.Build("processing", "remux", status, "  disk   went\n away ", Payload);

        Assert.Equal("Couldn't finish this job for Heat.mkv.", built.Message);
        Assert.Equal("disk went away", built.TechnicalDetail);
    }

    [Theory]
    [InlineData("database is locked")]
    [InlineData("SQLite: database table is locked")]
    public void A_busy_database_on_a_processing_job_tells_the_person_to_lower_files_at_once(string error)
    {
        var built = OperatorJobStatus.Build("Processing", "remux", "failed", error, Payload);

        Assert.Equal("Weir could not save the result while another local operation was using the database for Heat.mkv.", built.Message);
        Assert.Contains("set ‘Files at once’ to 1", built.NextAction, StringComparison.Ordinal);
    }

    [Fact]
    public void A_busy_database_on_another_module_names_that_module()
    {
        var built = OperatorJobStatus.Build("library", "scan", "failed", "database is locked", payloadJson: null);

        Assert.Equal("Weir could not save the Library result because another local operation was using the database.", built.Message);
        Assert.Equal("Try the job again after the current local work finishes.", built.NextAction);
    }

    [Fact]
    public void A_busy_database_on_a_module_with_no_name_still_gets_a_message()
    {
        var built = OperatorJobStatus.Build(string.Empty, "scan", "failed", "database is locked", payloadJson: null);

        Assert.StartsWith("Weir could not save the ", built.Message, StringComparison.Ordinal);
        Assert.EndsWith(" result because another local operation was using the database.", built.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("This is not a supported processing media type")]
    [InlineData("Unsupported processing request")]
    [InlineData("Processing does not process this file")]
    [InlineData("Weir does not process .iso files")]
    public void A_file_of_a_kind_the_pass_does_not_handle_is_explained(string error)
    {
        var built = OperatorJobStatus.Build("processing", "remux", "failed", error, Payload);

        Assert.Equal("This file is not a supported media file for this pass for Heat.mkv.", built.Message);
        Assert.Contains("update the workflow’s media types", built.NextAction, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Weir could not find this file")]
    [InlineData("File not found: Heat.mkv")]
    [InlineData("No such file or directory")]
    public void A_file_that_has_gone_missing_is_explained(string error)
    {
        var built = OperatorJobStatus.Build("processing", "remux", "failed", error, Payload);

        Assert.Equal("Weir could not find this file under the saved watched folder for Heat.mkv.", built.Message);
        Assert.Contains("restore the file", built.NextAction, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ffprobe failed with code 1")]
    [InlineData("Weir could not read this media file")]
    [InlineData("Couldn't read this file")]
    [InlineData("can't read this file")]
    [InlineData("Media tools couldn't finish")]
    public void A_file_the_media_tools_cannot_read_is_explained(string error)
    {
        var built = OperatorJobStatus.Build("processing", "remux", "failed", error, Payload);

        Assert.Equal("Weir could not read this media file for Heat.mkv.", built.Message);
    }

    [Theory]
    [InlineData("Legacy processing dry_run job")]
    [InlineData("legacy Weir dry_run job")]
    public void A_job_from_an_older_dry_run_mode_is_explained(string error)
    {
        var built = OperatorJobStatus.Build("processing", "remux", "failed", error, Payload);

        Assert.Equal("This job was created with an older processing mode for Heat.mkv.", built.Message);
        Assert.Contains("Remove the old entry from Activity", built.NextAction, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("file modified too recently")]
    [InlineData("file changed too recently")]
    [InlineData("file still being written")]
    public void A_file_that_is_still_changing_is_waited_for(string error)
    {
        var built = OperatorJobStatus.Build("processing", "remux", "failed", error, Payload);

        Assert.Equal("Weir is waiting for this file to finish changing for Heat.mkv.", built.Message);
    }

    [Fact]
    public void A_file_with_no_audio_worth_keeping_is_explained()
    {
        var built = OperatorJobStatus.Build("processing", "remux", "failed", "no retainable audio tracks", Payload);

        Assert.Equal("Weir could not build a safe audio plan for this file for Heat.mkv.", built.Message);
        Assert.Contains("saved audio rules", built.NextAction, StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_longer_than_ten_thousand_characters_is_cut_there()
    {
        var built = OperatorJobStatus.Build("processing", "remux", "failed", new string('x', 10_050), payloadJson: null);

        Assert.Equal(10_000, built.TechnicalDetail!.Length);
    }
}
