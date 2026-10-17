using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>What the alert channels hear about a file's pass: finished only for a file that was done, failed only for one Weir has given up on.</summary>
public sealed class RemuxPassAlertTests
{
    private static WireObject Pass(bool ok, string outcome) => new WireObject().Set("ok", ok).Set("outcome", outcome);

    [Fact]
    public void A_file_that_was_cleaned_is_finished() =>
        Assert.Equal(JobAlert.Finished, RemuxPassHandler.AlertFor(Pass(ok: true, RemuxPassOutcomes.LiveSkippedNotRequired)));

    [Fact]
    public void A_file_the_tools_failed_on_with_no_retry_owed_is_a_failure() =>
        Assert.Equal(
            JobAlert.FailedForGood,
            RemuxPassHandler.AlertFor(Pass(ok: false, RemuxPassOutcomes.FailedDuringExecution).Set("retry_scheduled", false)));

    [Fact]
    public void A_failure_before_any_work_with_no_retry_owed_is_a_failure() =>
        Assert.Equal(JobAlert.FailedForGood, RemuxPassHandler.AlertFor(Pass(ok: false, RemuxPassOutcomes.FailedBeforeExecution)));

    [Fact]
    public void A_failure_that_will_be_tried_again_says_nothing_yet() =>
        Assert.Equal(
            JobAlert.Nothing,
            RemuxPassHandler.AlertFor(Pass(ok: false, RemuxPassOutcomes.FailedDuringExecution).Set("retry_scheduled", true)));

    [Fact]
    public void A_file_waiting_for_another_program_to_let_go_says_nothing() =>
        Assert.Equal(JobAlert.Nothing, RemuxPassHandler.AlertFor(Pass(ok: false, RemuxPassOutcomes.FailedBeforeExecution).Set("retryable_wait", true)));

    [Fact]
    public void A_file_whose_source_is_gone_says_nothing() =>
        Assert.Equal(JobAlert.Nothing, RemuxPassHandler.AlertFor(Pass(ok: false, RemuxPassOutcomes.SourceGone)));

    [Fact]
    public void A_file_a_safety_check_left_alone_says_nothing() =>
        Assert.Equal(JobAlert.Nothing, RemuxPassHandler.AlertFor(Pass(ok: false, RemuxPassOutcomes.SkippedGuardrail)));

    [Fact]
    public void A_release_refused_so_another_can_be_found_says_nothing() =>
        Assert.Equal(
            JobAlert.Nothing,
            RemuxPassHandler.AlertFor(Pass(ok: false, RemuxPassOutcomes.FailedBeforeExecution).Set("rejection_kind", "no_audio")));
}
