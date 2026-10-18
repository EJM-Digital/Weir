import { useState } from "react";

import { Panel } from "../../../../components/panels/panel";
import { ColumnsMenu } from "../../../../components/shared/columns-menu";
import { LoadError } from "../../../../components/shared/load-error";
import {
  useActiveSessionsQuery,
  useRevokeOtherSessionsMutation,
  useRevokeSessionMutation,
} from "../../../../lib/auth/queries";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { useTableColumns } from "../../../../lib/ui/use-table-columns";
import { SESSION_COLUMNS } from "./session-columns";
import { SessionsTable } from "./sessions-table";

const SESSIONS_NOTE =
  "Browsers signed in to Weir. Session tokens are never shown.";
const SESSIONS_DETAIL =
  "Review signed-in browsers and sign out anything you no longer recognize. Session tokens are never shown.";

/** Every browser signed in to Weir, with a way to sign out the ones you do not recognise. */
export function ActiveSessionsSection({ enabled }: { enabled: boolean }) {
  const sessionsQ = useActiveSessionsQuery(enabled);
  const columns = useTableColumns(SESSION_COLUMNS);
  const revokeOthers = useRevokeOtherSessionsMutation();
  const revokeSession = useRevokeSessionMutation();
  const [status, setStatus] = useState<string | null>(null);
  const sessions = sessionsQ.data ?? [];
  const others = sessions.filter((session) => !session.current);

  const signOutOthers = () => {
    setStatus(null);
    revokeOthers.mutate(undefined, {
      onSuccess: (result) => setStatus(result.message),
      onError: () =>
        setStatus(
          "Could not sign out the other sessions. Refresh and try again.",
        ),
    });
  };
  const signOut = (sessionId: string) => {
    setStatus(null);
    revokeSession.mutate(sessionId, {
      onSuccess: (result) => setStatus(result.message),
      onError: () =>
        setStatus(
          "Could not sign out that session. It may already be inactive.",
        ),
    });
  };

  return (
    <Panel
      title="Active sessions"
      headingId="suite-security-sessions-heading"
      padded
      note={<span title={SESSIONS_DETAIL}>{SESSIONS_NOTE}</span>}
      aside={
        <>
          {others.length > 0 ? (
            <button
              type="button"
              className={`${mmActionButtonClass({ variant: "danger-outline" })} mm-sys-btn`}
              disabled={revokeOthers.isPending}
              onClick={signOutOthers}
            >
              {revokeOthers.isPending
                ? "Signing out…"
                : "Sign out other sessions"}
            </button>
          ) : null}
          {sessions.length > 0 ? <ColumnsMenu table={columns} /> : null}
        </>
      }
    >
      {sessionsQ.isError ? (
        <LoadError thing="active sessions" error={sessionsQ.error} />
      ) : sessionsQ.isPending ? (
        <p className="mm-quiet-note">Loading active sessions…</p>
      ) : sessions.length === 0 ? (
        <p className="mm-quiet-note">No active sessions were found.</p>
      ) : (
        <SessionsTable
          sessions={sessions}
          columns={columns}
          revoking={revokeSession.isPending}
          onRevoke={signOut}
        />
      )}
      {status ? (
        <p className="mm-sys-note" role="status">
          {status}
        </p>
      ) : null}
    </Panel>
  );
}
