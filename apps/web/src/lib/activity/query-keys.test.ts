import { expect, it } from "vitest";

import { activityKeys } from "./query-keys";

it("keys a list with no filters the same as one with an empty set of them", () => {
  expect(activityKeys.recentList()).toEqual(activityKeys.recentList({}));
  expect(activityKeys.recentList({ limit: 5 })).toEqual([
    "activity",
    "recent",
    { limit: 5 },
  ]);
});
