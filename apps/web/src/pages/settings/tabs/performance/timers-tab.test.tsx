import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, expect, it, vi } from "vitest";

import * as maintenanceApi from "../../../../lib/processing/maintenance-api";
import * as settingsQueries from "../../../../lib/settings/queries";
import type { AppSettings } from "../../../../lib/settings/types";
import { TimersTab } from "./timers-tab";

function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return (
    <MemoryRouter>
      <QueryClientProvider client={qc}>{children}</QueryClientProvider>
    </MemoryRouter>
  );
}

const SETTINGS: AppSettings = {
  setup_wizard_state: "completed",
  app_timezone: "UTC",
  log_retention_days: 30,
  activity_retention_days: 90,
  configuration_backup_enabled: true,
  configuration_backup_interval_hours: 24,
  configuration_backup_preferred_time: "02:00",
  configuration_backup_last_run_at: null,
  updated_at: "2026-04-11T00:00:00Z",
};

function stubSettings(
  over: Partial<ReturnType<typeof settingsQueries.useAppSettingsQuery>>,
) {
  vi.spyOn(settingsQueries, "useAppSettingsQuery").mockReturnValue(
    over as ReturnType<typeof settingsQueries.useAppSettingsQuery>,
  );
}

afterEach(() => {
  vi.restoreAllMocks();
});

it("lists Weir's own jobs, each with where to change it", async () => {
  stubSettings({ data: SETTINGS, isPending: false, isError: false });
  vi.spyOn(maintenanceApi, "fetchProcessingMaintenance").mockResolvedValue({
    families: [],
  } as Awaited<ReturnType<typeof maintenanceApi.fetchProcessingMaintenance>>);

  render(<TimersTab />, { wrapper });

  const table = await screen.findByTestId("schedule-timers");
  expect(
    within(table).getByRole("link", { name: "Change in Backups" }),
  ).toHaveAttribute("href", "/system?tab=backups");
  expect(
    within(table).getAllByRole("link", { name: "Change in Cleanup" })[0],
  ).toHaveAttribute("href", "/setup/performance/cleanup");
});

it("says the timers are loading while the settings are still coming", () => {
  stubSettings({ data: undefined, isPending: true, isError: false });

  render(<TimersTab />, { wrapper });

  expect(screen.getByRole("status")).toHaveTextContent("Loading timers");
  expect(screen.queryByTestId("schedule-timers")).not.toBeInTheDocument();
});

it("shows a load error when the settings fail to load", () => {
  stubSettings({ data: undefined, isPending: false, isError: true });

  render(<TimersTab />, { wrapper });

  expect(screen.getByTestId("settings-load-error")).toHaveTextContent(
    "Weir couldn’t load your timers.",
  );
});

function jobNames(table: HTMLElement) {
  return within(table)
    .getAllByRole("row")
    .slice(1)
    .map((row) => row.querySelector("th")?.textContent);
}

it("sorts the jobs by a heading, reverses on a second click and puts the order back with Reset columns", async () => {
  localStorage.clear();
  stubSettings({ data: SETTINGS, isPending: false, isError: false });
  vi.spyOn(maintenanceApi, "fetchProcessingMaintenance").mockResolvedValue({
    families: [],
  } as Awaited<ReturnType<typeof maintenanceApi.fetchProcessingMaintenance>>);
  render(<TimersTab />, { wrapper });
  const table = await screen.findByTestId("schedule-timers");
  const listed = [
    "Leftover work files",
    "Cleaned copies nobody picked up",
    "Settings backup",
  ];
  expect(jobNames(table)).toEqual(listed);

  fireEvent.click(within(table).getByRole("button", { name: "Job" }));
  expect(jobNames(table)).toEqual([
    "Cleaned copies nobody picked up",
    "Leftover work files",
    "Settings backup",
  ]);

  fireEvent.click(within(table).getByRole("button", { name: "Job" }));
  expect(jobNames(table)[0]).toBe("Settings backup");

  fireEvent.click(screen.getByRole("button", { name: "Columns" }));
  fireEvent.click(screen.getByRole("menuitem", { name: "Reset columns" }));
  expect(jobNames(table)).toEqual(listed);
});
