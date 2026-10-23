import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

import type { ProcessingFile } from "../lib/processing/files-api";
import type { LibraryClean } from "../lib/processing/library-cleans-api";
import { filesPage, readyServer, workflowRow } from "./dashboard-fixtures";
import {
  stubWeirApi,
  type StubbedApi,
  type StubbedRequest,
} from "./stub-weir-api";
import { ActivityPage } from "../pages/activity/activity-page";

export const FILES = "GET /api/v1/processing/files";
export const CLEANS = "GET /api/v1/processing/library-cleans";
export const WORKFLOWS = "GET /api/v1/processing/libraries";
export const KEPT = "GET /api/v1/processing/kept-files";

/** The log route of one file: what Weir recorded each time it worked on it. */
export const logRoute = (id: number) =>
  `GET /api/v1/processing/files/${id}/log`;

/** The empty record of a file: no pass has been written for it. */
export const emptyLog = (
  file: Pick<ProcessingFile, "id" | "relative_path">,
) => ({
  file_id: file.id,
  relative_path: file.relative_path,
  retention_days: 90,
  entries: [],
});

/**
 * The server behind the Activity page: two workflows, the person signed in as an admin, nothing paused, and the
 * given downloads, library cleans and (as the Needs you read) attention files. `overrides` answers over any route.
 */
export function stubActivityServer({
  files,
  cleans = [],
  attention = [],
  overrides = {},
}: {
  files: ProcessingFile[];
  cleans?: LibraryClean[];
  attention?: ProcessingFile[];
  overrides?: Record<string, unknown>;
}): StubbedApi {
  return stubWeirApi({
    [FILES]: (request: StubbedRequest) =>
      filesPage(request.query.has("file_status") ? attention : files),
    [CLEANS]: { cleans, returned: cleans.length, limit: 200 },
    [WORKFLOWS]: [
      workflowRow({ id: 1, name: "Movies" }),
      workflowRow({ id: 2, name: "TV", media_type: "tv" }),
    ],
    [KEPT]: { files: [] },
    "GET /api/v1/auth/me": {
      user: { id: 1, username: "admin", role: "admin" },
    },
    "GET /api/v1/media-managers/connections": [],
    "GET /api/v1/system/readiness": readyServer(),
    "GET /api/v1/processing/jobs/inspection": { jobs: [] },
    "GET /api/v1/pause": { paused: false },
    "GET /api/v1/suite/settings": { app_timezone: "UTC" },
    "GET /api/v1/processing/operator-settings": {
      file_log_retention_days: 90,
    },
    "GET /api/v1/auth/csrf": { csrf_token: "csrf-for-this-test" },
    ...Object.fromEntries(
      [...files, ...attention].map((file) => [
        logRoute(file.id),
        emptyLog(file),
      ]),
    ),
    ...overrides,
  });
}

/** The Activity page as it is on screen, outside the shell, at `address`. */
export function renderActivityPage(address = "/activity") {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter initialEntries={[address]}>
        <ActivityPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

/** The answer of a route the page asks and never gets, so a test sees what it shows meanwhile. */
export const never = () => new Promise<never>(() => undefined);
