import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { systemReplies } from "../../../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  CSRF_TOKEN,
  renderWithSettingsForm,
  SAVED_SETTINGS,
  SAVING,
  SETTINGS,
} from "../../system-test-support";
import { BackupsTab } from "./backups-tab";

const SETTINGS_PATH = "/api/v1/suite/settings";
const BACKUPS_PATH = "/api/v1/suite/configuration-backups";
const BUNDLE_PATH = "/api/v1/suite/configuration-bundle";
const RESTORE_DIALOG = "restore-configuration-dialog";

/** What the Backups tab saves: every setting as the server holds it, with the schedule as the drafts say. */
function savedWith(schedule: Record<string, unknown>) {
  return {
    setup_wizard_state: SAVED_SETTINGS.setup_wizard_state,
    app_timezone: SAVED_SETTINGS.app_timezone,
    log_retention_days: SAVED_SETTINGS.log_retention_days,
    activity_retention_days: SAVED_SETTINGS.activity_retention_days,
    configuration_backup_enabled: SAVED_SETTINGS.configuration_backup_enabled,
    configuration_backup_interval_hours:
      SAVED_SETTINGS.configuration_backup_interval_hours,
    configuration_backup_preferred_time:
      SAVED_SETTINGS.configuration_backup_preferred_time,
    ...schedule,
    csrf_token: CSRF_TOKEN,
  };
}

const bundleText = systemReplies.bundle.body;

function renderBackups(routes: RecordedRoutes): RecordedWeir {
  const weir = serveRecordedReplies({
    ...SAVING,
    ...SETTINGS,
    [`GET ${BACKUPS_PATH}`]: systemReplies.backupsEmpty,
    ...routes,
  });
  renderWithSettingsForm((form, settings) => (
    <BackupsTab form={form} editable settings={settings} />
  ));
  return weir;
}

/** The bodies the page sent to a route, in order. */
const sent = (weir: RecordedWeir, method: string, path: string) =>
  weir.sent(method, path).map((request) => request.body);

const button = (name: string) => screen.findByRole("button", { name });

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("Backups: the schedule", () => {
  it("sends configuration_backup_enabled true when Back up automatically is switched on and saved", async () => {
    const weir = renderBackups({
      [`GET ${SETTINGS_PATH}`]: [
        systemReplies.settings,
        systemReplies.settingsPutBackupOn,
      ],
      [`PUT ${SETTINGS_PATH}`]: systemReplies.settingsPutBackupOn,
    });

    fireEvent.click(await screen.findByRole("radio", { name: "On" }));
    fireEvent.click(await button("Save schedule"));

    expect(await screen.findByText("Backup schedule saved.")).toBeVisible();
    expect(sent(weir, "PUT", SETTINGS_PATH)).toEqual([
      savedWith({ configuration_backup_enabled: true }),
    ]);
    expect(
      screen.queryByRole("button", { name: "Save schedule" }),
    ).not.toBeInTheDocument();
  });

  it("sends configuration_backup_enabled false when it is switched off and saved", async () => {
    const weir = renderBackups({
      [`GET ${SETTINGS_PATH}`]: [
        systemReplies.settingsPutBackupOn,
        systemReplies.settings,
      ],
      [`PUT ${SETTINGS_PATH}`]: systemReplies.settings,
    });

    fireEvent.click(await screen.findByRole("radio", { name: "Off" }));
    fireEvent.click(await button("Save schedule"));

    expect(await screen.findByText("Backup schedule saved.")).toBeVisible();
    expect(sent(weir, "PUT", SETTINGS_PATH)).toEqual([
      savedWith({
        configuration_backup_enabled: false,
      }),
    ]);
  });

  it("says the schedule could not be saved, and keeps Save for another try", async () => {
    renderBackups({
      [`PUT ${SETTINGS_PATH}`]: systemReplies.settingsPutFails,
    });

    fireEvent.click(await screen.findByRole("radio", { name: "On" }));
    fireEvent.click(await button("Save schedule"));

    expect(
      await screen.findByTestId("suite-settings-backup-save-error"),
    ).toHaveTextContent("Could not save settings.");
    expect(screen.getByRole("button", { name: "Save schedule" })).toBeEnabled();
    expect(screen.queryByText("Backup schedule saved.")).toBeNull();
  });
});

describe("Backups: Back up now", () => {
  it("asks for a backup, says it was made and lists it", async () => {
    const weir = renderBackups({
      [`GET ${BACKUPS_PATH}`]: [
        systemReplies.backupsEmpty,
        systemReplies.backupsOne,
      ],
      [`POST ${BACKUPS_PATH}`]: systemReplies.backupNow,
    });

    fireEvent.click(await button("Back up now"));

    expect(await screen.findByText("Backup created.")).toBeVisible();
    expect(sent(weir, "POST", BACKUPS_PATH)).toEqual([
      { csrf_token: CSRF_TOKEN },
    ]);
    expect(
      await screen.findByRole("button", { name: /^Restore the backup taken/ }),
    ).toBeVisible();
  });

  it("says when the backup could not be made, and not that one was", async () => {
    renderBackups({
      [`POST ${BACKUPS_PATH}`]: systemReplies.backupNowFails,
    });

    fireEvent.click(await button("Back up now"));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Could not create a backup.",
    );
    expect(screen.queryByText("Backup created.")).toBeNull();
  });
});

describe("Backups: Download settings", () => {
  const saved: { fileName: string; blob: Blob }[] = [];

  beforeEach(() => {
    saved.length = 0;
    const blobs = new Map<string, Blob>();
    Object.defineProperty(URL, "createObjectURL", {
      configurable: true,
      value: (blob: Blob) => {
        const url = `blob:recorded-${blobs.size}`;
        blobs.set(url, blob);
        return url;
      },
    });
    Object.defineProperty(URL, "revokeObjectURL", {
      configurable: true,
      value: () => undefined,
    });
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      saved.push({
        fileName: this.download,
        blob: blobs.get(this.href) as Blob,
      });
    });
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-10-16T09:30:00Z"));
  });

  afterEach(() => {
    vi.useRealTimers();
    Reflect.deleteProperty(URL, "createObjectURL");
    Reflect.deleteProperty(URL, "revokeObjectURL");
  });

  it("downloads the whole bundle as weir-configuration-<date>.json and says the download started", async () => {
    const weir = renderBackups({
      [`GET ${BUNDLE_PATH}`]: systemReplies.bundle,
    });

    fireEvent.click(await button("Download settings"));

    expect(await screen.findByText("Download started.")).toBeVisible();
    expect(weir.sent("GET", BUNDLE_PATH)).toHaveLength(1);
    expect(saved).toHaveLength(1);
    expect(saved[0].fileName).toBe("weir-configuration-2026-10-16.json");
    expect(saved[0].blob.type).toBe("application/json");
    expect(JSON.parse(await saved[0].blob.text())).toEqual(
      JSON.parse(bundleText),
    );
  });

  it("says the settings could not be exported and saves no file", async () => {
    renderBackups({ [`GET ${BUNDLE_PATH}`]: systemReplies.bundleGetFails });

    fireEvent.click(await button("Download settings"));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Could not export configuration.",
    );
    expect(screen.queryByText("Download started.")).toBeNull();
    expect(saved).toEqual([]);
  });
});

describe("Backups: Restore from a file", () => {
  async function chooseFile(text: string) {
    await button("Restore from file…");
    const input =
      document.querySelector<HTMLInputElement>('input[type="file"]');
    const file = new File([text], "weir-configuration.json", {
      type: "application/json",
    });
    fireEvent.change(input as HTMLInputElement, { target: { files: [file] } });
    return screen.findByTestId(RESTORE_DIALOG);
  }

  it("replaces nothing when the person keeps the current settings", async () => {
    const weir = renderBackups({
      [`PUT ${BUNDLE_PATH}`]: systemReplies.bundlePut,
    });

    const dialog = await chooseFile(bundleText);
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Keep current settings" }),
    );

    await waitFor(() =>
      expect(screen.queryByTestId(RESTORE_DIALOG)).not.toBeInTheDocument(),
    );
    expect(weir.sent("PUT", BUNDLE_PATH)).toEqual([]);
    expect(screen.queryByText("Configuration restored.")).toBeNull();
  });

  it("replaces nothing when the person presses Escape", async () => {
    const weir = renderBackups({
      [`PUT ${BUNDLE_PATH}`]: systemReplies.bundlePut,
    });

    await chooseFile(bundleText);
    fireEvent.keyDown(document, { key: "Escape" });

    await waitFor(() =>
      expect(screen.queryByTestId(RESTORE_DIALOG)).not.toBeInTheDocument(),
    );
    expect(weir.sent("PUT", BUNDLE_PATH)).toEqual([]);
    expect(screen.queryByText("Configuration restored.")).toBeNull();
  });

  it("asks first, then sends the chosen file's bundle as it is and says it was restored", async () => {
    const weir = renderBackups({
      [`PUT ${BUNDLE_PATH}`]: systemReplies.bundlePut,
    });

    const dialog = await chooseFile(bundleText);
    expect(weir.sent("PUT", BUNDLE_PATH)).toEqual([]);
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Replace settings" }),
    );

    expect(await screen.findByText("Configuration restored.")).toBeVisible();
    expect(screen.queryByTestId(RESTORE_DIALOG)).not.toBeInTheDocument();
    expect(sent(weir, "PUT", BUNDLE_PATH)).toStrictEqual([
      { bundle: JSON.parse(bundleText), csrf_token: CSRF_TOKEN },
    ]);
    // Everything read from the server may have changed, the settings among it.
    await waitFor(() =>
      expect(weir.sent("GET", SETTINGS_PATH).length).toBeGreaterThan(1),
    );
  });

  it("shows the server's reason when it refuses the file", async () => {
    const weir = renderBackups({
      [`PUT ${BUNDLE_PATH}`]: systemReplies.bundlePutBadVersion,
    });

    const dialog = await chooseFile(bundleText);
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Replace settings" }),
    );

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "This backup was made by a version of Weir that this one cannot restore.",
    );
    expect(screen.queryByText("Configuration restored.")).toBeNull();
    expect(weir.sent("PUT", BUNDLE_PATH)).toHaveLength(1);
  });
});
