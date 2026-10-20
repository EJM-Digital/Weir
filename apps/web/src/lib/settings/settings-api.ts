import { fetchCsrfToken } from "../api/auth-api";
import { sendJson } from "../api/send-json";
import { apiFetch, readJson, requireOk } from "../api/client";

import type {
  NotificationChannelIn,
  NotificationChannelListOut,
  NotificationChannelOut,
  NotificationChannelTestOut,
  BackupPasswordOut,
  ConfigurationBackupItem,
  ConfigurationBackupList,
  ServerLogs,
  ServerMetrics,
  HistoryResetResult,
  SecurityOverview,
  AppSettings,
  AppSettingsPutBody,
  UpdateStatus,
  UpdateSettingsOut,
  UpdateSettingsPutBody,
  UpdateStateOut,
  NetworkAccessPutBody,
  NetworkAccessStatus,
} from "./types";

export const appSettingsPath = () => "/api/v1/suite/settings";
export const securityOverviewPath = () => "/api/v1/suite/security-overview";
export const updateStatusPath = () => "/api/v1/suite/update-status";
export const networkAccessPath = () => "/api/v1/suite/network-access";
export const serverLogsPath = () => "/api/v1/suite/logs";
export const serverLogsDownloadPath = () => "/api/v1/suite/logs/download";
export const serverMetricsPath = () => "/api/v1/suite/metrics";
export const operationalHistoryResetPath = () =>
  "/api/v1/suite/operational-history/reset";
export const updateSettingsPath = () => "/api/v1/suite/update-settings";
export const updateStatePath = () => "/api/v1/suite/update-state";
export const checkUpdatePath = () => "/api/v1/suite/check-update";
export const downloadUpdatePath = () => "/api/v1/suite/download-update";
export const applyUpdatePath = () => "/api/v1/suite/apply-update";

/** GET/PUT configuration bundle, at its one address: a 404 here means the request is wrong. */
export const configurationBundlePath = () =>
  "/api/v1/suite/configuration-bundle";
export const configurationBackupsPath = () =>
  "/api/v1/suite/configuration-backups";
export const backupPasswordPath = () => "/api/v1/suite/backup-password";

export type ConfigurationBundle = Record<string, unknown> & {
  format_version: number;
};

export async function fetchAppSettings(): Promise<AppSettings> {
  const path = appSettingsPath();
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load settings");
  return readJson<AppSettings>(r);
}

export async function putAppSettings(
  body: AppSettingsPutBody,
): Promise<AppSettings> {
  const path = appSettingsPath();
  const r = await sendJson(path, "PUT", body, "Could not save settings");
  return readJson<AppSettings>(r);
}

export async function fetchSecurityOverview(): Promise<SecurityOverview> {
  const path = securityOverviewPath();
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load security overview");
  return readJson<SecurityOverview>(r);
}

export async function fetchUpdateStatus(): Promise<UpdateStatus> {
  const path = updateStatusPath();
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not check for updates");
  return readJson<UpdateStatus>(r);
}

export async function fetchNetworkAccess(): Promise<NetworkAccessStatus> {
  const path = networkAccessPath();
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not check network access");
  return readJson<NetworkAccessStatus>(r);
}

/** Who can reach Weir over the network. The answer is the new state, with the change pending until the tray has carried it out. */
export async function putNetworkAccess(
  body: NetworkAccessPutBody,
): Promise<NetworkAccessStatus> {
  const path = networkAccessPath();
  const r = await sendJson(
    path,
    "PUT",
    body,
    "Could not change network access",
  );
  return readJson<NetworkAccessStatus>(r);
}

export async function fetchServerLogs(filters?: {
  level?: string;
  search?: string;
  has_exception?: boolean;
  limit?: number;
}): Promise<ServerLogs> {
  const params = new URLSearchParams();
  if (filters?.level) params.set("level", filters.level);
  if (filters?.search) params.set("search", filters.search);
  if (typeof filters?.has_exception === "boolean")
    params.set("has_exception", String(filters.has_exception));
  if (typeof filters?.limit === "number")
    params.set("limit", String(filters.limit));
  const path =
    params.size > 0
      ? `${serverLogsPath()}?${params.toString()}`
      : serverLogsPath();
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load logs");
  return readJson<ServerLogs>(r);
}

/** The whole server log as a file, for handing to someone helping with a support request. */
export async function fetchServerLogDownload(): Promise<Blob> {
  const path = serverLogsDownloadPath();
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not download the server log");
  return r.blob();
}

export async function fetchServerMetrics(): Promise<ServerMetrics> {
  const path = serverMetricsPath();
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load runtime health");
  return readJson<ServerMetrics>(r);
}

export async function fetchUpdateSettings(): Promise<UpdateSettingsOut> {
  const path = updateSettingsPath();
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load update settings");
  return readJson<UpdateSettingsOut>(r);
}

export async function putUpdateSettings(
  body: UpdateSettingsPutBody,
): Promise<UpdateSettingsOut> {
  const path = updateSettingsPath();
  const r = await sendJson(path, "PUT", body, "Could not save update settings");
  return readJson<UpdateSettingsOut>(r);
}

export async function fetchUpdateState(): Promise<UpdateStateOut> {
  const path = updateStatePath();
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load update state");
  return readJson<UpdateStateOut>(r);
}

export async function postCheckUpdate(): Promise<UpdateStateOut> {
  const path = checkUpdatePath();
  const r = await sendJson(
    path,
    "POST",
    {},
    "Could not ask for an update check",
  );
  return readJson<UpdateStateOut>(r);
}

export async function postDownloadUpdate(): Promise<UpdateStateOut> {
  const path = downloadUpdatePath();
  const r = await sendJson(path, "POST", {}, "Could not ask for the download");
  return readJson<UpdateStateOut>(r);
}

export async function postApplyUpdate(): Promise<UpdateStateOut> {
  const path = applyUpdatePath();
  const r = await sendJson(path, "POST", {}, "Could not signal update apply");
  return readJson<UpdateStateOut>(r);
}

/** Exactly what clearing history would remove, so the confirmation can say so. Removes nothing. */
export async function fetchOperationalHistoryPreview(): Promise<HistoryResetResult> {
  const path = "/api/v1/suite/operational-history/preview";
  const r = await apiFetch(path);
  await requireOk(
    path,
    r,
    "Could not check what clearing history would remove",
  );
  return readJson<HistoryResetResult>(r);
}

export async function resetOperationalHistory(
  confirm: string,
): Promise<HistoryResetResult> {
  const path = operationalHistoryResetPath();
  const r = await sendJson(
    path,
    "POST",
    { confirm },
    "Could not reset activity history",
  );
  return readJson<HistoryResetResult>(r);
}

export async function fetchConfigurationBundle(): Promise<ConfigurationBundle> {
  const path = configurationBundlePath();
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not export configuration");
  return readJson<ConfigurationBundle>(r);
}

/** Restores a configuration; `password` is the backup password, for a backup that holds locked secrets. */
export async function putConfigurationBundle(
  bundle: ConfigurationBundle,
  password?: string,
): Promise<ConfigurationBundle> {
  const path = configurationBundlePath();
  const r = await sendJson(
    path,
    "PUT",
    password ? { bundle, password } : { bundle },
    "Could not restore configuration",
  );
  return readJson<ConfigurationBundle>(r);
}

/** Writes a configuration snapshot right now, the same file format the automatic schedule writes. */
export async function postConfigurationBackupNow(): Promise<ConfigurationBackupItem> {
  const path = configurationBackupsPath();
  const r = await sendJson(path, "POST", {}, "Could not create a backup");
  return readJson<ConfigurationBackupItem>(r);
}

/** Sets or changes the password future backups lock their secrets with. The server never sends it back. */
export async function putBackupPassword(
  password: string,
  confirmPassword: string,
): Promise<BackupPasswordOut> {
  const r = await sendJson(
    backupPasswordPath(),
    "PUT",
    { password, confirm_password: confirmPassword },
    "Could not set the backup password",
  );
  return readJson<BackupPasswordOut>(r);
}

export async function fetchConfigurationBackupList(): Promise<ConfigurationBackupList> {
  const path = configurationBackupsPath();
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load automatic snapshots");
  return readJson<ConfigurationBackupList>(r);
}

export async function fetchStoredConfigurationBackupBlob(
  backupId: number,
): Promise<Blob> {
  const path = `${configurationBackupsPath()}/${backupId}/download`;
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not download automatic snapshot");
  return r.blob();
}

export const notificationChannelsPath = () =>
  "/api/v1/suite/notification-channels";

export async function fetchNotificationChannels(): Promise<NotificationChannelListOut> {
  const path = notificationChannelsPath();
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load notification channels");
  return readJson<NotificationChannelListOut>(r);
}

export async function createNotificationChannel(
  data: NotificationChannelIn,
): Promise<NotificationChannelOut> {
  const path = notificationChannelsPath();
  const r = await sendJson(
    path,
    "POST",
    data,
    "Could not create notification channel",
  );
  return readJson<NotificationChannelOut>(r);
}

export async function updateNotificationChannel(
  id: number,
  data: NotificationChannelIn,
): Promise<NotificationChannelOut> {
  const path = `${notificationChannelsPath()}/${id}`;
  const r = await sendJson(
    path,
    "PUT",
    data,
    "Could not update notification channel",
  );
  return readJson<NotificationChannelOut>(r);
}

export async function deleteNotificationChannel(id: number): Promise<void> {
  const csrf_token = await fetchCsrfToken();
  const path = `${notificationChannelsPath()}/${id}`;
  const r = await apiFetch(path, {
    method: "DELETE",
    headers: { "X-CSRF-Token": csrf_token },
  });
  if (!r.ok && r.status !== 204) {
    await requireOk(path, r, "Could not delete notification channel");
  }
}

export async function testNotificationChannel(
  id: number,
): Promise<NotificationChannelTestOut> {
  const path = `${notificationChannelsPath()}/${id}/test`;
  const r = await sendJson(
    path,
    "POST",
    {},
    "Could not test notification channel",
  );
  return readJson<NotificationChannelTestOut>(r);
}
