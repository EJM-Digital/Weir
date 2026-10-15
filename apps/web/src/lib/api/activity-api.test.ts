import { describe, expect, it } from "vitest";
import { activityExportPath, activityRecentPath } from "./activity-api";

describe("activity-api paths", () => {
  it("sends the new history filters on the feed", () => {
    const path = activityRecentPath({
      limit: 100,
      trigger: "scheduled",
      result: "failed",
      library_id: 3,
      file: "Movies/A b.mkv",
      before_id: 9,
    });
    const url = new URL(path, "http://x");
    expect(url.pathname).toBe("/api/v1/activity/recent");
    expect(url.searchParams.get("trigger")).toBe("scheduled");
    expect(url.searchParams.get("result")).toBe("failed");
    expect(url.searchParams.get("library_id")).toBe("3");
    expect(url.searchParams.get("file")).toBe("Movies/A b.mkv");
    expect(url.searchParams.get("before_id")).toBe("9");
    expect(url.searchParams.get("limit")).toBe("100");
  });

  it("asks for each file as it stands now only when told to, and never for the export", () => {
    const asked = (path: string) =>
      new URL(path, "http://x").searchParams.get("current_only");

    expect(asked(activityRecentPath({ current_only: true }))).toBe("true");
    expect(asked(activityRecentPath({}))).toBeNull();
    expect(asked(activityExportPath("csv", { current_only: true }))).toBeNull();
  });

  it("leaves out the count only when told to", () => {
    const sent = (path: string) =>
      new URL(path, "http://x").searchParams.get("with_total");

    expect(sent(activityRecentPath({ with_total: false }))).toBe("false");
    expect(sent(activityRecentPath({}))).toBeNull();
    expect(sent(activityRecentPath({ with_total: true }))).toBeNull();
  });

  it("exports the same filters without paging", () => {
    const path = activityExportPath("json", {
      limit: 100,
      before_id: 9,
      module: "processing",
      trigger: "webhook",
      date_from: "2026-08-12T08:00:00.000Z",
    });
    const url = new URL(path, "http://x");
    expect(url.pathname).toBe("/api/v1/activity/export");
    expect(url.searchParams.get("format")).toBe("json");
    expect(url.searchParams.get("module")).toBe("processing");
    expect(url.searchParams.get("trigger")).toBe("webhook");
    expect(url.searchParams.get("date_from")).toBe("2026-08-12T08:00:00.000Z");
    expect(url.searchParams.has("limit")).toBe(false);
    expect(url.searchParams.has("before_id")).toBe(false);
  });
});

it("leaves out a number that is not a number instead of asking for NaN", () => {
  const path = activityRecentPath({
    library_id: Number.NaN,
    before_id: Infinity,
    limit: Number.NaN,
  });
  expect(path).toBe("/api/v1/activity/recent");
});
