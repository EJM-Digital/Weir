import { useMutation } from "@tanstack/react-query";
import { useState } from "react";
import { Link } from "react-router-dom";

import {
  QuietDisclosure,
  quietActionRowClass,
} from "../../../../components/shared/quiet-section";
import { SidePanel } from "../../../../components/shared/side-panel";
import { errorMessage } from "../../../../lib/api/error-message";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import type { ProcessingRuleSet } from "../../../../lib/processing/rule-sets-api";
import type { WorkflowPath } from "../../../../lib/processing/workflow-story";
import { useProcessingRejectSupportQuery } from "../../../../lib/processing/libraries-queries";
import {
  workflowBadgeLabel,
  workflowKindOf,
} from "../../../../lib/processing/workflow-kind";
import { connectionTitle } from "../../../../lib/ui/connection-title";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { SAVE_MODEL_WORDS } from "../../save-model-note";
import { useLeaveConfirmation, useUnsavedChanges } from "../../unsaved-changes";
import { effectiveGrid, windowNow } from "../schedule/schedule-model";
import { LibraryFolderChain } from "./library-folder-chain";
import { LibraryFoldersGroup } from "./library-folders-group";
import {
  linkedConnectionIds,
  sameLibraryForm,
  type LibraryForm,
} from "./library-form";
import { LibraryFfmpegFold } from "./library-ffmpeg-fold";
import {
  LibraryIntakeGroup,
  LibraryReadinessGroup,
} from "./library-intake-groups";
import { LibraryDownloadClientSuggestions } from "./library-download-client-suggestions";
import { LibraryLinkSection } from "./library-link-section";
import { LibraryManagerSetup } from "./library-manager-setup";
import { LibraryFailureGroup } from "./library-failure-group";
import {
  LibraryCapacityGroup,
  LibraryOutputGroup,
} from "./library-output-groups";
import type { LibraryFormBinding } from "./library-settings";
import { setupTabPath } from "../../../../lib/settings/setup-areas";

/** The edits that still differ from the workflow, so a field put back to what is saved follows the server again. */
function withoutUnchanged(
  edits: Partial<LibraryForm>,
  saved: LibraryForm,
): Partial<LibraryForm> {
  return Object.fromEntries(
    Object.entries(edits).filter(
      ([key, value]) => value !== saved[key as keyof LibraryForm],
    ),
  );
}

/** When the library may run, read from the schedule that owns its hours. */
function runHoursText(library: ProcessingLibrary | undefined): string {
  if (!library) return "Any time, until you choose hours. ";
  return windowNow(effectiveGrid(library), undefined, new Date()).kind === "any"
    ? "Any time. "
    : "On the hours chosen for it. ";
}

/** The one line under the drawer's title: how it saves, and what a new workflow is. */
function panelSubtitle(library: ProcessingLibrary | undefined): string {
  return library
    ? `${SAVE_MODEL_WORDS.explicit} A saved change applies on the next scan.`
    : `${SAVE_MODEL_WORDS.explicit} A workflow is a watched folder, a work area and an output folder.`;
}

/**
 * The library editor, as a slide-over: every setting of one workflow's new downloads in groups, the
 * rarely changed ones folded away. Nothing in it is saved until Save, and closing it with edits asks
 * first. The files a library already holds are set up on the Library page.
 */
export function LibraryEditor({
  library,
  initial,
  editable,
  ruleSets,
  connections,
  onSave,
  onClose,
}: {
  /** The saved library being edited; undefined while adding one. */
  library: ProcessingLibrary | undefined;
  initial: LibraryForm;
  editable: boolean;
  ruleSets: ProcessingRuleSet[];
  connections: MediaManagerConnection[];
  /** Saves the workflow's fields. */
  onSave: (form: LibraryForm) => Promise<unknown>;
  onClose: () => void;
}) {
  // Only what the person changed is held here; every other field is the workflow as it is now, so a change made
  // on the server while the editor is open (a media manager's sync, another browser) shows up and is never saved over.
  const [edits, setEdits] = useState<Partial<LibraryForm>>({});
  const form: LibraryForm = { ...initial, ...edits };
  const binding: LibraryFormBinding = {
    form,
    update: (patch) =>
      setEdits((current) =>
        withoutUnchanged({ ...current, ...patch }, initial),
      ),
    editable,
  };
  const saveAll = useMutation({
    mutationFn: () => onSave(form),
    onSuccess: onClose,
  });
  const linkedIds = linkedConnectionIds(form, library);
  const kind = workflowKindOf(
    { manager_connection_ids: linkedIds },
    connections,
  );
  // A manager that reports its folders owns the watched and output folders until the workflow is unlinked from it.
  const syncedConnection = connections.find(
    (connection) =>
      connection.id === library?.folders_synced_from_connection_id &&
      linkedIds.includes(connection.id),
  );
  const foldersEditable = editable && !syncedConnection;
  const workflowPath: WorkflowPath = {
    watched: form.watched_folder.trim(),
    work: form.work_folder.trim(),
    output: form.output_folder.trim(),
  };
  const dirty = !sameLibraryForm(form, initial);
  const thing = library ? library.name : "the new workflow";
  useUnsavedChanges(dirty ? thing : null);
  const { confirmLeave, dialog } = useLeaveConfirmation();
  const close = () => confirmLeave(dirty ? thing : null, onClose);
  // Reject is offered for the manager chosen in the editor, saved or not.
  const rejectSupport = useProcessingRejectSupportQuery(
    form.manager_connection_id ? [Number(form.manager_connection_id)] : [],
    true,
  );

  return (
    <>
      <SidePanel
        open
        title={library ? "Edit workflow" : "Add workflow"}
        eyebrow="Setup · Workflows"
        subtitle={panelSubtitle(library)}
        onClose={close}
        dataTestId="processing-library-form"
      >
        <div className="mm-editor">
          <div className="mm-editor-sections">
            <LibraryFoldersGroup
              binding={binding}
              ruleSets={ruleSets}
              syncedFrom={
                syncedConnection ? connectionTitle(syncedConnection) : undefined
              }
            />
            <QuietDisclosure
              title="Media manager"
              summaryWhenClosed={workflowBadgeLabel(kind)}
            >
              <div className="mm-quiet-stack">
                <LibraryLinkSection
                  kind={kind}
                  path={workflowPath}
                  connections={connections}
                  editable={editable}
                  onLink={(connectionId) =>
                    binding.update({
                      manager_connection_id: String(connectionId),
                    })
                  }
                  onUnlink={() => binding.update({ manager_connection_id: "" })}
                />
                <LibraryDownloadClientSuggestions
                  mediaType={form.media_type}
                  watchedFolder={form.watched_folder}
                  editable={foldersEditable}
                  onUseFolder={(watched) =>
                    binding.update({ watched_folder: watched })
                  }
                />
                {linkedIds.length > 0 ? (
                  <LibraryManagerSetup
                    mediaType={form.media_type}
                    watchedFolder={form.watched_folder}
                    outputFolder={form.output_folder}
                    workFolder={form.work_folder}
                    // A linked workflow never removes its originals, so a seeding client is no problem for it.
                    removeOriginal={false}
                    linkedConnectionIds={linkedIds}
                    editable={foldersEditable}
                    onUseFolders={(watched, output) =>
                      binding.update({
                        watched_folder: watched ?? form.watched_folder,
                        output_folder: output ?? form.output_folder,
                      })
                    }
                  />
                ) : null}
              </div>
            </QuietDisclosure>
            <LibraryFolderChain
              libraryId={library?.id}
              watchedFolder={form.watched_folder}
              workFolder={form.work_folder}
              outputFolder={form.output_folder}
              mediaType={form.media_type}
            />
            <LibraryIntakeGroup binding={binding} kind={kind} />
            <LibraryReadinessGroup binding={binding} />
            <LibraryOutputGroup binding={binding} kind={kind} />
            <LibraryCapacityGroup binding={binding} />
            <LibraryFailureGroup
              binding={binding}
              rejectSupport={rejectSupport}
            />
            <LibraryFfmpegFold binding={binding} />
            {/* The hours are drawn in Setup › Workflows › Schedule beside every other library's week, so the two never disagree. */}
            <QuietDisclosure title="When this workflow may run">
              <p
                className="mm-quiet-note"
                data-testid="processing-library-hours"
              >
                {runHoursText(library)}
                <Link
                  className="mm-schedule-link"
                  to={setupTabPath("schedule")}
                >
                  Change the hours in Schedule
                </Link>
              </p>
            </QuietDisclosure>
          </div>
          {library ? (
            <p
              className="mm-quiet-note mt-4"
              data-testid="processing-library-cleaning-link"
            >
              Cleaning files already in your library is on the{" "}
              <Link
                className="mm-schedule-link"
                to={`/library?library=${library.id}`}
              >
                Library page
              </Link>
              .
            </p>
          ) : null}
          {saveAll.isError ? (
            <p
              className="mm-status-text mt-4 text-sm"
              data-status="broken"
              role="alert"
              data-testid="processing-library-save-error"
            >
              {errorMessage(saveAll.error, "That workflow could not be saved.")}
            </p>
          ) : null}
          <div className={`${quietActionRowClass} mm-editor-actions`}>
            <button
              type="button"
              className={mmActionButtonClass({ variant: "primary" })}
              onClick={() => saveAll.mutate()}
              disabled={!editable || !form.name.trim() || saveAll.isPending}
              data-testid="processing-library-save"
            >
              {saveAll.isPending ? "Saving…" : "Save"}
            </button>
            <button
              type="button"
              className={mmActionButtonClass({ variant: "tertiary" })}
              disabled={saveAll.isPending}
              onClick={close}
            >
              Cancel
            </button>
          </div>
        </div>
      </SidePanel>
      {dialog}
    </>
  );
}
