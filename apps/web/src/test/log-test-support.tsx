import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
import { vi } from "vitest";

import type { SystemLogPage, SystemLogRow } from "../lib/system/system-log-api";
import { UnifiedLog } from "../pages/system/tabs/logs/unified-log";

/** A moment shortly after the fixture rows, so "Today" and the clock times in the tests are fixed. */
export const NOW = new Date("2026-10-02T12:00:00Z");

export const EVENT_ROW: SystemLogRow = {
  id: "event:41",
  source: "event",
  at: "2026-10-02T11:50:00Z",
  level: "success",
  category: "sign_in",
  workflow: null,
  title: "Sign-in finished",
  detail: null,
  event: {
    id: 41,
    created_at: "2026-10-02T11:50:00Z",
    event_type: "auth.login_succeeded",
    module: "auth",
    title: "Sign-in finished",
    detail: "alice",
    trigger: "manual",
    result: "success",
    library_id: null,
    relative_path: null,
    poster_url: null,
    run_key: null,
  },
  job: null,
  server: null,
};

export const JOB_ROW: SystemLogRow = {
  id: "job:7",
  source: "job",
  at: "2026-10-02T11:30:00Z",
  level: "error",
  category: "processing",
  workflow: { id: 1, name: "Movies" },
  title: "Couldn't finish this job for heat.mkv",
  detail: "Process a media file · attempt 3 of 3",
  event: null,
  job: {
    id: 7,
    dedupe_key: "remux:1:heat",
    job_kind: "processing.file.remux_pass.v1",
    status: "failed",
    attempt_count: 3,
    max_attempts: 3,
    lease_owner: null,
    lease_expires_at: null,
    last_error: "ffmpeg stopped unexpectedly",
    operator_message: "Couldn't finish this job for heat.mkv.",
    next_action:
      "Read the error below, fix the cause, then use Try again in Activity.",
    technical_detail: "ffmpeg stopped unexpectedly",
    payload_json:
      '{"library_id": 1, "relative_media_path": "Heat (1995)/heat.mkv"}',
    created_at: "2026-10-02T11:00:00Z",
    updated_at: "2026-10-02T11:30:00Z",
  },
  server: null,
};

export const PENDING_JOB_ROW: SystemLogRow = {
  ...JOB_ROW,
  id: "job:8",
  at: "2026-10-02T11:20:00Z",
  level: "info",
  title: "Queued for alien.mkv",
  job: {
    ...JOB_ROW.job!,
    id: 8,
    status: "pending",
    attempt_count: 0,
    last_error: null,
    operator_message: "Queued for alien.mkv.",
    next_action: "Nothing to do. It starts when a worker is free.",
  },
};

export const SERVER_ROW: SystemLogRow = {
  id: "server:310",
  source: "server",
  at: "2026-10-02T11:45:00Z",
  level: "warning",
  category: "backups",
  workflow: null,
  title: "The backup folder is nearly full",
  detail: null,
  event: null,
  job: null,
  server: {
    timestamp: "2026-10-02T11:45:00Z",
    level: "WARNING",
    component: "System",
    message: "The backup folder is nearly full",
    detail: null,
    traceback: "System.IO.IOException: Not enough space\n   at Backup.Write()",
    source: null,
    logger: "weir.platform.suite_settings.backups",
    correlation_id: "req-1",
    job_id: "7",
  },
};

export const ALL_ROWS = [EVENT_ROW, SERVER_ROW, JOB_ROW];

/** A page of the log with the counts its rows add up to, unless a test says otherwise. */
export function logPage(
  rows: readonly SystemLogRow[] = ALL_ROWS,
  overrides: Partial<SystemLogPage> = {},
): SystemLogPage {
  const count = (pick: (row: SystemLogRow) => string, key: string) =>
    rows.filter((row) => pick(row) === key).length;
  return {
    items: [...rows],
    next_cursor: null,
    total: rows.length,
    counts: {
      source: {
        event: count((row) => row.source, "event"),
        job: count((row) => row.source, "job"),
        server: count((row) => row.source, "server"),
      },
      level: {
        error: count((row) => row.level, "error"),
        warning: count((row) => row.level, "warning"),
        info: count((row) => row.level, "info"),
        success: count((row) => row.level, "success"),
      },
      category: {
        processing: count((row) => row.category, "processing"),
        scans: 0,
        cleanup: 0,
        library: 0,
        connections: 0,
        backups: count((row) => row.category, "backups"),
        sign_in: count((row) => row.category, "sign_in"),
        updates: 0,
        weir: 0,
      },
      workflow: rows.reduce<Record<string, number>>((counts, row) => {
        if (row.workflow) {
          const id = String(row.workflow.id);
          counts[id] = (counts[id] ?? 0) + 1;
        }
        return counts;
      }, {}),
    },
    ...overrides,
  };
}

/** Opens the level picker, toggles the options named, and closes it again. */
export function chooseLevels(...names: (RegExp | string)[]): void {
  const picker = screen.getByTestId("logs-level-picker");
  fireEvent.click(picker);
  for (const name of names) {
    fireEvent.click(screen.getByRole("option", { name }));
  }
  fireEvent.click(picker);
}

/** Shows the address, so a test can say what a choice did to it. */
function LocationProbe() {
  const location = useLocation();
  return <output data-testid="location">{location.search}</output>;
}

/** The log in a router at `address`, with nothing cached, and the address shown so a test can read it. */
export function renderLog(
  address = "/system?tab=logs",
  onOpenSettings: () => void = () => undefined,
) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  const tree = () => (
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[address]}>
        <UnifiedLog onOpenSettings={onOpenSettings} />
        <LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>
  );
  const view = render(tree());
  return { ...view, client };
}

/** Stands in for the browser's event stream, which the log listens to for new rows. */
export function stubEventSource(): void {
  class EventSourceStub {
    addEventListener = vi.fn();
    removeEventListener = vi.fn();
    close = vi.fn();
  }
  vi.stubGlobal("EventSource", EventSourceStub);
}
