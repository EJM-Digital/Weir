import { QueryClient } from "@tanstack/react-query";
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { AppSettings } from "../../../../lib/settings/types";
import { jsonAnswer } from "../../../../test/stub-weir-api";
import { library } from "../libraries/library-test-fixtures";
import {
  CSRF_TOKEN,
  newTestClient,
  serveWorkflows,
  signedIn,
  workflowTabWrapper,
  type WorkflowServer,
} from "../libraries/workflow-server";
import { ScheduleTab } from "./schedule-tab";

const ENQUEUE =
  "POST /api/v1/processing/jobs/watched-folder-remux-scan-dispatch/enqueue";

const SETTINGS: AppSettings = {
  setup_wizard_state: "completed",
  app_timezone: "UTC",
  log_retention_days: 30,
  activity_retention_days: 90,
  configuration_backup_enabled: false,
  configuration_backup_interval_hours: 24,
  configuration_backup_preferred_time: "02:00",
  configuration_backup_last_run_at: null,
  updated_at: "2026-04-11T00:00:00Z",
};

const QUEUED = {
  dedupe_key: "watched-folder-remux-scan-dispatch:movie:1",
  job_id: 41,
  job_kind: "watched_folder_remux_scan_dispatch",
  ok: true,
};

let server: WorkflowServer;
let client: QueryClient;

beforeEach(() => {
  client = newTestClient();
});

afterEach(() => {
  expect(server.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

function serve(
  options: Parameters<typeof serveWorkflows>[0],
  enqueue: unknown = QUEUED,
) {
  server = serveWorkflows({
    ...options,
    extra: {
      "GET /api/v1/suite/settings": SETTINGS,
      [ENQUEUE]: enqueue,
      ...options.extra,
    },
  });
}

async function openRow(name: string) {
  render(<ScheduleTab />, { wrapper: workflowTabWrapper(client) });
  await screen.findAllByTestId("schedule-library-row");
  await signedIn(client);
  const row = screen
    .getAllByTestId("schedule-library-row")
    .find((candidate) => within(candidate).queryByText(name));
  return row!;
}

describe("Scan now", () => {
  it("queues a scan of that workflow alone, reads Starting… while it is sent and Scan queued once it is", async () => {
    let answer: (value: unknown) => void = () => undefined;
    serve(
      { workflows: [library({ id: 1, name: "Movies" })] },
      () =>
        new Promise((resolve) => {
          answer = resolve;
        }),
    );
    const row = await openRow("Movies");
    const scan = within(row).getByRole("button", { name: "Scan Movies now" });
    expect(scan).toHaveTextContent("Scan now");

    fireEvent.click(scan);

    await waitFor(() => expect(scan).toHaveTextContent("Starting…"));
    expect(scan).toBeDisabled();
    expect(server.to(ENQUEUE)).toHaveLength(1);
    expect(server.to(ENQUEUE)[0]!.body).toEqual({
      enqueue_remux_jobs: true,
      media_scope: "movie",
      library_id: 1,
      csrf_token: CSRF_TOKEN,
    });
    answer(jsonAnswer(QUEUED));
    await waitFor(() => expect(scan).toHaveTextContent("Scan queued"));
    expect(scan).toBeEnabled();
  });

  it("scans a TV workflow as TV, and only the one that was clicked", async () => {
    serve({
      workflows: [
        library({ id: 1, name: "Movies" }),
        library({ id: 2, name: "TV", media_type: "tv", display_order: 1 }),
      ],
    });
    const row = await openRow("TV");

    fireEvent.click(within(row).getByRole("button", { name: "Scan TV now" }));

    await waitFor(() => expect(server.to(ENQUEUE)).toHaveLength(1));
    expect(server.to(ENQUEUE)[0]!.body).toMatchObject({
      media_scope: "tv",
      library_id: 2,
    });
    const [movies] = screen.getAllByTestId("schedule-library-row");
    expect(
      within(movies!).getByRole("button", { name: "Scan Movies now" }),
    ).toHaveTextContent("Scan now");
  });

  it("says why when the scan could not be queued, and offers Scan now again", async () => {
    serve(
      { workflows: [library({ id: 1, name: "Movies" })] },
      jsonAnswer({ detail: "Weir is paused. Resume it to scan." }, 409),
    );
    const row = await openRow("Movies");

    fireEvent.click(
      within(row).getByRole("button", { name: "Scan Movies now" }),
    );

    expect(await within(row).findByRole("alert")).toHaveTextContent(
      "Weir is paused. Resume it to scan.",
    );
    expect(
      within(row).getByRole("button", { name: "Scan Movies now" }),
    ).toHaveTextContent("Scan now");
  });

  it("is held, with the reason, for a workflow with no watched folder", async () => {
    serve({
      workflows: [library({ id: 1, name: "Movies", watched_folder: " " })],
    });
    const row = await openRow("Movies");

    const scan = within(row).getByRole("button", { name: "Scan Movies now" });
    expect(scan).toBeDisabled();
    expect(scan).toHaveAttribute(
      "title",
      "This workflow has no watched folder yet.",
    );
    fireEvent.click(scan);
    expect(server.to(ENQUEUE)).toHaveLength(0);
  });

  it("is held for a viewer", async () => {
    serve({ workflows: [library({ id: 1, name: "Movies" })], role: "viewer" });
    const row = await openRow("Movies");

    expect(
      within(row).getByRole("button", { name: "Scan Movies now" }),
    ).toBeDisabled();
  });
});
