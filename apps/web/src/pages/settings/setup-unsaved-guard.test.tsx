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
import type { AppSettings } from "../../lib/settings/types";
import { library } from "./tabs/libraries/library-test-fixtures";
import {
  serveWorkflows,
  signedIn,
  type WorkflowServer,
} from "./tabs/libraries/workflow-server";
import { featureFilms } from "./tabs/rules/rule-set-fixtures";

// The tabs here are the real ones, reading from a stand-in server at `fetch`: this file is about the guard that
// stops an edit being dropped when the person clicks away, with each tab's own way of holding an edit.

const SETTINGS: AppSettings = {
  setup_wizard_state: "completed",
  app_timezone: "UTC",
  log_retention_days: 30,
  activity_retention_days: 90,
  configuration_backup_enabled: false,
  configuration_backup_interval_hours: 24,
  configuration_backup_preferred_time: "02:00",
  configuration_backup_last_run_at: null,
  updated_at: "2026-04-11T00:00:00Z",
};

let server: WorkflowServer;
let client: QueryClient;

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
// waiting on a module to be compiled, which on a busy machine takes longer than a test waits for a page to appear.
beforeAll(async () => {
  await Promise.all([
    import("./setup-area-layout"),
    import("./tabs/libraries/libraries-tab"),
    import("./tabs/schedule/schedule-tab"),
    import("./tabs/rules/profiles-tab"),
    import("./tabs/rules/devices-tab"),
  ]);
}, 60_000);

beforeEach(() => {
  client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  server = serveWorkflows({
    workflows: [library({ id: 1, name: "Movies" })],
    ruleSets: [featureFilms],
    extra: { "GET /api/v1/suite/settings": SETTINGS },
  });
});

afterEach(() => {
  cleanup();
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
    <QueryClientProvider client={client}>
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

async function editMoviesWorkflow() {
  const row = await screen.findByTestId("processing-library-1");
  await signedIn(client);
  fireEvent.click(within(row).getByRole("button", { name: "Edit" }));
  const name = await screen.findByLabelText("Name");
  fireEvent.change(name, { target: { value: "Films" } });
  return name;
}

describe("a workflow edited in the real editor", () => {
  it("asks, naming the workflow, before another tab opens, and keeps the edit when told to stay", async () => {
    const router = await renderAt("/setup/workflows");
    const name = await editMoviesWorkflow();

    fireEvent.click(screen.getByRole("tab", { name: "Schedule" }));

    expect(warning()).toHaveTextContent(
      "You have unsaved changes to Movies. Leave without saving?",
    );
    keepEditing();
    expect(warning()).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/setup/workflows");
    expect(name).toHaveValue("Films");
  });

  it("asks before the side menu leaves setup, and Leave drops the edit", async () => {
    const router = await renderAt("/setup/workflows");
    await editMoviesWorkflow();

    fireEvent.click(screen.getByRole("link", { name: "Activity" }));
    expect(warning()).toHaveTextContent(
      "You have unsaved changes to Movies. Leave without saving?",
    );
    leave();

    expect(await screen.findByText("Activity page")).toBeVisible();
    expect(router.state.location.pathname).toBe("/activity");
    expect(server.to("PUT /api/v1/processing/libraries/1")).toHaveLength(0);

    await router.navigate("/setup/workflows");
    expect(await screen.findByText("Movies")).toBeVisible();
    expect(screen.queryByText("Films")).not.toBeInTheDocument();
    expect(
      screen.queryByTestId("processing-library-form"),
    ).not.toBeInTheDocument();
  });

  it("asks nothing once the edit is saved", async () => {
    const router = await renderAt("/setup/workflows");
    await editMoviesWorkflow();

    fireEvent.click(screen.getByTestId("processing-library-save"));
    await waitFor(() =>
      expect(
        screen.queryByTestId("processing-library-form"),
      ).not.toBeInTheDocument(),
    );
    // The editor tells the area it has nothing unsaved as it unmounts; let that land before the next click.
    await act(async () => undefined);
    fireEvent.click(screen.getByRole("tab", { name: "Schedule" }));

    await waitFor(() =>
      expect(router.state.location.pathname).toBe("/setup/workflows/schedule"),
    );
    expect(warning()).not.toBeInTheDocument();
  });

  it("asks nothing when the editor is open with no edit", async () => {
    const router = await renderAt("/setup/workflows");
    const row = await screen.findByTestId("processing-library-1");
    await signedIn(client);
    fireEvent.click(within(row).getByRole("button", { name: "Edit" }));
    await screen.findByLabelText("Name");

    fireEvent.click(screen.getByRole("tab", { name: "Schedule" }));

    await waitFor(() =>
      expect(router.state.location.pathname).toBe("/setup/workflows/schedule"),
    );
    expect(warning()).not.toBeInTheDocument();
  });
});

describe("a week of hours drawn in the real Schedule tab", () => {
  async function drawMondayHour() {
    const router = await renderAt("/setup/workflows/schedule");
    const [row] = await screen.findAllByTestId("schedule-library-row");
    await signedIn(client);
    fireEvent.click(within(row!).getByRole("button", { name: "Change hours" }));
    fireEvent.pointerDown(await screen.findByTestId("schedule-cell-0-9"));
    return router;
  }

  it("asks, naming the workflow's hours, before another tab opens", async () => {
    const router = await drawMondayHour();

    fireEvent.click(screen.getByRole("tab", { name: "File paths" }));

    expect(warning()).toHaveTextContent(
      "You have unsaved changes to Movies's hours. Leave without saving?",
    );
    keepEditing();
    expect(router.state.location.pathname).toBe("/setup/workflows/schedule");
    expect(screen.getByTestId("schedule-library-editor")).toBeInTheDocument();
  });

  it("asks before the side menu leaves setup, and Leave drops the week", async () => {
    const router = await drawMondayHour();

    fireEvent.click(screen.getByRole("link", { name: "Activity" }));
    expect(warning()).toHaveTextContent("Movies's hours");
    leave();

    expect(await screen.findByText("Activity page")).toBeVisible();
    expect(router.state.location.pathname).toBe("/activity");
    expect(server.to("PUT /api/v1/processing/libraries/1")).toHaveLength(0);
  });
});

describe("a profile edited in the real Profiles tab", () => {
  async function editProfile() {
    const router = await renderAt("/setup/rules");
    await screen.findByRole("heading", { name: "Audio" });
    await signedIn(client);
    fireEvent.click(
      screen.getByRole("checkbox", { name: /Keep the original language/ }),
    );
    return router;
  }

  it("asks, naming the profile, before another tab opens", async () => {
    const router = await editProfile();

    fireEvent.click(screen.getByRole("tab", { name: "Playback devices" }));

    expect(warning()).toHaveTextContent(
      "You have unsaved changes to Feature films. Leave without saving?",
    );
    keepEditing();
    expect(router.state.location.pathname).toBe("/setup/rules");
  });

  it("asks before the side menu leaves setup, and Leave drops the edit", async () => {
    const router = await editProfile();

    fireEvent.click(screen.getByRole("link", { name: "Activity" }));
    expect(warning()).toHaveTextContent("Feature films");
    leave();

    expect(await screen.findByText("Activity page")).toBeVisible();
    expect(router.state.location.pathname).toBe("/activity");
    expect(server.to("PUT /api/v1/processing/rule-sets/4")).toHaveLength(0);
  });
});

describe("a new profile opened in the real Profiles tab", () => {
  async function startNewProfile() {
    const router = await renderAt("/setup/rules");
    await screen.findByRole("heading", { name: "Audio" });
    await signedIn(client);
    fireEvent.click(screen.getByRole("button", { name: "New profile" }));
    await screen.findByRole("button", { name: "Create profile" });
    return router;
  }

  it("is unsaved from the moment it opens, so another tab asks, naming the new profile", async () => {
    const router = await startNewProfile();

    fireEvent.click(screen.getByRole("tab", { name: "Playback devices" }));

    expect(warning()).toHaveTextContent(
      "You have unsaved changes to the new profile. Leave without saving?",
    );
    keepEditing();
    expect(router.state.location.pathname).toBe("/setup/rules");
    expect(
      screen.getByRole("button", { name: "Create profile" }),
    ).toBeInTheDocument();
  });

  it("asks before the side menu leaves setup, and Leave drops it", async () => {
    const router = await startNewProfile();

    fireEvent.click(screen.getByRole("link", { name: "Activity" }));
    expect(warning()).toHaveTextContent("the new profile");
    leave();

    expect(await screen.findByText("Activity page")).toBeVisible();
    expect(router.state.location.pathname).toBe("/activity");
    expect(server.to("POST /api/v1/processing/rule-sets")).toHaveLength(0);
  });
});
