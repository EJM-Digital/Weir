/**
 * One library file, opened from the table: what is in it, what your rules would take out of it and why,
 * and what you can do about it. The track list is the rule-set editor's own preview (#502), so the two can
 * never disagree about a file. You can also pick the tracks for this one file (#501), mark it Left alone so
 * nothing cleans it, or ask its media manager to download it again when a past clean took too much (#509).
 */
import { Chip } from "../../components/panels/chip";
import { useState } from "react";
import { Link } from "react-router-dom";
import { SidePanel } from "../../components/shared/side-panel";
import { mmActionButtonClass } from "../../lib/ui/mm-control-roles";
import { useActivityRecentQuery } from "../../lib/activity/queries";
import { formatBytes } from "../../lib/format/bytes";
import { positionsByKind, videoCodecName } from "../../lib/format/track";
import type {
  LibraryCleanResult,
  LibraryFile,
  LibraryManualPlan,
} from "../../lib/processing/library-mode-api";
import {
  useLibraryFilePreviewQuery,
  useLibrarySettingsQuery,
} from "../../lib/processing/library-mode-queries";
import { originalsFolderLabel } from "../../lib/processing/library-mode-api";
import { baseName } from "../../lib/format/path";
import { errorMessage } from "../../lib/api/error-message";
import { useAppDateFormatter } from "../../lib/ui/mm-format-date";
import { plural } from "../../lib/ui/mm-plural";
import { LibraryCleanOutcome } from "./library-clean-dialog";
import { fileMeaning, statusNote, statusWords } from "./library-model";
import { REMOVAL_IS_FINAL, removalIsRecoverable } from "./library-clean-model";
import { TrackList, rulesKeep, toggled } from "./library-drawer-tracks";
import { LibraryLeaveAlone, useLeaveAlone } from "./library-leave-alone";
import { LibraryRedownload } from "./library-redownload";
import { setupTabPath } from "../../lib/settings/setup-areas";

const MANAGER_NAMES: Record<string, string> = {
  sonarr: "Sonarr",
  radarr: "Radarr",
};

/** How a clean started from this panel is going: waiting on the server, why it failed, or what it did. */
export interface FileCleanState {
  pending: boolean;
  failure: string | null;
  result: LibraryCleanResult | null;
  onDismissResult: () => void;
}

function FileChips({
  file,
  leftAlone,
}: {
  file: LibraryFile;
  leftAlone: boolean;
}) {
  const note = statusNote(file);
  return (
    <div className="mm-drawer__chips">
      {file.video_height ? <Chip dot={false}>{file.video_height}p</Chip> : null}
      {file.video_codec && file.video_codec !== "unknown" ? (
        <Chip dot={false}>{videoCodecName(file.video_codec)}</Chip>
      ) : null}
      <Chip dot={false}>{formatBytes(file.size_bytes)}</Chip>
      {file.manager_kind ? (
        <Chip meaning="done">
          {MANAGER_NAMES[file.manager_kind] ?? file.manager_kind}: matched
        </Chip>
      ) : null}
      <Chip meaning={leftAlone ? "idle" : fileMeaning(file)}>
        {leftAlone && file.status !== "left_alone"
          ? "Left alone"
          : statusWords(file)}
      </Chip>
      {note ? <span className="mm-drawer__note">{note}</span> : null}
    </div>
  );
}

function FileHistory({ path }: { path: string }) {
  const when = useAppDateFormatter();
  const history = useActivityRecentQuery(
    { limit: 6, file: path },
    { keepWhileLoading: false },
  );
  const items = history.data?.items ?? [];
  if (items.length === 0) return null;
  return (
    <div className="mm-drawer__history">
      <p className="mm-drawer__eyebrow">What has happened to this file</p>
      {items.map((item) => (
        <p key={item.id} className="mm-drawer__step">
          <span className="mm-drawer__when">{when(item.created_at)}</span>
          {item.title}
        </p>
      ))}
    </div>
  );
}

export function LibraryFileDrawer({
  libraryId,
  libraryName,
  file,
  onClose,
  onClean,
  clean,
}: {
  libraryId: number;
  libraryName: string;
  file: LibraryFile;
  onClose: () => void;
  /** Without a plan the library's rules decide; with one, exactly these tracks are kept. */
  onClean: (path: string, manual?: LibraryManualPlan) => void;
  clean: FileCleanState;
}): React.ReactElement {
  // The tracks you have said to keep, once you start choosing; null while the rules are deciding.
  const [keep, setKeep] = useState<Set<number> | null>(null);
  const leaveAlone = useLeaveAlone(libraryId, file);
  const settings = useLibrarySettingsQuery(libraryId);
  // The plan shown is the one the library cleans by, so it waits to hear which profile that is.
  const preview = useLibraryFilePreviewQuery(
    libraryId,
    file.path,
    settings.data?.library_rule_set_id ?? null,
    !settings.isPending,
  );

  const tracks = preview.data?.tracks ?? [];
  const positions = positionsByKind(tracks);
  const audio = tracks.filter((track) => track.type === "audio");
  const subtitles = tracks.filter((track) => track.type === "subtitle");
  const choosing = keep !== null;
  const kept = keep ?? new Set(tracks.filter(rulesKeep).map((t) => t.index));
  const keptAudio = audio.filter((track) => kept.has(track.index)).length;
  const removing = tracks.filter(
    (track) => track.type !== "video" && !kept.has(track.index),
  ).length;

  // A file Left alone is never cleaned; a choice must keep some audio and must actually change something; and
  // without a choice, only a file the rules would change has anything to do.
  const cannotClean =
    clean.pending ||
    leaveAlone.leftAlone ||
    (choosing
      ? keptAudio === 0 || removing === 0
      : file.classification !== "would_change");

  const manualPlan = (): LibraryManualPlan => {
    const chosen = tracks.filter(
      (track) => track.type === "video" || kept.has(track.index),
    );
    const firstAudio = chosen.find((track) => track.type === "audio");
    return {
      keep: chosen.map((track) => ({
        index: track.index,
        default: track.index === firstAudio?.index,
        forced: track.forced,
      })),
      order: chosen.map((track) => track.index),
      expected_size_bytes: file.size_bytes,
    };
  };

  return (
    <SidePanel
      open
      title={baseName(file.path)}
      eyebrow={`Library · ${libraryName}${file.manager_title ? ` · ${file.manager_title}` : ""}`}
      subtitle={file.path}
      onClose={onClose}
      dataTestId="library-file-drawer"
    >
      <>
        <FileChips file={file} leftAlone={leaveAlone.leftAlone} />

        <p className="mm-drawer__verdict">
          {choosing
            ? keptAudio === 0
              ? "Keep at least one audio track."
              : removing === 0
                ? "That is everything this file already holds, so there would be nothing to do."
                : `Keeping ${kept.size} of ${tracks.length} tracks; ${removing} would come out for good.`
            : (file.summary ??
              file.reason ??
              "Weir has not judged this file yet.")}
        </p>

        {preview.isPending ? (
          <p className="mm-drawer__note">{"Reading this file’s tracks…"}</p>
        ) : preview.isError ? (
          <p className="mm-drawer__note">
            Weir could not read this file just now:{" "}
            {errorMessage(preview.error, "it could not be opened.")}
          </p>
        ) : (
          <>
            <TrackList
              title={`Audio · ${plural(audio.length, "track", "tracks")}`}
              tracks={audio}
              positions={positions}
              kept={choosing ? kept : null}
              onToggle={(index) => setKeep(toggled(kept, index))}
            />
            <TrackList
              title={`Subtitles · ${plural(subtitles.length, "track", "tracks")}`}
              tracks={subtitles}
              positions={positions}
              kept={choosing ? kept : null}
              onToggle={(index) => setKeep(toggled(kept, index))}
            />
            <p className="mm-drawer__note">
              {choosing ? (
                "Your choice, for this file only."
              ) : (
                <>
                  {"Decided by this workflow’s rules. "}
                  <Link to={`${setupTabPath("profiles")}?library=${libraryId}`}>
                    Edit the rules →
                  </Link>
                </>
              )}
            </p>
          </>
        )}

        <div className="mm-drawer__actions">
          <button
            type="button"
            className={mmActionButtonClass({ variant: "primary" })}
            disabled={cannotClean}
            onClick={() =>
              onClean(file.path, choosing ? manualPlan() : undefined)
            }
          >
            {clean.pending
              ? "Checking this file…"
              : choosing
                ? "Clean with these tracks"
                : "Clean this file"}
          </button>
          <button
            type="button"
            className="mm-head-control"
            disabled={tracks.length === 0}
            onClick={() => setKeep(choosing ? null : new Set(kept))}
          >
            {choosing ? "Use this workflow’s rules" : "Choose tracks"}
          </button>
          <button
            type="button"
            className="mm-head-control"
            disabled={preview.isFetching}
            onClick={() => void preview.refetch()}
          >
            {preview.isFetching ? "Checking…" : "Check it again"}
          </button>
        </div>

        {clean.failure ? (
          <p className="mm-drawer__failure" data-status="broken" role="alert">
            {clean.failure}
          </p>
        ) : null}
        {clean.result ? (
          <LibraryCleanOutcome
            result={clean.result}
            known={[file]}
            onClose={clean.onDismissResult}
          />
        ) : null}

        <LibraryLeaveAlone file={file} state={leaveAlone} />

        <p className="mm-drawer__note">
          Cleaning rewrites this file where it sits and tells your media manager
          to look at it again.{" "}
          {settings.data?.keep_original_after_clean
            ? removalIsRecoverable(
                originalsFolderLabel(settings.data.originals_folder),
              )
            : REMOVAL_IS_FINAL}
        </p>

        <LibraryRedownload libraryId={libraryId} path={file.path} />

        <FileHistory path={file.path} />
      </>
    </SidePanel>
  );
}
