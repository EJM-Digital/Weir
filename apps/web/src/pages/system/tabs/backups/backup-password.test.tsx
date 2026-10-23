import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { systemReplies } from "../../../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  CSRF_TOKEN,
  renderWithSettingsForm,
  SAVING,
  SETTINGS,
} from "../../../../test/system-test-support";
import { BackupsTab } from "./backups-tab";

const BACKUPS_PATH = "/api/v1/suite/configuration-backups";
const BUNDLE_PATH = "/api/v1/suite/configuration-bundle";
const PASSWORD_PATH = "/api/v1/suite/backup-password";
const RESTORE_DIALOG = "restore-configuration-dialog";
const BACKUP_PASSWORD = "recorded backup password";

/** A backup the server made with a backup password set: its secrets are locked in the file. */
const sealedBackupText = systemReplies.sealedBackupFile.body;
const plainBackupText = systemReplies.bundle.body;

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

const sent = (weir: RecordedWeir, method: string, path: string) =>
  weir.sent(method, path).map((request) => request.body);

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const typeInto = (label: string, value: string) =>
  fireEvent.change(screen.getByLabelText(label, { selector: "input" }), {
    target: { value },
  });

describe("Backups: the backup password", () => {
  it("says that without a password the keys and passwords are left out, and offers to set one", async () => {
    renderBackups({});

    expect(
      await screen.findByTestId("backup-password-state"),
    ).toHaveTextContent("Not set");
    expect(screen.getByTestId("backup-password-words")).toHaveTextContent(
      "No backup password is set, so backups leave out your connections' API keys, passwords and alert addresses.",
    );
    expect(
      screen.getByRole("button", { name: "Set backup password" }),
    ).toBeDisabled();
  });

  it("sends the password typed twice, then says it is set and that older backups keep theirs", async () => {
    const weir = renderBackups({
      [`GET ${BACKUPS_PATH}`]: [
        systemReplies.backupsEmpty,
        systemReplies.backupsSealed,
      ],
      [`PUT ${PASSWORD_PATH}`]: systemReplies.backupPasswordSet,
    });

    await screen.findByTestId("backup-password-state");
    typeInto("Backup password", BACKUP_PASSWORD);
    expect(
      screen.getByRole("button", { name: "Set backup password" }),
    ).toBeDisabled();
    typeInto("Type it again", BACKUP_PASSWORD);
    fireEvent.click(
      screen.getByRole("button", { name: "Set backup password" }),
    );

    expect(
      await screen.findByText(
        "Backup password saved. Backups from now on use it.",
      ),
    ).toBeVisible();
    expect(sent(weir, "PUT", PASSWORD_PATH)).toStrictEqual([
      {
        password: BACKUP_PASSWORD,
        confirm_password: BACKUP_PASSWORD,
        csrf_token: CSRF_TOKEN,
      },
    ]);
    await waitFor(() =>
      expect(screen.getByTestId("backup-password-state")).toHaveTextContent(
        "Set",
      ),
    );
    expect(screen.getByTestId("backup-password-words")).toHaveTextContent(
      "Restoring one asks for it.",
    );
    expect(
      screen.getByText(
        /Backups already made keep the password they were made with\./,
      ),
    ).toBeVisible();
    expect(
      screen.getByRole("button", { name: "Change backup password" }),
    ).toBeVisible();
    // Neither box keeps the password once it is saved.
    expect(screen.getByLabelText("New backup password")).toHaveValue("");
    expect(screen.getByLabelText("Type it again")).toHaveValue("");
  });

  it("sends nothing when the two boxes differ, and says so", async () => {
    const weir = renderBackups({});

    await screen.findByTestId("backup-password-state");
    typeInto("Backup password", BACKUP_PASSWORD);
    typeInto("Type it again", "another one entirely");
    fireEvent.click(
      screen.getByRole("button", { name: "Set backup password" }),
    );

    expect(
      await screen.findByTestId("backup-password-error"),
    ).toHaveTextContent(
      "The two backup passwords do not match. Type the same password in both boxes.",
    );
    expect(weir.sent("PUT", PASSWORD_PATH)).toEqual([]);
  });

  it("shows the server's reason for a password that is too weak and keeps what was typed", async () => {
    renderBackups({
      [`PUT ${PASSWORD_PATH}`]: systemReplies.backupPasswordWeak,
    });

    await screen.findByTestId("backup-password-state");
    typeInto("Backup password", "short");
    typeInto("Type it again", "short");
    fireEvent.click(
      screen.getByRole("button", { name: "Set backup password" }),
    );

    expect(
      await screen.findByTestId("backup-password-error"),
    ).toHaveTextContent("Password must be at least 8 characters.");
    expect(
      screen.getByLabelText("Backup password", { selector: "input" }),
    ).toHaveValue("short");
    expect(screen.getByTestId("backup-password-state")).toHaveTextContent(
      "Not set",
    );
  });
});

describe("Backups: restoring a backup with locked secrets", () => {
  async function chooseFile(text: string) {
    await screen.findByRole("button", { name: "Restore from file…" });
    const input =
      document.querySelector<HTMLInputElement>('input[type="file"]');
    const file = new File([text], "weir-configuration.json", {
      type: "application/json",
    });
    fireEvent.change(input as HTMLInputElement, { target: { files: [file] } });
    return screen.findByTestId(RESTORE_DIALOG);
  }

  it("asks for the backup password, and will not restore until one is typed", async () => {
    const weir = renderBackups({
      [`PUT ${BUNDLE_PATH}`]: systemReplies.bundlePutSealed,
    });

    const dialog = await chooseFile(sealedBackupText);

    expect(dialog).toHaveTextContent(
      "locked with a backup password. Enter the password it was made with.",
    );
    const replace = within(dialog).getByRole("button", {
      name: "Replace settings",
    });
    expect(replace).toBeDisabled();
    expect(weir.sent("PUT", BUNDLE_PATH)).toEqual([]);
    fireEvent.change(within(dialog).getByLabelText("Backup password"), {
      target: { value: BACKUP_PASSWORD },
    });
    expect(replace).toBeEnabled();
  });

  it("sends the password with the backup and says it was restored", async () => {
    const weir = renderBackups({
      [`PUT ${BUNDLE_PATH}`]: systemReplies.bundlePutSealed,
    });

    const dialog = await chooseFile(sealedBackupText);
    fireEvent.change(within(dialog).getByLabelText("Backup password"), {
      target: { value: BACKUP_PASSWORD },
    });
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Replace settings" }),
    );

    expect(await screen.findByText("Configuration restored.")).toBeVisible();
    expect(screen.queryByTestId(RESTORE_DIALOG)).not.toBeInTheDocument();
    expect(sent(weir, "PUT", BUNDLE_PATH)).toStrictEqual([
      {
        bundle: JSON.parse(sealedBackupText),
        password: BACKUP_PASSWORD,
        csrf_token: CSRF_TOKEN,
      },
    ]);
  });

  it("stays open on a wrong password, says so, and restores once the right one is typed", async () => {
    const weir = renderBackups({
      [`PUT ${BUNDLE_PATH}`]: [
        systemReplies.bundlePutWrongPassword,
        systemReplies.bundlePutSealed,
      ],
    });

    const dialog = await chooseFile(sealedBackupText);
    fireEvent.change(within(dialog).getByLabelText("Backup password"), {
      target: { value: "not the password" },
    });
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Replace settings" }),
    );

    expect(await within(dialog).findByRole("alert")).toHaveTextContent(
      "That is not the backup password this backup was made with. Nothing was changed.",
    );
    expect(screen.getByTestId(RESTORE_DIALOG)).toBeVisible();
    expect(screen.queryByText("Configuration restored.")).toBeNull();

    fireEvent.change(within(dialog).getByLabelText("Backup password"), {
      target: { value: BACKUP_PASSWORD },
    });
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Replace settings" }),
    );

    expect(await screen.findByText("Configuration restored.")).toBeVisible();
    expect(weir.sent("PUT", BUNDLE_PATH)).toHaveLength(2);
  });

  it("does not ask for a password for a backup made without one", async () => {
    const weir = renderBackups({
      [`PUT ${BUNDLE_PATH}`]: systemReplies.bundlePut,
    });

    const dialog = await chooseFile(plainBackupText);

    expect(within(dialog).queryByLabelText("Backup password")).toBeNull();
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Replace settings" }),
    );
    expect(await screen.findByText("Configuration restored.")).toBeVisible();
    expect(sent(weir, "PUT", BUNDLE_PATH)).toStrictEqual([
      { bundle: JSON.parse(plainBackupText), csrf_token: CSRF_TOKEN },
    ]);
  });

  it("forgets a wrong password's refusal when the dialog is closed and another backup is chosen", async () => {
    renderBackups({
      [`PUT ${BUNDLE_PATH}`]: systemReplies.bundlePutNeedsPassword,
    });

    const dialog = await chooseFile(sealedBackupText);
    fireEvent.change(within(dialog).getByLabelText("Backup password"), {
      target: { value: "anything" },
    });
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Replace settings" }),
    );
    expect(await within(dialog).findByRole("alert")).toHaveTextContent(
      "Enter the backup password to restore it.",
    );
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Keep current settings" }),
    );
    await waitFor(() =>
      expect(screen.queryByTestId(RESTORE_DIALOG)).not.toBeInTheDocument(),
    );

    const again = await chooseFile(sealedBackupText);

    expect(within(again).queryByRole("alert")).toBeNull();
    expect(within(again).getByLabelText("Backup password")).toHaveValue("");
  });
});
