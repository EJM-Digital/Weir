import { Fragment, type ReactNode } from "react";

import { Chip } from "../../../../components/panels/chip";
import { StatusDot } from "../../../../components/panels/status-dot";
import { ReorderHandle } from "../../../../components/shared/reorder-handle";
import type { ReorderHandleProps } from "../../../../components/shared/use-row-reorder";
import { WorkflowKindBadge } from "../../../../components/shared/workflow-kind";
import { MmOnOffSwitch } from "../../../../components/ui/mm-on-off-switch";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import {
  processingMediaTypeBadge,
  type ProcessingLibrary,
} from "../../../../lib/processing/libraries-api";
import {
  workflowKindNote,
  workflowKindOf,
} from "../../../../lib/processing/workflow-kind";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { OutputFolderCell, WatchedFolderCell } from "./library-paths";
import {
  WORKFLOW_COLUMN_CLASS,
  type WorkflowColumnId,
} from "./workflow-columns";

export type LibraryRowActions = {
  onToggle: (library: ProcessingLibrary) => void;
  onEdit: (library: ProcessingLibrary) => void;
  onUnlink: (library: ProcessingLibrary) => void;
  onRemove: (library: ProcessingLibrary) => void;
  unlinking: boolean;
};

/**
 * The kind of workflow, said the same way on every page: Weir only, or linked to a media manager (and kept in
 * step with it when it came from one). A manager that did not answer is a short red word under the badge, and its own
 * last word is that word's hover and what a screen reader says, so every row stays the same height.
 */
function WorkflowSource({
  library,
  connections,
}: {
  library: ProcessingLibrary;
  connections: MediaManagerConnection[];
}) {
  const kind = workflowKindOf(library, connections);
  const note = workflowKindNote(kind);
  const unreachable = library.manager_coverage === "unreachable";
  const lastWord =
    library.manager_connection_ids
      .map((id) => connections.find((c) => c.id === id)?.last_test_detail)
      .find((detail) => detail) ??
    "Its media manager did not answer the last check.";
  return (
    <>
      {/* The badge says the kind; the sentence about it is its hover, so every row stays one line tall. */}
      <span className="block" title={note}>
        <WorkflowKindBadge kind={kind} className="mm-workflow-table__badge" />
        <span className="sr-only">{note}</span>
      </span>
      {library.discovered_from_connection_id ? (
        <span className="mm-quiet-table__sub">Kept in step with it.</span>
      ) : null}
      {unreachable ? (
        <span
          className="mm-quiet-table__sub mm-status-text"
          data-status="broken"
          title={lastWord}
        >
          <StatusDot className="mr-1.5 align-middle" meaning="broken" />
          <span aria-hidden="true">Not answering</span>
          <span className="sr-only">{lastWord}</span>
        </span>
      ) : null}
    </>
  );
}

/** Where the workflow stands in the order that decides which one takes a file; a grip moves it when it can be changed. */
function PriorityCell({
  position,
  name,
  handle,
  hintId,
}: {
  position: number;
  name: string;
  handle: ReorderHandleProps | null;
  hintId: string;
}) {
  return (
    <td
      data-col="priority"
      data-label="Priority"
      className={WORKFLOW_COLUMN_CLASS.priority}
    >
      <div className="mm-priority">
        {handle ? (
          <ReorderHandle
            label={`Move ${name}`}
            describedBy={hintId}
            handle={handle}
          />
        ) : null}
        <span className="mm-priority__number">{position}</span>
      </div>
    </td>
  );
}

export type LibraryRowProps = {
  library: ProcessingLibrary;
  /** The columns in the order the table shows them. */
  order: readonly WorkflowColumnId[];
  /** One for the workflow that takes a file first. */
  position: number;
  ruleSetName: string | undefined;
  connections: MediaManagerConnection[];
  editable: boolean;
  actions: LibraryRowActions;
  /** The grip, or null when the order cannot be changed. */
  handle: ReorderHandleProps | null;
  /** The id of the text that says how to use the grip. */
  hintId: string;
  grabbed: boolean;
  rowRef: (row: HTMLTableRowElement | null) => void;
};

export function LibraryRow({
  library,
  order,
  position,
  ruleSetName,
  connections,
  editable,
  actions,
  handle,
  hintId,
  grabbed,
  rowRef,
}: LibraryRowProps) {
  const badge = processingMediaTypeBadge(library);
  const remove = mmActionButtonClass({
    variant: "danger-outline",
    size: "row",
  });
  const secondary = mmActionButtonClass({ variant: "secondary", size: "row" });
  const cells: Record<WorkflowColumnId, ReactNode> = {
    priority: (
      <PriorityCell
        position={position}
        name={library.name}
        handle={handle}
        hintId={hintId}
      />
    ),
    workflow: (
      <th
        scope="row"
        data-col="workflow"
        className={`mm-quiet-table__name ${WORKFLOW_COLUMN_CLASS.workflow}`}
      >
        <div className="mm-workflow-name">
          <span className="mm-workflow-name__text" title={library.name}>
            {library.name}
          </span>
          {badge ? <Chip dot={false}>{badge}</Chip> : null}
        </div>
        {library.active_job_count > 0 ? (
          <span className="mm-quiet-table__sub">
            {library.active_job_count} in progress
          </span>
        ) : null}
      </th>
    ),
    kind: (
      <td
        data-col="kind"
        data-label="Kind"
        className={WORKFLOW_COLUMN_CLASS.kind}
      >
        <WorkflowSource library={library} connections={connections} />
      </td>
    ),
    watches: (
      <td
        data-col="watches"
        data-label="Watches"
        className={WORKFLOW_COLUMN_CLASS.watches}
      >
        <WatchedFolderCell library={library} />
      </td>
    ),
    cleans: (
      <td
        data-col="cleans"
        data-label="Cleans into"
        className={WORKFLOW_COLUMN_CLASS.cleans}
      >
        <OutputFolderCell library={library} />
      </td>
    ),
    rules: (
      <td
        data-col="rules"
        data-label="Rules"
        className={WORKFLOW_COLUMN_CLASS.rules}
        title={ruleSetName}
      >
        {ruleSetName ?? (
          <span className="mm-quiet-table__sub">Default rules</span>
        )}
      </td>
    ),
    on: (
      <td data-col="on" data-label="On" className={WORKFLOW_COLUMN_CLASS.on}>
        <MmOnOffSwitch
          id={`processing-library-enabled-${library.id}`}
          label={`${library.name} enabled`}
          enabled={library.enabled}
          disabled={!editable}
          onChange={(next) => {
            if (next !== library.enabled) actions.onToggle(library);
          }}
          layout="control"
          size="row"
        />
      </td>
    ),
    actions: (
      <td
        data-col="actions"
        data-label=""
        className={WORKFLOW_COLUMN_CLASS.actions}
      >
        <div className="mm-workflow-actions">
          <button
            type="button"
            className={secondary}
            onClick={() => actions.onEdit(library)}
            disabled={!editable}
          >
            Edit
          </button>
          {library.discovered_from_connection_id ? (
            <button
              type="button"
              className={secondary}
              onClick={() => actions.onUnlink(library)}
              disabled={!editable || actions.unlinking}
            >
              Unlink
            </button>
          ) : null}
          <button
            type="button"
            className={remove}
            onClick={() => actions.onRemove(library)}
            disabled={!editable}
            aria-haspopup="dialog"
            data-testid={`processing-library-remove-${library.id}`}
          >
            Remove
          </button>
        </div>
      </td>
    ),
  };
  return (
    <tr
      ref={rowRef}
      data-testid={`processing-library-${library.id}`}
      data-grabbed={grabbed || undefined}
    >
      {order.map((id) => (
        <Fragment key={id}>{cells[id]}</Fragment>
      ))}
    </tr>
  );
}
