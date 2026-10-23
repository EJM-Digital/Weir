/**
 * The Dashboard's Live view: today's figures, what is being worked on and what comes next, the Pipeline
 * every file moves through, what just finished and what Weir just did, and down the side its health
 * and what needs a person. Every number comes from the server; the view follows the Activity stream rather
 * than polling, and ticks once a second so countdowns and "min ago" move between updates. A working file's
 * percent, ETA and message come from the same stream's live-progress frame (#750), which moves about once a
 * second even though the file list itself only changes on a database write: the start of a pass, a stage
 * change, or its end.
 */
import { useCallback, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";

import {
  FileStoryPanel,
  type FileStoryPanelProps,
} from "../../../components/processing/file-story-panel";
import { ApiEntryError } from "../../../components/shared/api-entry-error";
import { PageLoading } from "../../../components/shared/page-loading";
import type { FinishedFile } from "../../../lib/activity/processing-outcome";
import { activityKeys } from "../../../lib/activity/query-keys";
import { useActivityStreamInvalidations } from "../../../lib/activity/use-activity-stream-invalidation";
import { loadErrorMessage } from "../../../lib/api/error-message";
import { formatBytes } from "../../../lib/format/bytes";
import { usePauseQuery } from "../../../lib/pause/pause-queries";
import type { ProcessingFile } from "../../../lib/processing/files-api";
import { useProcessingFileLog } from "../../../lib/processing/files-queries";
import { useProcessingFilesAtOnceQuery } from "../../../lib/processing/queries";
import { processingKeys } from "../../../lib/processing/query-keys";
import { classNames } from "../../../lib/ui/class-names";
import { useNow } from "../../../lib/ui/use-now";
import { useLeavingCards } from "../leaving-cards";
import { fileNowOf } from "../pipeline/file-now";
import { JustFinishedShelf } from "../pipeline/just-finished-shelf";
import { PipelineBoard } from "../pipeline/pipeline-board";
import { buildPipelineCards } from "../pipeline/pipeline-cards";
import { stackedBoardBudget } from "../pipeline/pipeline-layout";
import { TODAY_DAYS, shownBy, type Filter } from "../processing-filter";
import { prettyName } from "../processing-model";
import { FILES_QUERY, useLanes } from "../use-processing-lanes";
import { useFinishedFiles } from "../use-finished-files";
import { ACTIVE_JOBS_LIMIT, WORKING_FILES_QUERY } from "../working-count";
import { leavingInWorkflow } from "../workflow-scope";
import { ActivityStream } from "./activity-stream";
import { LOW_COLUMNS, type PageLayout } from "./dashboard-layout";
import { HealthPanel } from "./health-panel";
import { LiveGrid } from "./live-grid";
import { NeedsPanel } from "./needs-panel";
import { NextTile } from "./next-tile";
import { TodayTile } from "./today-tile";
import { useNextItems } from "./use-next-items";
import { useTodayFigures, type TodayFigures } from "./use-today-figures";
import { WorkingTile } from "./working-tile";
import { sharedWaitSeconds } from "./working-words";

const NO_FILES: ProcessingFile[] = [];
/** Once a second, so countdowns and "min ago" move between server updates. */
const TICK_MS = 1000;

// A running pass rewrites its progress row several times a second and every write reaches the
// stream, so the lanes follow it closely and the totals, which only change when a file finishes,
// follow it at a gentler pace. Just finished is read from the Activity entry a pass writes as it ends, so it
// refreshes with the lanes: a file leaves them and lands there in the same step (#852).
const LANE_KEYS = [
  processingKeys.fileList(FILES_QUERY),
  processingKeys.fileList(WORKING_FILES_QUERY),
  processingKeys.jobsInspectionList("active", ACTIVE_JOBS_LIMIT),
  activityKeys.recent,
] as const;
const TOTAL_KEYS = [
  processingKeys.overviewStats(TODAY_DAYS),
  processingKeys.filesAtOnce,
] as const;
const LANE_THROTTLE_MS = 750;
const TOTAL_THROTTLE_MS = 3_000;

/** "38 cleaned today · 41.2 GB saved", for the shelf's title row; no space is claimed when none is recorded. */
function cleanedToday(figures: TodayFigures | undefined): string | undefined {
  if (!figures) return undefined;
  const cleaned = `${figures.cleaned.toLocaleString()} cleaned today`;
  if (figures.savedBytes === null) return cleaned;
  return `${cleaned} · ${formatBytes(figures.savedBytes)} saved`;
}

/** The file whose story is open: its name, and what its header shows beside it. */
type StoryFile = Pick<FileStoryPanelProps, "poster"> & {
  id: number;
  name: string;
};

type LiveViewProps = {
  filter: Filter;
  /** Narrows every part to one workflow; every workflow when null. */
  workflowId: number | null;
  /** How the page lays itself out, decided from the width of its main area. */
  layout: PageLayout;
};

export function LiveView({ filter, workflowId, layout }: LiveViewProps) {
  useActivityStreamInvalidations(LANE_KEYS, { throttleMs: LANE_THROTTLE_MS });
  useActivityStreamInvalidations(TOTAL_KEYS, {
    throttleMs: TOTAL_THROTTLE_MS,
  });
  const now = useNow(TICK_MS);
  const board = useLanes(workflowId);
  const { files, libraries, lanes, scoped } = board;
  const filesAtOnce = useProcessingFilesAtOnceQuery();
  const today = useTodayFigures(workflowId, filter, now).figures;
  const pause = usePauseQuery();
  const fileLog = useProcessingFileLog();
  const navigate = useNavigate();
  const [storyFile, setStoryFile] = useState<StoryFile | null>(null);
  const [lowNeed, setLowNeed] = useState<number>();
  // Cards that left are tracked against every workflow's lanes, so narrowing the page to one kind of file or
  // one workflow is never taken for a file leaving.
  const { files: finished, error: finishedError } = useFinishedFiles(
    filter,
    workflowId,
  );
  const leaving = useLeavingCards(
    lanes.waiting,
    lanes.working,
    lanes.handing,
    files.data?.files ?? NO_FILES,
  );
  const next = useNextItems(libraries.data, workflowId, filter);
  // Where the open file is on the Pipeline, if it is on it or has just left it, worked out from the same lanes as the cards.
  const storyId = storyFile?.id;
  const storyNow = useMemo(() => {
    if (storyId === undefined) return undefined;
    const card = buildPipelineCards(lanes, leaving, "all", now).find(
      (entry) => entry.file?.id === storyId,
    );
    return card ? fileNowOf(card) : undefined;
  }, [lanes, leaving, now, storyId]);

  const openFile = useCallback(
    (file: ProcessingFile) => {
      setStoryFile({
        id: file.id,
        name: file.relative_path,
        poster: {
          url: file.poster_url,
          workflow: file.library_name,
        },
      });
      fileLog.mutate(file.id);
    },
    [fileLog],
  );
  const openFinished = useCallback(
    (item: FinishedFile) => {
      const match = files.data?.files.find(
        (f) =>
          f.relative_path === item.relativePath &&
          (item.libraryId == null || f.library_id === item.libraryId),
      );
      if (match) {
        openFile(match);
        return;
      }
      // Older than the files list reaches, or a library file: open its activity instead.
      const path = encodeURIComponent(item.relativePath);
      void navigate(
        item.source === "library"
          ? `/library?path=${path}`
          : `/activity?q=${path}`,
      );
    },
    [files.data, navigate, openFile],
  );

  if (files.isPending) return <PageLoading label="Loading the Dashboard" />;
  if (files.isError) return <ApiEntryError error={files.error} />;

  const workflows = libraries.data ?? [];
  const workflowNames = new Map(
    workflows.map((workflow) => [workflow.id, workflow.name]),
  );
  const enabledWorkflowIds = new Set(
    workflows
      .filter((workflow) => workflow.enabled)
      .map((workflow) => workflow.id),
  );
  const shownWorkflows = workflows.filter(
    (workflow) => workflowId === null || workflow.id === workflowId,
  );
  const working = scoped.working.filter((item) => shownBy(filter, item));
  const paused = pause.data?.paused ?? false;
  const across = layout.band === "across";
  const boardBudget = layout.sideBySide
    ? undefined
    : stackedBoardBudget(window.innerHeight);

  return (
    <>
      <LiveGrid layout={layout} lowNeed={lowNeed}>
        <div className="mm-dash__band">
          <div
            className={classNames(
              "mm-dash__tiles",
              across ? "mm-dash__tiles--across" : "mm-dash__tiles--stacked",
            )}
          >
            <TodayTile filter={filter} now={now} workflowId={workflowId} />
            <WorkingTile
              working={working}
              filesAtOnce={filesAtOnce.data?.effective_files_at_once ?? null}
              waitSeconds={
                filter === "library" ? null : sharedWaitSeconds(shownWorkflows)
              }
              filter={filter}
              across={across}
              onOpen={openFile}
            />
            <NextTile
              items={next}
              now={now}
              paused={paused}
              filter={filter}
              across={across}
            />
          </div>
        </div>
        <div
          className="mm-dash__board"
          style={
            boardBudget === undefined ? undefined : { maxHeight: boardBudget }
          }
        >
          <PipelineBoard
            lanes={scoped}
            leaving={leavingInWorkflow(leaving, workflowId)}
            filter={filter}
            now={now}
            paused={paused}
            fill={layout.sideBySide}
            budget={boardBudget}
            onOpen={openFile}
          />
        </div>
        <div
          className="mm-dash__low"
          style={
            layout.sideBySide ? { gridTemplateColumns: LOW_COLUMNS } : undefined
          }
        >
          <JustFinishedShelf
            items={finished}
            readError={finishedError}
            filter={filter}
            workflowId={workflowId}
            now={now}
            workflowNames={workflowNames}
            enabledWorkflowIds={enabledWorkflowIds}
            count={workflowId === null ? cleanedToday(today) : undefined}
            onOpen={openFinished}
            onHeightNeed={setLowNeed}
          />
          <ActivityStream now={now} workflowId={workflowId} filter={filter} />
        </div>
        <div className="mm-dash__needs">
          <NeedsPanel
            workflowId={workflowId}
            filter={filter}
            onOpen={openFile}
          />
        </div>
        <div className="mm-dash__health">
          <HealthPanel workflows={workflows} workflowId={workflowId} />
        </div>
      </LiveGrid>

      <FileStoryPanel
        open={storyFile !== null}
        fileName={storyFile ? prettyName(storyFile.name) : ""}
        poster={storyFile?.poster}
        now={storyNow}
        log={fileLog.data}
        loading={fileLog.isPending}
        error={
          fileLog.isError
            ? loadErrorMessage(fileLog.error, "what happened to this file")
            : null
        }
        onClose={() => setStoryFile(null)}
      />
    </>
  );
}
