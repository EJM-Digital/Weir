import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { settingsKeys } from "../../lib/settings/query-keys";
import { systemReplies } from "../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../test/recorded-weir";
import { SystemPage } from "./system-page";
import { csrfToken, me } from "./tabs/security/security-replies";

const SETTINGS = "GET /api/v1/suite/settings";
const SAVE = "PUT /api/v1/suite/settings";
const SAVE_PATH = "/api/v1/suite/settings";
const ME = "GET /api/v1/auth/me";
const LEAVE = "unsaved-changes-dialog";

const THIRTY_DAYS: RecordedReply = systemReplies.settings;
/** The server after someone else changed how long it keeps things, with the backups still off. */
const CHANGED_ELSEWHERE: RecordedReply = systemReplies.settingsPutRetention;
const VIEWER: RecordedReply = {
  status: 200,
  body: me.body.replace('"admin"', '"viewer"'),
};

const SERVER: RecordedRoutes = {
  [ME]: me,
  [SETTINGS]: THIRTY_DAYS,
  "GET /api/v1/auth/csrf": csrfToken,
  "GET /api/v1/processing/operator-settings": systemReplies.operatorSettings,
  "GET /api/v1/suite/configuration-backups": systemReplies.backupsEmpty,
};

let weir: RecordedWeir;
let client: QueryClient;

/** System at `address`, beside a place to leave for, so a test can walk away from it. */
function openSystem(address: string, routes: RecordedRoutes = {}) {
  weir = serveRecordedReplies({ ...SERVER, ...routes });
  client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: 0 },
      mutations: { retry: false },
    },
  });
  const router = createMemoryRouter(
    [
      { path: "/system", element: <SystemPage /> },
      { path: "/elsewhere", element: <p>Somewhere else</p> },
    ],
    { initialEntries: [address] },
  );
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

const leaveFor = (router: ReturnType<typeof openSystem>) =>
  act(() => void router.navigate("/elsewhere"));
const tab = (name: string) => screen.getByRole("tab", { name });
const scheduleSwitch = () =>
  screen.findByRole("radiogroup", { name: "Back up automatically" });
const turnBackupsOn = async () =>
  fireEvent.click(
    within(await scheduleSwitch()).getByRole("radio", { name: "On" }),
  );
const logDays = () => screen.findByLabelText("System log");

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("while System reads its settings", () => {
  it("says it is loading settings until the settings arrive", async () => {
    openSystem("/system", { [SETTINGS]: () => new Promise(() => undefined) });

    expect(await screen.findByText("Loading settings")).toBeVisible();
    expect(screen.queryByRole("tab", { name: "About" })).toBeNull();
  });

  it("says Weir cannot be reached when the server does not answer", async () => {
    openSystem("/system", {
      [SETTINGS]: () => Promise.reject(new TypeError("Failed to fetch")),
    });

    expect(
      await screen.findByText(
        "Could not reach the Weir API. Check that the backend is running.",
      ),
    ).toBeVisible();
  });

  it("asks the person to sign in again when the server refuses the read", async () => {
    openSystem("/system", { [SETTINGS]: systemReplies.settingsPutFails });

    expect(
      await screen.findByText(
        "The server refused this request. Sign in again, then try back here.",
      ),
    ).toBeVisible();
  });

  it("says something went wrong when the server's answer cannot be read", async () => {
    openSystem("/system", { [SETTINGS]: { status: 200, body: "not json" } });

    expect(
      await screen.findByText("Something went wrong loading settings."),
    ).toBeVisible();
  });
});

describe("leaving System with changes that are not saved", () => {
  it("asks first when the backup schedule has been changed, and Stay here keeps the change", async () => {
    const router = openSystem("/system?tab=backups");
    await turnBackupsOn();

    leaveFor(router);

    const dialog = await screen.findByTestId(LEAVE);
    expect(dialog).toHaveTextContent("Leave without saving?");
    fireEvent.click(within(dialog).getByRole("button", { name: "Stay here" }));
    await waitFor(() => expect(screen.queryByTestId(LEAVE)).toBeNull());
    expect(router.state.location.pathname).toBe("/system");
    expect(
      within(await scheduleSwitch()).getByRole("radio", { name: "On" }),
    ).toBeChecked();
  });

  it("goes on to the other place when the person chooses to leave without saving", async () => {
    const router = openSystem("/system?tab=backups");
    await turnBackupsOn();
    leaveFor(router);

    fireEvent.click(
      within(await screen.findByTestId(LEAVE)).getByRole("button", {
        name: "Leave without saving",
      }),
    );

    expect(await screen.findByText("Somewhere else")).toBeVisible();
    expect(weir.sent("PUT", SAVE_PATH)).toHaveLength(0);
  });

  it("asks first when a retention number has been changed", async () => {
    const router = openSystem("/system?tab=logs#retention");
    const days = await logDays();
    fireEvent.focus(days);
    fireEvent.change(days, { target: { value: "45" } });
    fireEvent.blur(days);

    leaveFor(router);

    expect(await screen.findByTestId(LEAVE)).toBeVisible();
  });

  it("asks first when a time zone has been picked and not saved", async () => {
    const router = openSystem("/system");
    fireEvent.click(await screen.findByRole("button", { name: "Time zone" }));
    fireEvent.click(screen.getByRole("option", { name: /Japan — Tokyo/ }));

    leaveFor(router);

    expect(await screen.findByTestId(LEAVE)).toBeVisible();
  });

  it("never asks when moving between System's own tabs, and keeps the schedule that was changed", async () => {
    openSystem("/system?tab=backups");
    await turnBackupsOn();

    fireEvent.click(tab("Logs"));
    expect(screen.queryByTestId(LEAVE)).toBeNull();
    fireEvent.click(tab("Backups"));

    expect(screen.queryByTestId(LEAVE)).toBeNull();
    expect(
      within(await scheduleSwitch()).getByRole("radio", { name: "On" }),
    ).toBeChecked();
  });

  it("asks nothing, and leaves at once, when nothing has been changed", async () => {
    const router = openSystem("/system?tab=backups");
    await scheduleSwitch();

    leaveFor(router);

    expect(await screen.findByText("Somewhere else")).toBeVisible();
    expect(screen.queryByTestId(LEAVE)).toBeNull();
  });

  it("holds the browser's own prompt for closing the tab only while something is unsaved", async () => {
    openSystem("/system?tab=backups");
    const closing = () => {
      const event = new Event("beforeunload", { cancelable: true });
      window.dispatchEvent(event);
      return event.defaultPrevented;
    };
    await scheduleSwitch();
    expect(closing()).toBe(false);

    await turnBackupsOn();

    expect(closing()).toBe(true);
  });
});

describe("a fresh read of the settings", () => {
  const refetch = () =>
    act(() => client.invalidateQueries({ queryKey: settingsKeys.app }));

  it("replaces a change that was not saved with what the server now holds", async () => {
    openSystem("/system?tab=backups");
    await turnBackupsOn();

    weir = serveRecordedReplies({ ...SERVER, [SETTINGS]: CHANGED_ELSEWHERE });
    await refetch();

    await waitFor(async () =>
      expect(
        within(await scheduleSwitch()).getByRole("radio", { name: "Off" }),
      ).toBeChecked(),
    );
    expect(screen.queryByRole("button", { name: "Save schedule" })).toBeNull();
  });

  it("keeps a change that was not saved when the server holds the same settings as before", async () => {
    openSystem("/system?tab=backups");
    await turnBackupsOn();

    await refetch();
    await waitFor(() =>
      expect(weir.sent("GET", SAVE_PATH).length).toBeGreaterThan(1),
    );

    expect(
      within(await scheduleSwitch()).getByRole("radio", { name: "On" }),
    ).toBeChecked();
    expect(screen.getByRole("button", { name: "Save schedule" })).toBeVisible();
  });
});

describe("the time zone", () => {
  it("says the time zone could not be saved when the server fails, and keeps Save for another try", async () => {
    openSystem("/system", { [SAVE]: systemReplies.settingsPutFails });
    fireEvent.click(await screen.findByRole("button", { name: "Time zone" }));
    fireEvent.click(screen.getByRole("option", { name: /Japan — Tokyo/ }));

    fireEvent.click(screen.getByTestId("schedule-save-timezone"));

    expect(await screen.findByText("Could not save settings.")).toBeVisible();
    expect(screen.getByTestId("schedule-save-timezone")).toBeEnabled();
  });

  it("is shown to a viewer with the picker off and no Save", async () => {
    openSystem("/system", { [ME]: VIEWER });

    const picker = await screen.findByRole("button", { name: "Time zone" });

    await waitFor(() => expect(picker).toBeDisabled());
    expect(screen.queryByTestId("schedule-save-timezone")).toBeNull();
  });
});

describe("the Backups tab for a viewer", () => {
  it("shows none of the controls that back up, download, restore or change the schedule", async () => {
    openSystem("/system?tab=backups", { [ME]: VIEWER });
    await screen.findByTestId("suite-settings-backup-tab");
    expect(weir.sent("GET", "/api/v1/auth/me")).toHaveLength(1);

    for (const name of [
      "Back up now",
      "Download settings",
      "Restore from file",
      "Save schedule",
    ]) {
      expect(screen.queryByRole("button", { name })).toBeNull();
    }
    expect(screen.queryByRole("heading", { name: "Schedule" })).toBeNull();
    expect(
      weir.sent("GET", "/api/v1/suite/configuration-backups"),
    ).toHaveLength(0);
  });
});
