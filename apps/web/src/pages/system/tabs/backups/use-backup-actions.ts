import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";

import { errorMessage } from "../../../../lib/api/error-message";
import {
  fetchConfigurationBundle,
  fetchStoredConfigurationBackupBlob,
  postConfigurationBackupNow,
  putConfigurationBundle,
  type ConfigurationBundle,
} from "../../../../lib/settings/settings-api";
import { settingsKeys } from "../../../../lib/settings/query-keys";
import { saveBlobAs } from "../../../../lib/ui/save-file";

/** The part of a backup that holds the connections' secrets, locked with the backup password. */
const SEALED_SECRETS = "sealed_secrets";

/** The longest file name a stored snapshot is saved under. */
const MAX_SNAPSHOT_NAME = 120;

/** Reads a configuration export from text, or says in plain words why it is not one. */
function parseBundle(
  text: string,
): { bundle: ConfigurationBundle } | { problem: string } {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return { problem: "This file is not valid JSON." };
  }
  if (typeof parsed !== "object" || parsed === null) {
    return { problem: "This file is not valid JSON." };
  }
  // Which format versions restore is the server's call; it answers 400 with a reason for one it cannot take.
  if (typeof (parsed as ConfigurationBundle).format_version !== "number") {
    return { problem: "This file is not a Weir configuration export." };
  }
  return { bundle: parsed as ConfigurationBundle };
}

/** Reads a chosen file as a configuration export, or says in plain words why it is not one. */
async function readBundle(
  file: File,
): Promise<{ bundle: ConfigurationBundle } | { problem: string }> {
  return parseBundle(await file.text());
}

/**
 * Downloading and restoring Weir's configuration. A restore is two steps: `chooseRestoreFile` (or
 * `chooseSavedBackup`) checks the file and holds it, and nothing is replaced until `confirmRestore`.
 */
export function useBackupActions() {
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [pendingRestore, setPendingRestore] =
    useState<ConfigurationBundle | null>(null);
  const [restoreProblem, setRestoreProblem] = useState<string | null>(null);

  const run = async (work: () => Promise<string>, failure: string) => {
    setProblem(null);
    setMessage(null);
    setBusy(true);
    try {
      setMessage(await work());
    } catch (error) {
      setProblem(errorMessage(error, failure));
    } finally {
      setBusy(false);
    }
  };

  return {
    busy,
    pendingRestore,
    /** Whether the backup waiting to be restored holds secrets that only the backup password opens. */
    restoreNeedsPassword:
      pendingRestore !== null && SEALED_SECRETS in pendingRestore,
    /** Why the last attempt to restore it was refused, while the confirmation is still open. */
    restoreProblem,
    /** What the last action did, or why it could not. */
    result: { message, problem },
    downloadConfiguration: () =>
      run(async () => {
        const bundle = await fetchConfigurationBundle();
        const date = new Date().toISOString().slice(0, 10);
        saveBlobAs(
          new Blob([JSON.stringify(bundle, null, 2)], {
            type: "application/json",
          }),
          `weir-configuration-${date}.json`,
        );
        return "Download started.";
      }, "Could not export."),
    backUpNow: () =>
      run(async () => {
        await postConfigurationBackupNow();
        await queryClient.invalidateQueries({
          queryKey: settingsKeys.configurationBackups,
        });
        return "Backup created.";
      }, "Could not create a backup."),
    downloadSnapshot: (id: number, fileName: string) =>
      run(async () => {
        const blob = await fetchStoredConfigurationBackupBlob(id);
        saveBlobAs(
          blob,
          fileName.replace(/[^\w.-]+/g, "_").slice(0, MAX_SNAPSHOT_NAME),
        );
        return "Download started.";
      }, "Could not download snapshot."),
    chooseRestoreFile: async (file: File) => {
      setProblem(null);
      setMessage(null);
      const read = await readBundle(file);
      if ("problem" in read) {
        setProblem(read.problem);
      } else {
        setRestoreProblem(null);
        setPendingRestore(read.bundle);
      }
    },
    chooseSavedBackup: async (id: number) => {
      setProblem(null);
      setMessage(null);
      setBusy(true);
      try {
        const blob = await fetchStoredConfigurationBackupBlob(id);
        const read = parseBundle(await blob.text());
        if ("problem" in read) {
          setProblem(read.problem);
        } else {
          setRestoreProblem(null);
          setPendingRestore(read.bundle);
        }
      } catch (error) {
        setProblem(errorMessage(error, "Could not read this backup."));
      } finally {
        setBusy(false);
      }
    },
    cancelRestore: () => {
      setRestoreProblem(null);
      setPendingRestore(null);
    },
    /** Restores the backup held for confirmation. `password` is the backup password, asked for only when the backup holds locked secrets. */
    confirmRestore: (password?: string) => {
      const bundle = pendingRestore;
      if (!bundle) return;
      const restore = async () => {
        await putConfigurationBundle(bundle, password || undefined);
        // Everything read from the server may have changed, the settings drafts included.
        await queryClient.invalidateQueries();
        return "Configuration restored.";
      };
      if (!(SEALED_SECRETS in bundle)) {
        setPendingRestore(null);
        void run(restore, "Could not restore.");
        return;
      }
      // A wrong password leaves the confirmation open, so the password can be typed again.
      setRestoreProblem(null);
      setProblem(null);
      setMessage(null);
      setBusy(true);
      void restore()
        .then((done) => {
          setPendingRestore(null);
          setMessage(done);
        })
        .catch((error: unknown) =>
          setRestoreProblem(errorMessage(error, "Could not restore.")),
        )
        .finally(() => setBusy(false));
    },
  };
}
