import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import type { ReactNode } from "react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { CurrentSession, UserPublic } from "../../lib/api/types";
import { authKeys } from "../../lib/auth/query-keys";
import { REPOSITORY_URL } from "../../lib/repository";
import * as processingQueries from "../../lib/processing/queries";
import * as settingsApi from "../../lib/settings/settings-api";
import { settingsKeys } from "../../lib/settings/query-keys";
import { systemKeys } from "../../lib/system/query-keys";
import type { SystemLogPage } from "../../lib/system/system-log-api";
import type {
  ServerMetrics,
  SecurityOverview,
  AppSettings,
  UpdateStatus,
} from "../../lib/settings/types";
import { SystemPage } from "./system-page";

const operatorMe: UserPublic = {
  id: 1,
  username: "alice",
  role: "operator",
  app_theme: null,
};
const viewerMe: UserPublic = {
  id: 2,
  username: "bob",
  role: "viewer",
  app_theme: null,
};

const minimalAppSettings: AppSettings = {
  setup_wizard_state: "pending",
  app_timezone: "UTC",
  log_retention_days: 30,
  activity_retention_days: 90,
  configuration_backup_enabled: false,
  configuration_backup_interval_hours: 24,
  configuration_backup_preferred_time: "02:00",
  configuration_backup_last_run_at: null,
  updated_at: "2026-04-11T00:00:00Z",
};

const minimalUpdateStatus: UpdateStatus = {
  current_version: "1.0.0",
  install_type: "source",
  status: "up_to_date",
  summary: "This install is already on Weir 1.0.0.",
  latest_version: "1.0.0",
  latest_name: "Weir 1.0.0",
  published_at: null,
  release_url: "https://example.com/release",
  windows_installer_url: null,
  docker_image: null,
  docker_tag: null,
  docker_update_command: null,
  in_app_upgrade_supported: false,
  in_app_upgrade_summary: null,
};

const windowsUpdateAvailableStatus: UpdateStatus = {
  ...minimalUpdateStatus,
  current_version: "2.0.7",
  install_type: "windows",
  status: "update_available",
  summary: "Weir 2.0.8 is available.",
  latest_version: "2.0.8",
  latest_name: "Weir 2.0.8",
  windows_installer_url: `${REPOSITORY_URL}/releases/download/v2.0.8/Weir-win-Setup.exe`,
  in_app_upgrade_supported: true,
  in_app_upgrade_summary:
    "Updates are managed by the Weir desktop app via Velopack.",
};

const minimalSecurity: SecurityOverview = {
  session_signing_configured: true,
  sign_in_cookie_https_mode: "auto",
  sign_in_cookie_https_plain:
    "Matched to each connection — on over HTTPS, off over plain HTTP on your network.",
  sign_in_cookie_same_site: "Lax (recommended for most setups)",
  standard_session_idle_timeout_plain: "14 days",
  standard_session_absolute_timeout_plain: "90 days",
  trusted_session_idle_timeout_plain: "60 days",
  trusted_session_absolute_timeout_plain: "365 days",
  extra_https_hardening_enabled: false,
  sign_in_attempt_limit: 30,
  sign_in_attempt_window_plain: "1 minute",
  first_time_setup_attempt_limit: 10,
  first_time_setup_attempt_window_plain: "1 hour",
  allowed_browser_origins_count: 1,
  restart_required_note:
    "These safety options are read when the app starts from the server configuration file. To change them, ask whoever runs the server to edit that file and restart the app.",
};

const minimalCurrentSession: CurrentSession = {
  session_id: "00000000-0000-0000-0000-000000000001",
  client_label: "Chrome on Windows",
  current: true,
  trusted_device: true,
  created_at: "2026-04-11T00:00:00Z",
  last_seen_at: "2026-04-11T00:05:00Z",
  absolute_expires_at: "2027-04-11T00:00:00Z",
  idle_timeout_minutes: 86400,
  absolute_timeout_days: 365,
};

/** A log with nothing in it, which is what Logs shows until something is recorded. */
const emptyLog: SystemLogPage = {
  items: [],
  next_cursor: null,
  total: 0,
  counts: {
    source: { event: 0, job: 0, server: 0 },
    level: { error: 0, warning: 0, info: 0, success: 0 },
    category: {
      processing: 0,
      scans: 0,
      cleanup: 0,
      library: 0,
      connections: 0,
      backups: 0,
      sign_in: 0,
      updates: 0,
      weir: 0,
    },
    workflow: {},
  },
};

const minimalMetrics: ServerMetrics = {
  uptime_seconds: 3600,
  total_requests: 20,
  average_response_ms: 12,
  error_log_count: 0,
  status_counts: { "2xx": 18, "3xx": 0, "4xx": 2, "5xx": 0 },
  busiest_routes: [],
};

const readiness = {
  ready: true,
  version: "1.0.0",
  machine_name: "RIG",
  machine_name_looks_generated: false,
  status: "ready",
  startup_seconds: 1,
  steps: [],
  worker_health: [],
};

function wrap(ui: ReactNode, client: QueryClient) {
  const router = createMemoryRouter([{ path: "*", element: ui }]);
  return (
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  );
}

function renderSettings(
  me: UserPublic,
  overrides?: {
    updateStatus?: UpdateStatus;
    machineNameLooksGenerated?: boolean;
    initialEntries?: string[];
    backupItems?: {
      id: number;
      created_at: string;
      size_bytes: number;
      file_name: string;
    }[];
  },
) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: Infinity } },
  });
  qc.setQueryData(settingsKeys.app, minimalAppSettings);
  qc.setQueryData(systemKeys.readiness, {
    ...readiness,
    machine_name_looks_generated: overrides?.machineNameLooksGenerated ?? false,
  });
  qc.setQueryData(settingsKeys.securityOverview, minimalSecurity);
  qc.setQueryData(authKeys.me, me);
  qc.setQueryData(authKeys.session, minimalCurrentSession);
  qc.setQueryData(settingsKeys.configurationBackups, {
    directory: "C:/Weir/backups/suite-configuration",
    password_set: false,
    items: overrides?.backupItems ?? [],
  });
  qc.setQueryData(
    settingsKeys.updateStatus,
    overrides?.updateStatus ?? minimalUpdateStatus,
  );
  qc.setQueryData(settingsKeys.metrics, minimalMetrics);
  qc.setQueryData(systemKeys.logEntries({}), emptyLog);
  const router = createMemoryRouter(
    [{ path: "*", element: <SystemPage /> }],
    overrides?.initialEntries
      ? { initialEntries: overrides.initialEntries }
      : undefined,
  );
  return render(
    <QueryClientProvider client={qc}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

async function renderSettingsWithSupportConfig(
  me: UserPublic,
  supportUrl: string | null,
  overrides?: { updateStatus?: UpdateStatus },
) {
  vi.resetModules();
  vi.doMock("../../lib/support", () => ({ SUPPORT_URL: supportUrl }));

  const { SystemPage: SettingsPageWithMockedSupport } =
    await import("./system-page");

  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: Infinity } },
  });
  qc.setQueryData(settingsKeys.app, minimalAppSettings);
  qc.setQueryData(settingsKeys.securityOverview, minimalSecurity);
  qc.setQueryData(authKeys.me, me);
  qc.setQueryData(authKeys.session, minimalCurrentSession);
  qc.setQueryData(settingsKeys.configurationBackups, {
    directory: "C:/Weir/backups/suite-configuration",
    password_set: false,
    items: [],
  });
  qc.setQueryData(
    settingsKeys.updateStatus,
    overrides?.updateStatus ?? minimalUpdateStatus,
  );
  qc.setQueryData(settingsKeys.metrics, minimalMetrics);
  return render(wrap(<SettingsPageWithMockedSupport />, qc));
}

describe("SystemPage", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    // Only the queries a test seeds have an answer; every other one fails as if the server were down.
    vi.stubGlobal(
      "fetch",
      vi.fn().mockRejectedValue(new TypeError("Failed to fetch")),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.doUnmock("../../lib/support");
    vi.resetModules();
  });

  it("does not mention Sonarr or Radarr on the System page", () => {
    const { container } = renderSettings(operatorMe, {
      initialEntries: ["/system"],
    });
    const t = (container.textContent ?? "").toLowerCase();
    expect(t).not.toContain("sonarr");
    expect(t).not.toContain("radarr");
    expect(screen.getByTestId("suite-system-page")).toBeTruthy();
    expect(screen.getByTestId("suite-settings-global")).toBeTruthy();
  });

  it("shows no support link, and no developer note, when the build has no support URL", async () => {
    await renderSettingsWithSupportConfig(operatorMe, null);

    expect(
      screen.queryByTestId("suite-settings-support"),
    ).not.toBeInTheDocument();
    expect(screen.queryByText(/VITE_SUPPORT_URL/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Development note/i)).not.toBeInTheDocument();
  });

  it("shows one small support link in About's footer when a valid support URL is configured", async () => {
    await renderSettingsWithSupportConfig(
      operatorMe,
      "https://example.com/support",
    );

    expect(
      screen.getByRole("link", { name: "Support Weir →" }),
    ).toHaveAttribute("href", "https://example.com/support");
    expect(screen.queryByText(/supporter licence/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/feature limits/i)).not.toBeInTheDocument();
  });

  it("shows viewers the retention numbers with no way to save them", () => {
    renderSettings(viewerMe, {
      initialEntries: ["/system?tab=logs#retention"],
    });
    expect(
      screen.getByTestId("suite-settings-activity-retention"),
    ).toBeDisabled();
    expect(
      screen.queryByTestId("suite-settings-save-logs"),
    ).not.toBeInTheDocument();
  });

  it("shows configuration backup + export for operators", () => {
    renderSettings(operatorMe, { initialEntries: ["/system?tab=backups"] });
    expect(screen.getByTestId("suite-settings-backup-restore")).toBeTruthy();
    expect(
      screen.getByRole("button", { name: "Download settings" }),
    ).toBeEnabled();
    expect(
      screen.getByRole("button", { name: "Restore from file…" }),
    ).toBeEnabled();
    // Nothing has changed, so there is no Save to press yet.
    expect(
      screen.queryByRole("button", { name: "Save schedule" }),
    ).not.toBeInTheDocument();
  });

  it("backs up now and shows the result beside the export buttons", async () => {
    const postNowSpy = vi
      .spyOn(settingsApi, "postConfigurationBackupNow")
      .mockResolvedValue({
        id: 9,
        created_at: "2026-04-12T00:00:00Z",
        size_bytes: 512,
        file_name: "weir-configuration-2026-04-12.json",
        made_by: "manual",
        secrets_sealed: false,
      });
    renderSettings(operatorMe, { initialEntries: ["/system?tab=backups"] });

    fireEvent.click(screen.getByRole("button", { name: "Back up now" }));

    await waitFor(() => {
      expect(postNowSpy).toHaveBeenCalledTimes(1);
    });
    expect(await screen.findByText("Backup created.")).toBeInTheDocument();
  });

  it("shows a backup made by hand as the last backup, and leaves the schedule where it was", async () => {
    const scheduled = {
      id: 3,
      created_at: "2026-04-10T02:00:00Z",
      size_bytes: 512,
      file_name: "suite-configuration-20260410-020000.json",
      made_by: "schedule" as const,
      secrets_sealed: false,
    };
    const byHand = {
      id: 9,
      created_at: "2026-04-12T12:00:00Z",
      size_bytes: 512,
      file_name: "suite-configuration-20260412-120000.json",
      made_by: "manual" as const,
      secrets_sealed: false,
    };
    vi.spyOn(settingsApi, "postConfigurationBackupNow").mockResolvedValue(
      byHand,
    );
    vi.spyOn(settingsApi, "fetchConfigurationBackupList").mockResolvedValue({
      directory: "C:/Weir/backups/suite-configuration",
      password_set: false,
      items: [byHand, scheduled],
    });
    renderSettings(operatorMe, {
      initialEntries: ["/system?tab=backups"],
      backupItems: [scheduled],
    });
    const last = screen.getByTestId("backup-last");
    expect(last).toHaveTextContent(/(^|[^0-9])10([^0-9]).*2026/);
    expect(last).toHaveTextContent("scheduled");
    const time = screen.getByLabelText("At") as HTMLInputElement;
    expect(time.value).toBe("02:00");

    fireEvent.click(screen.getByRole("button", { name: "Back up now" }));

    await waitFor(() => {
      expect(screen.getByTestId("backup-last")).toHaveTextContent(
        /(^|[^0-9])12([^0-9]).*2026/,
      );
    });
    expect(screen.getByTestId("backup-last")).toHaveTextContent("by hand");
    expect((screen.getByLabelText("At") as HTMLInputElement).value).toBe(
      "02:00",
    );
  });

  it("offers restoring a saved backup and confirms in Weir's own dialog, not window.confirm", async () => {
    const confirmSpy = vi.spyOn(window, "confirm");
    vi.spyOn(
      settingsApi,
      "fetchStoredConfigurationBackupBlob",
    ).mockResolvedValue(
      new Blob([JSON.stringify({ format_version: 4, suite_settings: {} })], {
        type: "application/json",
      }),
    );
    const putBundleSpy = vi
      .spyOn(settingsApi, "putConfigurationBundle")
      .mockResolvedValue({ format_version: 4 });
    renderSettings(operatorMe, {
      initialEntries: ["/system?tab=backups"],
      backupItems: [
        {
          id: 3,
          created_at: "2026-04-10T00:00:00Z",
          size_bytes: 2048,
          file_name: "weir-configuration-2026-04-10.json",
        },
      ],
    });

    fireEvent.click(
      screen.getByRole("button", { name: /^Restore the backup taken/ }),
    );

    const dialog = await screen.findByTestId("restore-configuration-dialog");
    expect(confirmSpy).not.toHaveBeenCalled();
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Replace settings" }),
    );

    await waitFor(() => {
      expect(putBundleSpy).toHaveBeenCalledTimes(1);
    });
    await waitFor(() => {
      expect(
        screen.queryByTestId("restore-configuration-dialog"),
      ).not.toBeInTheDocument();
    });
  });

  it("shows a plain-language restore error for a file that is not a Weir backup, never raw format_version text", async () => {
    renderSettings(operatorMe, { initialEntries: ["/system?tab=backups"] });
    const fileInput = document.querySelector(
      'input[type="file"]',
    ) as HTMLInputElement;
    const badFile = new File(['{"hello":true}'], "not-a-backup.json", {
      type: "application/json",
    });

    fireEvent.change(fileInput, { target: { files: [badFile] } });

    expect(
      await screen.findByText("This file is not a Weir configuration export."),
    ).toBeInTheDocument();
    expect(screen.queryByText(/format_version/)).not.toBeInTheDocument();
    expect(
      screen.queryByTestId("restore-configuration-dialog"),
    ).not.toBeInTheDocument();
  });

  it("saves how long Activity history is kept, including 0 for until cleared", async () => {
    vi.spyOn(settingsApi, "fetchAppSettings").mockResolvedValue(
      minimalAppSettings,
    );
    const putAppSettingsSpy = vi
      .spyOn(settingsApi, "putAppSettings")
      .mockImplementation(async (body) => ({
        ...minimalAppSettings,
        activity_retention_days: body.activity_retention_days ?? 0,
      }));

    renderSettings(operatorMe, {
      initialEntries: ["/system?tab=history#retention"],
    });
    const input = await screen.findByTestId(
      "suite-settings-activity-retention",
    );
    expect(input).toHaveValue(90);
    fireEvent.change(input, { target: { value: "0" } });
    fireEvent.click(screen.getByTestId("suite-settings-save-logs"));

    await waitFor(() => {
      expect(putAppSettingsSpy).toHaveBeenCalledTimes(1);
    });
    expect(putAppSettingsSpy.mock.calls[0]?.[0]).toMatchObject({
      activity_retention_days: 0,
      log_retention_days: 30,
    });
  });

  it("allows changing and saving backup schedule more than once", async () => {
    let currentSavedSettings: AppSettings = {
      ...minimalAppSettings,
      configuration_backup_enabled: true,
      configuration_backup_interval_hours: 24,
      configuration_backup_preferred_time: "02:00",
    };
    vi.spyOn(settingsApi, "fetchAppSettings").mockImplementation(
      async () => currentSavedSettings,
    );
    const putAppSettingsSpy = vi
      .spyOn(settingsApi, "putAppSettings")
      .mockImplementation(async (body) => {
        currentSavedSettings = {
          ...currentSavedSettings,
          configuration_backup_enabled:
            body.configuration_backup_enabled ?? false,
          configuration_backup_interval_hours:
            body.configuration_backup_interval_hours ?? 24,
          configuration_backup_preferred_time:
            body.configuration_backup_preferred_time ?? "02:00",
          updated_at: "2026-04-12T00:00:00Z",
        };
        return currentSavedSettings;
      });

    renderSettings(operatorMe, { initialEntries: ["/system?tab=backups"] });

    fireEvent.change(screen.getByLabelText("Every"), {
      target: { value: "12" },
    });
    fireEvent.change(screen.getByLabelText("At"), {
      target: { value: "03:30" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save schedule" }));

    await waitFor(() => {
      expect(putAppSettingsSpy).toHaveBeenCalledTimes(1);
    });
    await waitFor(() => {
      expect(
        screen.queryByRole("button", { name: "Save schedule" }),
      ).not.toBeInTheDocument();
    });

    fireEvent.change(screen.getByLabelText("Every"), {
      target: { value: "24" },
    });
    fireEvent.change(screen.getByLabelText("At"), {
      target: { value: "04:15" },
    });

    const saveButton = screen.getByRole("button", { name: "Save schedule" });
    expect(saveButton).toBeEnabled();
    fireEvent.click(saveButton);

    await waitFor(() => {
      expect(putAppSettingsSpy).toHaveBeenCalledTimes(2);
    });
    expect(putAppSettingsSpy.mock.calls[1]?.[0]).toMatchObject({
      configuration_backup_interval_hours: 24,
      configuration_backup_preferred_time: "04:15",
    });
  });

  it("goes back to the usual time when the backup time is cleared", () => {
    renderSettings(operatorMe, { initialEntries: ["/system?tab=backups"] });
    const time = screen.getByLabelText("At") as HTMLInputElement;
    fireEvent.change(time, { target: { value: "03:30" } });
    expect(time.value).toBe("03:30");

    fireEvent.change(time, { target: { value: "" } });

    expect(time.value).toBe("02:00");
  });

  it("hides configuration backup for viewers", () => {
    renderSettings(viewerMe);
    fireEvent.click(screen.getByRole("tab", { name: "About" }));
    expect(
      screen.queryByTestId("suite-settings-backup-restore"),
    ).not.toBeInTheDocument();
  });

  it("says on About that Weir is named after the computer, with no field to change it", () => {
    renderSettings(operatorMe);

    const name = screen.getByTestId("about-machine-name");
    expect(name).toHaveTextContent("RIG");
    expect(within(name).queryByRole("textbox")).not.toBeInTheDocument();
    expect(screen.queryByTestId("about-hostname-tip")).not.toBeInTheDocument();
  });

  it("tells a Docker install with a generated host name to set one in its compose file", () => {
    renderSettings(operatorMe, { machineNameLooksGenerated: true });

    expect(screen.getByTestId("about-hostname-tip")).toHaveTextContent(
      "Set hostname: in your compose file so Weir shows your server's name.",
    );
  });

  it("opens on About, and each tab holds one job", () => {
    renderSettings(operatorMe);
    // System opens with what the instance is, before anything you can change about it.
    expect(screen.getByRole("tab", { name: "About" })).toHaveAttribute(
      "aria-selected",
      "true",
    );
    expect(screen.getByText("Time zone")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Open setup wizard" }),
    ).toBeInTheDocument();
    // Housekeeping is a file-processing job, so it lives under Settings, not here.
    expect(screen.queryByText("Housekeeping")).not.toBeInTheDocument();
    expect(
      screen.queryByTestId("suite-settings-backup-restore"),
    ).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: "Backups" }));
    expect(screen.getByTestId("suite-settings-backup-tab")).toBeInTheDocument();
    expect(screen.queryByText("Time zone")).not.toBeInTheDocument();

    // The log is one list; how long things are kept is in the Log settings its card opens.
    fireEvent.click(screen.getByRole("tab", { name: "Logs" }));
    expect(screen.getByRole("heading", { name: "Log" })).toBeInTheDocument();
    expect(screen.getByTestId("settings-logs")).toBeInTheDocument();
    expect(screen.queryByText("System log")).not.toBeInTheDocument();
    expect(screen.queryByText("Server diagnostics")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Log settings" }));
    expect(screen.getByText("System log")).toBeInTheDocument();
    expect(screen.getByLabelText("Events")).toBeInTheDocument();
    expect(screen.getByText("Server diagnostics")).toBeInTheDocument();
  });

  it("keeps every retention setting in one panel under Logs: log days, Activity days and file activity days", () => {
    vi.spyOn(
      processingQueries,
      "useProcessingOperatorSettingsQuery",
    ).mockReturnValue({
      data: { file_log_retention_days: 45 },
      isError: false,
    } as unknown as ReturnType<
      typeof processingQueries.useProcessingOperatorSettingsQuery
    >);
    renderSettings(operatorMe, {
      initialEntries: ["/system?tab=logs#retention"],
    });

    const panel = screen
      .getByRole("heading", { name: "How long things are kept" })
      .closest("section") as HTMLElement;
    expect(within(panel).getByLabelText("System log")).toHaveValue(30);
    expect(within(panel).getByLabelText("Events")).toHaveValue(90);
    expect(within(panel).getByLabelText("File activity")).toHaveValue(45);
  });

  it("opens the Log settings from a link to the retention, and closes back to the log", () => {
    renderSettings(operatorMe, {
      initialEntries: ["/system?tab=logs#retention"],
    });

    const panel = screen.getByRole("dialog", { name: "Log settings" });
    expect(within(panel).getByText("Server diagnostics")).toBeInTheDocument();
    fireEvent.click(within(panel).getByRole("button", { name: "Close" }));

    expect(
      screen.queryByRole("dialog", { name: "Log settings" }),
    ).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Log" })).toBeInTheDocument();
  });

  it("keeps a changed retention number when the Log settings are closed and opened again", () => {
    renderSettings(operatorMe, {
      initialEntries: ["/system?tab=logs#retention"],
    });
    fireEvent.change(screen.getByLabelText("Events"), {
      target: { value: "12" },
    });
    fireEvent.click(screen.getAllByRole("button", { name: "Close" })[0]);
    fireEvent.click(screen.getByRole("button", { name: "Log settings" }));

    expect(screen.getByLabelText("Events")).toHaveValue(12);
  });

  it("holds the time zone on About, and saves a different one chosen there", async () => {
    const putAppSettingsSpy = vi
      .spyOn(settingsApi, "putAppSettings")
      .mockImplementation(async (body) => ({
        ...minimalAppSettings,
        app_timezone: body.app_timezone,
      }));
    renderSettings(operatorMe);

    expect(
      screen.getByRole("heading", { name: "This PC" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/Every time across Weir is in this zone./),
    ).toBeInTheDocument();
    // Nothing is chosen that differs from what is saved, so there is no Save yet.
    expect(
      screen.queryByTestId("schedule-save-timezone"),
    ).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Time zone" }));
    fireEvent.click(screen.getByRole("option", { name: /Sydney/ }));
    fireEvent.click(screen.getByTestId("schedule-save-timezone"));

    await waitFor(() => {
      expect(putAppSettingsSpy).toHaveBeenCalledTimes(1);
    });
    expect(putAppSettingsSpy.mock.calls[0]?.[0]).toMatchObject({
      app_timezone: expect.stringContaining("Sydney"),
    });
  });

  it("opens System when an old address asks for the upgrade tab", () => {
    renderSettings(operatorMe, {
      updateStatus: windowsUpdateAvailableStatus,
      initialEntries: ["/system?tab=upgrade"],
    });

    expect(screen.getByRole("tab", { name: "About" })).toHaveAttribute(
      "aria-selected",
      "true",
    );
    expect(
      screen.getByTestId("suite-settings-upgrade-tab"),
    ).toBeInTheDocument();
    // System is also where this instance's own facts live now, so they are here too.
    expect(screen.getByTestId("suite-settings-global")).toBeInTheDocument();
  });

  it("shows Checking... and disables button when refetch is in flight", async () => {
    vi.spyOn(settingsApi, "fetchUpdateStatus").mockImplementation(
      () => new Promise(() => {}),
    );
    const qc = new QueryClient({
      defaultOptions: { queries: { retry: false, staleTime: Infinity } },
    });
    qc.setQueryData(settingsKeys.app, minimalAppSettings);
    qc.setQueryData(settingsKeys.securityOverview, minimalSecurity);
    qc.setQueryData(authKeys.me, operatorMe);
    qc.setQueryData(authKeys.session, minimalCurrentSession);
    qc.setQueryData(settingsKeys.configurationBackups, {
      directory: "C:/Weir/backups/suite-configuration",
      password_set: false,
      items: [],
    });
    qc.setQueryData(settingsKeys.updateStatus, minimalUpdateStatus);
    qc.setQueryData(settingsKeys.metrics, minimalMetrics);

    render(wrap(<SystemPage />, qc));
    fireEvent.click(screen.getByRole("tab", { name: "About" }));

    // staleTime: Infinity means the pre-seeded data is fresh — the link reads "Check again →"
    expect(screen.getByRole("button", { name: "Check again →" })).toBeEnabled();

    // Trigger a manual refetch (mock never resolves)
    fireEvent.click(screen.getByRole("button", { name: "Check again →" }));

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: "Checking..." }),
      ).toBeDisabled();
    });
    expect(
      screen.queryByRole("button", { name: "Check again →" }),
    ).not.toBeInTheDocument();
  });

  it("shows the install source in words, not the raw install_type value", () => {
    renderSettings(operatorMe, { updateStatus: windowsUpdateAvailableStatus });
    fireEvent.click(screen.getByRole("tab", { name: "About" }));

    expect(screen.getByText("Windows installer")).toBeInTheDocument();
    expect(screen.queryByText("windows")).not.toBeInTheDocument();
  });

  it("does not show a success-looking pill for a failed update check", () => {
    renderSettings(operatorMe, {
      updateStatus: {
        ...minimalUpdateStatus,
        status: "unavailable",
        summary: "Could not reach the update server.",
      },
    });
    fireEvent.click(screen.getByRole("tab", { name: "About" }));

    const pill = within(
      screen.getByTestId("suite-settings-release-status"),
    ).getByText("Unavailable");
    expect(pill).not.toHaveAttribute("data-status", "done");
    expect(pill).toHaveAttribute("data-status", "attention");
  });

  function seededUpdateClient(overrides: {
    updateStatus: UpdateStatus;
    updateSettings?: {
      mode: "Auto" | "DownloadOnly" | "NotifyOnly";
      check_on_startup: boolean;
      check_interval_minutes: number;
      check_for_updates: boolean;
    };
  }) {
    const qc = new QueryClient({
      defaultOptions: { queries: { retry: false, staleTime: Infinity } },
    });
    qc.setQueryData(settingsKeys.app, minimalAppSettings);
    qc.setQueryData(settingsKeys.securityOverview, minimalSecurity);
    qc.setQueryData(authKeys.me, operatorMe);
    qc.setQueryData(authKeys.session, minimalCurrentSession);
    qc.setQueryData(settingsKeys.configurationBackups, {
      directory: "C:/Weir/backups/suite-configuration",
      password_set: false,
      items: [],
    });
    qc.setQueryData(settingsKeys.updateStatus, overrides.updateStatus);
    if (overrides.updateSettings) {
      qc.setQueryData(settingsKeys.updateSettings, overrides.updateSettings);
      qc.setQueryData(settingsKeys.updateState, {
        downloaded: false,
        pending_version: null,
        state: "idle",
        failure: null,
      });
    }
    qc.setQueryData(settingsKeys.metrics, minimalMetrics);
    return qc;
  }

  it("offers Download update in Notify-only mode too, and keeps the installer as a small link", () => {
    const qc = seededUpdateClient({
      updateStatus: windowsUpdateAvailableStatus,
      updateSettings: {
        mode: "NotifyOnly",
        check_on_startup: true,
        check_interval_minutes: 60,
        check_for_updates: true,
      },
    });

    render(wrap(<SystemPage />, qc));
    fireEvent.click(screen.getByRole("tab", { name: "About" }));

    expect(
      screen.getByRole("button", { name: "Download update" }),
    ).toBeEnabled();
    expect(
      screen.getByRole("link", { name: "Download installer →" }),
    ).toHaveAttribute(
      "href",
      windowsUpdateAvailableStatus.windows_installer_url,
    );
  });

  it("hides Docker update instructions when there is no update to apply", () => {
    const dockerStatus: UpdateStatus = {
      ...minimalUpdateStatus,
      install_type: "docker",
      docker_update_command: "docker compose pull && docker compose up -d",
    };
    render(
      wrap(<SystemPage />, seededUpdateClient({ updateStatus: dockerStatus })),
    );
    fireEvent.click(screen.getByRole("tab", { name: "About" }));

    expect(screen.queryByText("What happens next")).not.toBeInTheDocument();
  });

  it("shows Docker update instructions with a Copy button once an update exists", () => {
    const dockerStatus: UpdateStatus = {
      ...minimalUpdateStatus,
      install_type: "docker",
      status: "update_available",
      docker_update_command: "docker compose pull && docker compose up -d",
    };
    render(
      wrap(<SystemPage />, seededUpdateClient({ updateStatus: dockerStatus })),
    );
    fireEvent.click(screen.getByRole("tab", { name: "About" }));

    expect(screen.getByText("What happens next")).toBeInTheDocument();
    expect(
      screen.getByText("docker compose pull && docker compose up -d"),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Copy the update command" }),
    ).toBeInTheDocument();
  });

  it("keeps saying Copy when the browser refuses the clipboard", async () => {
    const writeText = vi.fn().mockRejectedValue(new Error("refused"));
    Object.defineProperty(navigator, "clipboard", {
      value: { writeText },
      configurable: true,
    });
    const dockerStatus: UpdateStatus = {
      ...minimalUpdateStatus,
      install_type: "docker",
      status: "update_available",
      docker_update_command: "docker compose pull && docker compose up -d",
    };
    render(
      wrap(<SystemPage />, seededUpdateClient({ updateStatus: dockerStatus })),
    );
    fireEvent.click(screen.getByRole("tab", { name: "About" }));

    fireEvent.click(
      screen.getByRole("button", { name: "Copy the update command" }),
    );

    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(1));
    expect(
      screen.getByRole("button", { name: "Copy the update command" }),
    ).toHaveTextContent("Copy →");
  });

  it("does not render mojibake in the upgrade panel", () => {
    renderSettings(operatorMe, { updateStatus: windowsUpdateAvailableStatus });
    fireEvent.click(screen.getByRole("tab", { name: "About" }));

    expect(document.body.textContent).not.toContain("â");
    expect(document.body.textContent).not.toContain("Ã");
    expect(document.body.textContent).not.toContain("�");
  });

  it("says a protection row needs attention instead of claiming success", () => {
    renderSettings(operatorMe);
    fireEvent.click(screen.getByRole("tab", { name: "Security" }));

    // The default fixture has extra HTTPS hardening off, which is a row that needs a look.
    expect(
      screen.getByText(/protections .* rows? needs? attention/),
    ).toBeInTheDocument();
  });

  it("shows change password in Security", () => {
    renderSettings(operatorMe);
    fireEvent.click(screen.getByRole("tab", { name: "Security" }));
    expect(
      screen.getByRole("heading", { name: "Change password" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("heading", { name: "How sign-in is protected" }),
    ).toBeInTheDocument();
  });

  it("change password fields use individually-named Show/Hide buttons and reset visibility when cleared", () => {
    renderSettings(operatorMe);
    fireEvent.click(screen.getByRole("tab", { name: "Security" }));
    const current = screen.getByPlaceholderText("Enter current password");
    expect(current).toHaveAttribute("type", "password");
    fireEvent.change(current, { target: { value: "current-secret" } });
    const showPassword = screen.getByRole("button", { name: "Show password" });
    expect(
      screen.getByRole("button", { name: "Show new password" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Show password confirmation" }),
    ).toBeInTheDocument();
    fireEvent.click(showPassword);
    expect(current).toHaveAttribute("type", "text");
    fireEvent.change(current, { target: { value: "" } });
    expect(current).toHaveAttribute("type", "password");
  });

  it("asks for the missing fields instead of leaving the account buttons disabled", () => {
    renderSettings(operatorMe);
    fireEvent.click(screen.getByRole("tab", { name: "Security" }));

    fireEvent.click(screen.getByRole("button", { name: "Change password" }));
    expect(
      screen.getByText("Fill in all three password fields."),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Change username" }));
    expect(
      screen.getByText("Enter the new username and your current password."),
    ).toBeInTheDocument();
  });

  it("puts the account's two forms in one card, with each password's eye inside its field", () => {
    renderSettings(operatorMe);
    fireEvent.click(screen.getByRole("tab", { name: "Security" }));

    const account = screen.getByTestId("suite-security-account");
    expect(
      within(account).getByRole("heading", { name: "Change username" }),
    ).toBeInTheDocument();
    expect(
      within(account).getByRole("heading", { name: "Change password" }),
    ).toBeInTheDocument();
    const eye = within(account).getByRole("button", { name: "Show password" });
    expect(eye.parentElement).toContainElement(
      screen.getByPlaceholderText("Enter current password"),
    );
  });

  it("saves an update choice as it is made, with no Save button", async () => {
    const putUpdateSettings = vi
      .spyOn(settingsApi, "putUpdateSettings")
      .mockImplementation(async (body) => ({
        mode: body.mode,
        check_on_startup: body.check_on_startup ?? true,
        check_interval_minutes: body.check_interval_minutes ?? 60,
        check_for_updates: body.check_for_updates ?? true,
      }));
    const qc = seededUpdateClient({
      updateStatus: windowsUpdateAvailableStatus,
      updateSettings: {
        mode: "NotifyOnly",
        check_on_startup: true,
        check_interval_minutes: 60,
        check_for_updates: true,
      },
    });
    render(wrap(<SystemPage />, qc));

    fireEvent.click(screen.getByRole("button", { name: "Auto" }));

    await waitFor(() => {
      expect(putUpdateSettings).toHaveBeenCalledWith({
        mode: "Auto",
        check_on_startup: true,
        check_interval_minutes: 60,
        check_for_updates: true,
      });
    });
    expect(
      screen.queryByRole("button", { name: "Save" }),
    ).not.toBeInTheDocument();
    expect(
      await screen.findByText("Update settings saved."),
    ).toBeInTheDocument();
  });
});
