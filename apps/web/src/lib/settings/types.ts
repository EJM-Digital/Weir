import type { RequestBody, Schema } from "../api/types";

/**
 * The server writes every key, null included. The schema lists the nullable ones as optional, so this
 * is kept by hand to say what actually arrives.
 */
export type AppSettings = {
  setup_wizard_state: "pending" | "skipped" | "completed" | string;
  app_timezone: string;
  log_retention_days: number;
  /** How far back Activity history goes. 0 keeps it until it is cleared. */
  activity_retention_days: number;
  configuration_backup_enabled: boolean;
  configuration_backup_interval_hours: number;
  configuration_backup_preferred_time: string;
  configuration_backup_last_run_at: string | null;
  updated_at: string;
};

export type AppSettingsPutBody = RequestBody<"SuiteSettingsPutIn">;

export type SecurityOverview = Schema<"SuiteSecurityOverviewOut">;
export type BackupPasswordOut = Schema<"BackupPasswordOut">;
export type ConfigurationBackupList = Schema<"SuiteConfigurationBackupListOut">;
export type ConfigurationBackupItem = Schema<"SuiteConfigurationBackupItemOut">;
export type UpdateStatus = Schema<"SuiteUpdateStatusOut">;
export type NetworkAccessStatus = Schema<"SuiteNetworkAccessOut">;
export type NetworkAccessPutBody = RequestBody<"SuiteNetworkAccessPutIn">;
export type NetworkScope = NetworkAccessPutBody["scope"];
export type UpdateSettingsOut = Schema<"UpdateSettingsOut">;
export type UpdateMode = UpdateSettingsOut["mode"];
export type UpdateSettingsPutBody = RequestBody<"UpdateSettingsPutIn">;

/** Where the tray is with an update: `idle` also covers an update it found and has not downloaded. */
export type UpdateStep = Schema<"UpdateStateOut">["state"];

export type UpdateStateOut = {
  downloaded: boolean;
  pending_version: string | null;
  state: UpdateStep;
  /** In plain words, when `state` is `failed`. */
  failure: string | null;
  /** Whether a tray is there to take a request: not on Docker or source, and not once the tray has gone quiet. */
  tray_running: boolean;
  /** When the tray last looked for an update, whether or not it found one. Absent until it has looked. */
  last_checked_at?: string;
  /** Why the tray held the update back: it could not save a copy of Weir's data first. Absent otherwise. */
  not_updated_reason?: string;
  /** Why this copy cannot update itself: it was not installed with the installer. Absent otherwise. */
  cannot_update_reason?: string;
};

export type HistoryResetResult = Schema<"SuiteOperationalHistoryResetOut">;
export type ServerLogs = Schema<"SuiteLogsOut">;
export type ServerMetrics = Schema<"SuiteMetricsOut">;
export type NotificationChannelOut = Schema<"NotificationChannelOut">;
export type NotificationChannelListOut = Schema<"NotificationChannelListOut">;
export type NotificationChannelIn = RequestBody<"NotificationChannelIn">;

export type NotificationChannelTestOut = {
  ok: boolean;
  error: string | null;
};
