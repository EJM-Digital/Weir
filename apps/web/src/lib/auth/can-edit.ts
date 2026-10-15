import { useMeQuery } from "./queries";

/** Operators and admins change settings and files; viewers only read. */
export function canEdit(role: string | undefined): boolean {
  return role === "operator" || role === "admin";
}

/** Connections and alerts are the install's own: only an admin removes one, because removing it can leave workflows without a feed or silence alerts. */
export function isAdmin(role: string | undefined): boolean {
  return role === "admin";
}

/** Whether the signed-in user is an admin. False until the session has loaded. */
export function useIsAdmin(): boolean {
  return isAdmin(useMeQuery().data?.role);
}

/** Whether the signed-in user may change things. False until the session has loaded. */
export function useCanEdit(): boolean {
  return canEdit(useMeQuery().data?.role);
}
