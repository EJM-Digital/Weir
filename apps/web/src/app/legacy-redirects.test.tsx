import { render, screen } from "@testing-library/react";
import {
  createMemoryRouter,
  RouterProvider,
  useLocation,
} from "react-router-dom";
import { describe, expect, it } from "vitest";

import {
  LegacyHistoryRedirect,
  LegacyProcessingRedirect,
  LegacySettingsRedirect,
} from "./legacy-redirects";

function Where() {
  const { pathname, search, hash } = useLocation();
  return <p data-testid="where">{`${pathname}${search}${hash}`}</p>;
}

function landOn(entry: string): string {
  const router = createMemoryRouter(
    [
      { path: "/settings", element: <LegacySettingsRedirect /> },
      { path: "/processing", element: <LegacyProcessingRedirect /> },
      { path: "/history", element: <LegacyHistoryRedirect /> },
      { path: "*", element: <Where /> },
    ],
    { initialEntries: [entry] },
  );
  render(<RouterProvider router={router} />);
  return screen.getByTestId("where").textContent ?? "";
}

describe("/settings", () => {
  it.each([
    ["/settings", "/setup/workflows"],
    ["/settings?tab=libraries", "/setup/workflows"],
    ["/settings?tab=libraries&edit=4", "/setup/workflows?edit=4"],
    ["/settings?tab=libraries&addFrom=6", "/setup/workflows?addFrom=6"],
    ["/settings?tab=rules", "/setup/rules"],
    ["/settings?tab=media-managers", "/setup/connections"],
    ["/settings?tab=performance", "/setup/performance"],
    ["/settings?tab=schedule", "/setup/workflows/schedule"],
    ["/settings?tab=cleanup", "/setup/performance/cleanup"],
    ["/settings?tab=alerts", "/setup/connections/alerts"],
    ["/settings?tab=audio-subtitles", "/setup/rules"],
    ["/settings?tab=running", "/setup/performance"],
    ["/settings?tab=processing", "/setup/performance"],
    ["/settings?tab=housekeeping", "/setup/performance/cleanup"],
    ["/settings?tab=maintenance", "/setup/performance/cleanup"],
    ["/settings?tab=schedules", "/setup/workflows/schedule"],
    ["/settings?tab=notifications", "/setup/connections/alerts"],
  ])("%s lands on %s", (entry, target) => {
    expect(landOn(entry)).toBe(target);
  });

  it.each([
    ["upgrade", "/system?tab=about"],
    ["support", "/system?tab=about"],
    ["backup", "/system?tab=backups"],
    ["security", "/system?tab=security"],
    ["logs", "/system?tab=logs"],
  ])("?tab=%s still lands on System at %s", (name, target) => {
    expect(landOn(`/settings?tab=${name}`)).toBe(target);
  });

  it("keeps the fragment of the address", () => {
    expect(landOn("/settings?tab=rules#profiles")).toBe(
      "/setup/rules#profiles",
    );
  });
});

describe("/processing", () => {
  it.each([
    ["/processing", "/"],
    ["/processing?tab=overview", "/"],
    ["/processing?tab=no-such-tab", "/"],
    ["/processing?tab=files", "/activity"],
    ["/processing?tab=libraries", "/setup/workflows"],
    ["/processing?tab=audio-subtitles", "/setup/rules"],
    ["/processing?tab=schedules", "/setup/workflows/schedule"],
    ["/processing?tab=library", "/library"],
    ["/processing?tab=jobs", "/system?tab=logs&source=job"],
    ["/processing?tab=maintenance", "/setup/performance/cleanup"],
  ])("%s lands on %s", (entry, target) => {
    expect(landOn(entry)).toBe(target);
  });

  it("carries a saved filter to Activity", () => {
    expect(landOn("/processing?tab=files&status=failed")).toBe(
      "/activity?status=failed",
    );
  });
});

describe("/history", () => {
  it.each([
    ["/history", "/activity"],
    ["/history?show=attention", "/activity?show=attention"],
    ["/history?show=failed&library=2", "/activity?show=failed&library=2"],
    ["/history?q=Heat.1995.mkv", "/activity?q=Heat.1995.mkv"],
    [
      "/history?show=failed&file=9&within=all",
      "/activity?show=failed&file=9&within=all",
    ],
  ])("%s lands on %s", (entry, target) => {
    expect(landOn(entry)).toBe(target);
  });

  it("keeps the fragment of the address", () => {
    expect(landOn("/history?file=3#story")).toBe("/activity?file=3#story");
  });

  it("keeps a search with encoded characters as it was written", () => {
    expect(landOn("/history?q=Se%C3%B1or%20X%2B")).toBe(
      "/activity?q=Se%C3%B1or%20X%2B",
    );
  });
});
