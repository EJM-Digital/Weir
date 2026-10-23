using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>What the handler makes of a pass's result when a key it looks for is missing or says nothing.</summary>
public sealed class RemuxPassResultShapeTests : IDisposable
{
    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<ProcessingLibraryRecord> LibraryAsync(string rejectedFileAction) =>
        (await _fixture.Db(uow => _fixture.Libraries.SeededForScopeAsync(uow, "movie"), commit: false))! with { RejectedFileAction = rejectedFileAction };

    [Fact]
    public void A_result_that_says_nothing_about_how_it_went_is_a_finished_job()
    {
        Assert.Equal(JobAlert.Finished, RemuxPassHandler.AlertFor(new WireObject()));
        Assert.Equal(JobAlert.Finished, RemuxPassHandler.AlertFor(new WireObject().Set("ok", "yes")));
    }

    [Fact]
    public void A_result_whose_rejection_kind_and_waits_are_empty_or_false_is_judged_by_its_outcome()
    {
        var result = new WireObject()
            .Set("rejection_kind", string.Empty)
            .Set("retryable_wait", false)
            .Set("retry_scheduled", false)
            .Set("outcome", "live_output_written")
            .Set("ok", false);

        Assert.Equal(JobAlert.FailedForGood, RemuxPassHandler.AlertFor(result));
    }

    [Fact]
    public async Task A_rejected_file_with_no_kind_is_deleted_when_the_workflow_asks_and_no_manager_keeps_it()
    {
        var result = new WireObject();

        RemuxPassHandler.ApplyRejectedFileAction(result, await LibraryAsync("delete_file"), WorkflowManagerLinks.None, origin: null);

        Assert.Equal("pending", WireConvert.Str(result["rejected_cleanup_status"]));
    }

    [Fact]
    public async Task A_rejected_file_a_linked_manager_keeps_is_left_where_it_is_and_the_manager_is_named_as_the_reason()
    {
        var result = new WireObject();
        var links = new WorkflowManagerLinks(["deluno"]);

        RemuxPassHandler.ApplyRejectedFileAction(result, await LibraryAsync("delete_file"), links, origin: null);

        Assert.Equal("left_in_place", WireConvert.Str(result["rejected_cleanup_status"]));
        Assert.Equal($"The file was left where it is. {links.KeptOriginalReason}", WireConvert.Str(result["rejected_cleanup_detail"]));
    }
}
