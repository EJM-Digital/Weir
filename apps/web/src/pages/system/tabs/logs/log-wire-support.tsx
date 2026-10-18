import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

import { systemReplies } from "../../../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedRoutes,
} from "../../../../test/recorded-weir";
import { SAVING } from "../../system-test-support";
import { me } from "../security/security-replies";
import { UnifiedLog } from "./unified-log";

/** The log as the server answers it: who is signed in, the time zone the times are shown in, and a confirmation token for a change. */
export function serveLog(routes: RecordedRoutes) {
  return serveRecordedReplies({
    ...SAVING,
    "GET /api/v1/auth/me": me,
    "GET /api/v1/suite/settings": systemReplies.settings,
    ...routes,
  });
}

/** The real Logs list, asking the server for everything it shows; nothing is cached before it. */
export function renderLogList() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/system?tab=logs"]}>
        <UnifiedLog onOpenSettings={() => undefined} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}
