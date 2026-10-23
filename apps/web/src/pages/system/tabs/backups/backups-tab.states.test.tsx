import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { systemReplies } from "../../../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  renderWithSettingsForm,
  SAVING,
  SETTINGS,
} from "../../../../test/system-test-support";
import { BackupsTab } from "./backups-tab";

const BACKUPS = "GET /api/v1/suite/configuration-backups";
const DOWNLOAD_ONE = "GET /api/v1/suite/configuration-backups/1/download";
const BACKUP_NOW = "POST /api/v1/suite/configuration-backups";
const RESTORE_DIALOG = "restore-configuration-dialog";

const NEWEST = JSON.parse(systemReplies.backupsOne.body) as {
  directory: string;
  items: {
    id: number;
    created_at: string;
    file_name: string;
    size_bytes: number;
  }[];
};

/** The list of stored backups with the one backup named as given. */
function listing(fileName: string): RecordedReply {
  return {
    status: 200,
    body: JSON.stringify({
      ...NEWEST,
      items: [{ ...NEWEST.items[0], file_name: fileName }],
    }),
  };
}

let weir: RecordedWeir;

function renderBackups(routes: RecordedRoutes) {
  weir = serveRecordedReplies({
    ...SAVING,
    ...SETTINGS,
    [BACKUPS]: systemReplies.backupsEmpty,
    ...routes,
  });
  renderWithSettingsForm((form, settings) => (
    <BackupsTab form={form} editable settings={settings} />
  ));
}

const button = (name: string) => screen.findByRole("button", { name });
const storedBackup = (action: "Download" | "Restore") =>
  screen.findByRole("button", {
    name: new RegExp(`^${action} the backup taken`),
  });

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("Backups: the list of stored backups", () => {
  it("says it is loading the list until the server has answered", async () => {
    renderBackups({ [BACKUPS]: () => new Promise(() => undefined) });

    expect(await screen.findByText("Loading snapshot list...")).toBeVisible();
  });

  it("says no automatic snapshots exist yet when the folder is empty", async () => {
    renderBackups({});

    expect(
      await screen.findByText("No automatic snapshots yet."),
    ).toBeVisible();
  });

  it("says the backups could not be loaded, rather than that there are none, when the server fails", async () => {
    renderBackups({ [BACKUPS]: systemReplies.settingsPutFails });

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Weir couldn't load your backups.",
    );
    expect(screen.queryByText("No automatic snapshots yet.")).toBeNull();
  });

  it("shows the folder the backups are kept in and how many are kept", async () => {
    renderBackups({ [BACKUPS]: systemReplies.backupsOne });

    expect(
      await screen.findByTestId("suite-configuration-backup-directory"),
    ).toHaveTextContent(NEWEST.directory);
    expect(screen.getByText("Keeps the latest 5")).toBeVisible();
  });
});

describe("Backups: the schedule's saved note", () => {
  it("goes away when the schedule is changed again, and Save schedule is offered again", async () => {
    renderBackups({
      [`GET /api/v1/suite/settings`]: [
        systemReplies.settings,
        systemReplies.settingsPutBackupOn,
      ],
      [`PUT /api/v1/suite/settings`]: systemReplies.settingsPutBackupOn,
    });
    fireEvent.click(await screen.findByRole("radio", { name: "On" }));
    fireEvent.click(await button("Save schedule"));
    expect(await screen.findByText("Backup schedule saved.")).toBeVisible();

    fireEvent.click(screen.getByRole("radio", { name: "Off" }));

    expect(screen.queryByText("Backup schedule saved.")).toBeNull();
    expect(screen.getByRole("button", { name: "Save schedule" })).toBeVisible();
  });
});

describe("Backups: while one action runs", () => {
  it("holds Download settings, Restore from file and the stored backups' buttons until Back up now has finished", async () => {
    let finish: (reply: RecordedReply) => void = () => undefined;
    renderBackups({
      [BACKUPS]: systemReplies.backupsOne,
      [BACKUP_NOW]: () => new Promise<RecordedReply>((r) => (finish = r)),
    });
    fireEvent.click(await button("Back up now"));

    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Download settings" }),
      ).toBeDisabled(),
    );
    expect(screen.getByRole("button", { name: "Back up now" })).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Restore from file…" }),
    ).toBeDisabled();
    expect(await storedBackup("Download")).toBeDisabled();
    expect(await storedBackup("Restore")).toBeDisabled();
    finish(systemReplies.backupNow);
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Download settings" }),
      ).toBeEnabled(),
    );
  });
});

describe("Backups: downloading one stored backup", () => {
  const saved: { fileName: string; blob: Blob }[] = [];

  beforeEach(() => {
    saved.length = 0;
    const blobs = new Map<string, Blob>();
    Object.defineProperty(URL, "createObjectURL", {
      configurable: true,
      value: (blob: Blob) => {
        const url = `blob:stored-${blobs.size}`;
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
  });

  afterEach(() => {
    Reflect.deleteProperty(URL, "createObjectURL");
    Reflect.deleteProperty(URL, "revokeObjectURL");
  });

  it("saves the file the server sent under the backup's own name", async () => {
    renderBackups({
      [BACKUPS]: systemReplies.backupsOne,
      [DOWNLOAD_ONE]: systemReplies.bundle,
    });

    fireEvent.click(await storedBackup("Download"));

    expect(await screen.findByText("Download started.")).toBeVisible();
    expect(
      weir.sent("GET", "/api/v1/suite/configuration-backups/1/download"),
    ).toHaveLength(1);
    expect(saved).toHaveLength(1);
    expect(saved[0].fileName).toBe(NEWEST.items[0].file_name);
    expect(await saved[0].blob.text()).toBe(systemReplies.bundle.body);
  });

  it("keeps only letters, digits, dots, dashes and underscores in the file name, and at most 120 characters", async () => {
    renderBackups({
      [BACKUPS]: listing(`my backup: 10/2026 ${"x".repeat(200)}.json`),
      [DOWNLOAD_ONE]: systemReplies.bundle,
    });

    fireEvent.click(await storedBackup("Download"));

    await screen.findByText("Download started.");
    expect(saved[0].fileName).toMatch(/^my_backup_10_2026_x+$/);
    expect(saved[0].fileName).toHaveLength(120);
  });

  it("says the backup could not be downloaded, and saves nothing, when the server fails", async () => {
    renderBackups({
      [BACKUPS]: systemReplies.backupsOne,
      [DOWNLOAD_ONE]: systemReplies.bundleGetFails,
    });

    fireEvent.click(await storedBackup("Download"));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Could not download automatic snapshot.",
    );
    expect(screen.queryByText("Download started.")).toBeNull();
    expect(saved).toEqual([]);
  });
});

describe("Backups: restoring", () => {
  const restoreFromFile = async (text: string) => {
    await button("Restore from file…");
    const input = document.querySelector<HTMLInputElement>(
      'input[type="file"]',
    ) as HTMLInputElement;
    fireEvent.change(input, {
      target: {
        files: [
          new File([text], "weir-configuration.json", {
            type: "application/json",
          }),
        ],
      },
    });
    return input;
  };

  it("says a file that is not JSON is not valid JSON, and opens no dialog", async () => {
    renderBackups({});

    await restoreFromFile("{ not json");

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "This file is not valid JSON.",
    );
    expect(screen.queryByTestId(RESTORE_DIALOG)).toBeNull();
  });

  it.each(["null", "42", '"text"'])(
    "says a file holding only %s is not valid JSON, and opens no dialog",
    async (text) => {
      renderBackups({});

      await restoreFromFile(text);

      expect(await screen.findByRole("alert")).toHaveTextContent(
        "This file is not valid JSON.",
      );
      expect(screen.queryByTestId(RESTORE_DIALOG)).toBeNull();
    },
  );

  it("says a JSON file that is not a Weir export is not one, and opens no dialog", async () => {
    renderBackups({});

    await restoreFromFile('{"hello": 1}');

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "This file is not a Weir configuration export.",
    );
    expect(screen.queryByTestId(RESTORE_DIALOG)).toBeNull();
  });

  it("asks again when the same file is chosen a second time, after the first choice was set aside", async () => {
    renderBackups({});
    const input = await restoreFromFile(systemReplies.bundle.body);
    fireEvent.click(
      within(await screen.findByTestId(RESTORE_DIALOG)).getByRole("button", {
        name: "Keep current settings",
      }),
    );
    await waitFor(() =>
      expect(screen.queryByTestId(RESTORE_DIALOG)).toBeNull(),
    );
    expect(input.value).toBe("");

    await restoreFromFile(systemReplies.bundle.body);

    expect(await screen.findByTestId(RESTORE_DIALOG)).toBeVisible();
  });

  it("says a stored backup could not be read when the server cannot give it, and opens no dialog", async () => {
    renderBackups({
      [BACKUPS]: systemReplies.backupsOne,
      [DOWNLOAD_ONE]: systemReplies.bundleGetFails,
    });

    fireEvent.click(await storedBackup("Restore"));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Could not download automatic snapshot.",
    );
    expect(screen.queryByTestId(RESTORE_DIALOG)).toBeNull();
  });

  it("says a stored backup that is not a Weir export is not one, and opens no dialog", async () => {
    renderBackups({
      [BACKUPS]: systemReplies.backupsOne,
      [DOWNLOAD_ONE]: { status: 200, body: '{"hello": 1}' },
    });

    fireEvent.click(await storedBackup("Restore"));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "This file is not a Weir configuration export.",
    );
    expect(screen.queryByTestId(RESTORE_DIALOG)).toBeNull();
  });

  it("opens the restore dialog for a stored backup that is a Weir export", async () => {
    renderBackups({
      [BACKUPS]: systemReplies.backupsOne,
      [DOWNLOAD_ONE]: systemReplies.bundle,
    });

    fireEvent.click(await storedBackup("Restore"));

    expect(await screen.findByTestId(RESTORE_DIALOG)).toBeVisible();
  });
});
