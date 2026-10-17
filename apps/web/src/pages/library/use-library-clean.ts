import { useState } from "react";

import { errorMessage } from "../../lib/api/error-message";
import {
  CLEAN_START_FAILED,
  type LibraryCleanResult,
  LibraryConfirmationRequired,
  LibraryManualPlan,
} from "../../lib/processing/library-mode-api";
import { useCleanLibraryFiles } from "../../lib/processing/library-mode-queries";

/** Where a clean was started from, which is where its confirmation came from and where its result belongs. */
export type CleanSource = "selection" | "file";

/** Exactly what the operator asked to clean. Confirming cleans this and nothing else, whatever changes behind the dialog. */
export interface CleanRequest {
  source: CleanSource;
  paths: string[];
  /** One file's own choice of tracks; without it the library's rules decide. */
  manual?: LibraryManualPlan;
}

export interface CleanConfirmation {
  request: CleanRequest;
  asked: LibraryConfirmationRequired;
}

export interface CleanOutcome {
  request: CleanRequest;
  result: LibraryCleanResult;
}

/**
 * One clean at a time, from the selection or from a file's panel, the same way for both: Weir first asks the
 * server what the clean would remove, shows that in a confirmation, and only then cleans the paths it asked
 * about.
 */
export function useLibraryClean(
  libraryId: number,
  onCleaned: (request: CleanRequest) => void,
) {
  const clean = useCleanLibraryFiles(libraryId);
  const [confirming, setConfirming] = useState<CleanConfirmation | null>(null);
  const [outcome, setOutcome] = useState<CleanOutcome | null>(null);
  const [latest, setLatest] = useState<CleanRequest | null>(null);

  const finish = (request: CleanRequest, result: LibraryCleanResult) => {
    setConfirming(null);
    setOutcome({ request, result });
    onCleaned(request);
  };

  const run = (request: CleanRequest, confirm: boolean) =>
    clean.mutate(
      { paths: request.paths, confirm, manual: request.manual },
      {
        onSuccess: (result) => {
          if (result.kind === "confirmation_required") {
            setConfirming({ request, asked: result });
          } else {
            finish(request, result);
          }
        },
      },
    );

  const failed = clean.isError
    ? errorMessage(clean.error, CLEAN_START_FAILED)
    : null;

  return {
    confirming,
    outcome,
    /** The clean most recently asked for, whether or not it went through. */
    latest,
    /** Asks what cleaning `request` would remove; the clean itself waits for the confirmation. */
    start: (request: CleanRequest) => {
      setLatest(request);
      setOutcome(null);
      run(request, false);
    },
    confirm: () => {
      if (confirming) run(confirming.request, true);
    },
    cancel: () => {
      setConfirming(null);
      clean.reset();
    },
    dismissOutcome: () => setOutcome(null),
    /** Whether a clean started from `source` is waiting on the server. */
    isPending: (source: CleanSource) =>
      clean.isPending && latest?.source === source,
    /** Why the last clean started from `source` could not start, once its confirmation is closed. */
    failure: (source: CleanSource) =>
      !confirming && latest?.source === source ? failed : null,
    /** Why confirming did not go through, shown inside the confirmation. */
    confirmFailure: confirming ? failed : null,
    confirmPending: confirming !== null && clean.isPending,
  };
}

export type LibraryClean = ReturnType<typeof useLibraryClean>;
