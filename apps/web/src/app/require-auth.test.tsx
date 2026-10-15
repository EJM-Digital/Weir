import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { afterEach, expect, it, vi } from "vitest";

import * as authQueries from "../lib/auth/queries";
import { markLoginSucceeded } from "../lib/auth/session-kept";
import {
  clearSignedIn,
  markSignedIn,
  wasSignedIn,
} from "../lib/auth/signed-in-before";
import {
  APP_THEME_STORAGE_KEY,
  persistAppTheme,
  readStoredAppTheme,
} from "../lib/ui/app-theme";
import { RequireAuth } from "./require-auth";
import type { UserPublic } from "../lib/api/types";

const mutate = vi.fn();

function renderBehindAuth(user: UserPublic | null) {
  vi.spyOn(authQueries, "useMeQuery").mockReturnValue({
    isPending: false,
    data: user,
  } as unknown as ReturnType<typeof authQueries.useMeQuery>);
  vi.spyOn(authQueries, "useSetThemeMutation").mockReturnValue({
    mutate,
  } as unknown as ReturnType<typeof authQueries.useSetThemeMutation>);

  const client = new QueryClient();
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/"]}>
        <Routes>
          <Route element={<RequireAuth />}>
            <Route path="/" element={<div>Protected</div>} />
          </Route>
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function Where() {
  const location = useLocation();
  return <div data-testid="where">{location.pathname + location.search}</div>;
}

function renderSignedOut() {
  vi.spyOn(authQueries, "useMeQuery").mockReturnValue({
    isPending: false,
    data: null,
  } as unknown as ReturnType<typeof authQueries.useMeQuery>);
  vi.spyOn(authQueries, "useSetThemeMutation").mockReturnValue({
    mutate,
  } as unknown as ReturnType<typeof authQueries.useSetThemeMutation>);
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <MemoryRouter initialEntries={["/"]}>
        <Routes>
          <Route element={<RequireAuth />}>
            <Route path="/" element={<div>Protected</div>} />
          </Route>
          <Route path="/login" element={<Where />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

afterEach(() => {
  clearSignedIn();
  vi.useRealTimers();
  vi.restoreAllMocks();
  mutate.mockReset();
  localStorage.removeItem(APP_THEME_STORAGE_KEY);
  document.documentElement.removeAttribute("data-mm-theme");
});

it("overrides a stale local theme with the signed-in account's own once /auth/me answers", () => {
  persistAppTheme("dark");

  renderBehindAuth({
    id: 1,
    username: "alice",
    role: "admin",
    app_theme: "light",
  });

  expect(screen.getByText("Protected")).toBeInTheDocument();
  expect(readStoredAppTheme()).toBe("light");
  expect(document.documentElement.getAttribute("data-mm-theme")).toBe("light");
  expect(mutate).not.toHaveBeenCalled();
});

it("keeps this browser's choice and adopts it onto an account with no preference, rather than losing it", () => {
  persistAppTheme("dark");

  renderBehindAuth({
    id: 1,
    username: "alice",
    role: "admin",
    app_theme: null,
  });

  // The upgrade that leaves every account at null must never look like the local choice itself
  // was forgotten: it stays exactly as it was, and is saved to the account.
  expect(readStoredAppTheme()).toBe("dark");
  expect(document.documentElement.getAttribute("data-mm-theme")).toBe("dark");
  expect(mutate).toHaveBeenCalledWith("dark");
});

it("follows the system setting and saves nothing when neither the account nor this browser has a preference", () => {
  renderBehindAuth({
    id: 1,
    username: "alice",
    role: "admin",
    app_theme: null,
  });

  expect(readStoredAppTheme()).toBeNull();
  expect(mutate).not.toHaveBeenCalled();
});

it("sends a visitor who never had a session to a plain sign-in", () => {
  renderSignedOut();

  expect(screen.getByTestId("where").textContent).toBe("/login");
});

it("forgets a session whose cookie the browser dropped, so it never later reads as expired", () => {
  vi.useFakeTimers({ toFake: ["Date"] });
  markSignedIn();
  markLoginSucceeded();

  renderSignedOut();

  expect(screen.getByTestId("where").textContent).toBe(
    "/login?problem=session-not-kept",
  );
  expect(wasSignedIn()).toBe(false);
});

it("tells a visitor whose session ended that it expired", () => {
  markSignedIn();

  renderSignedOut();

  expect(screen.getByTestId("where")).toHaveTextContent(
    "/login?session=expired",
  );
});
