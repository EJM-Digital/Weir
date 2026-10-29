/** Every query key for Weir's own settings, logs and notification channels. */
export const settingsKeys = {
  app: ["settings", "app"] as const,
  securityOverview: ["settings", "security-overview"] as const,
  configurationBackups: ["settings", "configuration-backups"] as const,
  updateStatus: ["settings", "update-status"] as const,
  networkAccess: ["settings", "network-access"] as const,
  homeNetworkSignIn: ["settings", "home-network-sign-in"] as const,
  updateSettings: ["settings", "update-settings"] as const,
  updateState: ["settings", "update-state"] as const,
  metrics: ["settings", "metrics"] as const,
  notificationChannels: ["settings", "notification-channels"] as const,
};
