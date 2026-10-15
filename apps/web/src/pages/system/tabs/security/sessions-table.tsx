import { Fragment, type ReactNode } from "react";

import { Chip } from "../../../../components/panels/chip";
import { SortableColumnHeader } from "../../../../components/shared/sortable-column-header";
import type { ActiveSession } from "../../../../lib/api/types";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import {
  parseAppTime,
  useAppDateFormatter,
} from "../../../../lib/ui/mm-format-date";
import { sortRows } from "../../../../lib/ui/table-columns";
import type { TableColumns } from "../../../../lib/ui/use-table-columns";
import type { SessionColumnId } from "./session-columns";

const UNNAMED_BROWSER = "Browser session";

function browserName(session: ActiveSession): string {
  return session.client_label || UNNAMED_BROWSER;
}

const SESSION_SORT_VALUES = {
  browser: browserName,
  lastSeen: (session: ActiveSession) => parseAppTime(session.last_seen_at),
  expires: (session: ActiveSession) =>
    parseAppTime(session.absolute_expires_at),
};

function SessionRow({
  session,
  order,
  revoking,
  onRevoke,
}: {
  session: ActiveSession;
  order: readonly SessionColumnId[];
  revoking: boolean;
  onRevoke: (sessionId: string) => void;
}) {
  const formatDate = useAppDateFormatter();
  const cells: Record<SessionColumnId, ReactNode> = {
    browser: (
      <th scope="row" data-col="browser">
        <span className="mm-sys-table__who">
          {browserName(session)}
          {session.current ? <Chip dot={false}>This browser</Chip> : null}
          {session.trusted_device ? <Chip meaning="done">Trusted</Chip> : null}
        </span>
      </th>
    ),
    lastSeen: <td data-col="lastSeen">{formatDate(session.last_seen_at)}</td>,
    expires: (
      <td data-col="expires">{formatDate(session.absolute_expires_at)}</td>
    ),
    signOut: (
      <td data-col="signOut">
        {/* The browser in use is signed out from the user menu, so it has no button here. */}
        {session.current ? null : (
          <button
            type="button"
            className={`${mmActionButtonClass({ variant: "secondary" })} mm-sys-btn`}
            disabled={revoking}
            onClick={() => onRevoke(session.session_id)}
          >
            {revoking ? "Signing out…" : "Sign out"}
          </button>
        )}
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

/**
 * The signed-in browsers as a table whose headings sort it and whose columns can be moved. It sits in the scrolling
 * wrapper every quiet table has, so a row wider than its panel scrolls inside it instead of running under the panel
 * beside it.
 */
export function SessionsTable({
  sessions,
  columns,
  revoking,
  onRevoke,
}: {
  sessions: readonly ActiveSession[];
  columns: TableColumns<SessionColumnId>;
  revoking: boolean;
  onRevoke: (sessionId: string) => void;
}) {
  return (
    <div className="mm-quiet-table-wrap">
      <table
        className="mm-quiet-table mm-sys-table"
        data-testid="active-sessions"
        {...columns.tableProps}
      >
        <thead>
          <tr>
            {columns.order.map((id) => (
              <SortableColumnHeader
                key={id}
                heading={columns.heading(id)}
                hideLabel={id === "signOut"}
              />
            ))}
          </tr>
        </thead>
        <tbody>
          {sortRows(sessions, columns.sort, SESSION_SORT_VALUES).map(
            (session) => (
              <SessionRow
                key={session.session_id}
                session={session}
                order={columns.order}
                revoking={revoking}
                onRevoke={onRevoke}
              />
            ),
          )}
        </tbody>
      </table>
    </div>
  );
}
