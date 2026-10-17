import { errorMessage } from "../../lib/api/error-message";
import {
  LEAVE_ALONE_FAILED,
  type LibraryFile,
} from "../../lib/processing/library-mode-api";
import { useSetLibraryFileLeaveAlone } from "../../lib/processing/library-mode-queries";

/**
 * Whether this file is Left alone, as the file panel shows it: the saved answer, or the one being saved until
 * the library's list catches up with it.
 */
export function useLeaveAlone(libraryId: number, file: LibraryFile) {
  const mutation = useSetLibraryFileLeaveAlone(libraryId);
  const asked = mutation.variables?.leaveAlone;
  const leftAlone =
    (mutation.isPending || mutation.isSuccess) && asked !== undefined
      ? asked
      : file.leave_alone;
  return { mutation, leftAlone };
}

type LeaveAlone = ReturnType<typeof useLeaveAlone>;

/** "Left alone": nothing cleans the file until this is cleared, with how saving the choice went. */
export function LibraryLeaveAlone({
  file,
  state,
}: {
  file: LibraryFile;
  state: LeaveAlone;
}) {
  const { mutation, leftAlone } = state;
  return (
    <div className="mm-drawer__aside-wrap">
      <label className="mm-drawer__aside">
        <input
          type="checkbox"
          checked={leftAlone}
          disabled={mutation.isPending}
          onChange={(event) =>
            mutation.mutate({
              path: file.path,
              leaveAlone: event.target.checked,
            })
          }
        />
        <span>
          <b>Left alone.</b> Nothing cleans this file — not a scan, not the
          schedule, not picking it in the table — until you clear this.
        </span>
      </label>
      <p
        className="mm-drawer__status"
        role="status"
        data-testid="library-leave-alone-status"
      >
        {mutation.isPending
          ? "Saving…"
          : mutation.isSuccess
            ? leftAlone
              ? "Saved. Weir will leave this file alone."
              : "Saved. This file can be cleaned again."
            : ""}
      </p>
      {mutation.isError ? (
        <p className="mm-drawer__failure" data-status="broken" role="alert">
          {errorMessage(mutation.error, LEAVE_ALONE_FAILED)}
        </p>
      ) : null}
    </div>
  );
}
