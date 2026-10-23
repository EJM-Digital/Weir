using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>What a pass does about a file it rejected, and what it tells a person: whether the workflow deletes it, and why it may not.</summary>
public sealed class RejectedFileActionTests
{
    private const string NoVideo = "no_video_stream";
    private const string Linked = "linked to Deluno";
    private const string Handoff = "from a hand-off";
    private const string Pending = "pending";
    private const string LeftInPlace = "left_in_place";

    private static readonly WorkflowManagerLinks LinkedToDeluno = new(["deluno"]);

    private static readonly string KeptOriginal = LinkedToDeluno.KeptOriginalReason;

    private static readonly string DeleteSaid =
        "This workflow is set to delete rejected files. Weir recorded the rejection and will now remove only this file.";

    private static readonly string LeaveSaid = "Weir left the rejected file in place because this workflow's cleanup action is Leave in place.";

    public static TheoryData<string, string, string, string?, string?, string, string, string> Choices() => new()
    {
        // kind, chosen action, failure policy, manager link, origin, then what the pass does: action, status, detail
        { NoVideo, RejectedFileActions.Leave, "pass_through", null, null, RejectedFileActions.Leave, LeftInPlace, WeirOnlyRejection.LeftInPlace },
        { NoVideo, RejectedFileActions.Leave, "pass_through", Linked, null, RejectedFileActions.Leave, LeftInPlace, WeirOnlyRejection.LeftInPlace },
        { NoVideo, RejectedFileActions.Leave, "pass_through", null, Handoff, RejectedFileActions.Leave, LeftInPlace, LeaveSaid },
        { NoVideo, RejectedFileActions.Leave, "pass_through", Linked, Handoff, RejectedFileActions.Leave, LeftInPlace, LeaveSaid },
        { NoVideo, RejectedFileActions.DeleteFile, "pass_through", null, null, RejectedFileActions.DeleteFile, Pending, WeirOnlyRejection.DeleteQueued },
        { NoVideo, RejectedFileActions.DeleteFile, "pass_through", null, Handoff, RejectedFileActions.DeleteFile, Pending, DeleteSaid },
        { NoVideo, RejectedFileActions.DeleteFile, "reject", null, null, RejectedFileActions.Leave, LeftInPlace, LeaveSaid },
        { NoVideo, RejectedFileActions.DeleteFile, "pass_through", Linked, null, RejectedFileActions.Leave, LeftInPlace, $"{WeirOnlyRejection.LeftInPlace} {KeptOriginal}" },
        { NoVideo, RejectedFileActions.DeleteFile, "pass_through", Linked, Handoff, RejectedFileActions.Leave, LeftInPlace, $"Weir left the rejected file in place. {KeptOriginal}" },
        { RejectionKinds.UnreadableFile, RejectedFileActions.DeleteFile, "pass_through", null, null, RejectedFileActions.Leave, LeftInPlace, ToolFailureText.UnreadableKept },
        { RejectionKinds.UnreadableFile, RejectedFileActions.Leave, "pass_through", null, null, RejectedFileActions.Leave, LeftInPlace, WeirOnlyRejection.LeftInPlace },
    };

    [Theory]
    [MemberData(nameof(Choices))]
    public void A_rejected_file_is_deleted_only_when_the_workflow_says_so_and_nothing_else_owns_it(
        string kind, string chosen, string policy, string? link, string? origin, string action, string status, string detail)
    {
        var library = new ProcessingLibraryRecord { Name = "Movies", RejectedFileAction = chosen, FailurePolicy = policy };
        var result = new WireObject().Set("rejection_kind", kind);

        RemuxPassHandler.ApplyRejectedFileAction(
            result, library, link is null ? WorkflowManagerLinks.None : LinkedToDeluno, origin is null ? null : new WireObject().Set("source_key", "deluno"));

        Assert.Equal((action, status, detail), (WireConvert.Str(result["rejected_file_action"]), WireConvert.Str(result["rejected_cleanup_status"]), WireConvert.Str(result["rejected_cleanup_detail"])));
    }
}
