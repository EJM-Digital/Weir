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
import {
  Link,
  Outlet,
  RouterProvider,
  createMemoryRouter,
} from "react-router-dom";
import {
  afterEach,
  beforeAll,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from "vitest";

import { setupRoutes } from "../../app/setup-routes";
import { adminMe, csrfToken } from "../../test/recorded-admin-session";
import { systemReplies } from "../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../test/recorded-weir";
import { oneChannel, tickSaved } from "./tabs/alerts/alert-replies";
import { maintenance } from "./tabs/cleanup/cleanup-replies";
import { connectionList } from "./tabs/media-managers/download-client-replies";
import {
  filesAtOnceNothingWaiting,
  operatorSettings,
  operatorSettingsSavedAsFour,
} from "./tabs/performance/performance-replies";

// The tabs here are the real ones, reading from recorded replies at `fetch`: this file is about the guard that stops
// an edit being dropped when the person clicks away, with each tab's own way of holding an edit.

const OPERATOR_SETTINGS = "/api/v1/processing/operator-settings";
const CHANNELS = "/api/v1/suite/notification-channels";
const DOWNLOAD_CLIENTS = "/api/v1/download-clients/connections";

let weir: RecordedWeir | undefined;

function serve(routes: RecordedRoutes): RecordedWeir {
  weir = serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    "GET /api/v1/auth/me": adminMe,
    "GET /api/v1/suite/settings": systemReplies.settings,
    ...routes,
  });
  return weir;
}

/** The shell around setup: a side menu with one link out of setup, and the page that link opens. */
function Shell() {
  return (
    <>
      <nav aria-label="Side menu">
        <Link to="/activity">Activity</Link>
      </nav>
      <Outlet />
    </>
  );
}

// Each page of setup is fetched when it is first opened. Fetching them here keeps the first test to open one from
// waiting on a module to be compiled.
beforeAll(async () => {
  await Promise.all([
    import("./setup-area-layout"),
    import("./tabs/performance/speed-tab"),
    import("./tabs/performance/timers-tab"),
    import("./tabs/cleanup/cleanup-tab"),
    import("./tabs/alerts/alerts-tab"),
    import("./tabs/media-managers/download-clients-tab"),
  ]);
}, 60_000);

beforeEach(() => localStorage.clear());

afterEach(() => {
  cleanup();
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
});

async function renderAt(entry: string) {
  const router = createMemoryRouter(
    [
      {
        element: <Shell />,
        children: [
          ...setupRoutes(null),
          { path: "activity", element: <div>Activity page</div> },
        ],
      },
    ],
    { initialEntries: [entry] },
  );
  render(
    <QueryClientProvider
      client={
        new QueryClient({
          defaultOptions: {
            queries: { retry: false },
            mutations: { retry: false },
          },
        })
      }
    >
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  await screen.findByRole("tablist");
  return router;
}

const warning = () => screen.queryByTestId("settings-unsaved-changes");
const keepEditing = () =>
  fireEvent.click(screen.getByTestId("settings-unsaved-changes-cancel"));
const leave = () =>
  fireEvent.click(screen.getByTestId("settings-unsaved-changes-confirm"));

describe("a Speed setting that was changed and not saved", () => {
  const speedRoutes = {
    [`GET ${OPERATOR_SETTINGS}`]: operatorSettings,
    "GET /api/v1/processing/files-at-once": filesAtOnceNothingWaiting,
    "GET /api/v1/processing/maintenance": maintenance,
  };

  async function pickFour() {
    const router = await renderAt("/setup/performance");
    fireEvent.click(await screen.findByRole("button", { name: "4" }));
    return router;
  }

  it("asks, naming the settings, before another tab opens, and keeps the change when told to stay", async () => {
    serve(speedRoutes);
    const router = await pickFour();

    fireEvent.click(screen.getByRole("tab", { name: "Cleanup" }));

    expect(warning()).toHaveTextContent(
      "You have unsaved changes to your speed settings. Leave without saving?",
    );
    keepEditing();
    expect(warning()).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/setup/performance");
    expect(screen.getByRole("button", { name: "4" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
  });

  it("asks before the side menu leaves setup, and Leave drops the change without saving it", async () => {
    const server = serve(speedRoutes);
    const router = await pickFour();

    fireEvent.click(screen.getByRole("link", { name: "Activity" }));
    expect(warning()).toHaveTextContent("your speed settings");
    leave();

    expect(await screen.findByText("Activity page")).toBeVisible();
    expect(router.state.location.pathname).toBe("/activity");
    expect(server.sent("PUT", OPERATOR_SETTINGS)).toEqual([]);
  });

  it("asks nothing once it is saved", async () => {
    serve({
      ...speedRoutes,
      [`GET ${OPERATOR_SETTINGS}`]: [
        operatorSettings,
        operatorSettingsSavedAsFour,
      ],
      [`PUT ${OPERATOR_SETTINGS}`]: operatorSettingsSavedAsFour,
    });
    const router = await pickFour();

    fireEvent.click(
      screen.getByRole("button", { name: "Save performance settings" }),
    );
    await waitFor(() =>
      expect(
        screen.queryByRole("button", { name: "Save performance settings" }),
      ).not.toBeInTheDocument(),
    );
    // The panel tells the area it has nothing unsaved as it is replaced; let that land before the next click.
    await act(async () => undefined);
    fireEvent.click(screen.getByRole("tab", { name: "Cleanup" }));

    await waitFor(() =>
      expect(router.state.location.pathname).toBe("/setup/performance/cleanup"),
    );
    expect(warning()).not.toBeInTheDocument();
  });

  it("asks nothing when nothing was changed", async () => {
    serve(speedRoutes);
    const router = await renderAt("/setup/performance");
    await screen.findByRole("button", { name: "1", pressed: true });

    fireEvent.click(screen.getByRole("tab", { name: "Cleanup" }));

    await waitFor(() =>
      expect(router.state.location.pathname).toBe("/setup/performance/cleanup"),
    );
    expect(warning()).not.toBeInTheDocument();
  });
});

describe("the days cleaned copies wait, typed and not saved", () => {
  const cleanupRoutes = {
    [`GET ${OPERATOR_SETTINGS}`]: operatorSettings,
    "GET /api/v1/processing/maintenance": maintenance,
  };
  const DAYS = "Cleaned copies nobody picked up wait for";

  it("asks, naming the setting, before another tab opens, and keeps what was typed when told to stay", async () => {
    serve(cleanupRoutes);
    const router = await renderAt("/setup/performance/cleanup");
    fireEvent.change(await screen.findByLabelText(DAYS), {
      target: { value: "30" },
    });

    fireEvent.click(screen.getByRole("tab", { name: "Weir's timers" }));

    expect(warning()).toHaveTextContent(
      "You have unsaved changes to the days cleaned copies nobody picked up wait. Leave without saving?",
    );
    keepEditing();
    expect(router.state.location.pathname).toBe("/setup/performance/cleanup");
    expect(screen.getByLabelText(DAYS)).toHaveValue(30);
  });

  it("asks nothing when the number is back to the saved one", async () => {
    serve(cleanupRoutes);
    const router = await renderAt("/setup/performance/cleanup");
    const days = await screen.findByLabelText(DAYS);
    fireEvent.change(days, { target: { value: "30" } });
    fireEvent.change(days, { target: { value: "14" } });

    fireEvent.click(screen.getByRole("tab", { name: "Weir's timers" }));

    await waitFor(() =>
      expect(router.state.location.pathname).toBe("/setup/performance/timers"),
    );
    expect(warning()).not.toBeInTheDocument();
  });
});

describe("an alert edited or added and not saved", () => {
  const alertRoutes = { [`GET ${CHANNELS}`]: oneChannel };

  async function editFirstAlert() {
    const router = await renderAt("/setup/connections/alerts");
    const row = (await screen.findByText("Discord alerts")).closest("tr");
    fireEvent.click(
      within(row as HTMLElement).getByRole("button", { name: "Edit" }),
    );
    fireEvent.change(await screen.findByLabelText("Label"), {
      target: { value: "Discord ops" },
    });
    return router;
  }

  it("asks, naming the alert, before another tab opens, and keeps the edit when told to stay", async () => {
    serve(alertRoutes);
    const router = await editFirstAlert();

    fireEvent.click(screen.getByRole("tab", { name: "Download clients" }));

    expect(warning()).toHaveTextContent(
      "You have unsaved changes to the alert Discord alerts. Leave without saving?",
    );
    keepEditing();
    expect(router.state.location.pathname).toBe("/setup/connections/alerts");
    expect(screen.getByLabelText("Label")).toHaveValue("Discord ops");
  });

  it("asks before Cancel drops an edit, and Cancel closes at once when nothing was changed", async () => {
    const server = serve(alertRoutes);
    await editFirstAlert();

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(warning()).toHaveTextContent("the alert Discord alerts");
    leave();

    expect(screen.queryByLabelText("Label")).not.toBeInTheDocument();
    expect(server.sent("PUT", `${CHANNELS}/1`)).toEqual([]);

    const row = screen.getByText("Discord alerts").closest("tr");
    fireEvent.click(
      within(row as HTMLElement).getByRole("button", { name: "Edit" }),
    );
    fireEvent.click(await screen.findByRole("button", { name: "Cancel" }));
    expect(warning()).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Label")).not.toBeInTheDocument();
  });

  it("asks nothing once the edit is saved", async () => {
    serve({
      ...alertRoutes,
      [`PUT ${CHANNELS}/1`]: tickSaved,
      [`GET ${DOWNLOAD_CLIENTS}`]: connectionList,
    });
    const router = await editFirstAlert();

    fireEvent.click(screen.getByRole("button", { name: "Save alert" }));
    await waitFor(() =>
      expect(screen.queryByLabelText("Label")).not.toBeInTheDocument(),
    );
    fireEvent.click(screen.getByRole("tab", { name: "Download clients" }));

    await waitFor(() =>
      expect(router.state.location.pathname).toBe(
        "/setup/connections/download-clients",
      ),
    );
    expect(warning()).not.toBeInTheDocument();
  });

  describe("a new alert in the drawer", () => {
    async function typeNewAlert() {
      const router = await renderAt("/setup/connections/alerts");
      fireEvent.click(await screen.findByRole("button", { name: /Add alert/ }));
      fireEvent.change(await screen.findByLabelText("Label"), {
        target: { value: "Pager" },
      });
      return router;
    }

    it("asks before another tab opens", async () => {
      serve(alertRoutes);
      const router = await typeNewAlert();

      fireEvent.click(screen.getByRole("tab", { name: "Download clients" }));

      expect(warning()).toHaveTextContent(
        "You have unsaved changes to your new alert. Leave without saving?",
      );
      keepEditing();
      expect(router.state.location.pathname).toBe("/setup/connections/alerts");
    });

    it.each([
      ["Escape", () => fireEvent.keyDown(document, { key: "Escape" })],
      [
        "the backdrop",
        () => {
          const backdrop = document.querySelector(".mm-drawer-backdrop");
          if (!backdrop) throw new Error("no backdrop");
          fireEvent.click(backdrop);
        },
      ],
      [
        "the x",
        () =>
          fireEvent.click(
            within(screen.getByTestId("alert-add-panel")).getByRole("button", {
              name: "Close",
            }),
          ),
      ],
    ])(
      "asks before %s drops it, and Keep editing leaves the drawer as it was",
      async (_, close) => {
        const server = serve(alertRoutes);
        await typeNewAlert();

        close();
        expect(warning()).toHaveTextContent("your new alert");
        keepEditing();

        expect(screen.getByTestId("alert-add-panel")).toBeInTheDocument();
        expect(screen.getByLabelText("Label")).toHaveValue("Pager");

        close();
        leave();
        expect(screen.queryByTestId("alert-add-panel")).not.toBeInTheDocument();
        expect(server.sent("POST", CHANNELS)).toEqual([]);
      },
    );

    it("closes at once when nothing was typed", async () => {
      serve(alertRoutes);
      await renderAt("/setup/connections/alerts");
      fireEvent.click(await screen.findByRole("button", { name: /Add alert/ }));
      await screen.findByLabelText("Label");

      fireEvent.keyDown(document, { key: "Escape" });

      expect(warning()).not.toBeInTheDocument();
      expect(screen.queryByTestId("alert-add-panel")).not.toBeInTheDocument();
    });
  });
});

describe("a download client edited and not saved", () => {
  async function editFirstClient() {
    const router = await renderAt("/setup/connections/download-clients");
    fireEvent.click(
      await screen
        .findAllByTestId("download-client-edit")
        .then((all) => all[0]),
    );
    fireEvent.change(
      await screen.findByTestId("download-client-edit-base-url"),
      {
        target: { value: "http://localhost:18083" },
      },
    );
    return router;
  }

  it("asks, naming the download client, before another tab opens, and keeps the edit when told to stay", async () => {
    serve({ [`GET ${DOWNLOAD_CLIENTS}`]: connectionList });
    const router = await editFirstClient();

    fireEvent.click(screen.getByRole("tab", { name: "Alerts" }));

    expect(warning()).toHaveTextContent(
      "You have unsaved changes to SABnzbd on localhost. Leave without saving?",
    );
    keepEditing();
    expect(router.state.location.pathname).toBe(
      "/setup/connections/download-clients",
    );
    expect(screen.getByTestId("download-client-edit-base-url")).toHaveValue(
      "http://localhost:18083",
    );
  });

  it("asks before the side menu leaves setup, and Leave drops the edit", async () => {
    const server = serve({ [`GET ${DOWNLOAD_CLIENTS}`]: connectionList });
    const router = await editFirstClient();

    fireEvent.click(screen.getByRole("link", { name: "Activity" }));
    expect(warning()).toHaveTextContent("SABnzbd on localhost");
    leave();

    expect(await screen.findByText("Activity page")).toBeVisible();
    expect(router.state.location.pathname).toBe("/activity");
    expect(server.sent("PUT", `${DOWNLOAD_CLIENTS}/1`)).toEqual([]);
  });
});
