import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
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
    data: { bootstrap_allowed: false, reason: "admin_exists" },
    error: null,
  }),
  useLoginMutation: () => ({
    isPending: false,
    isError: false,
    error: null,
    mutateAsync: vi.fn(),
  }),
}));

function wrap(ui: ReactNode, initialEntry = "/login") {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return (
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[initialEntry]}>{ui}</MemoryRouter>
    </QueryClientProvider>
  );
}

describe("LoginPage", () => {
  it("shows the bootstrap-created banner from the query string", () => {
    render(wrap(<LoginPage />, "/login?bootstrap=created"));

    expect(
      screen.getByText(
        "Initial account created. Sign in with the credentials you chose.",
      ),
    ).toBeVisible();
  });

  it("offers a way back in for a forgotten username or password", () => {
    render(wrap(<LoginPage />));

    expect(
      screen.getByRole("link", { name: "Forgot your username or password?" }),
    ).toHaveAttribute("href", "/recover");
  });

  it("offers trust this device, off by default", () => {
    render(wrap(<LoginPage />));

    const checkbox = screen.getByLabelText(
      "Trust this device",
    ) as HTMLInputElement;
    expect(checkbox.checked).toBe(false);

    fireEvent.click(checkbox);
    expect(checkbox.checked).toBe(true);
  });
});
