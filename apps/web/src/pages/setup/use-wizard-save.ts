import { useNavigate } from "react-router-dom";

import { errorMessage } from "../../lib/api/error-message";
import {
  writeFromProcessingLibrary,
  type ProcessingLibrary,
  type ProcessingMediaType,
} from "../../lib/processing/libraries-api";
import {
  useCreateProcessingLibrary,
  useUpdateProcessingLibrary,
} from "../../lib/processing/libraries-queries";
import { useAppSettingsSaveMutation } from "../../lib/settings/queries";
import type { AppSettings } from "../../lib/settings/types";
import type { BackupDraft } from "./setup-wizard-parts";
import type { SuggestedLibraryPlan } from "./use-suggested-libraries";

export type LibraryFolders = { watched: string; output: string };

export type WizardDraft = {
  timezone: string;
  backup: BackupDraft;
  movie: LibraryFolders;
  tv: LibraryFolders;
};

/** Where the libraries come from: folders typed by hand, or the libraries offered from what was connected. */
export type LibraryChoice =
  { kind: "typed" } | { kind: "offered"; plan: SuggestedLibraryPlan[] };

const LIBRARY_NAMES: Record<ProcessingMediaType, string> = {
  movie: "Movies",
  tv: "TV",
};

const TRAILING_SEPARATORS = /[\\/]+$/;

/** The library the wizard edits for a media type: the first one, when any exist. */
export function firstLibraryOfType(
  libraries: ProcessingLibrary[] | undefined,
  mediaType: ProcessingMediaType,
): ProcessingLibrary | undefined {
  return (libraries ?? [])
    .filter((library) => library.media_type === mediaType)
    .sort((a, b) => a.display_order - b.display_order)[0];
}

/** Why the draft cannot be saved yet, or null when it can. */
function missingOutputFolder(draft: WizardDraft): string | null {
  if (draft.tv.watched.trim() && !draft.tv.output.trim()) {
    return "Add a TV output folder: it is needed when a TV watched folder is set.";
  }
  if (draft.movie.watched.trim() && !draft.movie.output.trim()) {
    return "Add a Movies output folder: it is needed when a Movies watched folder is set.";
  }
  return null;
}

function comparableFolder(folder: string): string {
  return folder.trim().replace(TRAILING_SEPARATORS, "").toLowerCase();
}

/**
 * The library an offered one is saved into: the empty one it was offered for, or, when a first attempt already
 * created it and only a later step failed, the library now sitting at the same watched folder.
 */
function libraryToFill(
  libraries: ProcessingLibrary[],
  offered: SuggestedLibraryPlan,
): ProcessingLibrary | undefined {
  return libraries.find((library) =>
    offered.libraryId !== null
      ? library.id === offered.libraryId
      : library.media_type === offered.mediaType &&
        comparableFolder(library.watched_folder) ===
          comparableFolder(offered.watched),
  );
}

/** Where setup stands: "pending" until it is finished or skipped. */
export function wizardStateOf(settings: AppSettings | undefined): string {
  return (settings?.setup_wizard_state || "pending").trim().toLowerCase();
}

/** Saves the wizard: the libraries first, then the app settings. */
export function useWizardSave({
  settings,
  libraries,
  onMessage,
  onFinished,
}: {
  settings: AppSettings;
  libraries: ProcessingLibrary[] | undefined;
  onMessage: (message: string | null) => void;
  /** Called once the wizard's own data has saved and `setup_wizard_state` is "completed". */
  onFinished: () => void;
}) {
  const navigate = useNavigate();
  const saveAppSettings = useAppSettingsSaveMutation();
  const createLibrary = useCreateProcessingLibrary();
  const updateLibrary = useUpdateProcessingLibrary();

  /**
   * The wizard edits the first library of each media type. When there is none yet it adds one, but
   * only if a folder was entered: an empty library would do nothing.
   */
  async function saveTypedLibrary(
    current: ProcessingLibrary[],
    mediaType: ProcessingMediaType,
    folders: LibraryFolders,
  ) {
    const watched = folders.watched.trim();
    const output = folders.output.trim();
    const existing = firstLibraryOfType(current, mediaType);
    if (existing) {
      if (
        existing.watched_folder === watched &&
        existing.output_folder === output
      ) {
        return;
      }
      await updateLibrary.mutateAsync({
        id: existing.id,
        data: {
          ...writeFromProcessingLibrary(existing),
          watched_folder: watched,
          output_folder: output,
        },
      });
      return;
    }
    if (!watched && !output) return;
    await createLibrary.mutateAsync({
      name: LIBRARY_NAMES[mediaType],
      media_type: mediaType,
      watched_folder: watched,
      output_folder: output,
    });
  }

  /** An offered library fills in the empty one it was offered for, and is linked to the managers that cover it. */
  async function saveOfferedLibrary(
    current: ProcessingLibrary[],
    offered: SuggestedLibraryPlan,
  ) {
    const existing = libraryToFill(current, offered);
    if (existing) {
      await updateLibrary.mutateAsync({
        id: existing.id,
        data: {
          ...writeFromProcessingLibrary(existing),
          watched_folder: offered.watched,
          output_folder: offered.output,
          manager_connection_ids: [
            ...new Set([
              ...existing.manager_connection_ids,
              ...offered.managerConnectionIds,
            ]),
          ],
        },
      });
      return;
    }
    await createLibrary.mutateAsync({
      name: offered.name,
      media_type: offered.mediaType,
      watched_folder: offered.watched,
      output_folder: offered.output,
      manager_connection_ids: offered.managerConnectionIds,
    });
  }

  /**
   * Skipping leaves what is already saved alone: only the wizard's own state moves on, and only while it is pending.
   * A setup that is already finished or skipped stays as it is when its reopened wizard is skipped.
   */
  async function skip() {
    onMessage(null);
    try {
      await saveAppSettings.mutateAsync({
        setup_wizard_state:
          wizardStateOf(settings) === "pending"
            ? "skipped"
            : settings.setup_wizard_state,
        app_timezone: settings.app_timezone,
        log_retention_days: settings.log_retention_days,
        configuration_backup_enabled: settings.configuration_backup_enabled,
        configuration_backup_interval_hours:
          settings.configuration_backup_interval_hours,
        configuration_backup_preferred_time:
          settings.configuration_backup_preferred_time,
      });
      void navigate("/", { replace: true });
    } catch (err) {
      onMessage(errorMessage(err, "Could not skip setup."));
    }
  }

  async function saveLibraries(draft: WizardDraft, choice: LibraryChoice) {
    if (!libraries) return;
    if (choice.kind === "offered") {
      for (const offered of choice.plan) {
        await saveOfferedLibrary(libraries, offered);
      }
      return;
    }
    await saveTypedLibrary(libraries, "movie", draft.movie);
    await saveTypedLibrary(libraries, "tv", draft.tv);
  }

  /**
   * Libraries save before settings, so a library failure never leaves `setup_wizard_state` marked
   * "completed" over work that did not actually finish.
   */
  async function finish(draft: WizardDraft, choice: LibraryChoice) {
    onMessage(null);
    const missing = choice.kind === "typed" ? missingOutputFolder(draft) : null;
    if (missing) {
      onMessage(missing);
      return;
    }
    try {
      await saveLibraries(draft, choice);
      await saveAppSettings.mutateAsync({
        setup_wizard_state: "completed",
        app_timezone: draft.timezone,
        log_retention_days: settings.log_retention_days,
        configuration_backup_enabled: draft.backup.enabled,
        configuration_backup_interval_hours: Number.parseInt(
          draft.backup.intervalHours,
          10,
        ),
        configuration_backup_preferred_time: draft.backup.preferredTime,
      });
      onFinished();
    } catch (err) {
      onMessage(errorMessage(err, "Could not save setup."));
    }
  }

  const pending =
    saveAppSettings.isPending ||
    createLibrary.isPending ||
    updateLibrary.isPending;
  return { finish, skip, pending };
}
