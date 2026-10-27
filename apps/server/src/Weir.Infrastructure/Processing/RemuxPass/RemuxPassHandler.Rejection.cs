using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class RemuxPassHandler
{
    /// <summary>
    /// Decides what happens to the file a pass rejected. Under the reject policy the manager is asked, and the download is only
    /// removed once it accepts. Otherwise the library's own rejected-file choice applies, worded for a person when no manager is involved.
    /// </summary>
    internal static void ApplyRejectedFileAction(WireObject result, ProcessingLibraryRecord library, WorkflowManagerLinks links, WireObject? origin)
    {
        var weirOnly = WeirOnlyRejection.Applies(library, origin);
        if (weirOnly)
        {
            WeirOnlyRejection.Present(result);
        }

        var chosenDelete = string.Equals(WireStrings.Strip(library.RejectedFileAction), RejectedFileActions.DeleteFile, StringComparison.OrdinalIgnoreCase);
        // A file Weir could not read may be a good file it cannot read yet, or a share that hiccuped: Weir cannot vouch for it, so it never destroys it.
        var unreadable = result.Get("rejection_kind") is WireString { Value: RejectionKinds.UnreadableFile };
        var deletes = chosenDelete
                      && !unreadable
                      // Under reject, the reject job removes the download, and only after the manager accepts.
                      && ProcessingFailurePolicies.Normalize(library.FailurePolicy) != ProcessingFailurePolicies.Reject
                      // A linked workflow's original belongs to the download client and the manager, whatever the workflow's own choice says.
                      && !links.KeepsOriginals;
        result.Set("rejected_file_action", deletes ? RejectedFileActions.DeleteFile : RejectedFileActions.Leave);
        if (deletes)
        {
            result.Set("rejected_cleanup_status", "pending");
            result.Set(
                "rejected_cleanup_detail",
                weirOnly ? WeirOnlyRejection.DeleteQueued : "This workflow is set to delete rejected files. Weir recorded the rejection and will now remove only this file.");
        }
        else
        {
            result.Set("rejected_cleanup_status", "left_in_place");
            result.Set(
                "rejected_cleanup_detail",
                unreadable && chosenDelete ? ToolFailureText.UnreadableKept
                : weirOnly ? WeirOnlyLeftInPlace(chosenDelete, links)
                : RejectedLeftInPlace(library, links));
        }
    }

    /// <summary>The file stays; when the workflow asked for it to be deleted, a linked manager is why it was not.</summary>
    internal static string WeirOnlyLeftInPlace(bool chosenDelete, WorkflowManagerLinks links) =>
        chosenDelete && links.KeepsOriginals ? $"{WeirOnlyRejection.LeftInPlace} {links.KeptOriginalReason}" : WeirOnlyRejection.LeftInPlace;

    private static string RejectedLeftInPlace(ProcessingLibraryRecord library, WorkflowManagerLinks links) =>
        links.KeepsOriginals && string.Equals(WireStrings.Strip(library.RejectedFileAction), RejectedFileActions.DeleteFile, StringComparison.OrdinalIgnoreCase)
            ? $"Weir left the rejected file in place. {links.KeptOriginalReason}"
            : "Weir left the rejected file in place because this workflow's cleanup action is Leave in place.";

    /// <summary>
    /// Records a rules rejection no manager is involved in as <c>rejected</c>, with the file's current identity so Activity's
    /// remove dialog can act on it safely later. Its failure fields are cleared: rejecting is a decision, not an attempt to retry.
    /// </summary>
    private static async Task RecordWeirOnlyRejectionAsync(UnitOfWork uow, ProcessingLibraryRecord library, string relativePath, string reason, WireObject result)
    {
        await RemuxPassFileState.ClearFailureFieldsAsync(uow, library.Id, relativePath).ConfigureAwait(false);
        var source = result.Get("inspected_source_path") is WireString { Value.Length: > 0 } inspected && File.Exists(inspected.Value) ? inspected.Value : null;
        await RemuxPassFileState.UpsertRejectedAsync(uow, library.Id, relativePath, reason, ProcessingFailureClasses.Rules, source).ConfigureAwait(false);
    }
}
