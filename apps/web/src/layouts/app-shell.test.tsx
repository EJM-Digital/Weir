import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { PageLoading } from "../components/shared/page-loading";
import { PageHeader } from "../components/shell/page-header";
import { ShellHeaderSlot } from "../components/shell/shell-header-context";
import {
  reportLiveConnection,
  resetLiveConnection,
} from "../lib/live/live-connection";
import { LOST_CONNECTION_GRACE_MS } from "../lib/live/use-live-updates-paused";
import { AppShell } from "./app-shell";

const useLiveSync = vi.fn();
vi.mock("../lib/live/use-live-sync", () => ({
  useLiveSync: () => useLiveSync(),
}));

const logoutMutate = vi.fn();
const session = { id: "eaa2d170-0a7a-4499-84d0-97a4f35174c3" };
const scrollToMock = vi.fn();

vi.mock("../lib/auth/queries", () => ({
  useLogoutMutation: () => ({
    mutate: logoutMutate,
    isPending: false,
  }),
  // The header carries the pause control, which needs to know whether the signed-in person may
  // change it, and the side menu names them.
  useMeQuery: () => ({ data: { role: "operator", username: "ann.lee" } }),
  useCurrentSessionQuery: () => ({ data: { session_id: session.id } }),
  useSetThemeMutation: () => ({ mutate: vi.fn(), isError: false }),
}));

const pauseState = {
  paused: false,
  paused_until: null as string | null,
  scan_while_paused: true,
  reason: "",
  in_flight_policy: "Work already running finishes.",
};
vi.mock("../lib/pause/pause-queries", () => ({
  usePauseQuery: () => ({ data: pauseState }),
  useSavePause: () => ({ mutate: vi.fn(), isPending: false }),
}));

const readiness = { isError: false };
vi.mock("../lib/ui/mm-format-date", async (importActual) => ({
  ...(await importActual<typeof import("../lib/ui/mm-format-date")>()),
  useAppDateFormatter: () => (iso: string | null | undefined) => String(iso),
  useAppClockFormatter: () => () => "4:20 pm",
}));
vi.mock("../lib/system/readiness-queries", () => ({
  useSystemReadinessQuery: () => ({
    isError: readiness.isError,
    data: {
      version: "2.1.2",
      machine_name: "RIG",
    },
  }),
}));

// The Dashboard entry says how many cards the Working lane is showing; Activity says how many files
// need someone.
const counts = { working: 0, needsYou: 0 };
vi.mock("../pages/processing/working-count", () => ({
  useWorkingCount: () => counts.working,
}));
vi.mock("../pages/processing/dashboard/use-needs-you", () => ({
  useNeedsYou: () => ({
    groups: [],
    files: Array.from({ length: counts.needsYou }),
  }),
}));

function renderShell(
  entry: string,
  pages = <Route index element={<div>Main</div>} />,
) {
  return render(
    <MemoryRouter initialEntries={[entry]}>
      <Routes>
        <Route path="/" element={<AppShell />}>
          {pages}
        </Route>
      </Routes>
    </MemoryRouter>,
  );
}

const primaryNav = () => screen.getByRole("navigation", { name: "Primary" });

describe("AppShell", () => {
  beforeEach(() => {
    logoutMutate.mockReset();
    session.id = "eaa2d170-0a7a-4499-84d0-97a4f35174c3";
    scrollToMock.mockReset();
    window.scrollTo = scrollToMock;
    counts.working = 0;
    counts.needsYou = 0;
    pauseState.paused = false;
    pauseState.reason = "";
    readiness.isError = false;
  });

  it("opens on the Dashboard; there is no Home, Processing or History entry", () => {
    renderShell("/");

    expect(screen.getByRole("link", { name: "Dashboard" })).toHaveAttribute(
      "href",
      "/",
    );
    for (const retired of ["Home", "Processing", "History"]) {
      expect(
        screen.queryByRole("link", { name: retired }),
      ).not.toBeInTheDocument();
    }
  });

  it("sends every nav item to the screen its label names, and has no others", () => {
    renderShell("/");

    const items = within(primaryNav())
      .getAllByRole("link")
      .map((link) => [link.textContent, link.getAttribute("href")]);

    // The whole nav, in order. A new entry has to be added here deliberately, and a label that
    // stops matching its destination fails rather than quietly misleading someone.
    expect(items).toEqual([
      ["Dashboard", "/"],
      ["Activity", "/activity"],
      ["Library", "/library"],
      ["Workflows", "/setup/workflows"],
      ["Connections", "/setup/connections"],
      ["Rules", "/setup/rules"],
      ["Performance", "/setup/performance"],
      ["System", "/system"],
    ]);
  });

  it("groups the menu under Live, Your library, Setup and Weir", () => {
    renderShell("/");

    expect(
      within(primaryNav())
        .getAllByRole("group")
        .map((group) => group.getAttribute("aria-labelledby"))
        .map((id) => document.getElementById(id ?? "")?.textContent),
    ).toEqual(["Live", "Your library", "Setup", "Weir"]);
  });

  it("marks only the current screen, and marks nothing on a page that is not one", () => {
    const pages = (
      <>
        <Route index element={<div>Dashboard</div>} />
        <Route path="library" element={<div>Library</div>} />
        <Route path="*" element={<div>Not found</div>} />
      </>
    );
    const current = () =>
      within(primaryNav())
        .getAllByRole("link")
        .filter((link) => link.getAttribute("aria-current") === "page")
        .map((link) => link.textContent);

    const { unmount } = renderShell("/library", pages);
    expect(current()).toEqual(["Library"]);
    unmount();

    // The Dashboard is the index route at `/`; `/dashboard` is the Not found page (#585), so no entry
    // claims to be the screen you are on.
    renderShell("/dashboard", pages);
    expect(current()).toEqual([]);
  });

  it("marks the setup area the address names, whichever of its tabs is open", () => {
    const pages = <Route path="setup/*" element={<div>Setup</div>} />;
    const current = () =>
      within(primaryNav())
        .getAllByRole("link")
        .filter((link) => link.getAttribute("aria-current") === "page")
        .map((link) => link.textContent);

    const { unmount } = renderShell("/setup/workflows", pages);
    expect(current()).toEqual(["Workflows"]);
    unmount();

    const second = renderShell("/setup/workflows/schedule", pages);
    expect(current()).toEqual(["Workflows"]);
    second.unmount();

    renderShell("/setup/connections/alerts?library=3", pages);
    expect(current()).toEqual(["Connections"]);
  });

  it("shows how many files the Working lane holds beside the Dashboard, and nothing when none are", () => {
    counts.working = 2;
    const pages = <Route path="library" element={<div>Library</div>} />;
    const view = renderShell("/library", pages);
    const live = screen.getByRole("link", { name: "Dashboard, 2 working" });
    expect(
      within(live).getByTestId("nav-processing-working"),
    ).toHaveTextContent("2");

    counts.working = 0;
    view.rerender(
      <MemoryRouter initialEntries={["/library"]}>
        <Routes>
          <Route path="/" element={<AppShell />}>
            {pages}
          </Route>
        </Routes>
      </MemoryRouter>,
    );
    expect(screen.queryByTestId("nav-processing-working")).toBeNull();
    expect(screen.getByRole("link", { name: "Dashboard" })).toBeInTheDocument();
  });

  it("shows how many files need you beside Activity, and nothing when none do", () => {
    counts.needsYou = 3;
    const { unmount } = renderShell("/");
    const activity = screen.getByRole("link", { name: "Activity, 3 need you" });
    expect(
      within(activity).getByTestId("nav-activity-needs-you"),
    ).toHaveTextContent("3");
    unmount();

    counts.needsYou = 0;
    renderShell("/");
    expect(screen.queryByTestId("nav-activity-needs-you")).toBeNull();
  });

  it("opens Activity's Needs you view from Activity while files need you, and Activity itself when none do", () => {
    counts.needsYou = 3;
    const { unmount } = renderShell("/");
    expect(
      screen.getByRole("link", { name: "Activity, 3 need you" }),
    ).toHaveAttribute("href", "/activity?show=attention");
    unmount();

    counts.needsYou = 0;
    renderShell("/");
    expect(screen.getByRole("link", { name: "Activity" })).toHaveAttribute(
      "href",
      "/activity",
    );
  });

  it("keeps the Dashboard's link on the Dashboard while files are working", () => {
    counts.working = 2;
    renderShell("/library", <Route path="library" element={<div />} />);

    expect(
      screen.getByRole("link", { name: "Dashboard, 2 working" }),
    ).toHaveAttribute("href", "/");
  });

  it("collapses to icons when asked, and expands again", () => {
    renderShell("/");
    const sidebar = document.getElementById("mm-primary-sidebar");
    const toggle = screen.getByTestId("sidebar-collapse");
    expect(sidebar).not.toHaveClass("mm-sidebar--collapsed");
    fireEvent.click(toggle);
    expect(sidebar).toHaveClass("mm-sidebar--collapsed");
    expect(toggle).toHaveAttribute("aria-label", "Expand navigation");
    fireEvent.click(toggle);
    expect(sidebar).not.toHaveClass("mm-sidebar--collapsed");
  });

  it("names Weir after the machine in the browser tab and the sidebar", () => {
    renderShell("/");

    expect(document.title).toBe("Weir · RIG");
    expect(
      screen.getByRole("complementary", { name: "Weir · RIG" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("link", { name: "Weir · RIG home" }),
    ).toBeInTheDocument();
  });

  it("shows Weir and the computer it runs on at the top of the menu, as Deluno does", () => {
    renderShell("/");

    const brand = screen.getByRole("link", { name: "Weir · RIG home" });
    expect(brand).toHaveTextContent("Weir");
    expect(brand).toHaveTextContent("RIG");
    expect(brand).not.toHaveTextContent("Media cleaner");
  });

  it("shows who is signed in, and only that, at the foot of the menu", () => {
    renderShell("/");

    const user = screen.getByTestId("user-menu");
    expect(user).toHaveTextContent("AL");
    expect(user).toHaveTextContent("ann.lee");
    expect(user).not.toHaveTextContent("@operator");
    expect(user).not.toHaveTextContent("Weir on RIG");
  });

  it("signs out from the user menu, and keeps the version beside it", () => {
    renderShell("/");

    expect(screen.queryByTestId("sign-out")).not.toBeInTheDocument();
    fireEvent.click(screen.getByTestId("user-menu"));
    expect(screen.getByText("Version 2.1.2")).toBeInTheDocument();
    fireEvent.click(screen.getByTestId("sign-out"));

    expect(logoutMutate).toHaveBeenCalledTimes(1);
  });

  it("offers no Sign out to a device that skipped sign-in, since it never signed in", () => {
    session.id = "home-network";
    renderShell("/");

    fireEvent.click(screen.getByTestId("user-menu"));

    expect(screen.getByText("Version 2.1.2")).toBeInTheDocument();
    expect(screen.queryByTestId("sign-out")).not.toBeInTheDocument();
  });

  it("closes the user menu on Escape", () => {
    renderShell("/");
    fireEvent.click(screen.getByTestId("user-menu"));

    fireEvent.keyDown(document, { key: "Escape" });

    expect(screen.queryByTestId("sign-out")).not.toBeInTheDocument();
  });

  it("moves focus into the phone menu, closes it on Escape and hands focus back to Menu", () => {
    renderShell("/");
    const menu = screen.getByTestId("shell-nav-toggle");
    menu.focus();

    fireEvent.click(menu);
    expect(screen.getByRole("link", { name: "Dashboard" })).toHaveFocus();

    fireEvent.keyDown(document, { key: "Escape" });
    expect(document.getElementById("mm-primary-sidebar")).not.toHaveClass(
      "mm-sidebar--open",
    );
    expect(menu).toHaveFocus();
  });

  it("shows a screen that is still loading inside the one main landmark", () => {
    renderShell(
      "/",
      <Route index element={<PageLoading label="Loading the Dashboard" />} />,
    );

    const main = screen.getByRole("main");
    expect(screen.getAllByRole("main")).toHaveLength(1);
    expect(within(main).getByRole("status")).toHaveTextContent(
      "Loading the Dashboard",
    );
  });

  it("returns document scrolling to the top when the route changes", () => {
    renderShell(
      "/",
      <>
        <Route index element={<div>Dashboard page</div>} />
        <Route path="library" element={<div>Library page</div>} />
      </>,
    );

    fireEvent.click(screen.getByRole("link", { name: "Library" }));

    expect(screen.getByText("Library page")).toBeInTheDocument();
    expect(scrollToMock).toHaveBeenLastCalledWith(0, 0);
  });
});

describe("the shell's header", () => {
  afterEach(() => {
    resetLiveConnection();
    vi.useRealTimers();
  });

  beforeEach(() => {
    counts.working = 0;
    counts.needsYou = 0;
    pauseState.paused = false;
    pauseState.reason = "";
    readiness.isError = false;
  });

  it("names the page: its eyebrow, and its title as the one heading", () => {
    renderShell("/");

    expect(
      screen.getByRole("heading", { level: 1, name: "Dashboard" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText("Cleans new downloads and your library"),
    ).toBeInTheDocument();
  });

  it("titles a setup area by its name, with the area's own eyebrow, on any of its tabs", () => {
    renderShell(
      "/setup/connections/alerts",
      <Route path="setup/*" element={<div>Setup</div>} />,
    );

    expect(
      screen.getByRole("heading", { level: 1, name: "Connections" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        "The apps Weir hands cleaned files back to, and who it tells",
      ),
    ).toBeInTheDocument();
  });

  it("leaves the heading to a page that is not one of the menu's", () => {
    renderShell(
      "/nowhere",
      <Route path="*" element={<h1>This page doesn&apos;t exist.</h1>} />,
    );

    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    expect(
      screen.getByRole("heading", {
        level: 1,
        name: "This page doesn't exist.",
      }),
    ).toBeInTheDocument();
  });

  it("lets a page replace the eyebrow with a line of its own, and puts the menu's back when it leaves", () => {
    const view = renderShell(
      "/",
      <Route index element={<PageHeader eyebrow="Two files at once" />} />,
    );
    expect(screen.getByText("Two files at once")).toBeInTheDocument();
    expect(
      screen.queryByText("Cleans new downloads and your library"),
    ).not.toBeInTheDocument();

    view.unmount();
    renderShell("/");
    expect(
      screen.getByText("Cleans new downloads and your library"),
    ).toBeInTheDocument();
  });

  it("puts a page's own control in the header, beside the title", () => {
    renderShell(
      "/",
      <Route
        index
        element={
          <ShellHeaderSlot>
            <button type="button">Everything</button>
          </ShellHeaderSlot>
        }
      />,
    );

    const header = screen.getByTestId("shell-header");
    expect(
      within(header).getByRole("button", { name: "Everything" }),
    ).toBeInTheDocument();
  });

  it("carries Pause and the theme switch", () => {
    renderShell("/");

    const header = screen.getByTestId("shell-header");
    expect(within(header).getByTestId("pause-open")).toHaveTextContent(
      "Pause processing",
    );
    expect(within(header).getByTestId("theme-toggle")).toBeInTheDocument();
  });

  it("shows no status while all is well", () => {
    renderShell("/");

    expect(screen.queryByTestId("pause-badge")).not.toBeInTheDocument();
    expect(screen.queryByTestId("status-offline")).not.toBeInTheDocument();
  });

  it("says Paused, and until when, while processing is paused, with Resume in place of Pause", () => {
    pauseState.paused = true;
    renderShell("/");

    expect(screen.getByTestId("pause-badge")).toHaveTextContent(
      "Paused · until you resume",
    );
    expect(screen.getByTestId("pause-resume")).toHaveTextContent(
      "Resume processing",
    );
    expect(screen.queryByTestId("pause-open")).not.toBeInTheDocument();
  });

  it("keeps every screen current from the live stream", () => {
    renderShell("/");

    expect(useLiveSync).toHaveBeenCalled();
  });

  it("says live updates are paused while the connection is lost, under the header and above the page", () => {
    vi.useFakeTimers();
    renderShell("/");
    act(() => void reportLiveConnection("opened"));
    expect(
      screen.queryByTestId("live-connection-banner"),
    ).not.toBeInTheDocument();

    act(() => void reportLiveConnection("dropped"));
    act(() => void vi.advanceTimersByTime(LOST_CONNECTION_GRACE_MS));

    const banner = screen.getByTestId("live-connection-banner");
    expect(banner).toHaveTextContent("Live updates paused");
    expect(
      screen.getByTestId("shell-header").compareDocumentPosition(banner),
    ).toBe(Node.DOCUMENT_POSITION_FOLLOWING);
    expect(banner.compareDocumentPosition(screen.getByRole("main"))).toBe(
      Node.DOCUMENT_POSITION_FOLLOWING,
    );
  });

  it("says Weir cannot be reached when it does not answer", () => {
    readiness.isError = true;
    renderShell("/");

    expect(screen.getByTestId("status-offline")).toHaveTextContent(
      "Can't reach Weir",
    );
  });
});
