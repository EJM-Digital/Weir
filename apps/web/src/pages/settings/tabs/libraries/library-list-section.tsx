import { useId } from "react";

import { QuietSection } from "../../../../components/shared/quiet-section";
import { ColumnsMenu } from "../../../../components/shared/columns-menu";
import { SortableColumnHeader } from "../../../../components/shared/sortable-column-header";
import { useRowReorder } from "../../../../components/shared/use-row-reorder";
import { PageToolbarAddButton } from "../../../../components/shell/page-toolbar-actions";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import { type ProcessingRuleSet } from "../../../../lib/processing/rule-sets-api";
import {
  workflowKindCounts,
  workflowKindOf,
} from "../../../../lib/processing/workflow-kind";
import { plural } from "../../../../lib/ui/mm-plural";
import { useTableColumns } from "../../../../lib/ui/use-table-columns";
import { SaveModelNote } from "../../save-model-note";
import { LibraryRow, type LibraryRowActions } from "./library-row";
import { WORKFLOW_COLUMN_CLASS, WORKFLOW_COLUMNS } from "./workflow-columns";

const PRIORITY_HINT =
  "Drag the grip to change a workflow's priority. From the keyboard, press Alt with the up or down arrow, or press Space to pick it up, the arrow keys to move it and Space to drop it.";

const PRIORITY_MEANING =
  "Priority also sets each media type's default workflow, and the order and colour of workflows everywhere else.";

/** Every library in the order Weir offers them work, with what each one does. */
export function LibraryListSection({
  libraries,
  ruleSets,
  connections,
  editable,
  onAdd,
  onReorder,
  actions,
}: {
  /** Already in display order. */
  libraries: ProcessingLibrary[];
  ruleSets: ProcessingRuleSet[];
  connections: MediaManagerConnection[];
  editable: boolean;
  onAdd: () => void;
  /** Saves the workflows' ids in their new order. */
  onReorder: (ids: number[]) => Promise<unknown>;
  actions: LibraryRowActions;
}) {
  const hintId = useId();
  const columns = useTableColumns(WORKFLOW_COLUMNS);
  const reorder = useRowReorder({
    ids: libraries.map((library) => library.id),
    nameOf: (id) =>
      libraries
        .filter((library) => library.id === id)
        .map((library) => library.name)
        .join(""),
    onCommit: onReorder,
  });
  const inPriorityOrder = reorder.orderedIds.flatMap((id) =>
    libraries.filter((library) => library.id === id),
  );

  return (
    <QuietSection
      headingId="processing-libraries-heading"
      heading={
        libraries.length === 0
          ? "No workflows"
          : plural(libraries.length, "workflow", "workflows")
      }
      count={
        libraries.length === 0 ? null : (
          <span data-testid="workflow-kind-counts">
            {workflowKindCounts(
              libraries.map((library) => workflowKindOf(library, connections)),
            )}
          </span>
        )
      }
      aside={
        <>
          <SaveModelNote model="instant" />
          <ColumnsMenu table={columns} />
        </>
      }
    >
      {editable ? (
        <PageToolbarAddButton
          label="Add workflow"
          onClick={onAdd}
          dataTestId="processing-library-add"
        />
      ) : null}
      <p className="mm-quiet-note">
        Each workflow watches one folder and cleans into another, on its own or
        linked to a media manager.
      </p>
      {libraries.length === 0 ? (
        <p className="mm-quiet-note mt-4">
          No workflows yet. Add one to tell Weir which folder to watch.
        </p>
      ) : (
        <>
          <div className="mm-quiet-table-wrap mt-3">
            <table
              className="mm-quiet-table mm-workflow-table"
              {...columns.tableProps}
            >
              <thead>
                <tr>
                  {columns.order.map((id) => (
                    <SortableColumnHeader
                      key={id}
                      heading={columns.heading(id)}
                      className={WORKFLOW_COLUMN_CLASS[id]}
                      title={id === "priority" ? PRIORITY_MEANING : undefined}
                      hideLabel={id === "actions"}
                    />
                  ))}
                </tr>
              </thead>
              <tbody>
                {inPriorityOrder.map((library, index) => (
                  <LibraryRow
                    key={library.id}
                    library={library}
                    order={columns.order}
                    position={index + 1}
                    ruleSetName={
                      ruleSets.find((set) => set.id === library.rule_set_id)
                        ?.name
                    }
                    connections={connections}
                    editable={editable}
                    actions={actions}
                    handle={editable ? reorder.handleProps(library.id) : null}
                    hintId={hintId}
                    grabbed={reorder.grabbedId === library.id}
                    rowRef={reorder.rowRef(library.id)}
                  />
                ))}
              </tbody>
            </table>
          </div>
          <p
            className="mm-quiet-note mt-3"
            data-testid="workflow-priority-note"
          >
            When a file could go to more than one workflow, the higher one takes
            it.
          </p>
          <p id={hintId} className="sr-only">
            {PRIORITY_HINT}
          </p>
          <p
            className="sr-only"
            role="status"
            data-testid="workflow-priority-announcement"
          >
            {reorder.announcement}
          </p>
        </>
      )}
    </QuietSection>
  );
}
