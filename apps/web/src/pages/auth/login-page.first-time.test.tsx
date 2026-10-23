import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";

import { LoginPage } from "./login-page";

vi.mock("../../lib/auth/queries", () => ({
  useMeQuery: () => ({
    isPending: false,
    data: null,
    isError: false,
    error: null,
  }),
  useBootstrapStatusQuery: () => ({
    isPending: false,
    isError: false,
    data: { bootstrap_allowed: true, reason: "no_admin" },
    error: null,
  }),
  useLoginMutation: () => ({
    isPending: false,
    isError: false,
    error: null,
    mutateAsync: vi.fn(),
  }),
}));

type Entry = string | { pathname: string; state: unknown };

function visit(entry: Entry) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[entry]}>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="/setup" element={<p>The setup page</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("LoginPage on an install with no admin yet", () => {
  it("sends a visitor to setup, where the first account is made", () => {
    visit("/login");

    expect(screen.getByText("The setup page")).toBeInTheDocument();
  });

  it.each([
    ["having just made the account", "/login?bootstrap=created"],
    [
      "coming from setup's own link",
      { pathname: "/login", state: { fromSetup: true } },
    ],
    [
      "asking for the sign-in form from setup",
      { pathname: "/login", state: { manualSignIn: true } },
    ],
  ])(
    "shows the sign-in form to a visitor %s, with a way back to setup",
    (_, entry) => {
      visit(entry);

      expect(screen.getByLabelText("Trust this device")).toBeInTheDocument();
      expect(
        screen.getByRole("link", { name: "Create the admin account" }),
      ).toHaveAttribute("href", "/setup");
    },
  );
});
