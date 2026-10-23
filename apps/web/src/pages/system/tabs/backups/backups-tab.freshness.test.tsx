import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { systemReplies } from "../../../../test/recorded-system";
import { serveRecordedReplies } from "../../../../test/recorded-weir";
import {
  renderWithSettingsForm,
  SAVED_SETTINGS,
  SAVING,
  SETTINGS,
} from "../../../../test/system-test-support";
import { BackupsTab } from "./backups-tab";

const SETTINGS_PATH = "/api/v1/suite/settings";
const BACKUPS_PATH = "/api/v1/suite/configuration-backups";

const settingsWithLastBackup = (lastRunAt: string) => ({
  ...systemReplies.settings,
  body: JSON.stringify({
    ...SAVED_SETTINGS,
    configuration_backup_enabled: true,
    configuration_backup_last_run_at: lastRunAt,
  }),
});

function renderBackups(lastRunAt: string) {
  serveRecordedReplies({
    ...SAVING,
    ...SETTINGS,
    [`GET ${BACKUPS_PATH}`]: systemReplies.backupsEmpty,
    [`GET ${SETTINGS_PATH}`]: settingsWithLastBackup(lastRunAt),
  });
  renderWithSettingsForm((form, settings) => (
    <BackupsTab form={form} editable settings={settings} />
  ));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("Backups: whether the schedule is being kept", () => {
  it("says Overdue beside the last backup when the next one is long past due", async () => {
    renderBackups("2020-01-01T00:00:00Z");

    expect(await screen.findByText("Overdue")).toBeVisible();
  });

  it("says Up to date when the last backup was made on schedule", async () => {
    renderBackups(new Date().toISOString());

    expect(await screen.findByText("Up to date")).toBeVisible();
  });
});
