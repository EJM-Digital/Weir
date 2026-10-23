using Weir.Core.Activity;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Observability;
using Weir.Core.Processing;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

public sealed partial class ManagerWorkflowSync
{
    /// <summary>How long a notice that has already been recorded is not recorded again.</summary>
    private static readonly TimeSpan NoticeRepeat = TimeSpan.FromHours(24);

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    /// <summary>One connection's changes, made in the one unit of work its caller commits.</summary>
    private sealed class Run(ManagerWorkflowSync sync, UnitOfWork uow, ManagerConnection connection, long connectionId, string trigger)
    {
        private List<ProcessingLibraryRecord> _workflows = [];

        /// <summary>
        /// Makes every change <see cref="WorkflowSyncRules.Plan"/> asks for and reports what it leaves alone; true when a workflow changed.
        /// <paramref name="listed"/> is every library the manager lists, whether or not it processes with Weir.
        /// </summary>
        public async Task<bool> ApplyAsync(IReadOnlyList<SyncedLibrary> wanted, IReadOnlyList<ManagerLibraryDescriptor> listed)
        {
            _workflows = await sync._libraries.ListAsync(uow).ConfigureAwait(false);
            var snapshots = new List<SyncWorkflow>();
            foreach (var workflow in _workflows)
            {
                var linked = await sync._libraries.ManagerConnectionIdsAsync(uow, workflow.Id).ConfigureAwait(false);
                var synced = WorkflowSyncRules.FoldersSyncedFrom(workflow.DiscoveredFromConnectionId, workflow.DiscoveredLibraryKey, linked, connection.Kind) is not null;
                snapshots.Add(new SyncWorkflow(
                    workflow.Id, workflow.Name, workflow.MediaType, workflow.WatchedFolder, workflow.OutputFolder,
                    workflow.DiscoveredFromConnectionId, workflow.DiscoveredLibraryKey, synced, linked));
            }

            // A folder the manager now gives may only be free once another workflow has let go of the one it held,
            // so a change that conflicts is tried again after the rest and only then reported.
            var changed = false;
            var waiting = new List<WorkflowSyncAction>();
            foreach (var action in WorkflowSyncRules.Plan(connectionId, wanted, snapshots))
            {
                changed |= await ApplyAsync(action, waiting).ConfigureAwait(false);
            }

            foreach (var action in waiting)
            {
                changed |= await ApplyAsync(action, waiting: null).ConfigureAwait(false);
            }

            foreach (var library in wanted.Where(library => library.Problem is not null))
            {
                await ReportProblemAsync(library).ConfigureAwait(false);
            }

            foreach (var departed in WorkflowSyncRules.Departed(connectionId, listed, snapshots))
            {
                await ReportDepartedAsync(departed).ConfigureAwait(false);
            }

            return changed;
        }

        /// <summary>
        /// Makes one change. A change the editor's folder rules refuse is added to <paramref name="waiting"/> to be tried again, or
        /// reported when <paramref name="waiting"/> is null.
        /// </summary>
        private async Task<bool> ApplyAsync(WorkflowSyncAction action, List<WorkflowSyncAction>? waiting)
        {
            var library = action.Library;
            var existing = action.Workflow is { } snapshot ? _workflows.First(workflow => workflow.Id == snapshot.Id) : null;
            var watched = action.ChangesWatched ? library.WatchedFolder : action.ClearsWatched ? string.Empty : null;
            var output = action.ChangesOutput ? library.OutputFolder : null;
            if (await FolderConflictAsync(existing, watched, output).ConfigureAwait(false) is { } conflict)
            {
                if (waiting is not null)
                {
                    waiting.Add(action);
                }
                else
                {
                    await ReportConflictAsync(action, conflict).ConfigureAwait(false);
                }

                return false;
            }

            var result = action.Kind switch
            {
                WorkflowSyncKind.Create => await CreateAsync(library, watched, output).ConfigureAwait(false),
                WorkflowSyncKind.Adopt => await AdoptAsync(library, existing!, watched, output).ConfigureAwait(false),
                _ => await sync._libraries.SetFoldersFromManagerAsync(uow, existing!, watched, output).ConfigureAwait(false),
            };
            Replace(result);
            await ReportChangeAsync(action, result, watched, output).ConfigureAwait(false);
            return true;
        }

        /// <summary>What a workflow about to be made holds: no folders yet.</summary>
        private static readonly ProcessingLibraryRecord NoWorkflowYet = new() { Name = string.Empty };

        /// <summary>Why the folders the manager reports cannot be given to this workflow (the editor's own folder rules), or null.</summary>
        private async Task<string?> FolderConflictAsync(ProcessingLibraryRecord? existing, string? watched, string? output)
        {
            if (watched is null && output is null)
            {
                return null;
            }

            var others = await sync._libraries.OtherFoldersAsync(uow, existing?.Id).ConfigureAwait(false);
            try
            {
                var current = existing ?? NoWorkflowYet;
                LibraryRules.ValidateFolders(watched ?? current.WatchedFolder, current.WorkFolder, output ?? current.OutputFolder, others, sync._options.WeirHome);
                return null;
            }
            catch (ProcessingLibraryException exception)
            {
                return exception.Message;
            }
        }

        private async Task<ProcessingLibraryRecord> CreateAsync(SyncedLibrary library, string? watched, string? output)
        {
            var created = await sync._libraries.CreateDiscoveredAsync(uow, new ProcessingLibraryRecord
            {
                Name = LibraryDiscoveryService.UniqueName(_workflows.Select(workflow => workflow.Name).ToHashSet(StringComparer.Ordinal), library.Name),
                Enabled = !string.IsNullOrEmpty(output),
                MediaType = library.MediaType,
                DisplayOrder = _workflows.Count == 0 ? 0 : _workflows.Max(workflow => workflow.DisplayOrder) + 1,
                WatchedFolder = watched ?? string.Empty,
                OutputFolder = output ?? string.Empty,
                DiscoveredFromConnectionId = connectionId,
                DiscoveredLibraryKey = library.Key,
            }).ConfigureAwait(false);
            _workflows.Add(created);
            return created;
        }

        private Task<ProcessingLibraryRecord> AdoptAsync(SyncedLibrary library, ProcessingLibraryRecord existing, string? watched, string? output)
        {
            var others = _workflows.Where(workflow => workflow.Id != existing.Id).Select(workflow => workflow.Name).ToHashSet(StringComparer.Ordinal);
            return sync._libraries.AdoptForManagerAsync(
                uow, existing, LibraryDiscoveryService.UniqueName(others, library.Name), connectionId, library.Key, watched, output);
        }

        /// <summary>Puts a workflow just written in place of its earlier self; every workflow written here is already in the list.</summary>
        private void Replace(ProcessingLibraryRecord workflow) =>
            _workflows[_workflows.FindIndex(existing => existing.Id == workflow.Id)] = workflow;

        private async Task ReportChangeAsync(WorkflowSyncAction action, ProcessingLibraryRecord workflow, string? watched, string? output)
        {
            var label = connection.Label;
            var setUp = action.Kind != WorkflowSyncKind.Update;
            if (action.ClearsWatched)
            {
                await RecordAsync(
                    ActivityEventTypes.ProcessingWorkflowSynced,
                    WorkflowSyncRules.ClearedTitle(workflow.Name),
                    WorkflowSyncRules.ClearedMessage(workflow.Name, action.Workflow!.WatchedFolder, action.SharedWith!, action.Library.Problem!),
                    result: "warning",
                    nextAction: WorkflowSyncRules.ProblemNextAction(action.Library, label),
                    workflow).ConfigureAwait(false);
            }

            if (setUp || action.ChangesWatched || action.ChangesOutput)
            {
                await RecordAsync(
                    ActivityEventTypes.ProcessingWorkflowSynced,
                    setUp ? WorkflowSyncRules.SetUpTitle(workflow.Name, label) : WorkflowSyncRules.UpdatedTitle(workflow.Name, label, action.ChangesWatched, action.ChangesOutput),
                    setUp ? WorkflowSyncRules.SetUpMessage(workflow.Name, label, NullIfEmpty(workflow.WatchedFolder), NullIfEmpty(workflow.OutputFolder)) : WorkflowSyncRules.UpdatedMessage(workflow.Name, label, action.ChangesWatched ? watched : null, output),
                    result: "success",
                    nextAction: null,
                    workflow).ConfigureAwait(false);
            }
        }

        private async Task ReportConflictAsync(WorkflowSyncAction action, string conflict)
        {
            var name = action.Workflow?.Name ?? action.Library.Name;
            var label = connection.Label;
            var verb = action.Kind == WorkflowSyncKind.Update ? "update" : "set up";
            await NoticeAsync(
                $"Weir could not {verb} {name} from {label}",
                $"Weir could not use the folders {label} reports for {name}: {conflict} Weir left {name} as it is.",
                $"Give {name} and the other workflow separate folders: change the downloads folder for {name} in {label}, or unlink or remove the other workflow in Weir.",
                action.Library.MediaType,
                _workflows.FirstOrDefault(workflow => workflow.Id == action.Workflow?.Id)).ConfigureAwait(false);
        }

        /// <summary>A library whose folders the manager does not say yet: the workflow waits, and the person is told what to set.</summary>
        private async Task ReportProblemAsync(SyncedLibrary library)
        {
            var workflow = _workflows.FirstOrDefault(candidate =>
                candidate.DiscoveredFromConnectionId == connectionId && candidate.DiscoveredLibraryKey == library.Key);
            if (workflow is null)
            {
                return;
            }

            var linked = await sync._libraries.ManagerConnectionIdsAsync(uow, workflow.Id).ConfigureAwait(false);
            if (WorkflowSyncRules.FoldersSyncedFrom(connectionId, library.Key, linked, connection.Kind) is null)
            {
                return;
            }

            await NoticeAsync(
                WorkflowSyncRules.ProblemTitle(library, connection.Label),
                library.Problem!,
                WorkflowSyncRules.ProblemNextAction(library, connection.Label),
                library.MediaType,
                workflow).ConfigureAwait(false);
        }

        /// <summary>A workflow whose library stopped processing with Weir or is gone from the manager: left as it is, and the person is told.</summary>
        private Task ReportDepartedAsync(DepartedWorkflow departed)
        {
            var label = connection.Label;
            return NoticeAsync(
                WorkflowSyncRules.DepartedTitle(departed, label),
                WorkflowSyncRules.DepartedMessage(departed, label),
                WorkflowSyncRules.DepartedNextAction(departed, label),
                departed.Workflow.MediaType,
                _workflows.First(workflow => workflow.Id == departed.Workflow.Id));
        }

        private async Task NoticeAsync(string title, string message, string nextAction, string mediaType, ProcessingLibraryRecord? workflow)
        {
            var cutoff = Timestamp.FromUtc(sync._time.GetUtcNow().UtcDateTime - NoticeRepeat);
            var seen = await uow.ScalarAsync(
                "SELECT activity_events.id FROM activity_events WHERE activity_events.event_type = $type AND activity_events.title = $title " +
                "AND activity_events.created_at >= $cutoff LIMIT 1 OFFSET 0",
                ("$type", ActivityEventTypes.ProcessingWorkflowSyncNotice),
                ("$title", title),
                ("$cutoff", cutoff.ToSqlite())).ConfigureAwait(false);
            if (seen is null)
            {
                await RecordAsync(ActivityEventTypes.ProcessingWorkflowSyncNotice, title, message, "warning", nextAction, workflow, mediaType).ConfigureAwait(false);
            }
        }

        private Task<long> RecordAsync(
            string eventType, string title, string message, string result, string? nextAction, ProcessingLibraryRecord? workflow, string? mediaType = null)
        {
            var detail = OperatorMessages.ActivityDetailEnvelope(
                module: "processing",
                action: "workflow_sync",
                trigger: trigger,
                result: result,
                provider: connection.Kind,
                mediaScope: workflow?.MediaType ?? mediaType,
                userMessage: message,
                nextAction: nextAction);
            if (workflow is not null)
            {
                detail.Set("library_id", workflow.Id);
            }

            return SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(
                eventType, "processing", title, WireStrings.Slice(WireJsonWriter.Dumps(detail, WireJsonFormat.Compact), 10_000)));
        }
    }
}
