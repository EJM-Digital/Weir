import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";

import { Panel } from "../../../components/panels/panel";
import { loadErrorMessage } from "../../../lib/api/error-message";
import { useActivityRecentQuery } from "../../../lib/activity/queries";
import { classNames } from "../../../lib/ui/class-names";
import { FitText } from "../../../lib/ui/fit-text";
import { motionAllowed } from "../../../lib/ui/motion-allowed";
import { ACTIVITY_PATH } from "../../activity/activity-links";
import { ACTIVITY_MODULE_OF_WORK, type Filter } from "../processing-filter";
import { useFittingRows } from "./fit-rows";
import { StreamIcon } from "./stream-icons";
import {
  buildStream,
  noteWords,
  type StreamPart,
  type StreamRow,
} from "./stream-model";

/** Enough events to fill the list after the progress frames and routine entries are set aside. */
const RECENT_EVENTS = 80;
/** The most lines drawn: the panel's height decides how many whole ones show, and this bounds what is measured. */
const MOST_LINES = 40;

/** What an empty list says, for everything and for each kind of work it can be narrowed to. */
const NOTHING_YET_WORDS: Record<Filter, string> = {
  all: "Nothing yet. What Weir does appears here.",
  download: "Nothing yet from new downloads.",
  library: "Nothing yet from library cleaning.",
};

function Sentence({ parts }: { parts: readonly StreamPart[] }) {
  return parts.map((part, index) =>
    typeof part === "string" ? (
      <span key={index}>{part}</span>
    ) : (
      <b key={index}>{part.bold}</b>
    ),
  );
}

function Line({
  row,
  now,
  fresh,
}: {
  row: StreamRow;
  now: number;
  fresh: boolean;
}) {
  return (
    <li
      className={classNames("mm-stream__item", fresh && "mm-stream__item--new")}
      data-fit=""
    >
      <Link to={row.to} className="mm-stream__link">
        <span
          aria-hidden="true"
          className="mm-stream__icon mm-status-pill"
          data-status={row.meaning}
        >
          <StreamIcon meaning={row.meaning} />
        </span>
        <span className="mm-stream__text">
          <span className="mm-stream__what">
            <Sentence parts={row.parts} />
          </span>
          <FitText className="mm-stream__note" words={noteWords(row, now)} />
        </span>
      </Link>
    </li>
  );
}

type ActivityStreamProps = {
  now: number;
  /** Only what happened to this workflow's files; everything when null or left out. */
  workflowId?: number | null;
  /** Only what happened in this kind of work; both when left out. */
  filter?: Filter;
};

/**
 * What Weir just did, newest first, from the same feed the Activity log reads. The panel's height decides how
 * many whole lines it shows, never a line cut through, and the header says how many more there are. A line that
 * arrives while the page is open slides in and glows once; lines already there when the page opened do not.
 */
export function ActivityStream({
  now,
  workflowId = null,
  filter = "all",
}: ActivityStreamProps) {
  const recent = useActivityRecentQuery({
    limit: RECENT_EVENTS,
    library_id: workflowId ?? undefined,
    module: filter === "all" ? undefined : ACTIVITY_MODULE_OF_WORK[filter],
  });
  const items = recent.data?.items;
  const stream = useMemo(() => buildStream(items ?? []), [items]);
  const newestId = items?.[0]?.id ?? null;
  const [openedAt, setOpenedAt] = useState<number | null>(null);
  useEffect(() => {
    if (openedAt === null && newestId !== null) setOpenedAt(newestId);
  }, [openedAt, newestId]);
  const animate = motionAllowed();
  const [listRef, fits] = useFittingRows();
  const shown = stream.rows.slice(0, MOST_LINES);
  const more = stream.rows.length - Math.min(fits, shown.length);
  return (
    <Panel
      title="Activity"
      count={
        stream.routine > 0
          ? `${stream.routine.toLocaleString()} routine left out`
          : "as it happens"
      }
      to={ACTIVITY_PATH}
      toLabel="All activity"
      toText={more > 0 ? `${more.toLocaleString()} more` : undefined}
    >
      {/* The host is always there, so its height is watched from the first paint, before there is anything to list. */}
      <div ref={listRef} className="mm-stream__fit">
        {stream.rows.length === 0 ? (
          recent.isError ? (
            <p
              className="mm-stream__empty mm-status-text"
              data-status="broken"
              role="alert"
            >
              {loadErrorMessage(recent.error, "what Weir has done")}
            </p>
          ) : (
            <p className="mm-stream__empty">{NOTHING_YET_WORDS[filter]}</p>
          )
        ) : (
          <ul className="mm-stream__list" data-testid="live-stream">
            {shown.map((row) => (
              <Line
                key={row.key}
                row={row}
                now={now}
                fresh={animate && openedAt !== null && row.id > openedAt}
              />
            ))}
          </ul>
        )}
      </div>
    </Panel>
  );
}
