import { useCallback, useState } from "react";

import { PageLoading } from "../../../../components/shared/page-loading";
import { errorMessage } from "../../../../lib/api/error-message";
import { useCanEdit } from "../../../../lib/auth/can-edit";
import { useMediaManagerConnectionsQuery } from "../../../../lib/media-managers/queries";
import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import {
  useCreateProcessingLibrary,
  useDeleteProcessingLibrary,
  useProcessingLibrariesQuery,
  useProcessingRuleSetsQuery,
  useReorderProcessingLibraries,
  useUnlinkDiscoveredProcessingLibrary,
  useUpdateProcessingLibrary,
} from "../../../../lib/processing/libraries-queries";
import { inDisplayOrder } from "../../../../lib/processing/workflow-hues";
import { SettingsLoadError } from "../../settings-load-error";
import { AddWorkflowChoice, type WorkflowStart } from "./add-workflow-choice";
import { LibraryEditor } from "./library-editor";
import {
  EMPTY_LIBRARY_FORM,
  formFrom,
  writeFrom,
  type LibraryForm,
} from "./library-form";
import { LibraryImportSection } from "./library-import-section";
import { LibraryListSection } from "./library-list-section";
import { RemoveLibraryDialog } from "./remove-library-dialog";
import {
  useWorkflowDeepLinks,
  type WorkflowDeepLink,
} from "./use-workflow-deep-links";

type Editing =
  | { kind: "closed" }
  | { kind: "choosing"; fromConnectionId?: number }
  | { kind: "adding"; preset: Partial<LibraryForm> }
  | {
      kind: "editing";
      library: ProcessingLibrary;
      /** Values a suggestion fills in over the saved ones. */
      preset?: Partial<LibraryForm>;
    };

/**
 * Setup › Workflows › File paths: add, edit, reorder, switch on and off, and remove the libraries Weir
 * watches. A library is a row, so a fourth one is ordinary rather than a schema change (ADR-0014).
 * The list's own switches and priority order save at once; the editor saves only on its Save.
 */
export function LibrariesTab() {
  const editable = useCanEdit();
  const libraries = useProcessingLibrariesQuery();
  const ruleSets = useProcessingRuleSetsQuery();
  const connections = useMediaManagerConnectionsQuery();
  const create = useCreateProcessingLibrary();
  const update = useUpdateProcessingLibrary();
  const remove = useDeleteProcessingLibrary();
  const reorder = useReorderProcessingLibraries();
  const unlinkDiscovered = useUnlinkDiscoveredProcessingLibrary();

  const [editing, setEditing] = useState<Editing>({ kind: "closed" });
  const [removing, setRemoving] = useState<ProcessingLibrary | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const openDeepLink = useCallback(
    (link: WorkflowDeepLink) =>
      setEditing(
        link.kind === "add-from"
          ? { kind: "choosing", fromConnectionId: link.connectionId }
          : { kind: "editing", library: link.library },
      ),
    [],
  );
  useWorkflowDeepLinks({
    libraries: libraries.data,
    connectionsLoaded: connections.data !== undefined,
    onOpen: openDeepLink,
  });

  if (libraries.isPending) return <PageLoading label="Loading workflows" />;
  if (libraries.isError) return <SettingsLoadError what="workflows" />;

  const rows = inDisplayOrder(libraries.data);
  const managers = connections.data ?? [];
  // The workflow under edit as the server holds it now, not as it was when the editor opened.
  const editedLibrary =
    editing.kind === "editing"
      ? (rows.find((row) => row.id === editing.library.id) ?? editing.library)
      : undefined;

  /** Runs a change, clearing the last notice first and saying why if it fails. */
  const attempt = (work: Promise<unknown>, failure: string) => {
    setNotice(null);
    work.catch((error: unknown) => setNotice(errorMessage(error, failure)));
  };

  const saveLibrary = (form: LibraryForm) =>
    editedLibrary
      ? update.mutateAsync({
          id: editedLibrary.id,
          data: writeFrom(form, editedLibrary),
        })
      : create.mutateAsync(writeFrom(form));

  /** Saves a new order, saying why when it fails; the list returns to the saved order either way. */
  const reorderWorkflows = (ids: number[]) => {
    setNotice(null);
    return reorder
      .mutateAsync(ids)
      .catch((error: unknown) =>
        setNotice(errorMessage(error, "Workflows could not be reordered.")),
      );
  };

  return (
    <div className="mm-quiet-stack" data-testid="processing-libraries-section">
      {notice ? (
        <p
          className="text-sm font-medium text-mm-text1"
          role="status"
          data-testid="processing-library-notice"
        >
          {notice}
        </p>
      ) : null}

      <LibraryListSection
        libraries={rows}
        ruleSets={ruleSets.data ?? []}
        connections={managers}
        editable={editable}
        onAdd={() => {
          setNotice(null);
          setEditing({ kind: "choosing" });
        }}
        onReorder={reorderWorkflows}
        actions={{
          onToggle: (library) =>
            attempt(
              update.mutateAsync({
                id: library.id,
                data: {
                  ...writeFrom(formFrom(library), library),
                  enabled: !library.enabled,
                },
              }),
              "That workflow could not be changed.",
            ),
          onEdit: (library) => {
            setNotice(null);
            setEditing({ kind: "editing", library });
          },
          onUnlink: (library) =>
            attempt(
              unlinkDiscovered
                .mutateAsync(library.id)
                .then(() =>
                  setNotice(
                    `${library.name} is now a Weir-only workflow. Its folders and settings were not changed.`,
                  ),
                ),
              "That workflow could not be unlinked.",
            ),
          onRemove: (library) => {
            remove.reset();
            setRemoving(library);
          },
          unlinking: unlinkDiscovered.isPending,
        }}
      />

      {editing.kind === "choosing" ? (
        <AddWorkflowChoice
          connections={managers}
          workflows={rows}
          initialConnectionId={editing.fromConnectionId}
          onCancel={() => setEditing({ kind: "closed" })}
          onStart={(start: WorkflowStart) =>
            setEditing(
              start.fills
                ? {
                    kind: "editing",
                    library: start.fills,
                    preset: start.preset,
                  }
                : { kind: "adding", preset: start.preset },
            )
          }
        />
      ) : null}

      {editable && managers.length > 0 ? (
        <LibraryImportSection connections={managers} onNotice={setNotice} />
      ) : null}

      {editing.kind === "adding" || editing.kind === "editing" ? (
        <LibraryEditor
          key={editing.kind === "editing" ? editing.library.id : "new"}
          library={editedLibrary}
          initial={
            editedLibrary
              ? { ...formFrom(editedLibrary), ...editing.preset }
              : { ...EMPTY_LIBRARY_FORM, ...editing.preset }
          }
          editable={editable}
          ruleSets={ruleSets.data ?? []}
          connections={managers}
          onSave={saveLibrary}
          onClose={() => setEditing({ kind: "closed" })}
        />
      ) : null}

      {removing ? (
        <RemoveLibraryDialog
          library={removing}
          remove={remove}
          onClose={() => setRemoving(null)}
        />
      ) : null}
    </div>
  );
}
