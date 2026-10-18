import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { adminMe, csrfToken } from "../../test/recorded-admin-session";
import { systemReplies } from "../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedRoutes,
} from "../../test/recorded-weir";
import { SystemPage } from "./system-page";

// The page here is the real one, reading replies a running Weir server gave. The file is about the time zone on System ›
// About: what it says when the zone cannot be saved, and what happens to a zone picked and not yet saved when the person
// moves on.

const SETTINGS = "GET /api/v1/suite/settings";
const SAVE = "PUT /api/v1/suite/settings";
const SAVE_BUTTON = "schedule-save-timezone";
const PROMPT = "unsaved-changes-dialog";

let client: QueryClient;

beforeEach(() => {
  client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function openSystem(routes: RecordedRoutes = {}) {
  const weir = serveRecordedReplies({
    "GET /api/v1/auth/me": adminMe,
    "GET /api/v1/auth/csrf": csrfToken,
    [SETTINGS]: systemReplies.settings,
    ...routes,
  });
  const router = createMemoryRouter(
    [
      { path: "/system", element: <SystemPage /> },
      { path: "/activity", element: <p>Activity page</p> },
    ],
    { initialEntries: ["/system"] },
  );
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return { weir, router };
}

/** Picks a zone other than the saved one, so the Save button appears. */
async function pickSydney() {
  const section = await screen.findByTestId("about-time-zone");
  const picker = await within(section).findByRole("button", {
    name: "Time zone",
  });
  await waitFor(() => expect(picker).toBeEnabled());
  fireEvent.click(picker);
  fireEvent.click(screen.getByRole("option", { name: /Sydney/ }));
  return section;
}

describe("saving the time zone", () => {
  it("says why when the server refuses it, and keeps the zone picked with its Save button", async () => {
    const { weir } = openSystem({ [SAVE]: systemReplies.settingsPutFails });
    const section = await pickSydney();

    fireEvent.click(within(section).getByTestId(SAVE_BUTTON));

    expect(
      await within(section).findByText("Could not save settings."),
    ).toBeVisible();
    expect(weir.sent("PUT", "/api/v1/suite/settings")).toHaveLength(1);
    expect(within(section).getByTestId(SAVE_BUTTON)).toBeEnabled();
  });

  it("says Weir cannot be reached when the server does not answer", async () => {
    openSystem({
      [SAVE]: () => Promise.reject(new TypeError("Failed to fetch")),
    });
    const section = await pickSydney();

    fireEvent.click(within(section).getByTestId(SAVE_BUTTON));

    expect(
      await within(section).findByText(
        "Can't reach Weir. Check it's still running.",
      ),
    ).toBeVisible();
    expect(within(section).getByTestId(SAVE_BUTTON)).toBeEnabled();
  });
});

describe("a time zone picked and not yet saved", () => {
  it("is asked about before the person leaves System for another page", async () => {
    const { router } = openSystem();
    await pickSydney();

    void router.navigate("/activity");

    expect(await screen.findByTestId(PROMPT)).toHaveTextContent(
      "Leave without saving?",
    );
    expect(router.state.location.pathname).toBe("/system");
    fireEvent.click(screen.getByTestId(`${PROMPT}-cancel`));
    expect(screen.queryByTestId(PROMPT)).toBeNull();
    expect(screen.getByTestId(SAVE_BUTTON)).toBeVisible();
  });

  it("is asked about before another System tab replaces About, and stays when told to", async () => {
    const { router } = openSystem();
    await pickSydney();

    fireEvent.click(screen.getByRole("tab", { name: "Backups" }));

    expect(await screen.findByTestId(PROMPT)).toHaveTextContent(
      "Leave without saving?",
    );
    expect(router.state.location.search).toBe("");
    fireEvent.click(screen.getByTestId(`${PROMPT}-cancel`));
    expect(screen.getByTestId(SAVE_BUTTON)).toBeVisible();
  });

  it("is let go of when the person chooses to leave", async () => {
    const { router } = openSystem();
    await pickSydney();

    fireEvent.click(screen.getByRole("tab", { name: "Backups" }));
    fireEvent.click(await screen.findByTestId(`${PROMPT}-confirm`));

    await waitFor(() =>
      expect(router.state.location.search).toBe("?tab=backups"),
    );
    expect(screen.queryByTestId(PROMPT)).toBeNull();
  });

  it("asks nothing once it is saved", async () => {
    const sydney: RecordedReply = {
      status: 200,
      body: JSON.stringify({
        ...JSON.parse(systemReplies.settings.body),
        app_timezone: "Australia/Sydney",
      }),
    };
    const { router } = openSystem({
      [SETTINGS]: [systemReplies.settings, sydney],
      [SAVE]: sydney,
    });
    const section = await pickSydney();

    fireEvent.click(within(section).getByTestId(SAVE_BUTTON));
    await waitFor(() =>
      expect(within(section).queryByTestId(SAVE_BUTTON)).toBeNull(),
    );
    // The page learns the zone is saved a render after the button goes; let that land before the next click.
    await act(async () => undefined);
    fireEvent.click(screen.getByRole("tab", { name: "Backups" }));

    await waitFor(() =>
      expect(router.state.location.search).toBe("?tab=backups"),
    );
    expect(screen.queryByTestId(PROMPT)).toBeNull();
  });
});

describe("moving between the tabs of System with nothing unsaved", () => {
  it("asks nothing", async () => {
    const { router } = openSystem();
    await screen.findByTestId("about-time-zone");

    fireEvent.click(screen.getByRole("tab", { name: "Backups" }));

    await waitFor(() =>
      expect(router.state.location.search).toBe("?tab=backups"),
    );
    expect(screen.queryByTestId(PROMPT)).toBeNull();
  });
});
