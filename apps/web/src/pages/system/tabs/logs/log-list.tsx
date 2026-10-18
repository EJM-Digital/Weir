import { Fragment, useMemo, type CSSProperties } from "react";

import { SortableColumnHeader } from "../../../../components/shared/sortable-column-header";
import type { SystemLogRow } from "../../../../lib/system/system-log-api";
import {
  useAppClockSecondsFormatter,
  useAppDateFormatter,
  useAppTimeZone,
} from "../../../../lib/ui/mm-format-date";
import type { TableColumns } from "../../../../lib/ui/use-table-columns";
import { useNow } from "../../../../lib/ui/use-now";
import { datedClock, dayHeading, groupByDay, type LogDay } from "./log-days";
import { logGrid, type LogColumnId } from "./log-columns";
import { LogRow, type LogRowActions } from "./log-row";

/** Day headings are worked out again this often, so "Today" becomes "Yesterday" after midnight. */
const HEADING_TICK_MS = 60_000;

/** The headings over the rows. The arrow at a row's end has none. */
function LogHeadings({ columns }: { columns: TableColumns<LogColumnId> }) {
  return (
    <div role="row" className="mm-log-head">
      {columns.order.map((id) => (
        <SortableColumnHeader key={id} as="div" heading={columns.heading(id)} />
      ))}
    </div>
  );
}

/**
 * The rows under the day each fell on in Weir's time zone when they are in the order of time, and in one run when a
 * heading has put them in another, since a day's name over rows from many days says nothing: there each row's time
 * carries its own day. Each opens to what its source recorded, across the full width of the row whatever order the
 * columns are in.
 */
export function LogList({
  rows,
  columns,
  expandedId,
  onToggle,
  actions,
}: {
  rows: readonly SystemLogRow[];
  columns: TableColumns<LogColumnId>;
  expandedId: string | null;
  onToggle: (id: string) => void;
  actions: LogRowActions;
}) {
  const timeZone = useAppTimeZone();
  const timeOfDay = useAppClockSecondsFormatter();
  const fullTime = useAppDateFormatter();
  const now = useNow(HEADING_TICK_MS);
  const byTime = columns.sort?.id === "time";
  // With no day headings over the rows each time says its own day.
  const clock = useMemo(
    () =>
      byTime
        ? timeOfDay
        : (ms: number) => datedClock(ms, now, timeZone, timeOfDay),
    [byTime, now, timeZone, timeOfDay],
  );
  const days: LogDay[] = useMemo(
    () =>
      byTime ? groupByDay(rows, timeZone) : [{ key: "", rows: [...rows] }],
    [byTime, rows, timeZone],
  );
  return (
    <div
      className="mm-log-list"
      role="table"
      aria-label="Log"
      style={logGrid(columns.order, !byTime) as CSSProperties}
      {...columns.tableProps}
    >
      <LogHeadings columns={columns} />
      <div role="rowgroup" data-testid="log-feed">
        {days.map((day) => (
          <Fragment key={day.key}>
            {byTime ? (
              <div className="mm-log-day" aria-hidden="true">
                {dayHeading(day.key, now, timeZone)}
              </div>
            ) : null}
            {day.rows.map((row) => (
              <LogRow
                key={row.id}
                row={row}
                expanded={row.id === expandedId}
                onToggle={() => onToggle(row.id)}
                clock={clock}
                fullTime={(iso) => fullTime(iso)}
                order={columns.order}
                actions={actions}
              />
            ))}
          </Fragment>
        ))}
      </div>
    </div>
  );
}
