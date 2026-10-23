import { fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { SystemTask } from "../../../../lib/system/system-tasks-frame";
import { TasksCard } from "./tasks-card";

const NOW = Date.parse("2026-10-02T12:00:00Z");

type TasksQuery = {
  data: SystemTask[] | undefined;
  isError: boolean;
  isSuccess: boolean;
};
const query: TasksQuery = { data: [], isError: false, isSuccess: true };
let fits = Number.MAX_SAFE_INTEGER;

vi.mock("../fit-rows", () => ({
  useFittingRows: () => [{ current: null }, fits],
}));
vi.mock("../../../../lib/system/system-tasks", () => ({
  useSystemTasksQuery: () => query,
}));

function task(overrides: Partial<SystemTask> & { key: string }): SystemTask {
  return {
    label: overrides.key,
    running: false,
    last_run_at: null,
    last_ok: null,
    last_error: null,
    next_run_at: null,
    interval_seconds: null,
    ...overrides,
  };
}

function renderCard() {
  render(
    <MemoryRouter>
      <TasksCard />
    </MemoryRouter>,
  );
  return screen.getByRole("region", { name: "Scheduled tasks" });
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(NOW);
  localStorage.clear();
  query.data = [];
  query.isError = false;
  query.isSuccess = true;
  fits = Number.MAX_SAFE_INTEGER;
});

afterEach(() => {
  vi.useRealTimers();
});

describe("the Scheduled tasks card", () => {
  it("lists a task with how long ago it ran and a countdown to its next run", () => {
    query.data = [
      task({
        key: "scan",
        label: "Scan Movies",
        last_ok: true,
        last_run_at: "2026-10-02T11:58:00Z",
        next_run_at: "2026-10-02T12:00:42Z",
      }),
    ];
    const card = renderCard();

    const row = within(card).getByTestId("system-task");
    expect(row).toHaveTextContent("Scan Movies");
    expect(row).toHaveTextContent("2 min ago");
    expect(row).toHaveTextContent("0:42");
    expect(row).toHaveAttribute("data-status", "done");
  });

  it("shows a running task first with a blinking dot and the word running", () => {
    query.data = [
      task({
        key: "later",
        next_run_at: "2026-10-02T12:10:00Z",
        last_ok: true,
      }),
      task({ key: "busy", running: true }),
    ];
    const card = renderCard();

    const rows = within(card).getAllByTestId("system-task");
    expect(rows[0]).toHaveAttribute("data-status", "doing");
    expect(rows[0]).toHaveTextContent("running");
    expect(card).toHaveTextContent("1 running");
  });

  it("shows a failed task with a cross and the reason it gave", () => {
    query.data = [
      task({
        key: "backup",
        label: "Back up settings",
        last_ok: false,
        last_error: "The backup folder is full.",
        last_run_at: "2026-10-02T11:59:00Z",
      }),
    ];
    const card = renderCard();

    const row = within(card).getByTestId("system-task");
    expect(row).toHaveAttribute("data-status", "broken");
    expect(row).toHaveTextContent("✗");
    expect(row).toHaveTextContent("The backup folder is full.");
    expect(card).toHaveTextContent("1 failed");
  });

  it("links the header to the jobs Weir has run", () => {
    const card = renderCard();

    expect(
      within(card).getByRole("link", { name: "Jobs: Scheduled tasks" }),
    ).toHaveAttribute("href", "/system?tab=logs&source=job");
  });

  it("says how many tasks the card's height left out", () => {
    query.data = [
      task({ key: "a", next_run_at: "2026-10-02T12:01:00Z" }),
      task({ key: "b", next_run_at: "2026-10-02T12:02:00Z" }),
      task({ key: "c", next_run_at: "2026-10-02T12:03:00Z" }),
    ];
    fits = 2;
    const card = renderCard();

    expect(within(card).getByTestId("system-more")).toHaveTextContent("1 more");
  });

  it("says there are no tasks when the list is empty", () => {
    const card = renderCard();

    expect(card).toHaveTextContent("No scheduled tasks yet.");
    expect(within(card).queryByTestId("system-more")).toBeNull();
  });

  it("says nothing of tasks, neither that there are none nor that they failed, while they are still being read", () => {
    query.data = undefined;
    query.isSuccess = false;
    const card = renderCard();

    expect(card).not.toHaveTextContent("No scheduled tasks yet.");
    expect(card).not.toHaveTextContent("could not read its scheduled tasks");
    expect(within(card).queryByTestId("system-task")).toBeNull();
  });

  it("says Weir could not read the tasks when the read failed", () => {
    query.isError = true;
    query.isSuccess = false;
    const card = renderCard();

    expect(card).toHaveTextContent("could not read its scheduled tasks");
  });

  describe("sorting and moving columns", () => {
    function listed(card: HTMLElement) {
      return within(card)
        .getAllByTestId("system-task")
        .map((row) => row.querySelector("b")?.textContent);
    }

    beforeEach(() => {
      query.data = [
        task({
          key: "b",
          label: "Back up settings",
          last_ok: false,
          last_run_at: "2026-10-02T11:00:00Z",
          next_run_at: "2026-10-02T12:03:00Z",
        }),
        task({
          key: "c",
          label: "Clean copies",
          last_ok: true,
          last_run_at: "2026-10-02T11:00:00Z",
          next_run_at: "2026-10-02T12:01:00Z",
        }),
        task({
          key: "a",
          label: "Scan Movies",
          last_ok: true,
          last_run_at: "2026-10-02T11:00:00Z",
          next_run_at: "2026-10-02T12:02:00Z",
        }),
      ];
    });

    it("keeps the card's own order until a heading is clicked: failed first, then the one due soonest", () => {
      expect(listed(renderCard())).toEqual([
        "Back up settings",
        "Clean copies",
        "Scan Movies",
      ]);
    });

    it("sorts by the task's name, and reverses on the next click", () => {
      const card = renderCard();

      fireEvent.click(within(card).getByRole("button", { name: "Task" }));
      expect(listed(card)).toEqual([
        "Back up settings",
        "Clean copies",
        "Scan Movies",
      ]);

      fireEvent.click(within(card).getByRole("button", { name: "Task" }));
      expect(listed(card)).toEqual([
        "Scan Movies",
        "Clean copies",
        "Back up settings",
      ]);
    });

    it("sorts by how the last run went, tasks that went the same way staying in the card's order", () => {
      const card = renderCard();

      fireEvent.click(
        within(card).getByRole("button", { name: "Last result" }),
      );

      expect(listed(card)).toEqual([
        "Clean copies",
        "Scan Movies",
        "Back up settings",
      ]);
    });

    it("sorts by when the task runs next", () => {
      const card = renderCard();

      fireEvent.click(within(card).getByRole("button", { name: "Next" }));

      expect(listed(card)).toEqual([
        "Clean copies",
        "Scan Movies",
        "Back up settings",
      ]);
    });

    it("puts the card's order back with Reset columns", () => {
      const card = renderCard();
      fireEvent.click(within(card).getByRole("button", { name: "Next" }));

      fireEvent.click(within(card).getByRole("button", { name: "Columns" }));
      fireEvent.click(screen.getByRole("menuitem", { name: "Reset columns" }));

      expect(listed(card)[0]).toBe("Back up settings");
    });
  });
});
