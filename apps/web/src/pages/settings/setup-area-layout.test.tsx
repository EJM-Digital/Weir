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
import { afterEach, describe, expect, it, vi } from "vitest";

import { setupRoutes } from "../../app/setup-routes";
import {
  ShellHeaderProvider,
  useHeaderButtonsSlotRef,
  useHeaderTabsSlotRef,
} from "../../components/shell/shell-header-context";

// Each tab's own content is tested beside it; this file is about the area around it: which tab is showing, how
// the address and the tab row follow each other, where the tab's buttons go, and when leaving asks first.
vi.mock("./tabs/libraries/libraries-tab", () => ({
  LibrariesTab: () => <div>Workflows content</div>,
}));
vi.mock("./tabs/schedule/schedule-tab", () => ({
  ScheduleTab: () => <div>Schedule content</div>,
}));
vi.mock("./tabs/media-managers/media-managers-tab", () => ({
  MediaManagersTab: () => <div>Media managers content</div>,
}));
vi.mock("./tabs/media-managers/download-clients-tab", () => ({
  DownloadClientsTab: () => <div>Download clients content</div>,
}));
vi.mock("./tabs/rules/profiles-tab", () => ({
  ProfilesTab: () => <div>Profiles content</div>,
}));
vi.mock("./tabs/rules/devices-tab", () => ({
  DevicesTab: () => <div>Devices content</div>,
}));
vi.mock("./tabs/performance/speed-tab", () => ({
  SpeedTab: () => <div>Speed content</div>,
}));
vi.mock("./tabs/cleanup/cleanup-tab", () => ({
  CleanupTab: () => <div>Cleanup content</div>,
}));
vi.mock("./tabs/performance/timers-tab", () => ({
  TimersTab: () => <div>Timers content</div>,
}));
// Alerts stands in for any tab with an edit not yet saved, and with a button for the toolbar.
const alerts = vi.hoisted(() => ({ unsaved: null as string | null }));
vi.mock("./tabs/alerts/alerts-tab", async () => {
  const { useUnsavedChanges } = await import("./unsaved-changes");
  const { PageToolbarAddButton } =
    await import("../../components/shell/page-toolbar-actions");
  return {
    AlertsTab: () => {
      useUnsavedChanges(alerts.unsaved);
      return (
        <div>
          Alerts content
          <PageToolbarAddButton label="Add alert" onClick={() => undefined} />
        </div>
      );
    },
  };
});

/** The shell's header, reduced to the place its tabs go. */
function TitleLine() {
  const tabsRef = useHeaderTabsSlotRef();
  const buttonsRef = useHeaderButtonsSlotRef();
  return (
    <div data-testid="title-line">
      <div ref={tabsRef} />
      <div data-testid="title-line-buttons" ref={buttonsRef} />
    </div>
  );
}

function stubWideWindow() {
  vi.stubGlobal("matchMedia", () => ({
    matches: true,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }));
}

async function renderAt(entry: string) {
  const router = createMemoryRouter(
    [
      ...setupRoutes(null),
      { path: "/activity", element: <div>Activity page</div> },
    ],
    { initialEntries: [entry] },
  );
  render(<RouterProvider router={router} />);
  await screen.findByRole("tablist");
  return router;
}

const tabNames = () =>
  within(screen.getByRole("tablist"))
    .getAllByRole("tab")
    .map((tab) => tab.textContent);

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  alerts.unsaved = null;
});

describe("a setup area", () => {
  it.each([
    ["/setup/workflows", "Workflows sections", ["File paths", "Schedule"]],
    [
      "/setup/connections",
      "Connections sections",
      ["Media managers", "Download clients", "Alerts"],
    ],
    ["/setup/rules", "Rules sections", ["Profiles", "Playback devices"]],
    [
      "/setup/performance",
      "Performance sections",
      ["Speed", "Cleanup", "Weir's timers"],
    ],
  ])("opens %s on a tab row of its own tabs", async (address, name, tabs) => {
    await renderAt(address);

    expect(screen.getByRole("tablist", { name })).toBeInTheDocument();
    expect(tabNames()).toEqual(tabs);
    expect(screen.getAllByRole("tab")[0]).toHaveAttribute(
      "aria-selected",
      "true",
    );
  });

  it("opens the tab the address names, so a bookmark lands where it was made", async () => {
    await renderAt("/setup/rules/devices");

    expect(
      screen.getByRole("tab", { name: "Playback devices" }),
    ).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tabpanel")).toHaveTextContent("Devices content");
  });

  it("sends the address of the retired Metadata & artwork tab to the area's first tab", async () => {
    const router = await renderAt("/setup/rules/metadata");

    await waitFor(() =>
      expect(router.state.location.pathname).toBe("/setup/rules"),
    );
    expect(await screen.findByText("Profiles content")).toBeVisible();
  });

  it("moves to a tab through the address, so Back and Forward follow it", async () => {
    const router = await renderAt("/setup/connections");

    fireEvent.click(screen.getByRole("tab", { name: "Download clients" }));
    expect(await screen.findByText("Download clients content")).toBeVisible();
    expect(router.state.location.pathname).toBe(
      "/setup/connections/download-clients",
    );

    await act(() => router.navigate(-1));
    expect(router.state.location.pathname).toBe("/setup/connections");
    expect(await screen.findByText("Media managers content")).toBeVisible();

    await act(() => router.navigate(1));
    expect(await screen.findByText("Download clients content")).toBeVisible();
  });

  it("names the panel after the open tab", async () => {
    await renderAt("/setup/performance/cleanup");

    expect(screen.getByRole("tabpanel", { name: "Cleanup" })).toHaveTextContent(
      "Cleanup content",
    );
  });

  it.each([
    ["/setup/workflows/workflows", "/setup/workflows", "Workflows content"],
    [
      "/setup/connections/managers",
      "/setup/connections",
      "Media managers content",
    ],
    ["/setup/rules/profiles", "/setup/rules", "Profiles content"],
    ["/setup/performance/speed", "/setup/performance", "Speed content"],
  ])(
    "sends a first tab's own name back to the area's address (%s)",
    async (address, areaAddress, content) => {
      const router = await renderAt(address);

      expect(router.state.location.pathname).toBe(areaAddress);
      expect(await screen.findByText(content)).toBeVisible();
      expect(screen.getAllByRole("tab")[0]).toHaveAttribute(
        "aria-selected",
        "true",
      );
    },
  );

  it("leaves /setup itself to the first sign-in", () => {
    const router = createMemoryRouter(
      [
        { path: "/setup", element: <div>First sign-in</div> },
        ...setupRoutes(null),
      ],
      { initialEntries: ["/setup"] },
    );
    render(<RouterProvider router={router} />);

    expect(screen.getByText("First sign-in")).toBeInTheDocument();
  });

  it("puts the open tab's button beside the tabs in a narrow window, not in the panel", async () => {
    await renderAt("/setup/connections/alerts");

    const add = await screen.findByRole("button", { name: "Add alert" });
    const row = screen.getByRole("navigation", { name: "Page tabs" })
      .parentElement as HTMLElement;
    expect(row).toContainElement(add);
    expect(screen.getByRole("tabpanel")).not.toContainElement(add);
  });

  it("puts the tabs on the header's title line in a wide window, with the button at the right of that line", async () => {
    stubWideWindow();
    const router = createMemoryRouter(setupRoutes(null), {
      initialEntries: ["/setup/connections/alerts"],
    });
    render(
      <ShellHeaderProvider>
        <TitleLine />
        <RouterProvider router={router} />
      </ShellHeaderProvider>,
    );

    const add = await screen.findByRole("button", { name: "Add alert" });
    expect(
      within(screen.getByTestId("title-line")).getByRole("tablist", {
        name: "Connections sections",
      }),
    ).toBeInTheDocument();
    expect(screen.getByTestId("title-line-buttons")).toContainElement(add);
    expect(screen.getByRole("tabpanel")).not.toContainElement(add);
  });

  it("takes the button away when another tab opens", async () => {
    await renderAt("/setup/connections/alerts");
    await screen.findByRole("button", { name: "Add alert" });

    fireEvent.click(screen.getByRole("tab", { name: "Media managers" }));

    expect(await screen.findByText("Media managers content")).toBeVisible();
    expect(screen.queryByRole("button", { name: "Add alert" })).toBeNull();
  });
});

describe("with unsaved changes in a tab", () => {
  it("asks before moving to another tab, and stays when told to", async () => {
    alerts.unsaved = "the Discord alert";
    const router = await renderAt("/setup/connections/alerts");

    fireEvent.click(screen.getByRole("tab", { name: "Media managers" }));

    expect(screen.getByTestId("settings-unsaved-changes")).toHaveTextContent(
      "You have unsaved changes to the Discord alert. Leave without saving?",
    );
    fireEvent.click(screen.getByTestId("settings-unsaved-changes-cancel"));
    expect(router.state.location.pathname).toBe("/setup/connections/alerts");
    expect(screen.getByText(/Alerts content/)).toBeInTheDocument();
  });

  it("moves on once the person chooses to leave without saving", async () => {
    alerts.unsaved = "the Discord alert";
    const router = await renderAt("/setup/connections/alerts");

    fireEvent.click(screen.getByRole("tab", { name: "Media managers" }));
    await act(async () => {
      fireEvent.click(screen.getByTestId("settings-unsaved-changes-confirm"));
    });

    expect(router.state.location.pathname).toBe("/setup/connections");
    expect(await screen.findByText("Media managers content")).toBeVisible();
  });

  it("asks before moving to another area", async () => {
    alerts.unsaved = "the Discord alert";
    const router = await renderAt("/setup/connections/alerts");

    await act(() => router.navigate("/setup/rules"));

    expect(router.state.location.pathname).toBe("/setup/connections/alerts");
    expect(screen.getByTestId("settings-unsaved-changes")).toBeInTheDocument();
  });

  it("asks before leaving setup for another page", async () => {
    alerts.unsaved = "the Discord alert";
    const router = await renderAt("/setup/connections/alerts");

    await act(() => router.navigate("/activity"));

    expect(router.state.location.pathname).toBe("/setup/connections/alerts");
    expect(screen.getByTestId("settings-unsaved-changes")).toBeInTheDocument();
  });

  it("does not ask when only the query changes, as when a link opens an editor", async () => {
    alerts.unsaved = "the Discord alert";
    const router = await renderAt("/setup/connections/alerts");

    await act(() => router.navigate("/setup/connections/alerts?edit=2"));

    expect(screen.queryByTestId("settings-unsaved-changes")).toBeNull();
  });

  it("does not ask when nothing is unsaved", async () => {
    const router = await renderAt("/setup/connections/alerts");

    await act(() => router.navigate("/setup/rules"));

    expect(router.state.location.pathname).toBe("/setup/rules");
    expect(await screen.findByText("Profiles content")).toBeVisible();
  });
});
