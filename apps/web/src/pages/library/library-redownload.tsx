/**
 * "Download again" (#509), in the file panel of a file a past clean left missing a track the library's rules
 * now keep. The media manager deletes the file before it searches, so this always asks first.
 */
import { useState } from "react";

import { ConfirmDialog } from "../../components/ui/confirm-dialog";
import { errorMessage } from "../../lib/api/error-message";
import { languageName } from "../../lib/format/track";
import {
  useLibraryRedownloadsQuery,
  useRequestLibraryRedownload,
} from "../../lib/processing/library-mode-queries";
import type { LibraryRedownloadTitle } from "../../lib/processing/library-redownload-api";
import { mmActionButtonClass } from "../../lib/ui/mm-control-roles";

/** "French audio, English subtitles": what the file lost that the rules would now keep. */
function missingTracks(title: LibraryRedownloadTitle): string {
  const kinds = title.removed_tracks.map(
    (track) =>
      `${languageName(track.language)} ${track.type === "audio" ? "audio" : "subtitles"}`,
  );
  return [...new Set(kinds)].join(", ");
}

export function LibraryRedownload({
  libraryId,
  path,
}: {
  libraryId: number;
  path: string;
}) {
  const titles = useLibraryRedownloadsQuery(libraryId);
  const request = useRequestLibraryRedownload(libraryId);
  const [asking, setAsking] = useState(false);

  if (titles.isError) {
    return (
      <p className="mm-drawer__note">
        Weir couldn’t check whether this file is missing tracks it could
        download again. Close the panel and open it again to retry.
      </p>
    );
  }
  const title = titles.data?.titles.find((entry) => entry.path === path);
  if (!title && !request.data) return null;
  const them = title?.removed_tracks.length === 1 ? "it" : "them";
  // Once the manager has the request, asking again would only delete a file it may already be replacing. Any other answer leaves the person free to try again.
  const asked = request.data?.outcome === "requested";

  const send = () =>
    request.mutate(path, { onSettled: () => setAsking(false) });

  return (
    <section
      className="mm-drawer__redownload"
      aria-label="Download again"
      data-testid="library-redownload"
    >
      {title ? (
        <p className="mm-drawer__note">
          A past clean removed {missingTracks(title)} from this file, and this
          workflow’s rules would keep {them} now. The only way to get {them}{" "}
          back is to download the title again.
        </p>
      ) : null}
      {title?.waiting_note ? (
        <p className="mm-drawer__note" data-testid="library-redownload-waiting">
          {title.waiting_note}
        </p>
      ) : null}
      {title?.can_redownload && !asked ? (
        <button
          type="button"
          className={mmActionButtonClass({ variant: "secondary" })}
          disabled={request.isPending}
          onClick={() => setAsking(true)}
        >
          {request.isPending ? "Asking your media manager…" : "Download again"}
        </button>
      ) : title?.unavailable_reason ? (
        <p className="mm-drawer__note">{title.unavailable_reason}</p>
      ) : null}
      {request.data ? (
        <p className="mm-drawer__status" role="status">
          {request.data.message}
        </p>
      ) : null}
      {request.isError ? (
        <p className="mm-drawer__failure" data-status="broken" role="alert">
          {errorMessage(
            request.error,
            "Weir couldn't ask your media manager. Nothing was deleted; try again.",
          )}
        </p>
      ) : null}
      {asking && title ? (
        <ConfirmDialog
          title="Download this title again?"
          description={
            <>
              <p>
                Weir asks your media manager to delete this file and fetch the
                title again.
              </p>
              {title.confirmation_message ? (
                <p>{title.confirmation_message}</p>
              ) : null}
            </>
          }
          confirmLabel="Yes, download it again"
          tone="danger"
          cancelLabel="Not now"
          busy={request.isPending}
          busyLabel="Asking your media manager…"
          testId="library-redownload-confirm"
          onCancel={() => setAsking(false)}
          onConfirm={send}
        />
      ) : null}
    </section>
  );
}
