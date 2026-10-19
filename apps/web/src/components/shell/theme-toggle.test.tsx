import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import * as authQueries from "../../lib/auth/queries";
import { APP_THEME_STORAGE_KEY, persistAppTheme } from "../../lib/ui/app-theme";
import { jsonAnswer, stubWeirApi } from "../../test/stub-weir-api";
import { ThemeToggle } from "./theme-toggle";

const mutate = vi.fn<(theme: "light" | "dark") => void>();

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function setup(isError = false) {
  vi.spyOn(authQueries, "useSetThemeMutation").mockReturnValue({
    mutate,
    isError,
  } as unknown as ReturnType<typeof authQueries.useSetThemeMutation>);
}

afterEach(() => {
  vi.restoreAllMocks();
  mutate.mockReset();
  localStorage.removeItem(APP_THEME_STORAGE_KEY);
  document.documentElement.removeAttribute("data-mm-theme");
});

it("applies the new theme instantly and saves the choice to the account", () => {
  setup();
  persistAppTheme("dark");

  render(<ThemeToggle />, { wrapper });
  fireEvent.click(screen.getByTestId("theme-toggle"));

  expect(document.documentElement.getAttribute("data-mm-theme")).toBe("light");
  expect(localStorage.getItem(APP_THEME_STORAGE_KEY)).toBe("light");
  expect(mutate).toHaveBeenCalledWith("light");
});

it("says a failed save never touched the theme on other browsers, but keeps it here", () => {
  setup(true);
  persistAppTheme("dark");

  render(<ThemeToggle />, { wrapper });

  expect(screen.getByTestId("theme-toggle-alert")).toHaveTextContent(
    "couldn't save your theme for other browsers",
  );
  // The local flip and this attempt's failure are independent: the switch still shows dark's icon.
  expect(screen.getByTestId("theme-toggle")).toHaveAttribute(
    "aria-label",
    "Switch to light mode",
  );
});

it("shows no alert once a save has not failed", () => {
  setup(false);

  render(<ThemeToggle />, { wrapper });

  expect(screen.queryByTestId("theme-toggle-alert")).not.toBeInTheDocument();
});

describe("saving the choice to the account", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("keeps the theme here and says the account was not updated when the server refuses the save", async () => {
    const api = stubWeirApi({
      "GET /api/v1/auth/csrf": { csrf_token: "token-1" },
      "POST /api/v1/auth/theme": () => jsonAnswer({ detail: "No." }, 500),
    });
    persistAppTheme("dark");

    render(<ThemeToggle />, { wrapper });
    fireEvent.click(screen.getByTestId("theme-toggle"));

    expect(await screen.findByTestId("theme-toggle-alert")).toHaveTextContent(
      "couldn't save your theme for other browsers",
    );
    expect(api.to("POST /api/v1/auth/theme")[0]?.body).toEqual({
      csrf_token: "token-1",
      theme: "light",
    });
    expect(document.documentElement.getAttribute("data-mm-theme")).toBe(
      "light",
    );
  });

  it("shows no alert when the save goes through", async () => {
    const api = stubWeirApi({
      "GET /api/v1/auth/csrf": { csrf_token: "token-1" },
      "POST /api/v1/auth/theme": { message: "Saved.", app_theme: "light" },
    });
    persistAppTheme("dark");

    render(<ThemeToggle />, { wrapper });
    fireEvent.click(screen.getByTestId("theme-toggle"));

    await waitFor(() =>
      expect(api.to("POST /api/v1/auth/theme")).toHaveLength(1),
    );
    expect(screen.queryByTestId("theme-toggle-alert")).not.toBeInTheDocument();
  });
});
