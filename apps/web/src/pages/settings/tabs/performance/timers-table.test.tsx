import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { heldReply } from "../../../../test/held-reply";
import { adminMe, csrfToken } from "../../../../test/recorded-admin-session";
import { recordedBody } from "../../../../test/recorded";
import { systemReplies } from "../../../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import { TimersTab } from "./timers-tab";

const MAINTENANCE = "/api/v1/processing/maintenance";
const SETTINGS = "/api/v1/suite/settings";

type Family = Record<string, unknown>;

/**
 * The recorded maintenance reply, with the second job switched on and given a next run, so the two jobs have different
 * times to sort by. Every other field is as the server sent it.
 */
function maintenanceWithBothJobsOn(): RecordedReply {
  const { families } = recordedBody<{ families: Family[] }>(
    `GET ${MAINTENANCE}`,
  );
  const [workFiles, copies] = families;
  return {
    status: 200,
    body: JSON.stringify({
      families: [
        workFiles,
        { ...copies, enabled: true, next_run_at: "2026-10-15T18:00:00Z" },
      ],
    }),
  };
}

/** The recorded settings with the backup switched on, every two hours, and run once already. */
function settingsWithBackupOn(): RecordedReply {
  return {
    status: 200,
    body: JSON.stringify({
      ...JSON.parse(systemReplies.settings.body),
      configuration_backup_enabled: true,
      configuration_backup_interval_hours: 2,
      configuration_backup_last_run_at: "2026-10-16T02:00:00",
    }),
  };
}

let weir: RecordedWeir | undefined;

beforeEach(() => localStorage.clear());

afterEach(() => {
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
});

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return (
    <MemoryRouter>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </MemoryRouter>
  );
}

function serve(routes: RecordedRoutes = {}): void {
  weir = serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    "GET /api/v1/auth/me": adminMe,
    [`GET ${SETTINGS}`]: settingsWithBackupOn(),
    [`GET ${MAINTENANCE}`]: maintenanceWithBothJobsOn(),
    ...routes,
  });
}

const LISTED = [
  "Leftover work files",
  "Cleaned copies nobody picked up",
  "Settings backup",
];

async function showTimers(): Promise<HTMLElement> {
  render(<TimersTab />, { wrapper });
  const table = await screen.findByTestId("schedule-timers");
  await within(table).findByText("Every hour");
  return table;
}

function jobNames(table: HTMLElement): (string | null | undefined)[] {
  return within(table)
    .getAllByRole("row")
    .slice(1)
    .map((row) => row.querySelector("th")?.textContent);
}

function headings(table: HTMLElement): (string | null)[] {
  return within(table)
    .getAllByRole("columnheader")
    .map((heading) => heading.textContent);
}

describe("sorting the timers", () => {
  it("lists them as they come until a heading is pressed", async () => {
    serve();

    expect(jobNames(await showTimers())).toEqual(LISTED);
  });

  it("sorts by how often a job runs, shortest first, and reverses on the next press", async () => {
    serve();
    const table = await showTimers();

    fireEvent.click(within(table).getByRole("button", { name: "When" }));
    expect(jobNames(table)).toEqual([
      "Leftover work files",
      "Settings backup",
      "Cleaned copies nobody picked up",
    ]);

    fireEvent.click(within(table).getByRole("button", { name: "When" }));
    expect(jobNames(table)).toEqual([
      "Cleaned copies nobody picked up",
      "Settings backup",
      "Leftover work files",
    ]);
  });

  it("sorts by the last run, the latest first", async () => {
    serve();
    const table = await showTimers();

    fireEvent.click(within(table).getByRole("button", { name: "Last run" }));

    expect(jobNames(table)).toEqual([
      "Settings backup",
      "Leftover work files",
      "Cleaned copies nobody picked up",
    ]);
  });

  it("sorts by the next run, the soonest first, and keeps a job with none last", async () => {
    serve();
    const table = await showTimers();

    fireEvent.click(within(table).getByRole("button", { name: "Next run" }));
    expect(jobNames(table)).toEqual([
      "Cleaned copies nobody picked up",
      "Leftover work files",
      "Settings backup",
    ]);

    fireEvent.click(within(table).getByRole("button", { name: "Next run" }));
    expect(jobNames(table)).toEqual([
      "Leftover work files",
      "Cleaned copies nobody picked up",
      "Settings backup",
    ]);
  });
});

describe("moving the timers' columns", () => {
  it("moves a column with Alt and an arrow, and Reset columns puts the table back", async () => {
    serve();
    const table = await showTimers();
    const lastRun = within(table).getByRole("button", { name: "Last run" });
    expect(headings(table)).toEqual([
      "Job",
      "When",
      "Last run",
      "Next run",
      "Where to change it",
    ]);

    fireEvent.keyDown(lastRun, { key: "ArrowLeft", altKey: true });
    expect(headings(table)).toEqual([
      "Job",
      "Last run",
      "When",
      "Next run",
      "Where to change it",
    ]);

    fireEvent.click(screen.getByRole("button", { name: "Columns" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "Reset columns" }));
    expect(headings(table)).toEqual([
      "Job",
      "When",
      "Last run",
      "Next run",
      "Where to change it",
    ]);
  });
});

describe("the cleanup jobs' own times", () => {
  it("say the jobs are loading, not that they have no times, while Weir is still asked", async () => {
    const answer = heldReply({ status: 200, body: '{"families":[]}' });
    serve({ [`GET ${MAINTENANCE}`]: answer.route });
    render(<TimersTab />, { wrapper });
    await screen.findByTestId("schedule-timers");

    expect(
      await screen.findByText("Loading when the cleanup jobs run…"),
    ).toBeInTheDocument();
  });

  it("say Weir could not load them, not that they have none, when the request fails", async () => {
    serve({
      [`GET ${MAINTENANCE}`]: {
        status: 500,
        body: '{"detail":"Internal Server Error"}',
      },
    });
    render(<TimersTab />, { wrapper });
    await screen.findByTestId("schedule-timers");

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Weir couldn't load when the cleanup jobs run. Reload the page to try again.",
    );
  });

  it("show no note once they have loaded", async () => {
    serve();
    await showTimers();

    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(
      screen.queryByText("Loading when the cleanup jobs run…"),
    ).not.toBeInTheDocument();
  });
});
