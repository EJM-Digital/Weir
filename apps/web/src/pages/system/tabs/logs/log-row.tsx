import { Fragment, type ReactNode } from "react";

import { Chip } from "../../../../components/panels/chip";
import { eventDisplay } from "../../../../lib/activity/activity-display";
import { classNames } from "../../../../lib/ui/class-names";
import {
  LOG_LEVEL_MEANING,
  type SystemLogRow,
} from "../../../../lib/system/system-log-api";
import { parseAppTime } from "../../../../lib/ui/mm-format-date";
import { needsYou } from "../../../../lib/ui/status-meaning";
import type { LogColumnId } from "./log-columns";
import { LOG_CATEGORY_LABELS, LOG_SOURCES } from "./log-filters";
import { LogEventDetail } from "./log-event-detail";
import { LogJobDetail } from "./log-job-detail";
import { LogServerDetail } from "./log-server-detail";

/** The level as words, for a screen reader and for the dot's tooltip: the dot's colour is never the only signal. */
export const LOG_LEVEL_WORDS: Record<SystemLogRow["level"], string> = {
  error: "Error",
  warning: "Warning",
  info: "Information",
  success: "Success",
};

const SOURCE_WORDS = Object.fromEntries(
  LOG_SOURCES.map((source) => [source.value, source.label.replace(/s$/, "")]),
);

/** The two lines a row shows: its title in plain words and, quieter, what there is to add. */
function linesOf(row: SystemLogRow): { title: string; detail: string | null } {
  if (row.event) {
    const display = eventDisplay(row.event);
    return { title: display.title, detail: display.summary };
  }
  return { title: row.title, detail: row.detail };
}

/** How the page lets a row act on the log: what a row's detail offers beyond reading it. */
export type LogRowActions = {
  canAct: boolean;
  /** Narrows the log to one job and what mentions it. */
  onRelated: (jobId: number) => void;
  /** Called once a job has been cancelled or its result recovered, so the log shows the job as it now is. */
  onJobChanged: () => void;
};

function RowBody({
  row,
  time,
  actions,
}: {
  row: SystemLogRow;
  time: string;
  actions: LogRowActions;
}) {
  if (row.event) {
    return (
      <LogEventDetail
        ev={row.event}
        time={time}
        onRelated={actions.onRelated}
      />
    );
  }
  if (row.job) {
    return (
      <LogJobDetail
        job={row.job}
        canAct={actions.canAct}
        time={time}
        onRelated={actions.onRelated}
        onChanged={actions.onJobChanged}
      />
    );
  }
  return row.server ? (
    <LogServerDetail
      line={row.server}
      time={time}
      onRelated={actions.onRelated}
    />
  ) : null;
}

/**
 * One thing that happened: when, how it went, where it came from, what it is about, the workflow, and in a line or two
 * what it was. It opens to everything the source recorded about it.
 */
export function LogRow({
  row,
  expanded,
  onToggle,
  clock,
  fullTime,
  order,
  actions,
}: {
  row: SystemLogRow;
  expanded: boolean;
  onToggle: () => void;
  /** A time of day, with seconds, in Weir's time zone. */
  clock: (ms: number) => string;
  /** A date and time in full, in Weir's time zone. */
  fullTime: (iso: string) => string;
  /** The columns in the order to show them. */
  order: readonly LogColumnId[];
  actions: LogRowActions;
}) {
  const { title, detail } = linesOf(row);
  const at = parseAppTime(row.at);
  const bodyId = `log-row-${row.id.replace(":", "-")}`;
  const meaning = LOG_LEVEL_MEANING[row.level];
  const cells: Record<LogColumnId, ReactNode> = {
    time: (
      <time
        data-col="time"
        className="mm-log-row__time"
        dateTime={row.at}
        title={fullTime(row.at)}
      >
        {at === null ? "" : clock(at)}
      </time>
    ),
    level: (
      <span
        data-col="level"
        className="mm-status-dot"
        role="img"
        aria-label={LOG_LEVEL_WORDS[row.level]}
        title={LOG_LEVEL_WORDS[row.level]}
      />
    ),
    source: (
      <span data-col="source">
        <Chip dot={false}>{SOURCE_WORDS[row.source]}</Chip>
      </span>
    ),
    category: (
      <span data-col="category" className="mm-log-row__category">
        {LOG_CATEGORY_LABELS[row.category]}
      </span>
    ),
    workflow: (
      <span
        data-col="workflow"
        className="mm-log-row__workflow"
        title={row.workflow?.name ?? undefined}
      >
        {row.workflow?.name ?? ""}
      </span>
    ),
    title: (
      <span data-col="title" className="mm-log-row__text">
        <strong title={title}>{title}</strong>
        {detail ? <small title={detail}>{detail}</small> : null}
      </span>
    ),
  };
  return (
    <div
      role="row"
      className={classNames(
        "mm-log-row",
        needsYou(meaning) && "mm-log-row--problem",
      )}
      data-status={meaning}
      data-testid="log-row"
      data-source={row.source}
      data-level={row.level}
    >
      <div role="cell">
        <button
          type="button"
          className="mm-log-row__head"
          aria-expanded={expanded}
          aria-controls={expanded ? bodyId : undefined}
          onClick={onToggle}
        >
          {order.map((id) => (
            <Fragment key={id}>{cells[id]}</Fragment>
          ))}
          <svg
            className="mm-log-row__chevron"
            viewBox="0 0 24 24"
            width="14"
            height="14"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
          >
            <path d="m6 9 6 6 6-6" />
          </svg>
        </button>
        {expanded ? (
          <div className="flex flex-col gap-2.5 px-3 pb-4" id={bodyId}>
            <RowBody row={row} time={fullTime(row.at)} actions={actions} />
          </div>
        ) : null}
      </div>
    </div>
  );
}
