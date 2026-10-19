import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { systemReplies } from "../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedRoutes,
} from "../../test/recorded-weir";
import { SystemPage } from "./system-page";
import { csrfToken, me } from "./tabs/security/security-replies";

const SERVER: RecordedRoutes = {
  "GET /api/v1/auth/me": me,
  "GET /api/v1/suite/settings": systemReplies.settings,
  "GET /api/v1/auth/csrf": csrfToken,
  "GET /api/v1/processing/operator-settings": systemReplies.operatorSettings,
  "GET /api/v1/suite/configuration-backups": systemReplies.backupsEmpty,
};

/** System at `address`; the address is what the tabs read and write, so the router is returned to read it back. */
async function openSystem(address: string) {
  serveRecordedReplies(SERVER);
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: 0 } },
  });
  const router = createMemoryRouter(
    [{ path: "/system", element: <SystemPage /> }],
    {
      initialEntries: [address],
    },
  );
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  await screen.findByRole("tab", { name: "About" });
  return router;
}

const pick = (name: string) =>
  fireEvent.click(screen.getByRole("tab", { name }));
const selected = () =>
  screen
    .getAllByRole("tab")
    .filter((tab) => tab.getAttribute("aria-selected") === "true")
    .map((tab) => tab.textContent);
const address = (router: Awaited<ReturnType<typeof openSystem>>) =>
  router.state.location.pathname + router.state.location.search;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the address of System is its tab", () => {
  it("names the tab picked, and names none for About, where System opens", async () => {
    const router = await openSystem("/system");

    pick("Security");
    expect(address(router)).toBe("/system?tab=security");
    pick("Backups");
    expect(address(router)).toBe("/system?tab=backups");
    pick("Logs");
    expect(address(router)).toBe("/system?tab=logs");
    pick("About");
    expect(address(router)).toBe("/system");
    expect(selected()).toEqual(["About"]);
  });

  it("takes the filters of the log away when another tab is picked, and keeps what is not the log's", async () => {
    const router = await openSystem(
      "/system?tab=logs&level=error&source=job&q=heat&keep=this",
    );

    pick("Backups");

    expect(address(router)).toBe("/system?tab=backups&keep=this");
  });

  it("leaves no tab and no filter in the address of About", async () => {
    const router = await openSystem("/system?tab=logs&level=error&q=heat");

    pick("About");

    expect(address(router)).toBe("/system");
  });

  it("moves between the tabs with the browser's Back and Forward", async () => {
    const router = await openSystem("/system");
    pick("Backups");
    pick("Security");
    expect(selected()).toEqual(["Security"]);

    await act(() => router.navigate(-1));
    expect(address(router)).toBe("/system?tab=backups");
    expect(selected()).toEqual(["Backups"]);

    await act(() => router.navigate(-1));
    expect(address(router)).toBe("/system");
    expect(selected()).toEqual(["About"]);

    await act(() => router.navigate(1));
    expect(selected()).toEqual(["Backups"]);
    await act(() => router.navigate(1));
    expect(selected()).toEqual(["Security"]);
  });
});
