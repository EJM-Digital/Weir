import { useLayoutEffect, useRef, useState, type RefObject } from "react";
import { Link } from "react-router-dom";

import { Panel } from "../../../components/panels/panel";
import { loadErrorMessage } from "../../../lib/api/error-message";
import type { ProcessingFile } from "../../../lib/processing/files-api";
import { classNames } from "../../../lib/ui/class-names";
import { useProcessingLibrariesQuery } from "../../../lib/processing/libraries-queries";
import type { Filter } from "../processing-filter";
import { activityGroupPath } from "../../activity/activity-links";
import { ProcessRejectedAgain } from "../../activity/activity-rejected-again";
import { NeedFileActions, type NeedNotice } from "./needs-file-actions";
import { useFittingRows } from "./fit-rows";
import {
  NEEDS_A_LOOK_PATH,
  needUnits,
  needsLeftOut,
  type NeedGroup,
  type NeedRow,
} from "./needs-model";
import { NEEDS_PANEL_ID } from "./show-needs-panel";
import { useNeedsYou } from "./use-needs-you";

type RowHandlers = {
  /** Which kind of media each workflow holds, by id. */
  mediaScopes: ReadonlyMap<number, "movie" | "tv">;
  onNotice: (notice: NeedNotice) => void;
  onOpen: ((file: ProcessingFile) => void) | undefined;
};

/**
 * A row leaves when what it asked of the person is done. If their focus was in it, it would fall to the top of the page
 * and a keyboard user would start again from there, so it goes to the panel, where the rows that remain are next.
 */
function useHandFocusToPanelWhenGone(row: RefObject<HTMLElement | null>) {
  useLayoutEffect(() => {
    const element = row.current;
    return () => {
      if (element?.contains(document.activeElement)) {
        document.getElementById(NEEDS_PANEL_ID)?.focus();
      }
    };
  }, [row]);
}

function NeedItem({
  row,
  mediaScopes,
  onNotice,
  onOpen,
}: { row: NeedRow } & RowHandlers) {
  const item = useRef<HTMLLIElement>(null);
  useHandFocusToPanelWhenGone(item);
  return (
    <li
      ref={item}
      className="mm-need"
      title={row.detail}
      data-status={row.meaning}
      data-fit=""
    >
      <b className="mm-need__title">{row.title}</b>
      {row.file ? (
        <small className="mm-need__workflow">{row.file.library_name}</small>
      ) : null}
      <span className="mm-need__reason">{row.reason}</span>
      <span className="mm-need__actions">
        {row.file ? (
          <NeedFileActions
            file={row.file}
            mediaScope={mediaScopes.get(row.file.library_id) ?? "movie"}
            onNotice={onNotice}
            onOpen={onOpen}
          />
        ) : null}
        {row.link ? (
          <Link className="mm-need__link" to={row.link.to}>
            {row.link.label} →
          </Link>
        ) : null}
      </span>
    </li>
  );
}

function Group({
  group,
  workflowId,
  workflowName,
  ...handlers
}: {
  group: NeedGroup;
  workflowId: number | null | undefined;
  workflowName: string | undefined;
} & RowHandlers) {
  return (
    <section
      aria-label={group.title}
      className="mm-needs__group"
      data-status={group.meaning}
    >
      <header className="mm-needs__group-head" data-fit="with-next">
        <h3 className="mm-needs__group-title">{group.title}</h3>
        {group.rejected ? (
          <ProcessRejectedAgain
            libraryId={workflowId ?? undefined}
            libraryName={workflowName}
            onNotice={(text, failed) => handlers.onNotice({ text, failed })}
          />
        ) : null}
      </header>
      <ul className="mm-needs__rows">
        {group.rows.map((row) => (
          <NeedItem key={row.key} row={row} {...handlers} />
        ))}
      </ul>
      {group.more > 0 && group.activity ? (
        <Link
          className="mm-need__link mm-needs__more"
          to={activityGroupPath(group.activity, workflowId)}
          data-fit=""
        >
          and {group.more.toLocaleString()} more in Activity →
        </Link>
      ) : null}
    </section>
  );
}

/** What an empty panel says it is clear of: everything, or just the kind of work the page is narrowed to. */
const CLEAR_WORDS: Record<Filter, string> = {
  all: "Nothing needs you.",
  download: "Nothing from new downloads.",
  library: "Nothing from library cleaning.",
};

function AllClear({ filter }: { filter: Filter }) {
  return (
    <p className="mm-needs__clear" data-status="done">
      <svg
        viewBox="0 0 24 24"
        width="16"
        height="16"
        fill="none"
        stroke="currentColor"
        strokeWidth="2.4"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
      >
        <path d="m5 12 5 5 9-10" />
      </svg>
      <b>All clear</b>
      <span>{CLEAR_WORDS[filter]}</span>
    </p>
  );
}

/** Said when the files could not be read: nothing listed is then proof that nothing waits on a person. */
function ReadFailed({ error }: { error: unknown }) {
  return (
    <p
      className="mm-needs__notice mm-status-text"
      data-status="broken"
      role="alert"
    >
      {loadErrorMessage(error, "the files that need you")}
    </p>
  );
}

type NeedsPanelProps = {
  /** Narrows the panel to one workflow's files. */
  workflowId?: number | null;
  /** Narrows the panel to one kind of work; both when left out. */
  filter?: Filter;
  /** Opens a file's story. Without it, Open goes to the file in Activity. */
  onOpen?: (file: ProcessingFile) => void;
};

/**
 * The files and conditions that wait on a person, grouped by what went wrong, each file with its own actions.
 * Says "All clear" when there are none.
 */
export function NeedsPanel({
  workflowId,
  filter = "all",
  onOpen,
}: NeedsPanelProps) {
  const workflows = useProcessingLibrariesQuery().data;
  const { groups, files, filesError } = useNeedsYou(workflowId, filter);
  const count = files.length;
  const [notice, setNotice] = useState<NeedNotice | null>(null);
  const [listRef, fits] = useFittingRows();
  const left = needsLeftOut(needUnits(groups), fits);
  const mediaScopes = new Map(
    (workflows ?? []).map((workflow) => [workflow.id, workflow.media_type]),
  );
  const workflowName = workflows?.find(
    (workflow) => workflow.id === workflowId,
  )?.name;
  return (
    <Panel
      id={NEEDS_PANEL_ID}
      tabIndex={-1}
      title="Needs you"
      count={
        count === 0 ? undefined : (
          <span
            className="mm-needs__count mm-status-text"
            data-status="attention"
          >
            {count.toLocaleString()} to look at
          </span>
        )
      }
      to={NEEDS_A_LOOK_PATH}
      toLabel="Activity"
      toText={left > 0 ? `${left.toLocaleString()} more` : undefined}
    >
      {notice ? (
        <p
          className={classNames(
            "mm-needs__notice",
            notice.failed && "mm-status-text",
          )}
          data-status={notice.failed ? "broken" : undefined}
          role={notice.failed ? "alert" : "status"}
        >
          {notice.text}
        </p>
      ) : null}
      {/* The host is always there, so its height is watched from the first paint, before there is anything to list. */}
      <div className="mm-needs">
        <div ref={listRef} className="mm-needs__fit">
          {filesError ? <ReadFailed error={filesError} /> : null}
          {groups.length === 0 ? (
            filesError ? null : (
              <AllClear filter={filter} />
            )
          ) : (
            <div className="mm-needs__list" data-testid="live-needs">
              {groups.map((group) => (
                <Group
                  key={group.key}
                  group={group}
                  workflowId={workflowId}
                  workflowName={workflowName}
                  mediaScopes={mediaScopes}
                  onNotice={setNotice}
                  onOpen={onOpen}
                />
              ))}
            </div>
          )}
        </div>
      </div>
    </Panel>
  );
}
