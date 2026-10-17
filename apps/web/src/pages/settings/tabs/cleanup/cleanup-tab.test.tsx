import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, expect, it, vi } from "vitest";

import * as authQueries from "../../../../lib/auth/queries";
import type { MaintenanceState } from "../../../../lib/processing/maintenance-api";
import * as maintenanceQueries from "../../../../lib/processing/maintenance-queries";
import * as processingQueries from "../../../../lib/processing/queries";
import * as settingsQueries from "../../../../lib/settings/queries";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { CleanupTab } from "./cleanup-tab";

const mutate = vi.fn();
const saveSettings = vi.fn();

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function state(over: Partial<MaintenanceState> = {}): MaintenanceState {
  return {
    families: [
      {
        family: "work_temp_stale_sweep",
        enabled: true,
        description: "Reclaims Weir's own stale working files.",
        pending: 0,
        running: 0,
        last_completed_at: null,
        last_failed_at: null,
        last_error: null,
        interval_seconds: 3600,
        next_run_at: "2026-08-19T05:00:00",
      },
      {
        family: "unclaimed_handbacks",
        enabled: false,
        description:
          "Deletes Weir's own cleaned copy from a hand-back folder when no media manager imported it in time.",
        pending: 0,
        running: 0,
        last_completed_at: null,
        last_failed_at: null,
        last_error: null,
        interval_seconds: 21600,
        next_run_at: null,
        window_days: 14,
      },
    ],
    ...over,
  };
}

function setup(
  data: MaintenanceState,
  role = "operator",
  options: { savePending?: boolean; keepFailedWorkFiles?: boolean } = {},
) {
  vi.spyOn(authQueries, "useMeQuery").mockReturnValue({
    data: { role },
  } as ReturnType<typeof authQueries.useMeQuery>);
  vi.spyOn(settingsQueries, "useAppSettingsQuery").mockReturnValue({
    data: undefined,
  } as ReturnType<typeof settingsQueries.useAppSettingsQuery>);
  vi.spyOn(maintenanceQueries, "useProcessingMaintenanceQuery").mockReturnValue(
    {
      data,
      isPending: false,
      isError: false,
    } as ReturnType<typeof maintenanceQueries.useProcessingMaintenanceQuery>,
  );
  vi.spyOn(
    maintenanceQueries,
    "useProcessingRuntimeSettingsQuery",
  ).mockReturnValue({ data: undefined } as ReturnType<
    typeof maintenanceQueries.useProcessingRuntimeSettingsQuery
  >);
  vi.spyOn(
    processingQueries,
    "useProcessingOperatorSettingsQuery",
  ).mockReturnValue({
    data: {
      keep_failed_work_files: options.keepFailedWorkFiles ?? false,
      unclaimed_handback_window_days: 14,
    },
    isPending: false,
    isError: false,
  } as ReturnType<typeof processingQueries.useProcessingOperatorSettingsQuery>);
  vi.spyOn(
    processingQueries,
    "useProcessingOperatorSettingsSaveMutation",
  ).mockReturnValue({
    mutateAsync: saveSettings,
    isPending: options.savePending ?? false,
  } as unknown as ReturnType<
    typeof processingQueries.useProcessingOperatorSettingsSaveMutation
  >);
  vi.spyOn(maintenanceQueries, "useRunProcessingMaintenance").mockReturnValue({
    mutateAsync: mutate,
    isPending: false,
  } as unknown as ReturnType<
    typeof maintenanceQueries.useRunProcessingMaintenance
  >);
}

afterEach(() => {
  vi.restoreAllMocks();
  mutate.mockReset();
  saveSettings.mockReset();
});

it("lists each job with its switch, how often it runs and when it next does", async () => {
  setup(state());

  render(<CleanupTab />, { wrapper });

  const sweep = await screen.findByTestId(
    "processing-maintenance-work_temp_stale_sweep",
  );
  expect(sweep).toHaveTextContent("Leftover work files");
  expect(within(sweep).getByRole("radio", { name: "On" })).toHaveAttribute(
    "aria-checked",
    "true",
  );
  expect(
    within(sweep).getByRole("combobox", {
      name: "How often Leftover work files runs",
    }),
  ).toHaveValue("3600");
  const cleanup = screen.getByTestId(
    "processing-maintenance-unclaimed_handbacks",
  );
  expect(cleanup).toHaveTextContent("Cleaned copies nobody picked up");
  expect(within(cleanup).getByRole("radio", { name: "Off" })).toHaveAttribute(
    "aria-checked",
    "true",
  );
  expect(cleanup).toHaveTextContent("Off");
});

it("holds only the leftover work files, the kept half-written copy and the copies nobody picked up", async () => {
  setup(state());

  render(<CleanupTab />, { wrapper });

  await screen.findByTestId("processing-maintenance-work_temp_stale_sweep");
  expect(
    screen.queryByText("Downloads of failed files"),
  ).not.toBeInTheDocument();
  expect(screen.queryByText("Old file history")).not.toBeInTheDocument();
  expect(
    screen.queryByLabelText("Keep file history for"),
  ).not.toBeInTheDocument();
  const rows = screen
    .getAllByRole("row")
    .map((row) => row.getAttribute("data-testid"))
    .filter(Boolean);
  expect(rows).toEqual([
    "processing-maintenance-work_temp_stale_sweep",
    "processing-maintenance-keep-failed-copy",
    "processing-maintenance-unclaimed_handbacks",
    "processing-maintenance-handback-window",
  ]);
});

it("gives each setting between the jobs one cell across every column, so it does not sit under any one heading", async () => {
  setup(state());

  render(<CleanupTab />, { wrapper });

  const columnCount = (await screen.findAllByRole("columnheader")).length;
  for (const testId of [
    "processing-maintenance-keep-failed-copy",
    "processing-maintenance-handback-window",
  ]) {
    const cells = within(screen.getByTestId(testId)).getAllByRole("cell");
    expect(cells).toHaveLength(1);
    expect(cells[0]).toHaveAttribute("colspan", String(columnCount));
  }
});

it("keeps a failed file's half-written copy for a day when switched on, and saves at once", async () => {
  setup(state());
  saveSettings.mockResolvedValue({});

  render(<CleanupTab />, { wrapper });
  const row = await screen.findByTestId(
    "processing-maintenance-keep-failed-copy",
  );
  expect(row).toHaveTextContent(
    "Keep a failed file’s half-written copy for a day, so you can look at it.",
  );
  expect(within(row).getByRole("radio", { name: "Off" })).toHaveAttribute(
    "aria-checked",
    "true",
  );
  fireEvent.click(within(row).getByRole("radio", { name: "On" }));

  await waitFor(() =>
    expect(saveSettings).toHaveBeenCalledWith({ keep_failed_work_files: true }),
  );
  expect(
    await screen.findByTestId("processing-maintenance-notice"),
  ).toHaveTextContent(
    "A failed file’s half-written copy is now kept for a day.",
  );
});

it("shows the kept half-written copy switched on as saved, and switches it off", async () => {
  setup(state(), "operator", { keepFailedWorkFiles: true });
  saveSettings.mockResolvedValue({});

  render(<CleanupTab />, { wrapper });
  const row = await screen.findByTestId(
    "processing-maintenance-keep-failed-copy",
  );
  expect(within(row).getByRole("radio", { name: "On" })).toHaveAttribute(
    "aria-checked",
    "true",
  );
  fireEvent.click(within(row).getByRole("radio", { name: "Off" }));

  await waitFor(() =>
    expect(saveSettings).toHaveBeenCalledWith({
      keep_failed_work_files: false,
    }),
  );
});

it("does not let a viewer change whether a failed file's copy is kept", async () => {
  setup(state(), "viewer");

  render(<CleanupTab />, { wrapper });
  const row = await screen.findByTestId(
    "processing-maintenance-keep-failed-copy",
  );

  expect(within(row).getByRole("radio", { name: "On" })).toBeDisabled();
  expect(within(row).getByRole("radio", { name: "Off" })).toBeDisabled();
});

it("asks for confirmation before switching on a destructive job, then saves once confirmed", async () => {
  setup(state());
  saveSettings.mockResolvedValue({});

  render(<CleanupTab />, { wrapper });
  const cleanup = await screen.findByTestId(
    "processing-maintenance-unclaimed_handbacks",
  );
  fireEvent.click(within(cleanup).getByRole("radio", { name: "On" }));

  expect(saveSettings).not.toHaveBeenCalled();
  const dialog = await screen.findByTestId(
    "processing-maintenance-confirm-unclaimed_handbacks-enable",
  );
  expect(dialog).toHaveTextContent(
    'Switch on "Cleaned copies nobody picked up"?',
  );
  expect(within(dialog).getByRole("button", { name: "Switch on" })).toHaveClass(
    ...mmActionButtonClass({ variant: "primary" }).split(" "),
  );
  fireEvent.click(within(dialog).getByRole("button", { name: "Switch on" }));

  await waitFor(() =>
    expect(saveSettings).toHaveBeenCalledWith({
      unclaimed_handback_cleanup_enabled: true,
    }),
  );
  expect(
    await screen.findByTestId("processing-maintenance-notice"),
  ).toHaveTextContent("Cleaned copies nobody picked up is on.");
  expect(
    screen.queryByTestId(
      "processing-maintenance-confirm-unclaimed_handbacks-enable",
    ),
  ).not.toBeInTheDocument();

  fireEvent.change(
    within(cleanup).getByRole("combobox", {
      name: "How often Cleaned copies nobody picked up runs",
    }),
    { target: { value: "86400" } },
  );
  await waitFor(() =>
    expect(saveSettings).toHaveBeenCalledWith({
      unclaimed_handback_cleanup_interval_seconds: 86400,
    }),
  );
});

it("switches off a destructive job straight away, with no confirmation", async () => {
  setup(
    state({
      families: [
        {
          family: "unclaimed_handbacks",
          enabled: true,
          description: "Deletes Weir's own cleaned copy.",
          pending: 0,
          running: 0,
          last_completed_at: null,
          last_failed_at: null,
          last_error: null,
        },
      ],
    }),
  );
  saveSettings.mockResolvedValue({});

  render(<CleanupTab />, { wrapper });
  const cleanup = await screen.findByTestId(
    "processing-maintenance-unclaimed_handbacks",
  );
  fireEvent.click(within(cleanup).getByRole("radio", { name: "Off" }));

  await waitFor(() =>
    expect(saveSettings).toHaveBeenCalledWith({
      unclaimed_handback_cleanup_enabled: false,
    }),
  );
  expect(
    screen.queryByTestId(
      "processing-maintenance-confirm-unclaimed_handbacks-enable",
    ),
  ).not.toBeInTheDocument();
});

it("switches on a non-destructive job straight away, with no confirmation", async () => {
  setup(
    state({
      families: [
        {
          family: "work_temp_stale_sweep",
          enabled: false,
          description: "Reclaims Weir's own stale working files.",
          pending: 0,
          running: 0,
          last_completed_at: null,
          last_failed_at: null,
          last_error: null,
        },
      ],
    }),
  );
  saveSettings.mockResolvedValue({});

  render(<CleanupTab />, { wrapper });
  const sweep = await screen.findByTestId(
    "processing-maintenance-work_temp_stale_sweep",
  );
  fireEvent.click(within(sweep).getByRole("radio", { name: "On" }));

  await waitFor(() =>
    expect(saveSettings).toHaveBeenCalledWith({
      work_temp_stale_sweep_enabled: true,
    }),
  );
  expect(
    screen.queryByTestId(
      "processing-maintenance-confirm-work_temp_stale_sweep-enable",
    ),
  ).not.toBeInTheDocument();
});

it("carries the warning where the switch is", async () => {
  setup(state());

  render(<CleanupTab />, { wrapper });

  // The copies job deletes files, and the operator sees that beside the button.
  expect(
    await screen.findByTestId("processing-maintenance-unclaimed_handbacks"),
  ).toHaveTextContent(/deletes weir's own cleaned copy/i);
});

it("runs a job for both kinds of library", async () => {
  setup(state());
  mutate.mockResolvedValue({
    queued: true,
    detail: "Queued a work file sweep.",
  });

  render(<CleanupTab />, { wrapper });
  fireEvent.click(
    await screen.findByTestId(
      "processing-maintenance-run-work_temp_stale_sweep",
    ),
  );

  await waitFor(() =>
    expect(mutate).toHaveBeenCalledWith({
      family: "work_temp_stale_sweep",
      mediaScope: "tv",
    }),
  );
  expect(mutate).toHaveBeenCalledWith({
    family: "work_temp_stale_sweep",
    mediaScope: "movie",
  });
});

it("shows the server's own words when nothing was queued", async () => {
  setup(state());
  mutate.mockResolvedValue({
    queued: false,
    detail: "A cleanup for this scope is already waiting or running.",
  });

  render(<CleanupTab />, { wrapper });
  fireEvent.click(
    await screen.findByTestId("processing-maintenance-run-unclaimed_handbacks"),
  );
  const dialog = await screen.findByTestId(
    "processing-maintenance-confirm-unclaimed_handbacks-run",
  );
  fireEvent.click(within(dialog).getByRole("button", { name: "Run now" }));

  expect(
    await screen.findByTestId("processing-maintenance-notice"),
  ).toHaveTextContent(/already waiting or running/);
});

it("asks for confirmation before running a destructive job now", async () => {
  setup(state());
  mutate.mockResolvedValue({ queued: true, detail: "Queued." });

  render(<CleanupTab />, { wrapper });
  fireEvent.click(
    await screen.findByTestId("processing-maintenance-run-unclaimed_handbacks"),
  );

  expect(mutate).not.toHaveBeenCalled();
  const dialog = await screen.findByTestId(
    "processing-maintenance-confirm-unclaimed_handbacks-run",
  );
  expect(dialog).toHaveTextContent(
    'Run "Cleaned copies nobody picked up" now?',
  );
  fireEvent.click(within(dialog).getByRole("button", { name: "Run now" }));

  await waitFor(() =>
    expect(mutate).toHaveBeenCalledWith({
      family: "unclaimed_handbacks",
      mediaScope: "movie",
    }),
  );
  expect(
    screen.queryByTestId(
      "processing-maintenance-confirm-unclaimed_handbacks-run",
    ),
  ).not.toBeInTheDocument();
});

it("cancels a destructive job's confirmation without saving or running it", async () => {
  setup(state());

  render(<CleanupTab />, { wrapper });
  const cleanup = await screen.findByTestId(
    "processing-maintenance-unclaimed_handbacks",
  );
  fireEvent.click(within(cleanup).getByRole("radio", { name: "On" }));
  const dialog = await screen.findByTestId(
    "processing-maintenance-confirm-unclaimed_handbacks-enable",
  );
  fireEvent.click(within(dialog).getByRole("button", { name: "Keep it" }));

  expect(
    screen.queryByTestId(
      "processing-maintenance-confirm-unclaimed_handbacks-enable",
    ),
  ).not.toBeInTheDocument();
  expect(saveSettings).not.toHaveBeenCalled();
});

it("reports a running family rather than showing it as idle", async () => {
  setup(
    state({
      families: [
        {
          family: "work_temp_stale_sweep",
          enabled: true,
          description: "Reclaims stale working files.",
          pending: 0,
          running: 1,
          last_completed_at: null,
          last_failed_at: null,
          last_error: null,
        },
      ],
    }),
  );

  render(<CleanupTab />, { wrapper });

  expect(
    await screen.findByTestId("processing-maintenance-work_temp_stale_sweep"),
  ).toHaveTextContent(/Running now/);
});

it("says a queued family is waiting for a free worker", async () => {
  setup(
    state({
      families: [
        {
          family: "work_temp_stale_sweep",
          enabled: true,
          description: "Reclaims stale working files.",
          pending: 1,
          running: 0,
          last_completed_at: null,
          last_failed_at: null,
          last_error: null,
        },
      ],
    }),
  );

  render(<CleanupTab />, { wrapper });

  expect(
    await screen.findByTestId("processing-maintenance-work_temp_stale_sweep"),
  ).toHaveTextContent("Queued, waiting for a free worker.");
});

it("surfaces the reason a run failed", async () => {
  setup(
    state({
      families: [
        {
          family: "unclaimed_handbacks",
          enabled: true,
          description: "Deletes Weir's own cleaned copy.",
          pending: 0,
          running: 0,
          last_completed_at: null,
          last_failed_at: "2026-08-26T14:00:00Z",
          last_error: "The work folder was missing.",
        },
      ],
    }),
  );

  render(<CleanupTab />, { wrapper });

  expect(
    await screen.findByTestId("processing-maintenance-unclaimed_handbacks"),
  ).toHaveTextContent("The work folder was missing.");
});

it("lists cleaned copies nobody picked up, off, and saves how long a copy waits", async () => {
  setup(state());
  saveSettings.mockResolvedValue({});

  render(<CleanupTab />, { wrapper });

  const unclaimed = await screen.findByTestId(
    "processing-maintenance-unclaimed_handbacks",
  );
  expect(unclaimed).toHaveTextContent("Cleaned copies nobody picked up");
  expect(within(unclaimed).getByRole("radio", { name: "Off" })).toHaveAttribute(
    "aria-checked",
    "true",
  );
  expect(
    within(unclaimed).getByRole("combobox", {
      name: "How often Cleaned copies nobody picked up runs",
    }),
  ).toHaveValue("21600");

  const wait = screen.getByLabelText(
    "Cleaned copies nobody picked up wait for",
  );
  expect(wait).toHaveValue(14);
  fireEvent.change(wait, { target: { value: "30" } });
  fireEvent.click(
    within(
      screen.getByTestId("processing-maintenance-handback-window"),
    ).getByRole("button", { name: "Save" }),
  );
  await waitFor(() =>
    expect(saveSettings).toHaveBeenCalledWith({
      unclaimed_handback_window_days: 30,
    }),
  );
  expect(
    await screen.findByTestId("processing-maintenance-notice"),
  ).toHaveTextContent("Cleaned copies nobody picked up now wait 30 days.");
});

it("shows the Save button as pending while a day-count setting is saving", async () => {
  setup(state());

  const { rerender } = render(<CleanupTab />, { wrapper });

  const wait = await screen.findByTestId(
    "processing-maintenance-handback-window",
  );
  fireEvent.change(
    screen.getByLabelText("Cleaned copies nobody picked up wait for"),
    { target: { value: "30" } },
  );
  expect(within(wait).getByRole("button", { name: "Save" })).not.toBeDisabled();

  setup(state(), "operator", { savePending: true });
  rerender(<CleanupTab />);

  const button = within(wait).getByRole("button", { name: "Saving…" });
  expect(button).toBeDisabled();
});

it("does not offer a viewer the run buttons", async () => {
  setup(state(), "viewer");

  render(<CleanupTab />, { wrapper });

  await screen.findByTestId("processing-maintenance-work_temp_stale_sweep");
  expect(
    screen.queryByTestId("processing-maintenance-run-work_temp_stale_sweep"),
  ).not.toBeInTheDocument();
});

it("shows a load error instead of an empty state when maintenance data fails to load", async () => {
  setup(state());
  vi.spyOn(maintenanceQueries, "useProcessingMaintenanceQuery").mockReturnValue(
    {
      data: undefined,
      isPending: false,
      isError: true,
    } as ReturnType<typeof maintenanceQueries.useProcessingMaintenanceQuery>,
  );

  render(<CleanupTab />, { wrapper });

  expect(await screen.findByTestId("settings-load-error")).toHaveTextContent(
    "Weir couldn’t load your cleanup settings. Reload the page to try again.",
  );
  expect(
    screen.queryByText("No cleanup jobs are available on this instance."),
  ).not.toBeInTheDocument();
});

it("shows a load error when operator settings fail to load, even though maintenance data arrived", async () => {
  setup(state());
  vi.spyOn(
    processingQueries,
    "useProcessingOperatorSettingsQuery",
  ).mockReturnValue({
    data: undefined,
    isPending: false,
    isError: true,
  } as ReturnType<typeof processingQueries.useProcessingOperatorSettingsQuery>);

  render(<CleanupTab />, { wrapper });

  expect(await screen.findByTestId("settings-load-error")).toBeInTheDocument();
});

it("shows a loading state rather than the empty state while data is still arriving", async () => {
  setup(state());
  vi.spyOn(maintenanceQueries, "useProcessingMaintenanceQuery").mockReturnValue(
    {
      data: undefined,
      isPending: true,
      isError: false,
    } as ReturnType<typeof maintenanceQueries.useProcessingMaintenanceQuery>,
  );

  render(<CleanupTab />, { wrapper });

  expect(
    await screen.findByText("Loading cleanup settings"),
  ).toBeInTheDocument();
  expect(
    screen.queryByText("No cleanup jobs are available on this instance."),
  ).not.toBeInTheDocument();
});
