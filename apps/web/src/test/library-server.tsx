import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";

import type { LibraryFile } from "../lib/processing/library-mode-api";
import { LibraryPage } from "../pages/library/library-page";
import { activityPage, workflowRow } from "./dashboard-fixtures";
import {
  cleanQueued,
  libraryFilesPage,
  libraryOverview,
  librarySettings,
  previewOfTwoAudioTracks,
} from "./library-fixtures";
import { stubWeirApi, type StubbedApi } from "./stub-weir-api";

export const LIBRARY_ID = 1;
const BASE = `/api/v1/processing/libraries/${LIBRARY_ID}`;

/** The routes of the Library page, written as `stubWeirApi` takes them. */
export const LIBRARY_ROUTES = {
  files: `GET ${BASE}/library-files`,
  overview: `GET ${BASE}/library-overview`,
  settings: `GET ${BASE}/library-settings`,
  clean: `POST ${BASE}/library-files/clean`,
  leaveAlone: `POST ${BASE}/library-files/leave-alone`,
  scan: `POST ${BASE}/library-scan`,
  redownloads: `GET ${BASE}/library-redownloads`,
  preview: `POST ${BASE}/preview`,
} as const;

export const CSRF_TOKEN = "csrf-for-this-test";

/**
 * Stands in for the server behind the Library page: one movie workflow holding `files`, scanned, with the rules
 * previewing every file as one English and one French audio track. `routes` answers over any of it.
 */
export function stubLibraryServer(
  files: LibraryFile[],
  routes: Record<string, unknown> = {},
): StubbedApi {
  return stubWeirApi({
    "GET /api/v1/processing/libraries": [
      workflowRow({ id: LIBRARY_ID, name: "Movies" }),
    ],
    [LIBRARY_ROUTES.files]: libraryFilesPage(files),
    [LIBRARY_ROUTES.overview]: libraryOverview(files),
    [LIBRARY_ROUTES.settings]: librarySettings(),
    [LIBRARY_ROUTES.clean]: cleanQueued(1),
    [LIBRARY_ROUTES.redownloads]: {
      library_id: LIBRARY_ID,
      titles: [],
      total: 0,
    },
    [LIBRARY_ROUTES.preview]: previewOfTwoAudioTracks(),
    "GET /api/v1/auth/csrf": { csrf_token: CSRF_TOKEN },
    "GET /api/v1/auth/me": {
      user: { id: 1, username: "admin", role: "admin" },
    },
    "GET /api/v1/pause": { paused: false },
    "GET /api/v1/suite/settings": { app_timezone: "UTC" },
    "GET /api/v1/activity/recent": activityPage([]),
    "GET /api/v1/media-managers/connections": [],
    ...routes,
  });
}

/** What the address says, so a test can read what the page wrote to it. */
function Address() {
  return <output data-testid="address">{useLocation().search}</output>;
}

/** The Library page as it is on screen, outside the shell, at `address`. */
export function renderLibraryPage(address = `/library?library=${LIBRARY_ID}`) {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter initialEntries={[address]}>
        <LibraryPage />
        <Address />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}
