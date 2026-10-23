import { QueryClient } from "@tanstack/react-query";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { jsonAnswer } from "../../../../test/stub-weir-api";
import { library } from "../libraries/library-test-fixtures";
import {
  LIBRARIES,
  newTestClient,
  serveWorkflows,
  signedIn,
  updateRoute,
  workflowTabWrapper,
  type WorkflowServer,
} from "../libraries/workflow-server";
import { ScheduleTab } from "./schedule-tab";

// The tab here is the real one, reading from a stand-in server at `fetch`. The file is about the rows of the week, and about
// drawing hours: when Save is on, what it says while it works and when it fails, and what leaving unsaved hours does.

const SETTINGS = {
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

let server: WorkflowServer;
let client: QueryClient;

beforeEach(() => {
  client = newTestClient();
});

afterEach(() => {
  expect(server.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

function serve(workflows = [library({ id: 1, name: "Movies" })]) {
  server = serveWorkflows({
    workflows,
    extra: { "GET /api/v1/suite/settings": SETTINGS },
  });
}

async function openTab() {
  render(<ScheduleTab />, { wrapper: workflowTabWrapper(client) });
  await screen.findByTestId("processing-schedules-section");
  await signedIn(client);
}

/** Opens the hours of the workflow in the nth row (each row is asked for its own button, which is far quicker than naming every cell). */
async function changeHours(nth = 0) {
  const rows = await screen.findAllByTestId("schedule-library-row");
  fireEvent.click(
    within(rows[nth]!).getByRole("button", { name: "Change hours" }),
  );
  return screen.findByTestId("schedule-library-editor");
}

/** One press on Monday 09:00, let go again, which switches that hour the other way. */
function drawMondayNine() {
  const hour = screen.getByTestId("schedule-cell-0-9");
  fireEvent.pointerDown(hour);
  fireEvent.pointerUp(hour);
}
const save = () => screen.getByTestId("schedule-library-save");

describe("the rows", () => {
  it("say a workflow is switched off in Workflows, and only that one", async () => {
    serve([
      library({ id: 1, name: "Movies" }),
      library({ id: 2, name: "TV", enabled: false, display_order: 1 }),
    ]);

    await openTab();

    const [movies, tv] = await screen.findAllByTestId("schedule-library-row");
    expect(movies).not.toHaveTextContent("Switched off in Workflows");
    expect(tv).toHaveTextContent("Switched off in Workflows");
  });

  it("say there is nothing to schedule when there are no workflows", async () => {
    serve([]);

    await openTab();

    expect(
      await screen.findByText(
        "Add a workflow under Workflows to schedule it here.",
      ),
    ).toBeInTheDocument();
    expect(screen.queryByTestId("schedule-library-row")).toBeNull();
  });
});

describe("Save on the hours", () => {
  it("is off until the week changes, on once it has, and off again when the change is undone", async () => {
    serve();
    await openTab();
    await changeHours();
    expect(save()).toBeDisabled();

    drawMondayNine();
    expect(save()).toBeEnabled();
    expect(save()).toHaveTextContent("Save Movies's hours");

    drawMondayNine();
    expect(save()).toBeDisabled();
  });

  it("reads Saving… and holds the hours while the server works", async () => {
    serve();
    server.set(updateRoute(1), () => new Promise<never>(() => undefined));
    await openTab();
    await changeHours();
    drawMondayNine();

    fireEvent.click(save());

    expect(
      await screen.findByRole("button", { name: "Saving…" }),
    ).toBeDisabled();
    expect(screen.getByTestId("schedule-cell-0-9")).toBeDisabled();
  });

  it("sends the drawn week to the workflow's own address", async () => {
    serve();
    await openTab();
    await changeHours();
    drawMondayNine();

    fireEvent.click(save());

    await vi.waitFor(() => expect(server.to(updateRoute(1))).toHaveLength(1));
    expect(
      (server.to(updateRoute(1))[0]!.body as { schedule_grid: string })
        .schedule_grid,
    ).toHaveLength(672);
  });

  it("says why when the server refuses the hours, and keeps the editor open with the week drawn", async () => {
    serve();
    server.set(updateRoute(1), () =>
      jsonAnswer(
        {
          detail:
            "Another change was saved first. Reload the page, then draw the hours again.",
        },
        409,
      ),
    );
    await openTab();
    await changeHours();
    drawMondayNine();

    fireEvent.click(save());

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Another change was saved first. Reload the page, then draw the hours again.",
    );
    expect(screen.getByTestId("schedule-library-editor")).toBeVisible();
    expect(screen.getByTestId("schedule-cell-0-9")).toHaveAttribute(
      "aria-pressed",
      "false",
    );
    expect(save()).toBeEnabled();
  });

  it("says Weir cannot be reached when the server does not answer", async () => {
    serve();
    server.set(updateRoute(1), () => {
      throw new TypeError("Failed to fetch");
    });
    await openTab();
    await changeHours();
    drawMondayNine();

    fireEvent.click(save());

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Can't reach Weir. Check it's still running.",
    );
    expect(server.to(`PUT ${LIBRARIES}/1`)).toHaveLength(1);
  });
});

describe("hours drawn and not saved", () => {
  beforeEach(() => {
    serve([
      library({ id: 1, name: "Movies" }),
      library({
        id: 2,
        name: "TV",
        display_order: 1,
        schedule_grid: `${"0".repeat(4)}${"1".repeat(668)}`,
      }),
    ]);
  });

  const asks = () => screen.queryByTestId("settings-unsaved-changes");
  const leave = () =>
    fireEvent.click(screen.getByTestId("settings-unsaved-changes-confirm"));

  it("are asked about before another workflow's hours replace them, and Leave without saving switches to the other week", async () => {
    await openTab();
    await changeHours(0);
    drawMondayNine();

    const rows = screen.getAllByTestId("schedule-library-row");
    fireEvent.click(
      within(rows[1]!).getByRole("button", { name: "Change hours" }),
    );
    expect(asks()).toHaveTextContent("Movies's hours");
    leave();

    expect(screen.getByTestId("schedule-library-editor")).toHaveAccessibleName(
      "TV hours",
    );
    expect(screen.getByTestId("schedule-cell-0-0")).toHaveAttribute(
      "aria-pressed",
      "false",
    );
    expect(screen.getByTestId("schedule-cell-0-9")).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    expect(asks()).toBeNull();
    expect(server.to(updateRoute(1))).toHaveLength(0);
  });

  it("are asked about on Close, Keep editing leaves them drawn, and Leave without saving drops them", async () => {
    await openTab();
    await changeHours(0);
    drawMondayNine();

    fireEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(asks()).toHaveTextContent(
      "You have unsaved changes to Movies's hours. Leave without saving?",
    );
    fireEvent.click(screen.getByTestId("settings-unsaved-changes-cancel"));
    expect(screen.getByTestId("schedule-library-editor")).toBeVisible();
    expect(screen.getByTestId("schedule-cell-0-9")).toHaveAttribute(
      "aria-pressed",
      "false",
    );

    fireEvent.click(screen.getByRole("button", { name: "Close" }));
    leave();

    expect(screen.queryByTestId("schedule-library-editor")).toBeNull();
    expect(server.to(updateRoute(1))).toHaveLength(0);
  });

  it("close when the same workflow's Change hours is pressed again, and ask first about a week drawn", async () => {
    await openTab();
    await changeHours(0);
    drawMondayNine();

    const rows = await screen.findAllByTestId("schedule-library-row");
    fireEvent.click(
      within(rows[0]!).getByRole("button", { name: "Change hours" }),
    );
    expect(asks()).toHaveTextContent(
      "You have unsaved changes to Movies's hours. Leave without saving?",
    );
    leave();

    expect(screen.queryByTestId("schedule-library-editor")).toBeNull();
  });

  it("are not asked about when the editor is closed with nothing drawn", async () => {
    await openTab();
    await changeHours(0);

    fireEvent.click(screen.getByRole("button", { name: "Close" }));

    expect(asks()).toBeNull();
    expect(screen.queryByTestId("schedule-library-editor")).toBeNull();
  });
});
