import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, expect, it, vi } from "vitest";

import * as authQueries from "../../../../lib/auth/queries";
import * as managerApi from "../../../../lib/media-managers/media-managers-api";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import * as maintenanceApi from "../../../../lib/processing/maintenance-api";
import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import * as librariesApi from "../../../../lib/processing/libraries-api";
import * as settingsApi from "../../../../lib/settings/queries";
import type { AppSettings } from "../../../../lib/settings/types";
import { ScheduleTab } from "./schedule-tab";

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
  configuration_backup_enabled: false,
  configuration_backup_interval_hours: 24,
  configuration_backup_preferred_time: "02:00",
  configuration_backup_last_run_at: null,
  updated_at: "2026-04-11T00:00:00Z",
};

function library(over: Partial<ProcessingLibrary> = {}): ProcessingLibrary {
  return {
    id: 1,
    name: "Movies",
    enabled: true,
    media_type: "movie",
    display_order: 0,
    watched_folder: "/srv/movies/in",
    work_folder: "",
    output_folder: "/srv/movies/out",
    media_extensions_csv: ".mkv,.mp4",
    exclude_markers_csv: "__admin__",
    include_patterns_csv: "",
    exclude_patterns_csv: "",
    min_file_size_mb: 0,
    max_file_size_mb: 0,
    rejected_file_action: "leave",
    ready_after_seconds: 60,
    created_after: null,
    created_before: null,
    modified_after: null,
    modified_before: null,
    exclude_hidden: true,
    top_level_only: false,
    scan_interval_seconds: 300,
    sidecar_patterns_csv: ".srt,.nfo",
    preserve_original_timestamps: false,
    remove_original_after_success: true,
    minimum_free_disk_space_mb: 5120,
    remux_writer: "best",
    output_collision_policy: "replace",
    ffmpeg_strictness: "normal",
    ignore_size_changes: false,
    skip_access_tests: false,
    file_system_events_enabled: true,
    max_attempts: 3,
    retry_backoff_seconds: 300,
    retry_execution_failures: true,
    retry_preflight_failures: false,
    failure_policy: "pass_through",
    schedule_grid: "",
    schedule_enabled: true,
    schedule_hours_limited: false,
    schedule_days: "",
    schedule_start: "00:00",
    schedule_end: "23:59",
    max_concurrent_files: 1,
    effective_max_concurrent_files: 1,
    priority: 0,
    rule_set_id: null,
    manager_connection_ids: [],
    manager_coverage: "no_upstream_signal",
    manager_coverage_detail:
      "No media manager has been tested for this workflow.",
    discovered_from_connection_id: null,
    discovered_library_key: null,
    folders_synced_from_connection_id: null,
    active_job_count: 0,
    updated_at: null,
    ...over,
  };
}

function asOperator() {
  vi.spyOn(authQueries, "useMeQuery").mockReturnValue({
    data: { role: "operator" },
  } as ReturnType<typeof authQueries.useMeQuery>);
  vi.spyOn(settingsApi, "useAppSettingsQuery").mockReturnValue({
    isPending: false,
    isError: false,
    data: SETTINGS,
  } as ReturnType<typeof settingsApi.useAppSettingsQuery>);
  vi.spyOn(maintenanceApi, "fetchProcessingMaintenance").mockResolvedValue({
    families: [],
  });
  vi.spyOn(managerApi, "fetchMediaManagerConnections").mockResolvedValue([]);
}

afterEach(() => {
  vi.restoreAllMocks();
});

it("shows an explicit load error instead of an empty schedule when libraries fail to load", async () => {
  asOperator();
  vi.spyOn(librariesApi, "fetchProcessingLibraries").mockRejectedValue(
    new Error("offline"),
  );

  render(<ScheduleTab />, { wrapper });

  expect(await screen.findByTestId("settings-load-error")).toHaveTextContent(
    "Weir couldn’t load your schedules. Reload the page to try again.",
  );
});

it("names the time zone the hours are read in, and sends changing it to System", async () => {
  asOperator();
  vi.spyOn(librariesApi, "fetchProcessingLibraries").mockResolvedValue([
    library(),
  ]);

  render(<ScheduleTab />, { wrapper });

  await screen.findAllByTestId("schedule-library-row");
  expect(screen.getByText(/in UTC./)).toBeInTheDocument();
  expect(
    screen.getByRole("link", { name: "Change the time zone" }),
  ).toHaveAttribute("href", "/system?tab=about");
  expect(
    screen.queryByTestId("schedule-save-timezone"),
  ).not.toBeInTheDocument();
});

it("draws a week with no gaps as one bar, and a week with gaps as its seven days", async () => {
  asOperator();
  vi.spyOn(librariesApi, "fetchProcessingLibraries").mockResolvedValue([
    library({ id: 1, name: "Movies" }),
    library({
      id: 2,
      name: "TV",
      schedule_enabled: true,
      schedule_grid: "0".repeat(672),
    }),
  ]);

  render(<ScheduleTab />, { wrapper });

  const [movies, tv] = await screen.findAllByTestId("schedule-library-row");
  expect(movies!.querySelectorAll(".mm-week__day")).toHaveLength(0);
  expect(tv!.querySelectorAll(".mm-week__day")).toHaveLength(7);
});

it("asks before an unsaved library's hours are replaced by another library's", async () => {
  asOperator();
  vi.spyOn(librariesApi, "fetchProcessingLibraries").mockResolvedValue([
    library({ id: 1, name: "Movies" }),
    library({ id: 2, name: "TV" }),
  ]);

  render(<ScheduleTab />, { wrapper });

  // Each row is asked for its own button: a role query over the whole page would also have to name
  // the open editor's 168 hour cells, slow enough to starve the test when the machine is busy.
  const [movies, tv] = await screen.findAllByTestId("schedule-library-row");
  fireEvent.click(
    within(movies!).getByRole("button", { name: "Change hours" }),
  );
  fireEvent.pointerDown(screen.getByTestId("schedule-cell-0-9"));
  fireEvent.click(within(tv!).getByRole("button", { name: "Change hours" }));

  expect(screen.getByTestId("settings-unsaved-changes")).toHaveTextContent(
    "You have unsaved changes to Movies's hours. Leave without saving?",
  );
  fireEvent.click(screen.getByTestId("settings-unsaved-changes-cancel"));
  expect(screen.getByTestId("schedule-library-editor")).toHaveAccessibleName(
    "Movies hours",
  );
});

it("keeps a workflow's other settings when its hours are saved", async () => {
  asOperator();
  vi.spyOn(librariesApi, "fetchProcessingLibraries").mockResolvedValue([
    library({ remux_writer: "ffmpeg" }),
  ]);
  const update = vi
    .spyOn(librariesApi, "updateProcessingLibrary")
    .mockResolvedValue(library());

  render(<ScheduleTab />, { wrapper });
  const [movies] = await screen.findAllByTestId("schedule-library-row");
  fireEvent.click(
    within(movies!).getByRole("button", { name: "Change hours" }),
  );
  fireEvent.pointerDown(screen.getByTestId("schedule-cell-0-9"));
  fireEvent.click(screen.getByTestId("schedule-library-save"));

  await waitFor(() => expect(update).toHaveBeenCalled());
  expect(update.mock.calls[0]![1]).toMatchObject({
    remux_writer: "ffmpeg",
  });
});

it("offers Scan now for a workflow Weir scans, and says Deluno hands over the downloads of one it does not", async () => {
  asOperator();
  vi.spyOn(managerApi, "fetchMediaManagerConnections").mockResolvedValue([
    { id: 4, kind: "deluno", name: "Deluno on RIG" },
    { id: 5, kind: "radarr", name: "Radarr on RIG" },
  ] as MediaManagerConnection[]);
  vi.spyOn(librariesApi, "fetchProcessingLibraries").mockResolvedValue([
    library({ id: 1, name: "Movies", manager_connection_ids: [4] }),
    library({ id: 2, name: "TV", manager_connection_ids: [5] }),
    library({ id: 3, name: "Anime", manager_connection_ids: [] }),
  ]);

  render(<ScheduleTab />, { wrapper });

  await waitFor(() =>
    expect(
      screen.getByText("Deluno on RIG hands this workflow its downloads."),
    ).toBeInTheDocument(),
  );
  const [movies, tv, anime] = screen.getAllByTestId("schedule-library-row");
  expect(
    within(movies!).queryByRole("button", { name: "Scan Movies now" }),
  ).toBeNull();
  expect(
    within(movies!).getByText(
      "Deluno on RIG hands this workflow its downloads.",
    ),
  ).toBeInTheDocument();
  expect(
    within(tv!).getByRole("button", { name: "Scan TV now" }),
  ).toBeInTheDocument();
  expect(within(tv!).getByText("Every 5 minutes")).toBeInTheDocument();
  expect(
    within(anime!).getByRole("button", { name: "Scan Anime now" }),
  ).toBeInTheDocument();
});
