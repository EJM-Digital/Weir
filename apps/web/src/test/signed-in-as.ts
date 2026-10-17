import { vi } from "vitest";

import * as authQueries from "../lib/auth/queries";

/** Answers `/auth/me` with an account of this role, so a screen that reads the role needs no request. */
export function signedInAs(role: "admin" | "operator" | "viewer"): void {
  vi.spyOn(authQueries, "useMeQuery").mockReturnValue({
    data: { role },
  } as ReturnType<typeof authQueries.useMeQuery>);
}
