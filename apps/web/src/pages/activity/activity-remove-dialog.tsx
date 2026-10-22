import { useRef, useState } from "react";

import { FileName } from "../../components/shared/file-name";
import { ConfirmDialog } from "../../components/ui/confirm-dialog";
import { formatBytes } from "../../lib/format/bytes";
import type {
  ProcessingFileRemoveOptions,
  ProcessingFileRemovalResolution,
} from "../../lib/processing/files-api";
import { useAppDateFormatter } from "../../lib/ui/mm-format-date";

/** The four choices offered when a title's file is still in the watched folder (#785). */
type ActivityRemoveChoice = Extract<
  ProcessingFileRemovalResolution,
  "remove" | "delete" | "keep" | "retry"
>;

/**
 * The label and explanation for each choice, built from what `remove-options` reported. "remove" is always
 * offered, whatever the other three say, since it is the one choice that never needs the file's identity
 * confirmed — it never touches the file at all (#786 follow-up).
 */
function choices(options: ProcessingFileRemoveOptions): {
  value: ActivityRemoveChoice;
  label: string;
  detail: string;
  /** Why this choice cannot be picked, when it cannot. */
  unavailable?: string;
}[] {
  const manager = options.manager_label;
  return [
    {
      value: "delete",
      label:
        options.delete_handled_by_manager && manager
          ? `Delete the download and ask ${manager} for another copy`
          : "Delete the file",
      detail:
        options.delete_refused_reason ??
        (options.delete_handled_by_manager && manager
          ? `${manager} removes it, blocks this release and searches again.`
          : "Weir alone: the file is deleted."),
      unavailable: options.delete_refused_reason ?? undefined,
    },
    {
      value: "keep",
      label: "Keep the file, but don't process it again",
      detail:
        options.keep_notifies_manager && manager
          ? `Weir leaves it until it changes. ${manager} is told it won't be imported.`
          : "Weir leaves it until it changes.",
    },
    {
      value: "retry",
      label: "Try it again",
      detail: "Weir processes it on the next pass.",
    },
    {
      value: "remove",
      label: "Just remove it from the list",
      detail:
        "Leaves the file exactly as it is. Weir stops watching it, so a later scan could pick it up again.",
    },
  ];
}

/**
 * Activity's remove dialog for a failed or rejected title whose file is still in the watched folder (#785):
 * delete the download (naming whichever manager will do it, or saying Weir will delete it itself), keep it
 * without processing it again, try it again, or just remove it from the list. A title that does not qualify for
 * this — finished, or its file already gone — keeps the plain confirm in {@link ActivityFileActions} instead of
 * this dialog. When `options.fingerprint_recorded` is false (a title that failed or was rejected before Weir
 * started recording one, #786 follow-up), the file Weir means is shown plainly so the owner can check it before
 * choosing delete or keep — "remove" needs no such check, since it never touches the file. Nothing is chosen when
 * the dialog opens, Remove stays disabled until something is, and focus starts on the first choice, so nothing
 * happens to the file unless the person picks what should (#838).
 */
export function ActivityRemoveDialog({
  fileName,
  options,
  busy,
  error,
  onCancel,
  onConfirm,
}: {
  fileName: string;
  options: ProcessingFileRemoveOptions;
  busy: boolean;
  error: string | null;
  onCancel: () => void;
  onConfirm: (resolution: ActivityRemoveChoice) => void;
}) {
  const [choice, setChoice] = useState<ActivityRemoveChoice | null>(null);
  const firstChoice = useRef<HTMLInputElement>(null);
  const formatDate = useAppDateFormatter();
  const choiceList = choices(options);
  const firstAvailable = choiceList.findIndex(
    (opt) => opt.unavailable === undefined,
  );

  return (
    <ConfirmDialog
      testId="activity-remove-dialog"
      title={`Remove "${fileName}" from Activity?`}
      confirmLabel="Remove"
      tone="danger"
      cancelLabel="Cancel"
      busy={busy}
      busyLabel="Removing…"
      confirmDisabled={choice === null}
      initialFocus={firstChoice}
      error={error}
      onCancel={onCancel}
      onConfirm={() => {
        if (choice) onConfirm(choice);
      }}
      description={
        <fieldset>
          <legend className="mb-2">
            Its file is still in the watched folder.
          </legend>
          {!options.fingerprint_recorded ? (
            <div
              className="mb-3"
              data-testid="activity-remove-dialog-unconfirmed"
            >
              <p className="font-medium">
                <FileName path={fileName} />
                {options.unconfirmed_size_bytes === null
                  ? ""
                  : ` · ${formatBytes(options.unconfirmed_size_bytes)}`}
                {options.unconfirmed_modified_at
                  ? ` · modified ${formatDate(options.unconfirmed_modified_at)}`
                  : ""}
              </p>
              <p className="mm-quiet-note">
                Weir didn&apos;t note this file&apos;s details when it failed,
                so check it&apos;s the one you mean.
              </p>
            </div>
          ) : null}
          {choiceList.map((opt, index) => (
            <label
              key={opt.value}
              data-testid={`activity-remove-dialog-choice-${opt.value}`}
              className={`mm-dialog-choice${choice === opt.value ? " mm-dialog-choice--chosen" : ""}`}
            >
              <input
                ref={index === firstAvailable ? firstChoice : undefined}
                type="radio"
                name="activity-remove-resolution"
                value={opt.value}
                checked={choice === opt.value}
                disabled={opt.unavailable !== undefined}
                onChange={() => setChoice(opt.value)}
                className="mt-0.5 h-4 w-4 shrink-0 accent-mm-accent"
              />
              <span className="min-w-0">
                <span className="block font-medium">{opt.label}</span>
                <span className="block text-xs text-mm-text3">
                  {opt.detail}
                </span>
              </span>
            </label>
          ))}
        </fieldset>
      }
    />
  );
}
