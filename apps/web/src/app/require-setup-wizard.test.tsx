import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { authKeys } from "../lib/auth/query-keys";
import * as settingsQueries from "../lib/settings/queries";
import { settingsKeys } from "../lib/settings/query-keys";
import { RequireSetupWizard } from "./require-setup-wizard";

function renderGuard(role: string, wizardState: string, at = "/library") {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: Infinity } },
  });
  client.setQueryData(authKeys.me, { id: 1, username: "someone", role });
  client.setQueryData(settingsKeys.app, { setup_wizard_state: wizardState });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[at]}>
        <Routes>
          <Route element={<RequireSetupWizard />}>
            <Route path="/library" element={<div>The app</div>} />
            <Route path="/setup-wizard" element={<div>The wizard</div>} />
          </Route>
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function renderWithSettings(state: { isPending: boolean; isError: boolean }) {
  vi.spyOn(settingsQueries, "useAppSettingsQuery").mockReturnValue({
    ...state,
    data: undefined,
  } as unknown as ReturnType<typeof settingsQueries.useAppSettingsQuery>);
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: Infinity } },
  });
  client.setQueryData(authKeys.me, { id: 1, username: "admin", role: "admin" });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/library"]}>
        <Routes>
          <Route element={<RequireSetupWizard />}>
            <Route path="/library" element={<div>The app</div>} />
          </Route>
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

afterEach(() => vi.restoreAllMocks());

describe("RequireSetupWizard", () => {
  it("shows that setup is loading while the settings are read", () => {
    renderWithSettings({ isPending: true, isError: false });

    expect(screen.getByText("Loading setup")).toBeInTheDocument();
    expect(screen.queryByText("The app")).not.toBeInTheDocument();
  });

  it("lets everyone into the app when the settings cannot be read", () => {
    renderWithSettings({ isPending: false, isError: true });

    expect(screen.getByText("The app")).toBeInTheDocument();
  });

  it("keeps an administrator on the wizard itself while the first run is not finished", () => {
    renderGuard("admin", "pending", "/setup-wizard");

    expect(screen.getByText("The wizard")).toBeInTheDocument();
  });

  it.each(["admin", "operator"])(
    "sends %s to the wizard while the first run is not finished",
    (role) => {
      renderGuard(role, "pending");

      expect(screen.getByText("The wizard")).toBeInTheDocument();
    },
  );

  it("tells a viewer an administrator needs to finish setting Weir up, and never sends them to the wizard", () => {
    renderGuard("viewer", "pending");

    expect(screen.queryByText("The wizard")).not.toBeInTheDocument();
    expect(screen.queryByText("The app")).not.toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "Weir isn't set up yet" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        "An administrator needs to finish setting Weir up first. Check back once they have.",
      ),
    ).toBeInTheDocument();
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });

  it.each(["viewer", "operator", "admin"])(
    "lets %s into the app once the first run is finished or skipped",
    (role) => {
      renderGuard(role, "skipped");

      expect(screen.getByText("The app")).toBeInTheDocument();
    },
  );
});
