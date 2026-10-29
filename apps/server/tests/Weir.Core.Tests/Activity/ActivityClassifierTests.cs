using Weir.Core.Activity;

namespace Weir.Core.Tests.Activity;

/// <summary>
/// How an activity event's type and detail become its facts (trigger, result, library, file and run), and the
/// event type constants.
/// </summary>
public sealed partial class ActivityClassifierTests
{
    [Fact]
    public void Facts_are_lifted_from_the_detail()
    {
        Assert.Equal(
            new ActivityFacts("worker", "warning", 3, "Movies/a.mkv", "run:7"),
            ActivityClassifier.Classify("processing.file_processed", "{\"trigger\": \" Worker \", \"result\": \"warning\", \"library_id\": 3, \"relative_media_path\": \" Movies/a.mkv \", \"run_id\": 7}"));
        Assert.Equal(
            new ActivityFacts("scheduled", "retrying", 4, "a/b.mkv", "run:9"),
            ActivityClassifier.Classify(
                ActivityEventTypes.ProcessingWorkerFailure,
                "{\"job_id\":3,\"result\":\"Retrying\",\"trigger\":\" Scheduled \",\"library_id\":4,\"relative_media_path\":\" a/b.mkv \",\"run_id\":9}"));
        Assert.Equal("skipped", ActivityClassifier.Classify(ActivityEventTypes.ProcessingFailureCleanupSweepCompleted, "{\"result\":\"skipped\"}").Result);
    }

    [Fact]
    public void Person_started_events_are_manual_and_results_fall_back_to_the_event_type()
    {
        Assert.Equal(new ActivityFacts("manual", "success", null, null, null), ActivityClassifier.Classify(ActivityEventTypes.AuthLoginSucceeded, "alice"));
        Assert.Equal(new ActivityFacts("manual", "failed", null, null, null), ActivityClassifier.Classify(ActivityEventTypes.AuthBootstrapDenied, "An admin account already exists."));
        Assert.Equal(new ActivityFacts("manual", null, null, null, null), ActivityClassifier.Classify(ActivityEventTypes.SystemReconciliationRepair, null));
        Assert.Equal(new ActivityFacts("manual", null, null, null, null), ActivityClassifier.Classify(ActivityEventTypes.SystemNetworkAccessChanged, "Only this PC can reach Weir. Changed by alice."));
        Assert.Equal(new ActivityFacts("manual", null, null, null, null), ActivityClassifier.Classify(ActivityEventTypes.SystemHomeNetworkSignInChanged, "Skip sign-in for devices on the local network turned on."));
        Assert.Equal(new ActivityFacts(null, "failed", null, null, null), ActivityClassifier.Classify(ActivityEventTypes.ProcessingWorkerFailure, null));
        // #540 item 8: the type's own terminal verb ("completed") is matched before "failure" in its name,
        // so a cleanup sweep that finished with no result given reads as "success", not "failed".
        Assert.Equal("success", ActivityClassifier.Classify(ActivityEventTypes.ProcessingFailureCleanupSweepCompleted, "not json").Result);
        Assert.Equal("warning", ActivityClassifier.Classify(ActivityEventTypes.ProcessingFileRejectFellBack, null).Result);
    }

    [Fact]
    public void Booleans_are_not_library_ids_and_not_run_ids()
    {
        Assert.Equal(new ActivityFacts(null, "failed", null, null, null), ActivityClassifier.Classify("processing.x", "{\"ok\": false, \"library_id\": true}"));
        // #540 item 5: booleans are rejected, so run_id: true does not become the run key "run:True"
        // and neither id is set.
        Assert.Equal(new ActivityFacts("manual", "failed", null, null, null), ActivityClassifier.Classify("auth.login", "{\"ok\":false,\"library_id\":true,\"run_id\":true}"));
        Assert.Equal(new ActivityFacts(null, null, null, null, null), ActivityClassifier.Classify("processing.x", "{\"library_id\": 1.0, \"run_id\": \" \"}"));
    }

    [Fact]
    public void The_detail_is_read_leniently_and_its_values_fully_trimmed()
    {
        // The detail accepts NaN, keeps integers of any size and lets the last duplicate key win.
        Assert.Equal("worker", ActivityClassifier.Classify("processing.x", "{\"trigger\":\"worker\",\"x\":NaN}").Trigger);
        Assert.Equal("retry", ActivityClassifier.Classify("processing.x", "{\"trigger\":\"worker\",\"trigger\":\"retry\"}").Trigger);
        Assert.Equal(
            "run:123456789012345678901234567890",
            ActivityClassifier.Classify("processing.x", "{\"run_id\":123456789012345678901234567890}").RunKey);
        // Trimming also removes U+001C..U+001F, which .NET's Trim keeps.
        Assert.Equal(
            new ActivityFacts("worker", null, null, "a", null),
            ActivityClassifier.Classify("processing.x", "{\"trigger\":\"\\u001cWorker\\u001c\",\"relative_media_path\":\"\\u2028a\\u001f\"}"));
        Assert.Equal(2000, ActivityClassifier.Classify("processing.x", "{\"relative_media_path\":\"" + new string('p', 2500) + "\"}").RelativePath!.Length);
    }
}
