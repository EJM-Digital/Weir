import { Fragment, type ReactNode } from "react";
import { Link } from "react-router-dom";

import { Panel } from "../../../../components/panels/panel";
import { ColumnsMenu } from "../../../../components/shared/columns-menu";
import { LoadError } from "../../../../components/shared/load-error";
import { SortableColumnHeader } from "../../../../components/shared/sortable-column-header";
import { useProcessingMaintenanceQuery } from "../../../../lib/processing/maintenance-queries";
import type { AppSettings } from "../../../../lib/settings/types";
import {
  parseAppTime,
  useAppDateFormatter,
} from "../../../../lib/ui/mm-format-date";
import { sortRows } from "../../../../lib/ui/table-columns";
import {
  useTableColumns,
  type TableColumns,
} from "../../../../lib/ui/use-table-columns";
import { TIMER_COLUMNS, type TimerColumnId } from "./timer-columns";
import { timerRows, type TimerRow } from "./timer-rows";

const UNKNOWN = "—";
const NOT_YET = "Not yet";

const TIMER_SORT_VALUES = {
  job: (row: TimerRow) => row.name,
  when: (row: TimerRow) => row.everySeconds,
  lastRun: (row: TimerRow) => parseAppTime(row.lastRun),
  nextRun: (row: TimerRow) => parseAppTime(row.nextRun),
};

function TimerLine({
  row,
  order,
}: {
  row: TimerRow;
  order: readonly TimerColumnId[];
}) {
  const formatDate = useAppDateFormatter();
  const cells: Record<TimerColumnId, ReactNode> = {
    job: (
      <th scope="row" data-col="job" className="mm-quiet-table__name">
        {row.name}
      </th>
    ),
    when: (
      <td data-col="when" data-label="When">
        {row.when}
      </td>
    ),
    lastRun: (
      <td data-col="lastRun" data-label="Last run">
        {row.lastRun ? formatDate(row.lastRun) : NOT_YET}
      </td>
    ),
    nextRun: (
      <td data-col="nextRun" data-label="Next run">
        {row.nextRun ? formatDate(row.nextRun) : UNKNOWN}
      </td>
    ),
    change: (
      <td data-col="change" data-label="">
        <Link
          className="mm-quiet-link"
          to={row.change.to}
          aria-label={row.change.ariaLabel}
        >
          {row.change.label}
        </Link>
      </td>
    ),
  };
  return (
    <tr>
      {order.map((id) => (
        <Fragment key={id}>{cells[id]}</Fragment>
      ))}
    </tr>
  );
}

function TimersTable({
  rows,
  columns,
}: {
  rows: readonly TimerRow[];
  columns: TableColumns<TimerColumnId>;
}) {
  return (
    <div className="mm-quiet-table-wrap">
      <table
        className="mm-quiet-table"
        data-testid="schedule-timers"
        {...columns.tableProps}
      >
        <thead>
          <tr>
            {columns.order.map((id) => (
              <SortableColumnHeader
                key={id}
                heading={columns.heading(id)}
                hideLabel={id === "change"}
              />
            ))}
          </tr>
        </thead>
        <tbody>
          {sortRows(rows, columns.sort, TIMER_SORT_VALUES).map((row) => (
            <TimerLine key={row.key} row={row} order={columns.order} />
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** When Weir's own jobs run: the cleanup jobs and the settings backup, each on its own clock. */
export function TimersSection({
  headingId,
  settings,
}: {
  headingId: string;
  settings: AppSettings;
}) {
  const maintenance = useProcessingMaintenanceQuery();
  const columns = useTableColumns(TIMER_COLUMNS);
  const rows = timerRows(maintenance.data?.families ?? [], settings);
  return (
    <Panel
      title="Next runs"
      headingId={headingId}
      count="Each runs on its own clock, whatever the workflow hours say."
      aside={<ColumnsMenu table={columns} />}
      padded
    >
      {maintenance.isError ? (
        <LoadError
          thing="when the cleanup jobs run"
          error={maintenance.error}
        />
      ) : null}
      {maintenance.isPending ? (
        <p className="mm-quiet-note" role="status">
          Loading when the cleanup jobs run…
        </p>
      ) : null}
      <TimersTable rows={rows} columns={columns} />
    </Panel>
  );
}
