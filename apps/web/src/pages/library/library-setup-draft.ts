import { useState } from "react";

import { asSentence } from "../../lib/api/api-error-text";
import { errorMessage } from "../../lib/api/error-message";
import type { LibrarySettings } from "../../lib/processing/library-mode-api";
import {
  useSaveLibrarySettings,
  useSetLibrarySchedule,
} from "../../lib/processing/library-mode-queries";

/** A library's setup as the setup panel holds it until Save: the saved settings, edited. */
export type SetupDraft = LibrarySettings;

function sameFolders(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((folder, i) => folder === b[i]);
}

/** Everything the settings endpoint saves; the daily clean goes through its own endpoint. */
function settingsChanged(draft: SetupDraft, saved: SetupDraft): boolean {
  return (
    !sameFolders(draft.library_folders, saved.library_folders) ||
    draft.clean_hardlinked_files !== saved.clean_hardlinked_files ||
    draft.skip_if_manager_would_redownload !==
      saved.skip_if_manager_would_redownload ||
    draft.keep_original_after_clean !== saved.keep_original_after_clean ||
    draft.originals_folder !== saved.originals_folder ||
    draft.library_rule_set_id !== saved.library_rule_set_id
  );
}

/** What to tell a person whose other changes went through but whose daily clean change did not, so they do not think nothing was saved. */
function partialSaveMessage(switchingOn: boolean, error: unknown): string {
  const reason = errorMessage(error, "Weir could not change it.");
  return `Your other changes were saved, but the daily clean was not switched ${switchingOn ? "on" : "off"}. ${asSentence(reason)}`;
}

/**
 * One library's setup, held as a draft so Save and Cancel cover every field of the panel together.
 * The draft starts from the settings the page already read; open the panel again for a fresh one.
 */
export function useLibrarySetupDraft(libraryId: number, saved: SetupDraft) {
  const saveSettings = useSaveLibrarySettings(libraryId);
  const setSchedule = useSetLibrarySchedule(libraryId);
  const [draft, setDraft] = useState<SetupDraft>(saved);

  const dirty =
    settingsChanged(draft, saved) ||
    draft.library_schedule_enabled !== saved.library_schedule_enabled;

  /**
   * Folders and checks first, because the daily clean needs folders to switch on. The daily clean is
   * only ever switched on here after its warning was accepted, so it goes with its confirmation.
   */
  const save = async (): Promise<void> => {
    if (!dirty) return;
    const savingSettings = settingsChanged(draft, saved);
    if (savingSettings) {
      await saveSettings.mutateAsync({
        library_folders: draft.library_folders,
        clean_hardlinked_files: draft.clean_hardlinked_files,
        skip_if_manager_would_redownload:
          draft.skip_if_manager_would_redownload,
        keep_original_after_clean: draft.keep_original_after_clean,
        originals_folder: draft.originals_folder,
        library_rule_set_id: draft.library_rule_set_id,
      });
    }
    if (draft.library_schedule_enabled !== saved.library_schedule_enabled) {
      try {
        const result = await setSchedule.mutateAsync({
          enabled: draft.library_schedule_enabled,
          confirm: draft.library_schedule_enabled,
        });
        if ("kind" in result) throw new Error(result.detail);
      } catch (error) {
        throw savingSettings
          ? new Error(partialSaveMessage(draft.library_schedule_enabled, error))
          : error;
      }
    }
  };

  return {
    draft,
    dirty,
    change: (patch: Partial<SetupDraft>) =>
      setDraft((current) => ({ ...current, ...patch })),
    save,
  };
}

export type LibrarySetupDraft = ReturnType<typeof useLibrarySetupDraft>;
