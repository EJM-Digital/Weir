import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { systemReplies } from "../test/recorded-system";
import { jsonAnswer, stubWeirApi } from "../test/stub-weir-api";
import { held } from "../test/held";
import { AppShell } from "./app-shell";

/**
 * The shell's side menu against a stubbed `fetch`, with the real menu, header, queries and sign-out. The
 * readiness answer is the one a running server gave.
 */

const readiness = JSON.parse(systemReplies.readiness.body) as {
  machine_name: string;
};
const COLLAPSED_KEY = "weir.sidebar.collapsed";
const LAPTOP_WIDTH = "(min-width: 921px) and (max-width: 1099px)";

function Where() {
  const location = useLocation();
  return <p data-testid="where">{location.pathname + location.search}</p>;
}

function serveWeir(extra: Record<string, unknown> = {}) {
  return stubWeirApi({
    "GET /api/v1/auth/me": {
      user: { id: 1, username: "ann.lee", role: "admin", app_theme: null },
    },
    "GET /api/v1/system/readiness": readiness,
    ...extra,
  });
}

function renderShell(entry = "/activity") {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[entry]}>
        <Routes>
          <Route path="/login" element={<p>Sign-in page</p>} />
          <Route path="/" element={<AppShell />}>
            <Route index element={<Where />} />
            <Route path="*" element={<Where />} />
          </Route>
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const sidebar = () => document.getElementById("mm-primary-sidebar")!;
const openDrawer = () =>
  fireEvent.click(screen.getByTestId("shell-nav-toggle"));
const drawerIsOpen = () => sidebar().classList.contains("mm-sidebar--open");

beforeEach(() => {
  window.scrollTo = vi.fn();
  localStorage.removeItem(COLLAPSED_KEY);
});

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.removeItem(COLLAPSED_KEY);
});

describe("the phone drawer", () => {
  it("closes when a place in the menu is chosen, and the page moves there", () => {
    serveWeir();
    renderShell("/activity");
    openDrawer();
    expect(drawerIsOpen()).toBe(true);

    fireEvent.click(screen.getByRole("link", { name: "Library" }));

    expect(screen.getByTestId("where")).toHaveTextContent("/library");
    expect(drawerIsOpen()).toBe(false);
    expect(screen.getByTestId("shell-nav-toggle")).toHaveAttribute(
      "aria-expanded",
      "false",
    );
  });

  it("closes when the brand is chosen, and the page goes to the Dashboard", async () => {
    serveWeir();
    renderShell("/activity");
    openDrawer();

    fireEvent.click(
      await screen.findByRole("link", {
        name: `Weir · ${readiness.machine_name} home`,
      }),
    );

    expect(screen.getByTestId("where")).toHaveTextContent(/^\/$/);
    expect(drawerIsOpen()).toBe(false);
  });

  it("closes from the backdrop, and from the Menu button a second time", () => {
    serveWeir();
    renderShell("/activity");

    openDrawer();
    expect(drawerIsOpen()).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "Close navigation" }));
    expect(drawerIsOpen()).toBe(false);
    expect(
      screen.queryByRole("button", { name: "Close navigation" }),
    ).not.toBeInTheDocument();

    openDrawer();
    expect(drawerIsOpen()).toBe(true);
    openDrawer();
    expect(drawerIsOpen()).toBe(false);
  });

  it("closes, with the user menu, when Change password is chosen, and opens the Security tab", () => {
    serveWeir();
    renderShell("/activity");
    openDrawer();
    fireEvent.click(screen.getByTestId("user-menu"));

    fireEvent.click(screen.getByRole("link", { name: "Change password" }));

    expect(screen.getByTestId("where")).toHaveTextContent(
      "/system?tab=security",
    );
    expect(drawerIsOpen()).toBe(false);
    expect(screen.queryByTestId("sign-out")).not.toBeInTheDocument();
  });
});

describe("the menu folding to icons", () => {
  function windowIs(laptop: boolean) {
    vi.stubGlobal("matchMedia", (query: string) => ({
      matches: query === LAPTOP_WIDTH && laptop,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    }));
  }

  it("folds by itself at laptop width and stays open at any other", () => {
    serveWeir();
    windowIs(true);
    const { unmount } = renderShell();
    expect(sidebar()).toHaveClass("mm-sidebar--collapsed");
    unmount();

    windowIs(false);
    renderShell();
    expect(sidebar()).not.toHaveClass("mm-sidebar--collapsed");
  });

  it("keeps the person's own choice over the window's width", () => {
    serveWeir();
    windowIs(true);
    localStorage.setItem(COLLAPSED_KEY, "false");

    renderShell();

    expect(sidebar()).not.toHaveClass("mm-sidebar--collapsed");
    expect(screen.getByTestId("sidebar-collapse")).toHaveAttribute(
      "aria-label",
      "Collapse navigation",
    );
  });

  it("is remembered in this browser and restored on the next visit", () => {
    serveWeir();
    const first = renderShell();
    fireEvent.click(screen.getByTestId("sidebar-collapse"));
    expect(localStorage.getItem(COLLAPSED_KEY)).toBe("true");
    first.unmount();

    renderShell();
    expect(sidebar()).toHaveClass("mm-sidebar--collapsed");
    expect(screen.getByTestId("sidebar-collapse")).toHaveAttribute(
      "aria-label",
      "Expand navigation",
    );

    fireEvent.click(screen.getByTestId("sidebar-collapse"));
    expect(localStorage.getItem(COLLAPSED_KEY)).toBe("false");
  });
});

describe("signing out", () => {
  it("shows the sign-in page at once, while the server is still ending the session", async () => {
    const loggedOut = held<Response>();
    const api = serveWeir({
      "GET /api/v1/auth/csrf": { csrf_token: "token-1" },
      "POST /api/v1/auth/logout": () => loggedOut.promise,
    });
    renderShell("/activity");
    fireEvent.click(screen.getByTestId("user-menu"));

    fireEvent.click(screen.getByTestId("sign-out"));

    expect(screen.getByText("Sign-in page")).toBeInTheDocument();
    await act(async () => loggedOut.resolve(jsonAnswer({ ok: true })));
    expect(api.to("POST /api/v1/auth/logout")).toHaveLength(1);
  });
});

describe("the header's word on whether Weir answers", () => {
  it("shows Can't reach Weir when the readiness check fails", async () => {
    serveWeir({
      "GET /api/v1/system/readiness": () => jsonAnswer({ detail: "boom" }, 500),
    });
    renderShell();

    expect(await screen.findByTestId("status-offline")).toHaveTextContent(
      "Can't reach Weir",
    );
  });

  it("shows nothing when the server answers 503 with its readiness, as it does while starting", async () => {
    serveWeir({
      "GET /api/v1/system/readiness": () =>
        jsonAnswer({ ...readiness, ready: false, status: "starting" }, 503),
    });
    renderShell();

    await screen.findByRole("link", {
      name: `Weir · ${readiness.machine_name} home`,
    });
    expect(screen.queryByTestId("status-offline")).not.toBeInTheDocument();
  });
});
