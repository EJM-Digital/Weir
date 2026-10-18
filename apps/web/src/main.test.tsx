import { screen } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

import { APP_THEME_STORAGE_KEY } from "./lib/ui/app-theme";
import { jsonAnswer, stubWeirApi } from "./test/stub-weir-api";

/**
 * What main.tsx wires together, run for real against a page with a #root: the saved theme is on the document
 * before anything is drawn, and a failure of the whole app ends at the error screen rather than a blank page.
 */

vi.mock("./app/router", () => ({
  AppRouter: () => {
    throw new Error("the router broke while it drew");
  },
}));
vi.mock("./app/preload-fonts", () => ({ preloadAboveTheFoldFonts: () => {} }));

beforeEach(() => {
  document.body.innerHTML = '<div id="root"></div>';
  localStorage.setItem(APP_THEME_STORAGE_KEY, "light");
  document.documentElement.removeAttribute("data-mm-theme");
  vi.spyOn(console, "error").mockImplementation(() => {});
  stubWeirApi({
    "GET /ready": { ready: true, status: "ready" },
    "GET /api/v1/auth/me": () =>
      jsonAnswer({ detail: "Not authenticated." }, 401),
  });
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  localStorage.clear();
  document.documentElement.removeAttribute("data-mm-theme");
});

it("puts the saved theme on the page before the first draw, and shows the error screen when the app itself fails", async () => {
  await import("./main");

  expect(document.documentElement.getAttribute("data-mm-theme")).toBe("light");
  expect(
    await screen.findByText("the router broke while it drew", { exact: false }),
  ).toBeInTheDocument();
  expect(screen.getByRole("main")).toHaveAttribute("id", "mm-main-content");
});
