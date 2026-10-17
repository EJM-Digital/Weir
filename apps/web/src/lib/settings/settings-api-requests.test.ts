import { afterEach, describe, expect, it, vi } from "vitest";

import {
  putProcessingOperatorSettings,
  processingOperatorSettingsPath,
} from "../processing/operator-settings-api";
import {
  fetchProcessingRuntimeSettings,
  processingRuntimeSettingsPath,
} from "../processing/runtime-settings-api";
import {
  createNotificationChannel,
  fetchAppSettings,
  fetchConfigurationBackupList,
  fetchConfigurationBundle,
  fetchOperationalHistoryPreview,
  fetchSecurityOverview,
  fetchServerLogs,
  fetchServerMetrics,
  fetchUpdateSettings,
  fetchUpdateState,
  fetchUpdateStatus,
  postApplyUpdate,
  postCheckUpdate,
  postConfigurationBackupNow,
  postDownloadUpdate,
  putAppSettings,
  putConfigurationBundle,
  putUpdateSettings,
  resetOperationalHistory,
  testNotificationChannel,
  updateNotificationChannel,
} from "./settings-api";

type Sent = { url: string; method: string; body: unknown };

const CSRF = "csrf-from-the-server";

/** Answers the token request and then every other request with an empty object, and writes down what was asked. */
function recordRequests(): Sent[] {
  const sent: Sent[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn((url: string, init?: RequestInit) => {
      const isToken = url.endsWith("/api/v1/auth/csrf");
      if (!isToken) {
        sent.push({
          url,
          method: init?.method ?? "GET",
          body:
            typeof init?.body === "string" ? JSON.parse(init.body) : undefined,
        });
      }
      return Promise.resolve(
        new Response(JSON.stringify(isToken ? { csrf_token: CSRF } : {}), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      );
    }),
  );
  return sent;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the requests the settings screens send", () => {
  it("reads each setting from its own address with no body", async () => {
    const sent = recordRequests();

    await fetchAppSettings();
    await fetchSecurityOverview();
    await fetchUpdateStatus();
    await fetchServerMetrics();
    await fetchUpdateSettings();
    await fetchUpdateState();
    await fetchOperationalHistoryPreview();
    await fetchConfigurationBundle();
    await fetchConfigurationBackupList();
    await fetchProcessingRuntimeSettings();
    await fetchServerLogs({ level: "ERROR", search: "disk", limit: 50 });

    expect(sent).toEqual([
      { url: "/api/v1/suite/settings", method: "GET" },
      { url: "/api/v1/suite/security-overview", method: "GET" },
      { url: "/api/v1/suite/update-status", method: "GET" },
      { url: "/api/v1/suite/metrics", method: "GET" },
      { url: "/api/v1/suite/update-settings", method: "GET" },
      { url: "/api/v1/suite/update-state", method: "GET" },
      { url: "/api/v1/suite/operational-history/preview", method: "GET" },
      { url: "/api/v1/suite/configuration-bundle", method: "GET" },
      { url: "/api/v1/suite/configuration-backups", method: "GET" },
      { url: processingRuntimeSettingsPath(), method: "GET" },
      {
        url: "/api/v1/suite/logs?level=ERROR&search=disk&limit=50",
        method: "GET",
      },
    ]);
  });

  it("saves the app settings with a PUT that carries the token and the three settings", async () => {
    const sent = recordRequests();

    await putAppSettings({
      setup_wizard_state: "complete",
      app_timezone: "Australia/Sydney",
      log_retention_days: 30,
    } as unknown as Parameters<typeof putAppSettings>[0]);

    expect(sent).toEqual([
      {
        url: "/api/v1/suite/settings",
        method: "PUT",
        body: {
          setup_wizard_state: "complete",
          app_timezone: "Australia/Sydney",
          log_retention_days: 30,
          csrf_token: CSRF,
        },
      },
    ]);
  });

  it("saves the operator settings with a PUT of only the fields given, and the token", async () => {
    const sent = recordRequests();

    await putProcessingOperatorSettings({
      max_concurrent_files: 3,
    } as unknown as Parameters<typeof putProcessingOperatorSettings>[0]);

    expect(sent).toEqual([
      {
        url: processingOperatorSettingsPath(),
        method: "PUT",
        body: { max_concurrent_files: 3, csrf_token: CSRF },
      },
    ]);
  });

  it("changes the update settings, the history, the configuration and the backups with the right verb and token", async () => {
    const sent = recordRequests();

    await putUpdateSettings({
      mode: "notify",
    } as unknown as Parameters<typeof putUpdateSettings>[0]);
    await postCheckUpdate();
    await postDownloadUpdate();
    await postApplyUpdate();
    await resetOperationalHistory("RESET");
    await putConfigurationBundle({ format_version: 1 });
    await postConfigurationBackupNow();

    expect(sent).toEqual([
      {
        url: "/api/v1/suite/update-settings",
        method: "PUT",
        body: { mode: "notify", csrf_token: CSRF },
      },
      {
        url: "/api/v1/suite/check-update",
        method: "POST",
        body: { csrf_token: CSRF },
      },
      {
        url: "/api/v1/suite/download-update",
        method: "POST",
        body: { csrf_token: CSRF },
      },
      {
        url: "/api/v1/suite/apply-update",
        method: "POST",
        body: { csrf_token: CSRF },
      },
      {
        url: "/api/v1/suite/operational-history/reset",
        method: "POST",
        body: { confirm: "RESET", csrf_token: CSRF },
      },
      {
        url: "/api/v1/suite/configuration-bundle",
        method: "PUT",
        body: { bundle: { format_version: 1 }, csrf_token: CSRF },
      },
      {
        url: "/api/v1/suite/configuration-backups",
        method: "POST",
        body: { csrf_token: CSRF },
      },
    ]);
  });

  it("creates, changes and tests an alert with the token in the body", async () => {
    const sent = recordRequests();
    const alert = {
      label: "Discord",
      provider: "discord",
      url: "https://discord.example.test/hook",
      events: ["job_failed"],
      enabled: true,
    } as unknown as Parameters<typeof createNotificationChannel>[0];

    await createNotificationChannel(alert);
    await updateNotificationChannel(4, alert);
    await testNotificationChannel(4);

    expect(sent).toEqual([
      {
        url: "/api/v1/suite/notification-channels",
        method: "POST",
        body: { ...alert, csrf_token: CSRF },
      },
      {
        url: "/api/v1/suite/notification-channels/4",
        method: "PUT",
        body: { ...alert, csrf_token: CSRF },
      },
      {
        url: "/api/v1/suite/notification-channels/4/test",
        method: "POST",
        body: { csrf_token: CSRF },
      },
    ]);
  });
});
