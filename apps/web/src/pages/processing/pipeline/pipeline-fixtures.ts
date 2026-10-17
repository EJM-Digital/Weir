/** Files, jobs and ended cards for the Pipeline's tests, built the way the page builds them. */
import type { ProcessingFile } from "../../../lib/processing/files-api";
import type { ProcessingJobInspectionRow } from "../../../lib/processing/jobs-inspection/types";
import type { LeavingCard } from "../leaving-cards";
import {
  LIBRARY_CLEAN_JOB_KIND,
  buildLanes,
  type Lanes,
} from "../processing-model";
import { recordedProcessingFile } from "../../../test/recorded";

export const NOW = Date.parse("2026-10-02T10:00:00Z");

const WORKFLOWS = new Map([
  [1, "Movies"],
  [2, "TV"],
]);

export function aFile(
  id: number,
  status: ProcessingFile["status"],
  overrides: Partial<ProcessingFile> = {},
): ProcessingFile {
  return {
    ...recordedProcessingFile(),
    id,
    library_id: 2,
    library_name: "TV",
    relative_path: `The.Quiet.Harbour.S01E0${id}.1080p.WEB-DL.mkv`,
    status,
    status_reason: "",
    blocked_by_connection: null,
    size_bytes: 2_437_000_000,
    video_height: 1080,
    video_codec: "h264",
    duration_seconds: 2700,
    progress_percent: null,
    progress_eta_seconds: null,
    hold_until: null,
    size_changed_at: null,
    created_at: "2026-10-02T09:50:00",
    updated_at: "2026-10-02T09:59:00",
    ...overrides,
  };
}

/** A pass that is writing the file: the stage and percent the server's progress reports. */
export function aWriting(
  id: number,
  overrides: Partial<ProcessingFile> = {},
): ProcessingFile {
  return aFile(id, "processing", {
    progress_status: "processing",
    progress_stage: "writing",
    progress_percent: 42,
    progress_eta_seconds: 600,
    progress_speed: "148x",
    progress_elapsed_seconds: 134,
    ...overrides,
  });
}

export function aCleanJob(
  id: number,
  status: ProcessingJobInspectionRow["status"],
): ProcessingJobInspectionRow {
  return {
    id,
    dedupe_key: `clean-${id}`,
    job_kind: LIBRARY_CLEAN_JOB_KIND,
    status,
    payload_json: JSON.stringify({
      library_id: 1,
      path: `Paper Lanterns (2023)/Paper.Lanterns.2023.1080p.BluRay.${id}.mkv`,
    }),
  } as ProcessingJobInspectionRow;
}

export function lanesOf(
  files: ProcessingFile[],
  jobs: ProcessingJobInspectionRow[] = [],
  nextLooks = new Map<number, { at: number; interval: number }>(),
): Lanes {
  return buildLanes(files, jobs, WORKFLOWS, new Map(), nextLooks);
}

export function anEndedCard(
  id: number,
  outcome: LeavingCard["outcome"],
  overrides: Partial<LeavingCard> = {},
): LeavingCard {
  const file = aFile(id, "processed");
  return {
    key: `file-${id}`,
    lane: "working",
    source: "download",
    name: "The Quiet Harbour S01E01",
    path: file.relative_path,
    libraryName: "TV",
    step: "write",
    file,
    outcome,
    ...overrides,
  };
}
