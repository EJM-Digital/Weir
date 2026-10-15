import { useIsAdmin } from "../../lib/auth/can-edit";

/**
 * Says why Add, Edit and Remove are switched off for anyone who is not an admin: connections and alerts are the
 * install's own, so changing them is for the admin. Shows nothing to an admin.
 */
export function AdminOnlyNote({
  what,
  testId,
}: {
  what: string;
  testId: string;
}) {
  if (useIsAdmin()) return null;
  return (
    <p className="mm-quiet-note" data-testid={testId}>
      Only an admin can add, change or remove {what}.
    </p>
  );
}
