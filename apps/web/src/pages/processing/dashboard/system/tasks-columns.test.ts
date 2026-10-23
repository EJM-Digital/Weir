import { expect, it } from "vitest";

import type { TaskRow } from "./tasks-card-model";
import { TASK_SORT_VALUES } from "./tasks-columns";

const row = (over: Partial<TaskRow>): TaskRow => ({
  key: "scan",
  label: "Library scan",
  state: "ok",
  lastAt: null,
  nextAt: 5_000,
  why: null,
  ...over,
});

it("sorts a task by when it runs next, and a running task, which has no next time, last", () => {
  expect(TASK_SORT_VALUES.next(row({}))).toBe(5_000);
  expect(TASK_SORT_VALUES.next(row({ state: "running" }))).toBeNull();
});
